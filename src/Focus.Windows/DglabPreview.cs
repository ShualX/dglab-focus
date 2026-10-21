using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DglabFocus.Windows;
// V4 pairing and discovery only. No output commands are implemented in this preview build.
public sealed class DglabPreview : IDisposable
{
    public const string RelayUrl = "wss://trex.dungeon-lab.cn/v4";
    private ClientWebSocket? socket;
    private CancellationTokenSource? cancel;
    public event Action<string, string?>? Updated;
    public async Task Connect()
    {
        Dispose();
        cancel = new CancellationTokenSource();
        cancel.CancelAfter(TimeSpan.FromSeconds(10));
        socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri(RelayUrl), cancel.Token);
            cancel.CancelAfter(Timeout.InfiniteTimeSpan);
            _ = Listen(socket, cancel.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        { Updated?.Invoke("中继连接失败，可继续使用模拟反馈。", null); Dispose(); }
    }
    private async Task Listen(ClientWebSocket ws, CancellationToken ct)
    {
        var bytes = new byte[65536];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var length = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(bytes, length, bytes.Length - length), ct);
                    if (result.MessageType != WebSocketMessageType.Text || (length += result.Count) >= bytes.Length) throw new InvalidDataException();
                } while (!result.EndOfMessage);
                using var json = JsonDocument.Parse(bytes.AsMemory(0, length));
                var root = json.RootElement;
                if (!root.TryGetProperty("type", out var type)) continue;
                switch (type.GetString())
                {
                    case "hello":
                        var target = root.GetProperty("clientId").GetString();
                        var appUrl = RelayUrl + "/?tid=" + Uri.EscapeDataString(target!);
                        Updated?.Invoke("等待 DG-LAB 4 App 扫码，仅预览连接。", "https://dungeon-lab.cn/s/?v=1&action=socket&url=" + Uri.EscapeDataString(appUrl));
                        break;
                    case "client_attached": Updated?.Invoke("App 已配对。当前版本不会发送电刺激指令。", null); break;
                    case "client_disconnected": Updated?.Invoke("App 已断开，不会自动恢复输出。", ""); break;
                    case "message":
                        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("ev", out var ev)
                            && ev.GetString() == "devices.snapshot" && data.TryGetProperty("devices", out var devices))
                            Updated?.Invoke($"App 已连接，发现 {devices.GetArrayLength()} 台设备。仅预览。", "");
                        break;
                    case "idle_timeout": Updated?.Invoke("配对等待超时，请重新连接。", ""); break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { if (!ct.IsCancellationRequested) Updated?.Invoke("设备连接已断开。", ""); }
    }
    public void Dispose()
    {
        cancel?.Cancel();
        socket?.Dispose();
        cancel?.Dispose();
        socket = null;
        cancel = null;
    }
}
