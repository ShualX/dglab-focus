using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DglabFocus.Windows;

var token = new string('A', 48);
using var bridge = new BrowserBridge(token);
bridge.Start();
var tests = 0;
void Assert(bool success, string message) { if (!success) throw new Exception(message); Console.WriteLine("PASS " + message); tests++; }
async Task<ClientWebSocket> Connect(string origin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
{
    var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Origin", origin);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{BrowserBridge.Port}/browser"), timeout.Token);
    return ws;
}
async Task Send(ClientWebSocket ws, object data) => await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)), WebSocketMessageType.Text, true, CancellationToken.None);
async Task<string?> Receive(ClientWebSocket ws)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var buffer = new byte[8192]; var result = await ws.ReceiveAsync(buffer, timeout.Token);
    return result.MessageType == WebSocketMessageType.Close ? null : Encoding.UTF8.GetString(buffer, 0, result.Count);
}
async Task Until(Func<bool> condition)
{
    for (int i = 0; i < 50; i++) { if (condition()) return; await Task.Delay(20); }
    throw new Exception("condition timed out");
}
try
{
    var rejected = false;
    try { using var forbidden = await Connect("https://evil.example.com"); } catch (WebSocketException) { rejected = true; }
    Assert(rejected, "rejects ordinary web-page origins");
    using (var invalid = await Connect())
    {
        await Send(invalid, new { type = "auth", token = "wrong", browser = "Chrome" });
        Assert(await Receive(invalid) == null, "rejects invalid pairing token");
    }
    using var first = await Connect();
    await Send(first, new { type = "auth", token, browser = "Chrome" });
    Assert((await Receive(first))?.Contains("ready") == true, "accepts extension origin and correct token");
    await Send(first, new { type = "report", url = "https://study.example.com/lesson?id=2", focused = true, ambiguous = false });
    await Until(() => bridge.Get("Chrome", DateTimeOffset.UtcNow) != null);
    Assert(bridge.Get("Chrome", DateTimeOffset.UtcNow)?.Url.EndsWith("id=2") == true, "receives current URL for in-memory rule matching");
    Assert(bridge.Get("Edge", DateTimeOffset.UtcNow) == null, "keeps different browsers separate");
    Assert(bridge.Get("Chrome", DateTimeOffset.UtcNow.AddSeconds(13)) == null, "stale extension report becomes unknown");
    using (var second = await Connect("chrome-extension://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"))
    {
        await Send(second, new { type = "auth", token, browser = "Chrome" }); await Receive(second);
        await Send(second, new { type = "report", url = "https://other.example.com", focused = true, ambiguous = false });
        await Until(() => bridge.ConnectionCount(DateTimeOffset.UtcNow) == 2);
        Assert(bridge.Get("Chrome", DateTimeOffset.UtcNow) == null, "multiple focused profiles abstain rather than guess");
    }
    await Until(() => bridge.ConnectionCount(DateTimeOffset.UtcNow) == 1);
    Assert(bridge.Get("Chrome", DateTimeOffset.UtcNow) != null, "disconnected profile no longer affects matching");
    await Send(first, new { type = "report", url = "https://study.example.com", focused = false, ambiguous = false });
    await Until(() => bridge.Get("Chrome", DateTimeOffset.UtcNow) == null);
    Assert(true, "background browser cannot count as foreground study");
    await first.SendAsync(Encoding.UTF8.GetBytes(new string('x', 9000)), WebSocketMessageType.Text, true, CancellationToken.None);
    await Until(() => bridge.ConnectionCount(DateTimeOffset.UtcNow) == 0);
    Assert(true, "oversized message disconnects without retaining a trusted report");
    Console.WriteLine($"{tests}/{tests} bridge integration checks passed");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
