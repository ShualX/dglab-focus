using DglabFocus.Core;
using System.Text.Json;

var tests = new List<(string Name, Action Run)>();
var origin = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));
void Test(string name, Action run) => tests.Add((name, run));
void True(bool condition, string why = "assertion failed") { if (!condition) throw new Exception(why); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
Session New(StudyMode mode = StudyMode.Computer, SessionOptions? options = null)
{
    var session = new Session();
    session.Start(options ?? new() { FocusMinutes = 0, Mode = mode }, [new(RuleKind.Domain, "study.example.com")], origin);
    return session;
}
void Advance(Session session, int seconds, Attention attention = Attention.Allowed, int offset = 0, double idle = 0)
{ for (int i = 1; i <= seconds; i++) session.Tick(TimeSpan.FromSeconds(1), origin.AddSeconds(offset + i), attention, idle); }
AllowRule Domain(string host, bool children = false) => new(RuleKind.Domain, host, children);

Test("domain matches exact host and rejects lookalike/suffix attacks", () => {
    True(Rules.MatchesUrl(Domain("example.com"), "https://example.com/a"));
    foreach (var url in new[] { "https://example.com.evil.com", "https://notexample.com", "https://example.com@evil.com", "https://www.example.com" }) True(!Rules.MatchesUrl(Domain("example.com"), url), url);
});
Test("subdomain allowance respects dot boundaries", () => {
    True(Rules.MatchesUrl(Domain("example.com", true), "https://course.example.com/a"));
    True(!Rules.MatchesUrl(Domain("example.com", true), "https://evil-example.com"));
});
Test("normalizes IDN hosts", () => { True(Rules.TryHost("https://例子.测试/课", out var host)); True(host.StartsWith("xn--")); });
Test("rejects userinfo, wildcard and non-web schemes", () => {
    foreach (var value in new[] { "https://user:pass@example.com", "*.example.com", "javascript://example.com", "file:///C:/study", "localhost" }) True(!Rules.TryHost(value, out _), value);
});
Test("exact page keeps lesson query and fragment", () => {
    var rule = new AllowRule(RuleKind.Page, "https://learn.example.com/?lesson=1#topic");
    True(Rules.MatchesUrl(rule, rule.Value));
    True(!Rules.MatchesUrl(rule, "https://learn.example.com/?lesson=2#topic"));
    True(!Rules.MatchesUrl(rule, "https://learn.example.com/?lesson=1#other"));
});
Test("path prefixes respect host, port and segment boundary", () => {
    var rule = new AllowRule(RuleKind.PathPrefix, "https://example.com/course");
    True(Rules.MatchesUrl(rule, "https://example.com/course/1?x=2"));
    foreach (var url in new[] { "https://example.com/coursework", "https://evil.com/course/1", "https://example.com:8443/course/1" }) True(!Rules.MatchesUrl(rule, url));
});
Test("browser executable cannot bypass configured web rules", () => {
    AllowRule[] rules = [new(RuleKind.Application, "chrome.exe"), Domain("example.com")];
    Equal(Attention.Outside, Rules.Evaluate(rules, "chrome.exe", "https://evil.com", true, true));
    Equal(Attention.Unknown, Rules.Evaluate(rules, "chrome.exe", null, true, false));
    Equal(Attention.Allowed, Rules.Evaluate(rules, "chrome.exe", "https://example.com", true, true));
});
Test("unsupported browser pages are unknown", () => Equal(Attention.Unknown, Rules.Evaluate([Domain("example.com")], "chrome.exe", "chrome://settings", true, true)));
Test("app path comparison is exact and case insensitive", () => {
    AllowRule[] rules = [new(RuleKind.Application, @"C:\Study\reader.exe")];
    Equal(Attention.Allowed, Rules.Evaluate(rules, @"c:\study\READER.EXE", null, false, false));
    Equal(Attention.Outside, Rules.Evaluate(rules, @"C:\Other\reader.exe", null, false, false));
});
Test("grace period does not charge time retroactively", () => {
    var s = New(); Advance(s, 10, Attention.Outside); Equal(0d, s.DebtSeconds); Equal(0, s.RecentCount(origin.AddSeconds(10)));
    Advance(s, 1, Attention.Outside, 10); Equal(1d, s.DebtSeconds); Equal(1, s.RecentCount(origin.AddSeconds(11)));
});
Test("continuous distraction counts once and emits one feedback", () => {
    var s = New(); var count = 0; s.Changed += e => { if (e.Kind == "feedback") count++; };
    Advance(s, 180, Attention.Outside); Equal(170d, s.DebtSeconds); Equal(1, s.RecentCount(origin.AddMinutes(3))); Equal(1, count);
});
Test("brief allowed-window flashes cannot multiply episodes", () => {
    var s = New(); Advance(s, 12, Attention.Outside); Advance(s, 1, Attention.Allowed, 12); Advance(s, 12, Attention.Outside, 13);
    Equal(1, s.RecentCount(origin.AddSeconds(25)));
});
Test("stable return separates two episodes", () => {
    var s = New(); Advance(s, 12, Attention.Outside); Advance(s, 5, Attention.Allowed, 12); Advance(s, 12, Attention.Outside, 17);
    Equal(2, s.RecentCount(origin.AddSeconds(29)));
});
Test("recovery requires continuous focus and uses configured rate", () => {
    var s = New(); Advance(s, 70, Attention.Outside); Equal(60d, s.DebtSeconds);
    Advance(s, 120, Attention.Allowed, 70); Equal(60d, s.DebtSeconds);
    Advance(s, 20, Attention.Allowed, 190); Equal(50d, s.DebtSeconds);
    Advance(s, 300, Attention.Allowed, 210); Equal(0d, s.DebtSeconds);
});
Test("unknown and own-panel states neither accumulate nor recover", () => {
    var s = New(); Advance(s, 70, Attention.Outside); Advance(s, 300, Attention.Unknown, 70); Advance(s, 300, Attention.Neutral, 370);
    Equal(60d, s.DebtSeconds);
});
Test("inactivity prompts and freezes scoring without electrical action", () => {
    var s = New(); Advance(s, 181, Attention.Allowed, idle: 181); True(s.NeedsCheckIn); Equal(0d, s.DebtSeconds);
    s.CheckIn(origin.AddSeconds(181)); True(!s.NeedsCheckIn); Advance(s, 30, Attention.Allowed, 181, 500); True(!s.NeedsCheckIn);
});
Test("reading mode avoids short inactivity false positives", () => {
    var s = New(StudyMode.Reading); Advance(s, 300, Attention.Allowed, idle: 999); True(!s.NeedsCheckIn);
    Advance(s, 600, Attention.Allowed, 300, 999); True(s.NeedsCheckIn);
});
Test("paper mode accepts no rule and only prompts for check-in", () => {
    var s = new Session(); s.Start(new() { Mode = StudyMode.Paper, FocusMinutes = 0, CheckInMinutes = 1 }, [], origin);
    Advance(s, 100, Attention.Outside); Equal(0d, s.DebtSeconds); Equal(0, s.RecentCount(origin.AddSeconds(100))); True(s.NeedsCheckIn);
});
Test("each session permits exactly one temporary domain", () => {
    var s = New(); True(s.AddTemporaryDomain("https://docs.example.com/a", origin, out _));
    True(!s.AddTemporaryDomain("second.example.com", origin, out _)); Equal("docs.example.com", s.TemporaryHost);
});
Test("existing domain does not consume temporary allowance", () => {
    var s = New(); True(!s.AddTemporaryDomain("study.example.com", origin, out _)); Equal<string?>(null, s.TemporaryHost);
    True(s.AddTemporaryDomain("other.example.com", origin, out _));
});
Test("break and cancelled exit preserve temporary allowance", () => {
    var s = New(); s.AddTemporaryDomain("docs.example.com", origin, out _); s.TakeBreak(origin); s.EndBreak(origin);
    s.RequestExit(origin); s.CancelExit(origin);
    True(!s.AddTemporaryDomain("other.example.com", origin, out _));
});
Test("exit waits five minutes and keeps scoring while waiting", () => {
    var s = New(); s.RequestExit(origin); Advance(s, 299, Attention.Outside); Equal(SessionState.ExitPending, s.State); True(s.DebtSeconds > 0);
    Advance(s, 1, Attention.Allowed, 299); Equal(SessionState.Completed, s.State);
});
Test("exit cancellation returns to same focus session", () => {
    var s = New(); s.RequestExit(origin); Advance(s, 100); s.CancelExit(origin.AddSeconds(100));
    Equal(SessionState.Focusing, s.State); Equal(100d, s.FocusSeconds); Equal(0d, s.ExitRemaining);
});
Test("immediate pause works during exit countdown", () => {
    var s = New(); s.RequestExit(origin); s.Suspend(origin, "pause"); Advance(s, 300, Attention.Outside);
    Equal(SessionState.Suspended, s.State); Equal(300d, s.ExitRemaining); Equal(0d, s.DebtSeconds);
});
Test("dispatcher stall pauses instead of issuing catch-up feedback", () => {
    var s = New(); s.Tick(TimeSpan.FromMinutes(20), origin.AddMinutes(20), Attention.Outside, 0);
    Equal(SessionState.Suspended, s.State); Equal(0d, s.DebtSeconds); Equal(0d, s.FocusSeconds);
});
Test("restart retains quota, incidents and pending-exit remaining time", () => {
    var s = New(); s.AddTemporaryDomain("docs.example.com", origin, out _); Advance(s, 20, Attention.Outside); s.RequestExit(origin.AddSeconds(20)); Advance(s, 20, Attention.Allowed, 20);
    var restored = Session.Restore(s.Snapshot()); Equal(SessionState.Suspended, restored.State);
    True(!restored.AddTemporaryDomain("other.example.com", origin, out _)); restored.Resume(origin.AddSeconds(40));
    Equal(SessionState.ExitPending, restored.State); Equal(280d, restored.ExitRemaining); Equal(1, restored.RecentCount(origin.AddSeconds(40)));
});
Test("hour window expires exactly at boundary", () => {
    var s = New(); Advance(s, 11, Attention.Outside);
    Equal(1, s.RecentCount(origin.AddSeconds(3610))); Equal(0, s.RecentCount(origin.AddSeconds(3611)));
});
Test("fixed focus and rest complete a single session", () => {
    var s = New(options: new() { FocusMinutes = 1, RestMinutes = 1 }); Advance(s, 60); Equal(SessionState.Resting, s.State);
    Advance(s, 60, offset: 60); Equal(SessionState.Completed, s.State);
});
Test("repeat rounds share temporary quota", () => {
    var s = New(options: new() { FocusMinutes = 1, RestMinutes = 1, Repeat = true }); s.AddTemporaryDomain("docs.example.com", origin, out _);
    Advance(s, 120); Equal(SessionState.Focusing, s.State); True(!s.AddTemporaryDomain("other.example.com", origin, out _));
});
Test("new session resets temporary rule but retains rolling incidents", () => {
    var s = New(); s.AddTemporaryDomain("docs.example.com", origin, out _); Advance(s, 11, Attention.Outside); s.RequestExit(origin.AddSeconds(11)); Advance(s, 300, offset: 11);
    s.Start(new() { FocusMinutes = 0 }, [Domain("study.example.com")], origin.AddSeconds(311));
    Equal<string?>(null, s.TemporaryHost); True(!s.Rules.Any(x => x.Value == "docs.example.com")); Equal(1, s.RecentCount(origin.AddSeconds(311)));
});
Test("rejects non-finite options", () => { try { new SessionOptions { FocusMinutes = double.NaN }.Validate(); throw new Exception("accepted NaN"); } catch (ArgumentException) { } });
Test("non-paper session requires an allow rule", () => { try { new Session().Start(new(), [], origin); throw new Exception("started without rules"); } catch (ArgumentException) { } });

Test("software blacklist overrides an identical whitelist rule in both modes", () => {
    AllowRule[] rules = [new(RuleKind.Application, @"C:\Study\reader.exe"), new(RuleKind.Application, @"C:\Study\reader.exe", false, RuleAction.Block)];
    foreach (var mode in Enum.GetValues<RuleMode>()) Equal(Attention.Outside, Rules.Evaluate(rules, @"C:\Study\reader.exe", null, false, false, mode));
});
Test("software blacklist mode allows unlisted apps and rejects only blocked paths", () => {
    AllowRule[] rules = [new(RuleKind.Application, @"C:\Games\game.exe", false, RuleAction.Block)];
    Equal(Attention.Allowed, Rules.Evaluate(rules, @"C:\Study\reader.exe", null, false, false, RuleMode.Blacklist));
    Equal(Attention.Outside, Rules.Evaluate(rules, @"C:\Games\game.exe", null, false, false, RuleMode.Blacklist));
    Equal(Attention.Unknown, Rules.Evaluate(rules, null, null, false, false, RuleMode.Blacklist));
});
Test("software whitelist mode rejects unlisted apps even with web blacklist mode", () =>
    Equal(Attention.Outside, Rules.Evaluate([], "game.exe", null, false, false, RuleMode.Whitelist, RuleMode.Blacklist)));
Test("website blacklist takes priority over domain and page whitelist entries", () => {
    AllowRule[] rules = [Domain("example.com", true), new(RuleKind.Page, "https://games.example.com/play"), new(RuleKind.Domain, "games.example.com", false, RuleAction.Block)];
    foreach (var mode in Enum.GetValues<RuleMode>()) Equal(Attention.Outside, Rules.Evaluate(rules, "msedge.exe", "https://games.example.com/play", true, true, RuleMode.Whitelist, mode));
});
Test("blocked page and path do not block unrelated pages on an allowed domain", () => {
    AllowRule[] rules = [Domain("example.com"), new(RuleKind.Page, "https://example.com/game?id=1", false, RuleAction.Block), new(RuleKind.PathPrefix, "https://example.com/social", false, RuleAction.Block)];
    foreach (var url in new[] { "https://example.com/game?id=1", "https://example.com/social/feed" }) Equal(Attention.Outside, Rules.Evaluate(rules, "chrome.exe", url, true, true));
    foreach (var url in new[] { "https://example.com/game?id=2", "https://example.com/social-science/lesson" }) Equal(Attention.Allowed, Rules.Evaluate(rules, "chrome.exe", url, true, true));
});
Test("website blacklist mode allows unmatched domains without a browser app whitelist", () => {
    AllowRule[] rules = [new(RuleKind.Domain, "games.example.com", true, RuleAction.Block)];
    Equal(Attention.Allowed, Rules.Evaluate(rules, "chrome.exe", "https://reference.example.com", true, true, RuleMode.Whitelist, RuleMode.Blacklist));
    Equal(Attention.Outside, Rules.Evaluate(rules, "chrome.exe", "https://sub.games.example.com", true, true, RuleMode.Blacklist, RuleMode.Blacklist));
});
Test("website blacklist mode abstains for stale and unsupported browser reports", () => {
    Equal(Attention.Unknown, Rules.Evaluate([], "chrome.exe", "https://example.com", true, false, RuleMode.Blacklist, RuleMode.Blacklist));
    Equal(Attention.Unknown, Rules.Evaluate([], "chrome.exe", "chrome://settings", true, true, RuleMode.Blacklist, RuleMode.Blacklist));
});
Test("blocked browser executable wins even when its current URL is allowed", () => {
    AllowRule[] rules = [new(RuleKind.Application, "chrome.exe", false, RuleAction.Block), Domain("study.example.com")];
    Equal(Attention.Outside, Rules.Evaluate(rules, "chrome.exe", "https://study.example.com", true, true));
    Equal(Attention.Outside, Rules.Evaluate(rules, "chrome.exe", null, true, false));
});
Test("website whitelist remains effective when software mode is blacklist", () => {
    Equal(Attention.Outside, Rules.Evaluate([Domain("study.example.com")], "chrome.exe", "https://games.example.com", true, true, RuleMode.Blacklist));
});
Test("blacklist-only sessions can start with empty lists", () => {
    var s = new Session(); s.Start(new() { ApplicationMode = RuleMode.Blacklist, WebsiteMode = RuleMode.Blacklist }, [], origin);
    Equal(SessionState.Focusing, s.State);
});
Test("blocked temporary domain is rejected without consuming quota", () => {
    var s = new Session(); s.Start(new() { FocusMinutes = 0 }, [Domain("study.example.com"), new(RuleKind.Domain, "games.example.com", true, RuleAction.Block)], origin);
    True(!s.AddTemporaryDomain("sub.games.example.com", origin, out _)); Equal<string?>(null, s.TemporaryHost);
    True(s.AddTemporaryDomain("docs.example.com", origin, out _));
});
Test("temporary whitelist does not override a more specific blocked page", () => {
    var s = new Session(); s.Start(new(), [Domain("study.example.com"), new(RuleKind.PathPrefix, "https://docs.example.com/games", false, RuleAction.Block)], origin);
    True(s.AddTemporaryDomain("docs.example.com", origin, out _));
    Equal(Attention.Allowed, Rules.Evaluate(s.Rules, "chrome.exe", "https://docs.example.com/lesson", true, true));
    Equal(Attention.Outside, Rules.Evaluate(s.Rules, "chrome.exe", "https://docs.example.com/games/play", true, true));
});
Test("website blacklist mode never consumes temporary allowance", () => {
    var s = New(options: new() { WebsiteMode = RuleMode.Blacklist });
    True(!s.AddTemporaryDomain("docs.example.com", origin, out _)); Equal<string?>(null, s.TemporaryHost);
});
Test("old saved JSON migrates to whitelist mode and disabled strength preview", () => {
    var saved = JsonSerializer.Deserialize<SessionSnapshot>("{\"Options\":{\"FocusMinutes\":0},\"State\":1,\"Rules\":[{\"Kind\":1,\"Value\":\"study.example.com\"}]}")!;
    var restored = Session.Restore(saved);
    Equal(RuleMode.Whitelist, restored.Options.ApplicationMode); Equal(RuleMode.Whitelist, restored.Options.WebsiteMode);
    Equal(RuleAction.Allow, restored.Rules[0].Action); True(!restored.Options.StrengthPreview.Enabled); Equal(0d, restored.PreviewStrength);
});
Test("mixed blacklist policy and rules survive a restart", () => {
    var s = new Session(); s.Start(new() { ApplicationMode = RuleMode.Blacklist, WebsiteMode = RuleMode.Blacklist }, [new(RuleKind.Application, "game.exe", false, RuleAction.Block)], origin);
    var restored = Session.Restore(JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(s.Snapshot()))!);
    Equal(RuleMode.Blacklist, restored.Options.ApplicationMode); Equal(RuleMode.Blacklist, restored.Options.WebsiteMode);
    Equal(RuleAction.Block, restored.Rules.Single().Action); Equal(SessionState.Suspended, restored.State);
});
Test("strength preview starts at threshold, increments by time and respects ceiling", () => {
    var p = new StrengthPreviewOptions { Enabled = true, Initial = 10, Maximum = 25, Increment = 5, IntervalSeconds = 30 };
    Equal(0d, p.At(59, 60)); Equal(10d, p.At(60, 60)); Equal(10d, p.At(89, 60)); Equal(15d, p.At(90, 60));
    Equal(25d, p.At(150, 60)); Equal(25d, p.At(100000, 60));
    Equal(10d, p.At(60, 60)); // A reduced cumulative time naturally selects the lower preview level.
});
Test("disabled and zero-ceiling previews remain zero", () => {
    Equal(0d, new StrengthPreviewOptions { Initial = 10, Maximum = 50 }.At(9999, 60));
    Equal(0d, new StrengthPreviewOptions { Enabled = true }.At(9999, 60));
});
Test("strength profile rejects invalid ranges and non-finite numbers", () => {
    foreach (var p in new StrengthPreviewOptions[] {
        new() { Initial = -1 }, new() { Initial = 20, Maximum = 10 }, new() { Maximum = 101 },
        new() { Increment = 0 }, new() { IntervalSeconds = 0 }, new() { IntervalSeconds = double.NaN }, new() { Maximum = double.PositiveInfinity }
    }) { var rejected = false; try { p.Validate(); } catch (ArgumentException) { rejected = true; } True(rejected); }
});
Test("live preview clears immediately on learning, neutral and unknown states", () => {
    foreach (var state in new[] { Attention.Allowed, Attention.Neutral, Attention.Unknown }) {
        var s = New(options: new() { FocusMinutes = 0, StrengthPreview = new() { Enabled = true, Initial = 10, Maximum = 25, Increment = 5, IntervalSeconds = 30 } });
        Advance(s, 110, Attention.Outside); Equal(15d, s.PreviewStrength);
        Advance(s, 1, state, 110); Equal(0d, s.PreviewStrength); True(s.DebtSeconds > 0);
    }
});
Test("new distraction grace does not reuse an earlier preview level", () => {
    var s = New(options: new() { FocusMinutes = 0, StrengthPreview = new() { Enabled = true, Initial = 10, Maximum = 25 } });
    Advance(s, 80, Attention.Outside); True(s.PreviewStrength > 0);
    Advance(s, 5, Attention.Allowed, 80); Advance(s, 10, Attention.Outside, 85); Equal(0d, s.PreviewStrength);
    Advance(s, 1, Attention.Outside, 95); True(s.PreviewStrength > 0);
});
Test("pause, restart, rest and check-in never retain active preview strength", () => {
    var s = New(options: new() { FocusMinutes = 0, IdleSeconds = 180, StrengthPreview = new() { Enabled = true, Initial = 10, Maximum = 25 } });
    Advance(s, 80, Attention.Outside); True(s.PreviewStrength > 0);
    Equal(0d, Session.Restore(s.Snapshot()).PreviewStrength);
    s.Suspend(origin.AddSeconds(80), "pause"); Equal(0d, s.PreviewStrength); s.Resume(origin.AddSeconds(80)); Equal(0d, s.PreviewStrength);
    Advance(s, 20, Attention.Outside, 80); True(s.PreviewStrength > 0); s.TakeBreak(origin.AddSeconds(100)); Equal(0d, s.PreviewStrength);
    s.EndBreak(origin.AddSeconds(100)); Advance(s, 181, Attention.Outside, 100, 999); True(s.NeedsCheckIn); Equal(0d, s.PreviewStrength);
});
Test("invalid list modes fail validation", () => {
    var rejected = false; try { new SessionOptions { ApplicationMode = (RuleMode)99 }.Validate(); } catch (ArgumentException) { rejected = true; } True(rejected);
});

var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed");
return failed == 0 ? 0 : 1;
