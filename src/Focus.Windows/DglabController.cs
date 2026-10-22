using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DglabFocus.Core;

namespace DglabFocus.Windows;

public sealed class DglabController : IDisposable
{
    public const string RelayUrl = "wss://trex.dungeon-lab.cn/v4";
    private readonly Uri relay;
    private readonly object sync = new();
    private readonly SemaphoreSlim sendGate = new(1), outputGate = new(1);
    private readonly SemaphoreSlim armGate = new(1);
    private readonly Dictionary<string, DglabDevice> devices = [];
    private readonly HashSet<string> clients = [];
    private readonly ConcurrentDictionary<string, (string Client, TaskCompletionSource<JsonObject> Completion)> pending = new();
    private ClientWebSocket? socket;
    private CancellationTokenSource? lifetime;
    private Task? listener, worker, probe;
    private volatile bool armed;
    private string? selectedKey;
    private int channel, cap, intensity;
    private double desired;
    private long intentAt = Stopwatch.GetTimestamp(), lastLease;
    private long epoch;
    public bool IsArmed => armed;
    public bool IsConnected => socket?.State == WebSocketState.Open;
    public int RequestedIntensity => Volatile.Read(ref intensity);
    public string Status { get; private set; } = "未连接";
    public event Action<string, string?>? Updated;
    public event Action? DevicesChanged;
    public event Action<string>? Faulted;
    public IReadOnlyList<DglabDevice> Devices { get { lock (sync) return devices.Values.OrderBy(x => x.Name).ToList(); } }

    public DglabController(Uri? relayUri = null) => relay = relayUri ?? new Uri(RelayUrl);
    private void Report(string text, string? url = null) { Status = text; Updated?.Invoke(text, url); }
    private DglabDevice? Selected { get { lock (sync) return selectedKey != null ? devices.GetValueOrDefault(selectedKey) : null; } }

    public async Task Connect()
    {
        await DisconnectAsync().ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        var ws = new ClientWebSocket();
        lifetime = cts; socket = ws;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(relay, timeout.Token).ConfigureAwait(false);
            listener = ListenAsync(ws, cts.Token);
            worker = Task.Run(() => OutputLoopAsync(cts.Token));
            probe = Task.Run(() => ProbeLoopAsync(cts.Token));
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        { Report("中继连接失败，请检查网络后重试。", ""); Dispose(); }
    }

