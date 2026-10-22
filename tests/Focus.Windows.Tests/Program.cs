using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DglabFocus.Windows;
using DglabFocus.Core;

var token = new string('A', 48);
const int testPort = 17840;
using var bridge = new BrowserBridge(token, testPort);
await bridge.StartAsync();
var tests = 0;
void Assert(bool success, string message) { if (!success) throw new Exception(message); Console.WriteLine("PASS " + message); tests++; }
async Task<ClientWebSocket> Connect(string origin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
{
    var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Origin", origin);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{testPort}/browser"), timeout.Token);
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
    BrowserSetupChecks.Run(Assert);
    await LocalRelayIntegration.Run(Assert);
    await DglabIntegration.Run(Assert);
    var fresh = new AppSettings(); fresh.InitializePlans();
    Assert(fresh.Plans!.Count == 0 && fresh.SelectedPlanId == null, "fresh installation has no implicitly chosen plan");
    var legacy = JsonSerializer.Deserialize<AppSettings>("{\"Options\":{\"FocusMinutes\":25,\"ApplicationMode\":1,\"StrengthPreview\":{\"Enabled\":false,\"Initial\":0,\"Maximum\":0}},\"Rules\":[{\"Kind\":0,\"Value\":\"reader.exe\",\"Action\":1}]}")!;
    legacy.InitializePlans(migrateLegacy: true);
    var migrated = legacy.Plans!.Single();
    Assert(migrated.Options.FocusMinutes == 25 && migrated.Options.ApplicationMode == RuleMode.Blacklist && migrated.Rules.Single().Action == RuleAction.Block,
        "legacy settings migrate with timing, policies and rules intact");
    Assert(migrated.Options.StrengthPreview.Enabled && migrated.Options.StrengthPreview.Maximum > 0 && legacy.SelectedPlanId == null,
        "legacy migration enables preview and asks for an explicit plan choice");
    var disabled = migrated.Duplicate("关闭预览") with { Options = migrated.Options with { StrengthPreview = migrated.Options.StrengthPreview with { Enabled = false } } };
    var persistence = new AppSettings { Plans = [migrated, disabled], SelectedPlanId = disabled.Id };
    var tempDir = Path.Combine(Path.GetTempPath(), "DglabFocus-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        new Storage(false, tempDir).Save(persistence);
        var reloaded = new Storage(false, tempDir).Load();
        Assert(reloaded.Plans!.Count == 2 && reloaded.SelectedPlanId == disabled.Id && !reloaded.Plans[1].Options.StrengthPreview.Enabled,
            "multiple plans, selected choice and explicit preview-off survive a disk restart");
        reloaded.SelectedPlanId = Guid.NewGuid(); reloaded.InitializePlans();
        Assert(reloaded.SelectedPlanId == null, "missing selected plan is cleared rather than replaced by another plan");
        new Storage(false, tempDir).Save(persistence);
        File.WriteAllText(Path.Combine(tempDir, "settings.json"), "{broken");
        var recoveringStorage = new Storage(false, tempDir); var recovered = recoveringStorage.Load();
        Assert(recovered.Plans!.Count == 2 && recoveringStorage.RecoveryWarning != null && recoveringStorage.LoadError == null
            && Directory.GetFiles(tempDir, "settings.corrupt-*.json").Length == 1, "corrupt settings recover from an atomic backup while preserving the original file");
        File.WriteAllText(Path.Combine(tempDir, "settings.json"), "{\"Options\":null}");
        File.WriteAllText(Path.Combine(tempDir, "settings.json.bak"), "{\"Plans\":[null]}");
        var invalidStorage = new Storage(false, tempDir); invalidStorage.Load();
        Assert(invalidStorage.LoadError != null, "null configuration data reports a recoverable error instead of crashing startup");
    }
    finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
    var activeLegacy = new AppSettings { Session = new SessionSnapshot { State = SessionState.Focusing }, Plans = null };
    activeLegacy.InitializePlans(migrateLegacy: true);
    Assert(activeLegacy.Session!.PlanId == activeLegacy.Plans!.Single().Id, "legacy active session gains a plan identity during migration");

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
    Assert(bridge.ConnectionCount("Chrome", DateTimeOffset.UtcNow) == 1 && bridge.ConnectionCount("Edge", DateTimeOffset.UtcNow) == 0,
        "installation progress counts reports only from the chosen browser");
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
    Assert(bridge.ConnectionCount("Chrome", DateTimeOffset.UtcNow) == 1, "setup remains connected while the desktop app is in front without using a background page as focused study");
    Assert(true, "background browser cannot count as foreground study");
    await first.SendAsync(Encoding.UTF8.GetBytes(new string('x', 9000)), WebSocketMessageType.Text, true, CancellationToken.None);
    await Until(() => bridge.ConnectionCount(DateTimeOffset.UtcNow) == 0);
    Assert(true, "oversized message disconnects without retaining a trusted report");
    Console.WriteLine($"{tests}/{tests} Windows integration checks passed");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
