using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DglabFocus.Core;
using DglabFocus.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class DglabIntegration
{
    public static async Task Run(Action<bool, string> assert)
    {
        await using var app = new FakeApp();
        await app.Start();
        using var client = new DglabController(app.Address);
        var faults = new ConcurrentQueue<string>();
        client.Faulted += faults.Enqueue;
        string? pairing = null;
        client.Updated += (_, url) => { if (!string.IsNullOrEmpty(url)) pairing = url; };
        await client.Connect();
        await Until(() => client.Devices.Count == 1);
        assert(pairing?.Contains("action=socket") == true && Uri.UnescapeDataString(pairing).Contains("tid=controller"), "V4 hello creates an App pairing link with tid");
        assert(!client.IsArmed && app.Positive.Count == 0, "connecting and device discovery never authorize output");
        var device = client.Devices.Single();
        var rejected = false;
        try { await client.ArmAsync(device.Key, 0, 101); } catch (ArgumentException) { rejected = true; }
        assert(rejected && !client.IsArmed, "manual cap outside 1–100 cannot arm output");
        rejected = false;
        try { await client.ArmAsync(device.Key, 0, 50); } catch (ArgumentException) { rejected = true; }
        assert(rejected && !client.IsArmed, "manual cap cannot exceed the App-reported channel maximum");
        await client.ArmAsync(device.Key, 0, 30);
        assert(client.IsArmed && app.ResetCount > 0 && app.Positive.Count == 0, "arming clears tasks and resets the chosen channel without starting output");
        client.SetDesired(40);
        await Until(() => app.Positive.Count > 0 && app.PulseCount > 0);
        assert(app.Positive.All(x => x.Level <= 12 && x.Duration == 1000 && x.Channel == 0 && x.Immediate), "curve maps to manual cap and every positive task has a finite one-second lease");
        assert(app.PulseCount > 0 && app.ValidPulseFrames, "actual V4 pulse RPC uses finite independent eight-byte frames");
        await client.StopAsync();
        var sent = app.Positive.Count;
        client.SetDesired(100);
        await Task.Delay(650);
        assert(!client.IsArmed && client.RequestedIntensity == 0 && app.Positive.Count == sent && app.Level == 0, "stop clears output and late positive intents cannot reauthorize it");

        await client.ArmAsync(device.Key, 1, 15);
        client.SetDesired(100);
        await Until(() => app.Positive.Skip(sent).Any(x => x.Channel == 1));
        assert(app.Positive.Skip(sent).All(x => x.Channel == 1 && x.Level <= 15), "B-channel authorization addresses only B and respects its independent cap");
        await client.StopAsync();

        app.FailNextPositive = true;
        await client.ArmAsync(device.Key, 0, 30); client.SetDesired(40);
        var beforeOld = app.Positive.Count;
        await Until(() => app.Positive.Count > beforeOld);
        await client.StopAsync(); await client.ArmAsync(device.Key, 0, 30); client.SetDesired(40);
        await Task.Delay(1200);
        assert(client.IsArmed, "late errors from a revoked lease cannot revoke a new authorization");
        await client.StopAsync();

        await client.ArmAsync(device.Key, 0, 30);
        client.SetDesired(50);
        await Until(() => app.Level > 0);
        client.SetDesired(0);
        await Until(() => app.Level == 0);
        assert(client.IsArmed && client.RequestedIntensity == 0, "learning or rest clears tasks while retaining the session authorization");

        client.SetDesired(50);
        await Until(() => app.Level > 0);
        await app.Patch(new JsonObject { ["slotId"] = "slot", ["slotState"] = new JsonObject { ["channelA"] = new JsonObject { ["comfortLimit"] = new JsonObject { ["overheat"] = true } } } });
        await Until(() => !client.IsArmed && app.Level == 0);
        await Until(() => faults.Any(x => x.Contains("过热")));
        assert(faults.Any(x => x.Contains("过热")), "nested status patches are merged and overheat revokes output");
        await app.Patch(new JsonObject { ["slotId"] = "slot", ["slotState"] = new JsonObject { ["channelA"] = new JsonObject { ["comfortLimit"] = new JsonObject { ["overheat"] = false } } } });
        await Until(() => client.Devices.Single().TryLimit(0, out _, out _));
        await client.ArmAsync(device.Key, 0, 30);
        client.SetDesired(100);
        await Until(() => app.Level > 0);
        await app.Patch(new JsonObject { ["slotId"] = "slot", ["slotState"] = new JsonObject { ["channelA"] = new JsonObject { ["intensityMax"] = 20 } } });
        await Until(() => !client.IsArmed && app.Level == 0);
        await Until(() => faults.Any(x => x.Contains("上限降低")));
        assert(faults.Any(x => x.Contains("上限降低")), "lowered App limits revoke an existing authorization");

        await client.ArmAsync(device.Key, 0, 20); client.SetDesired(50);
        await Until(() => app.Level > 0);
        await app.Patch(new JsonObject { ["slotId"] = "slot", ["slotState"] = new JsonObject { ["hasDevice"] = false } });
        await Until(() => !client.IsArmed && app.Level == 0);
        assert(!client.IsArmed, "Bluetooth device loss stops output while the phone connection remains open");
        await app.Patch(new JsonObject { ["slotId"] = "slot", ["slotState"] = new JsonObject { ["hasDevice"] = true } });
        await Until(() => client.Devices.Single().TryLimit(0, out _, out _));

        await client.ArmAsync(device.Key, 0, 20);
        client.SetDesired(50);
        await Until(() => app.Level > 0);
        await Until(() => !client.IsArmed && app.Level == 0, 6500);
        assert(faults.Any(x => x.Contains("未按时更新")), "independent watchdog stops output when the UI stops renewing its intent");

        var keepIntent = new CancellationTokenSource();
        var renew = Task.Run(async () => { try { while (true) { client.SetDesired(50); await Task.Delay(100, keepIntent.Token); } } catch (OperationCanceledException) { } });
        try
        {
            await client.ArmAsync(device.Key, 0, 20);
            app.IgnorePings = true;
            await Until(() => !client.IsArmed, 6500);
            await Until(() => app.Level == 0);
            await Until(() => faults.Any(x => x.Contains("响应超时")));
            assert(faults.Any(x => x.Contains("响应超时")), "phone RPC timeout stops output even while the desktop remains active");
            app.IgnorePings = false;

            app.OperationError = true;
            rejected = false;
            try { await client.ArmAsync(device.Key, 0, 20); } catch (InvalidOperationException) { rejected = true; }
            assert(rejected && !client.IsArmed, "App RPC errors prevent arming");
            app.OperationError = false;

            app.DelayReset = true;
            var arm = client.ArmAsync(device.Key, 0, 20);
            await Task.Delay(40);
            var stop = client.StopAsync();
            try { await arm; } catch (OperationCanceledException) { }
            await stop;
            assert(!client.IsArmed && app.Level == 0, "a stop during a pending arm cannot be undone by a late authorization response");
            app.DelayReset = false;

            await client.ArmAsync(device.Key, 0, 20);
            await Until(() => app.Level > 0);
            await app.CloseConnection();
            await Until(() => !client.IsArmed);
            await Until(() => app.Level == 0, 2200);
            assert(!client.IsArmed && app.Level == 0, "disconnect revokes authorization and the final remote lease expires");
            await client.Connect();
            await Until(() => client.Devices.Count == 1);
            assert(!client.IsArmed && client.RequestedIntensity == 0, "reconnection never restores output authorization");
            await client.DisconnectAsync();
            var exe = Path.Combine(AppContext.BaseDirectory, "DglabFocus.exe");
            var preserve = Environment.GetEnvironmentVariable("DGLAB_UI_TEST_CAPTURE");
            var folder = preserve ?? Path.Combine(Path.GetTempPath(), "DglabFocus-device-ui-" + Guid.NewGuid().ToString("N"));
            using var fixture = new ApplicationWindowFixture();
            ApplicationWindowFixture.CheckNative(fixture, assert);
            Directory.CreateDirectory(folder);
            var activationRequest = Path.Combine(folder, "window-capture-request");
            if (File.Exists(activationRequest)) File.Delete(activationRequest);
            var launch = new ProcessStartInfo(exe) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--smoke-test", "--device-test-relay", app.Address.ToString(), "--capture-dir", folder,
                "--app-picker-fixture", fixture.Handle.ToInt64().ToString(), "--fixture-executable", Environment.ProcessPath! }) launch.ArgumentList.Add(arg);
            using var process = Process.Start(launch) ?? throw new Exception("Could not start UI test host");
            using var notificationStop = new CancellationTokenSource();
            var notificationTask = Task.Run(async () =>
            {
                try
                {
                    while (!notificationStop.IsCancellationRequested)
                    {
                        if (File.Exists(activationRequest)) { File.Delete(activationRequest); fixture.NotifyActivation(); }
                        await Task.Delay(30, notificationStop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                var result = File.ReadAllText(Path.Combine(folder, "smoke-result.txt"));
                assert(process.ExitCode == 0 && result.Contains("UI authorization"), "Windows UI connects a local fake App, arms, starts its plan, emits bounded requests and stops immediately: " + (process.ExitCode == 0 ? "PASS" : result));
                assert(result.Contains("native window activation"), "Windows UI enumerates two fixture windows, merges them, captures a real WinEvent notification and applies its executable rule");
                assert(result.Contains("desktop extension setup"), "Windows UI prepares an automatically paired extension and distinguishes pending installation from an actual connection");
                assert(result.Contains("10-second activation with zero debt → accumulated-time ramp"), "Windows UI issues zero before 10 seconds, starts V4 output with zero debt at 10 seconds, and raises intensity after 30 accumulated seconds");
                assert(result.Contains("Live requested intensity in main header"), "Windows UI shows live requests 2 → 3 in the header and floating ball, then clears them on emergency stop");
                assert(result.Contains("hard mode rejects unlimited duration"), "hard-mode UI locks normal exit, early rest and app close while preserving emergency stop and fixed-duration completion");
                assert(result.Contains("first-run tutorial, all eight steps"), "first launch and help replay show all eight tutorial steps, navigate settings, support skip/completion and preserve user configuration");
                assert(result.Contains("tutorial preserves the locked session and its emergency stop"), "tutorial remains readable during hard mode and never blocks emergency stop or unlocks the session");
                assert(result.Contains("Allowed-content idle 5+10 seconds"), "allowed-content inactivity issues a bounded V4 request after five idle seconds plus ten distraction seconds, then clears on new input");
            }
            finally
            {
                if (!process.HasExited) process.Kill();
                notificationStop.Cancel(); await notificationTask;
                if (preserve == null && Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        finally { keepIntent.Cancel(); await renew; keepIntent.Dispose(); await client.DisconnectAsync(); }
    }

    private static async Task Until(Func<bool> predicate, int milliseconds = 3500)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < milliseconds) { if (predicate()) return; await Task.Delay(20); }
        throw new Exception("DG-LAB protocol test timed out");
    }

    private sealed class FakeApp : IAsyncDisposable
    {
        private WebApplication? server;
        private WebSocket? socket;
        private readonly SemaphoreSlim sendGate = new(1);
        private int leaseId;
        public Uri Address { get; private set; } = null!;
        public volatile bool IgnorePings, OperationError, DelayReset, FailNextPositive;
        public volatile int Level, ResetCount, PulseCount;
        public bool ValidPulseFrames = true;
        public ConcurrentQueue<(int Level, int Duration, int Channel, bool Immediate)> Positive { get; } = new();
        private static JsonObject Device() => (JsonObject)JsonNode.Parse("""
            {"slotId":"slot","name":"Protocol test device","type":"COYOTE_030",
             "props":{"connectState":"connected","channelAStatus":0,"channelBStatus":0,"intensityA":0,"intensityB":0},
             "slotState":{"hasDevice":true,"channelA":{"isMuted":false,"intensityMax":40,"comfortLimit":{"overheat":false}},"channelB":{"isMuted":false,"intensityMax":40}}}
            """)!;

        public async Task Start()
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            server = builder.Build(); server.UseWebSockets();
            server.Run(Serve);
            await server.StartAsync();
            var url = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Address = new Uri(url.Replace("http://", "ws://"));
        }
        private async Task Write(JsonObject frame)
        {
            await sendGate.WaitAsync();
            try { if (socket?.State == WebSocketState.Open) await socket.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch (WebSocketException) { }
            finally { sendGate.Release(); }
        }
        private async Task Respond(string id, JsonNode? result, string? error = null)
            => await Write(new() { ["type"] = "message", ["clientId"] = "app", ["data"] = new JsonObject { ["t"] = "resp", ["reqId"] = id, ["result"] = result, ["error"] = error } });
        public Task Patch(JsonObject patch) => Write(new() { ["type"] = "message", ["clientId"] = "app", ["data"] = new JsonObject { ["t"] = "ev", ["ev"] = "slots.patch", ["slots"] = new JsonArray(patch) } });
        private async Task Serve(Microsoft.AspNetCore.Http.HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest) return;
            using var ws = await context.WebSockets.AcceptWebSocketAsync(); socket = ws;
            await Write(new() { ["type"] = "hello", ["clientId"] = "controller" });
            await Write(new() { ["type"] = "client_attached", ["clientId"] = "app" });
            await Write(new() { ["type"] = "message", ["clientId"] = "app", ["data"] = new JsonObject { ["t"] = "ev", ["ev"] = "devices.snapshot", ["devices"] = new JsonArray(Device()) } });
            var bytes = new byte[65536];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var length = 0; WebSocketReceiveResult part;
                    do { part = await ws.ReceiveAsync(new ArraySegment<byte>(bytes, length, bytes.Length - length), context.RequestAborted); length += part.Count; } while (!part.EndOfMessage);
                    if (part.MessageType != WebSocketMessageType.Text) break;
                    var frame = (JsonObject)JsonNode.Parse(bytes.AsSpan(0, length))!;
                    var data = (JsonObject)frame["data"]!; var id = data["reqId"]!.GetValue<string>();
                    var method = data["m"]!.GetValue<string>();
                    if (method == "devices.get") await Respond(id, new JsonObject { ["devices"] = new JsonArray(Device()) });
                    else if (method == "ping") { if (!IgnorePings) await Respond(id, JsonValue.Create(1)); }
                    else if (OperationError) await Respond(id, null, "invalid_operate");
                    else if (method == "device.op.clear") { Level = 0; Interlocked.Increment(ref leaseId); await Respond(id, new JsonObject()); }
                    else if (method == "device.op")
                    {
                        var op = (JsonObject)data["data"]!; var type = op["t"]!.GetValue<int>();
                        if (type == 7)
                        {
                            if (DelayReset) await Task.Delay(150);
                            ResetCount++; Level = 0; await Respond(id, new JsonObject { ["reason"] = "completed" });
                        }
                        else
                        {
                            var duration = op["d"]!.GetValue<int>();
                            if (type == 4)
                            {
                                var level = op["v"]!.GetValue<int>(); Level = level;
                                Positive.Enqueue((level, duration, op["c"]!.GetValue<int>(), op["im"]!.GetValue<bool>()));
                                var lease = Interlocked.Increment(ref leaseId);
                                _ = Task.Run(async () => { await Task.Delay(duration); if (lease == Volatile.Read(ref leaseId)) Level = 0; });
                            }
                            if (type == 0)
                            {
                                PulseCount++;
                                ValidPulseFrames &= op["v"] is JsonArray frames && frames.Count == 10 && frames.All(x => x?.GetValue<string>().Length == 16) && duration == 1000;
                            }
                            var fail = type == 4 && FailNextPositive;
                            if (fail) FailNextPositive = false;
                            _ = Task.Run(async () => { await Task.Delay(duration); await Respond(id, fail ? null : new JsonObject { ["reason"] = "completed" }, fail ? "internal_error" : null); });
                        }
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        }
        public async Task CloseConnection()
        { if (socket != null) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test disconnect", CancellationToken.None); }
        public async ValueTask DisposeAsync()
        {
            try { socket?.Abort(); } catch (ObjectDisposedException) { }
            if (server != null) { await server.StopAsync(); await server.DisposeAsync(); }
        }
    }
}