    private async Task ListenAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var bytes = new byte[262144];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var length = 0; WebSocketReceiveResult part;
                do
                {
                    part = await ws.ReceiveAsync(new ArraySegment<byte>(bytes, length, bytes.Length - length), ct);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (part.MessageType != WebSocketMessageType.Text || (length += part.Count) >= bytes.Length) throw new InvalidDataException("中继消息无效。");
                } while (!part.EndOfMessage);
                if (JsonNode.Parse(bytes.AsSpan(0, length)) is not JsonObject frame) throw new InvalidDataException();
                HandleFrame(frame);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or JsonException or InvalidOperationException or ArgumentException)
        { if (!ct.IsCancellationRequested) Report("设备连接异常：" + e.Message, ""); }
        finally
        {
            if (ReferenceEquals(socket, ws) && !ct.IsCancellationRequested)
            {
                armed = false; Volatile.Write(ref desired, 0); Volatile.Write(ref intensity, 0);
                lifetime?.Cancel();
                foreach (var item in pending.Values) item.Completion.TrySetCanceled();
                lock (sync) { devices.Clear(); clients.Clear(); }
                DevicesChanged?.Invoke(); Report("设备连接已断开，需要重新连接并授权。", "");
                Faulted?.Invoke("设备连接已断开，已暂停真实输出。");
            }
        }
    }

    private void HandleFrame(JsonObject frame)
    {
        var type = DglabDevice.Text(frame, "type");
        var client = DglabDevice.Text(frame, "clientId");
        if (type == "hello" && !string.IsNullOrWhiteSpace(client))
        {
            var url = new UriBuilder(relay) { Query = "tid=" + Uri.EscapeDataString(client) }.Uri.ToString();
            Report("等待 DG-LAB 4 App 扫码连接。", "https://dungeon-lab.cn/s/?v=1&action=socket&url=" + Uri.EscapeDataString(url));
        }
        else if (type == "client_attached" && !string.IsNullOrWhiteSpace(client))
        {
            lock (sync) clients.Add(client);
            Report("App 已连接，等待设备列表。", "");
            _ = DiscoverAsync(client);
        }
        else if (type == "client_disconnected" && client != null)
        {
            var selected = Selected?.ClientId == client;
            lock (sync) { clients.Remove(client); foreach (var key in devices.Where(x => x.Value.ClientId == client).Select(x => x.Key).ToList()) devices.Remove(key); }
            foreach (var item in pending.Values.Where(x => x.Client == client)) item.Completion.TrySetCanceled();
            DevicesChanged?.Invoke();
            if (selected) _ = FailAsync("所选 App 已断开，需要重新授权。");
        }
        else if (type == "message" && client != null && frame["data"] is JsonObject data)
        {
            lock (sync) { if (!clients.Contains(client)) return; }
            var messageType = DglabDevice.Text(data, "t");
            if (messageType == "resp" && DglabDevice.Text(data, "reqId") is { } id && pending.TryGetValue(id, out var request) && request.Client == client)
            {
                if (DglabDevice.Text(data, "error") is { } error) request.Completion.TrySetException(new InvalidOperationException("App 拒绝操作：" + error));
                else if (data["result"] is JsonNode result) request.Completion.TrySetResult(new JsonObject { ["value"] = result.DeepClone() });
                else request.Completion.TrySetException(new InvalidOperationException("App 响应缺少结果。"));
            }
            else if (messageType == "ev")
            {
                var ev = DglabDevice.Text(data, "ev");
                if (ev == "devices.snapshot" && data["devices"] is JsonArray snapshot) ReplaceDevices(client, snapshot);
                else if (ev == "devices.patch")
                {
                    lock (sync)
                    {
                        if (data["removed"] is JsonArray removed) foreach (var idNode in removed)
                            if (idNode is JsonValue value && value.TryGetValue<string>(out var slot)) devices.Remove(client + "/" + slot);
                        if (data["added"] is JsonArray added) foreach (var entry in added.OfType<JsonObject>()) AddDevice(client, entry);
                    }
                    DevicesChanged?.Invoke();
                }
                else if (ev == "slots.patch" && data["slots"] is JsonArray slots)
                {
                    lock (sync)
                    {
                        foreach (var patch in slots.OfType<JsonObject>())
                        {
                            var key = client + "/" + DglabDevice.Text(patch, "slotId");
                            if (!devices.TryGetValue(key, out var old)) continue;
                            var props = (JsonObject)old.Props.DeepClone(); var state = (JsonObject)old.SlotState.DeepClone();
                            if (patch["props"] is JsonObject p) DglabProtocol.Merge(props, p);
                            if (patch["slotState"] is JsonObject s) DglabProtocol.Merge(state, s);
                            devices[key] = old with { Props = props, SlotState = state };
                        }
                    }
                    DevicesChanged?.Invoke();
                }
                if (armed && !Ready(out _, out var reason)) _ = FailAsync(reason);
            }
        }
        else if (type is "idle_timeout" or "error") throw new InvalidDataException("中继会话已失效，请重新连接。");
    }

    private void AddDevice(string client, JsonObject data)
    {
        if (DglabDevice.Text(data, "slotId") is not { Length: > 0 } slot) return;
        devices[client + "/" + slot] = new(client, slot, DglabDevice.Text(data, "name") ?? "未命名设备", DglabDevice.Text(data, "type") ?? "未知型号",
            data["props"] is JsonObject props ? (JsonObject)props.DeepClone() : new(), data["slotState"] is JsonObject state ? (JsonObject)state.DeepClone() : new());
    }
    private void ReplaceDevices(string client, JsonArray list)
    {
        lock (sync)
        {
            foreach (var key in devices.Where(x => x.Value.ClientId == client).Select(x => x.Key).ToList()) devices.Remove(key);
            foreach (var entry in list.OfType<JsonObject>()) AddDevice(client, entry);
        }
        DevicesChanged?.Invoke(); Report($"已发现 {Devices.Count} 台设备。选择主机和通道后，可授权本次输出。", "");
    }
    private async Task DiscoverAsync(string client)
    {
        try
        {
            var result = await RequestAsync(client, "devices.get");
            if (result["value"] is JsonObject value && value["devices"] is JsonArray list) ReplaceDevices(client, list);
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or TimeoutException or WebSocketException or IOException)
        { Report("设备列表请求失败，可点击刷新重试。"); }
    }
    public async Task RefreshDevicesAsync()
    { List<string> list; lock (sync) list = [..clients]; foreach (var client in list) await DiscoverAsync(client); }

    private async Task SendAsync(JsonObject frame, CancellationToken ct)
    {
        var ws = socket ?? throw new InvalidOperationException("设备连接未建立。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await sendGate.WaitAsync(timeout.Token);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, timeout.Token); }
        finally { sendGate.Release(); }
    }
    private async Task<Task<JsonObject>> QueueAsync(string client, string method, JsonObject? data = null, double timeoutSeconds = 4)
    {
        var ct = lifetime?.Token ?? new CancellationToken(true);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = (client, completion);
        try
        {
            await SendAsync(new() { ["type"] = "message", ["clientId"] = client, ["data"] = new JsonObject { ["t"] = "req", ["reqId"] = id, ["m"] = method, ["data"] = data } }, ct).ConfigureAwait(false);
            return AwaitAsync();
        }
        catch { pending.TryRemove(id, out _); completion.TrySetCanceled(); throw; }
        async Task<JsonObject> AwaitAsync()
        {
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false); }
            finally { pending.TryRemove(id, out _); }
        }
    }
    private async Task<JsonObject> RequestAsync(string client, string method, JsonObject? data = null, double timeoutSeconds = 4)
        => await (await QueueAsync(client, method, data, timeoutSeconds).ConfigureAwait(false)).ConfigureAwait(false);

    private bool Ready(out DglabDevice? target, out string reason)
    {
        target = Selected;
        reason = "所选设备已移除或未连接。";
        if (target == null || !IsConnected) return false;
        if (!target.TryLimit(channel, out var max, out reason)) return false;
        if (cap > max) { reason = "App 的通道上限降低，请重新确认输出上限并授权。"; return false; }
        return true;
    }
    public async Task ArmAsync(string key, int selectedChannel, int userCap)
    {
        await armGate.WaitAsync().ConfigureAwait(false);
        try
        {
        var stopping = StopAsync();
        long grant; lock (sync) grant = epoch;
        await stopping;
        if (selectedChannel is not (0 or 1) || userCap is < 1 or > 100) throw new ArgumentException("请手动填写 1–100 之间的本次设备上限。");
        selectedKey = key; channel = selectedChannel; cap = userCap;
        if (!Ready(out var target, out var reason)) throw new ArgumentException(reason);
        await outputGate.WaitAsync();
        try
        {
            await ResetAsync(target!);
            await RequestAsync(target!.ClientId, "ping", timeoutSeconds: 2);
            if (!Ready(out _, out reason)) throw new ArgumentException(reason);
            Volatile.Write(ref desired, 0); Volatile.Write(ref intentAt, Stopwatch.GetTimestamp());
            lock (sync)
            {
                if (grant != epoch) throw new OperationCanceledException("授权过程中已收到停止请求。");
                armed = true;
            }
            lastLease = 0;
            Report($"已授权 {target.Name} / {(channel == 0 ? "A" : "B")} 通道，本次硬上限 {cap}。等待方案开始。", "");
        }
        finally { outputGate.Release(); }
        }
        finally { armGate.Release(); }
    }
    public void SetDesired(double percent)
    {
        Volatile.Write(ref desired, double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0);
        Volatile.Write(ref intentAt, Stopwatch.GetTimestamp());
    }
    private async Task ResetAsync(DglabDevice target)
    {
        await RequestAsync(target.ClientId, "device.op.clear", new() { ["s"] = target.SlotId, ["c"] = channel }, 2).ConfigureAwait(false);
        await RequestAsync(target.ClientId, "device.op", DglabProtocol.Operation(target.SlotId, channel, 7, JsonValue.Create(0)!), 2).ConfigureAwait(false);
        Volatile.Write(ref intensity, 0);
    }
    public async Task StopAsync()
    {
        lock (sync) { epoch++; armed = false; }
        Volatile.Write(ref desired, 0);
        await outputGate.WaitAsync().ConfigureAwait(false);
        try { if (Selected is { } target && IsConnected) await ResetAsync(target); }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or TimeoutException or WebSocketException or IOException)
        { Report("已停止续发。停机确认未返回，请在手机 App 检查通道。"); }
        finally { Volatile.Write(ref intensity, 0); outputGate.Release(); }
    }
    private async Task FailAsync(string reason)
    {
        var notify = armed;
        await StopAsync(); Report(reason, "");
        if (notify) Faulted?.Invoke(reason);
    }
    private async Task ObserveAsync(Task<JsonObject> task, long generation)
    {
        try { await task; }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or TimeoutException or WebSocketException or IOException)
        { if (armed && Volatile.Read(ref epoch) == generation) await FailAsync("设备任务失败或响应超时，输出已停止。请重新授权。"); }
    }
    private async Task OutputLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!armed) continue;
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref intentAt)).TotalMilliseconds > 2500)
                { await FailAsync("专注检测未按时更新，设备输出已停止。"); continue; }
                if (!Ready(out _, out var reason)) { await FailAsync(reason); continue; }
                await outputGate.WaitAsync(ct);
                try
                {
                    if (!armed || !Ready(out var target, out _)) continue;
                    var level = (int)Math.Floor(cap * Volatile.Read(ref desired) / 100);
                    if (level <= 0) { if (RequestedIntensity > 0) await ResetAsync(target!); continue; }
                    if (Stopwatch.GetElapsedTime(lastLease).TotalMilliseconds < 500) continue;
                    var generation = Volatile.Read(ref epoch);
                    var strength = await QueueAsync(target!.ClientId, "device.op", DglabProtocol.TemporaryIntensity(target.SlotId, channel, level));
                    _ = ObserveAsync(strength, generation);
                    if (!armed) continue;
                    var pulse = await QueueAsync(target.ClientId, "device.op", DglabProtocol.Pulse(target.SlotId, channel));
                    _ = ObserveAsync(pulse, generation);
                    Volatile.Write(ref intensity, level); lastLease = Stopwatch.GetTimestamp();
                }
                finally { outputGate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is InvalidOperationException or WebSocketException or IOException or TimeoutException)
        { await FailAsync("设备发送失败：" + e.Message); }
    }
    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct))
                if (armed && Selected is { } target)
                {
                    try { await RequestAsync(target.ClientId, "ping", timeoutSeconds: 2); }
                    catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or TimeoutException or WebSocketException or IOException)
                    { if (armed) await FailAsync("手机 App 响应超时，设备输出已停止。"); }
                }
        }
        catch (OperationCanceledException) { }
    }
    public async Task DisconnectAsync()
    {
        await StopAsync();
        Dispose();
        var tasks = new[] { listener, worker, probe }.OfType<Task>().ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception e) when (e is OperationCanceledException or TimeoutException or WebSocketException) { }
        Report("已断开连接。输出授权已清除。", "");
    }
    public void Dispose()
    {
        lock (sync) { epoch++; armed = false; }
        Volatile.Write(ref desired, 0); Volatile.Write(ref intensity, 0);
        lifetime?.Cancel(); socket?.Dispose(); socket = null;
        foreach (var item in pending.Values) item.Completion.TrySetCanceled();
        lock (sync) { clients.Clear(); devices.Clear(); }
        DevicesChanged?.Invoke();
    }
}
