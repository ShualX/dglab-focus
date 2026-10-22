using System.Security.Cryptography;
using System.Text.Json;
using DglabFocus.Core;

namespace DglabFocus.Windows;
public sealed class AppSettings
{
    // Options and Rules retain the last valid editor state for migration from v0.2.
    public SessionOptions Options { get; set; } = new();
    public List<AllowRule> Rules { get; set; } = [];
    public List<FocusPlan>? Plans { get; set; }
    public Guid? SelectedPlanId { get; set; }
    public bool Sound { get; set; }
    public bool ReducedMotion { get; set; }
    public TutorialProgress Tutorial { get; set; } = new();
    public int? LastMottoIndex { get; set; }
    public int InactivityRuleVersion { get; set; }
    public string BridgeToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public SessionSnapshot? Session { get; set; }
    public List<SessionEvent> History { get; set; } = [];

    public void InitializePlans(bool migrateLegacy = false)
    {
        Tutorial ??= new();
        Tutorial.Normalize();
        if (Options == null || Rules == null || Rules.Any(x => x == null) || History == null
            || BridgeToken == null || BridgeToken.Length != 48 || !BridgeToken.All(Uri.IsHexDigit))
            throw new ArgumentException("本机配置不完整。");
        Options.Validate();
        if (Plans == null)
        {
            Plans = [];
            if (migrateLegacy)
            {
                var preview = Options.StrengthPreview;
                // Earlier releases disabled preview by default. Turn it on once during migration.
                preview = preview.Maximum == 0 ? new() : preview with { Enabled = true };
                var legacy = new FocusPlan { Name = "原有专注方案", Options = Options with { StrengthPreview = preview }, Rules = [..Rules] };
                Plans.Add(legacy);
                SelectedPlanId = null; // Let the person choose before starting a new session.
                if (Session != null && Session.PlanId == null)
                    Session = Session with { PlanId = legacy.Id, PlanName = legacy.Name };
            }
        }
        if (InactivityRuleVersion < 1)
        {
            static SessionOptions UpgradeIdle(SessionOptions options) => options.IdleSeconds == 180 && options.IdlePolicy == IdlePolicy.ConfirmOnly
                ? options with { IdleSeconds = 5, IdlePolicy = IdlePolicy.CountAsDistraction } : options;
            Options = UpgradeIdle(Options);
            for (int i = 0; i < Plans.Count; i++)
                if (Plans[i]?.Options != null) Plans[i] = Plans[i] with { Options = UpgradeIdle(Plans[i].Options) };
            if (Session?.Options != null) Session = Session with { Options = UpgradeIdle(Session.Options) };
            InactivityRuleVersion = 1;
        }
        foreach (var plan in Plans) { if (plan == null) throw new ArgumentException("方案数据不完整。"); plan.Validate(); }
        if (Plans.Select(x => x.Id).Distinct().Count() != Plans.Count)
            throw new ArgumentException("方案标识重复。");
        if (!Plans.Any(x => x.Id == SelectedPlanId)) SelectedPlanId = null;
        if (Session != null)
        {
            if (Session.Options == null || Session.Rules == null || Session.Incidents == null
                || !Enum.IsDefined(Session.State) || !Enum.IsDefined(Session.ResumeState)) throw new ArgumentException("会话快照无效。");
            Session.Options.Validate();
        }
    }
}
public sealed class Storage(bool smoke, string? directoryPath = null)
{
    public string DirectoryPath { get; } = directoryPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DglabFocus");
    public string? LoadError { get; private set; }
    public string? RecoveryWarning { get; private set; }
    public AppSettings Load()
    {
        if (smoke) return new() { Plans = [] };
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new() { Plans = [] };
        try
        {
            return Read(path);
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            try
            {
                var recovered = Read(path + ".bak");
                if (File.Exists(path)) File.Copy(path, Path.Combine(DirectoryPath, "settings.corrupt-" + Guid.NewGuid().ToString("N") + ".json"));
                File.Copy(path + ".bak", path, true);
                RecoveryWarning = "本机配置读取失败，已从上一次备份恢复。原文件已另存保留，请核对方案后继续。";
                return recovered;
            }
            catch (Exception backupError) when (backupError is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { }
            LoadError = "配置无法读取，原文件已保留。请检查本地配置后重启，以免丢失上次会话。";
            return new() { Plans = [] };
        }
    }
    private static AppSettings Read(string path)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new JsonException();
        settings.InitializePlans(migrateLegacy: true);
        return settings;
    }
    public void Save(AppSettings settings)
    {
        if (smoke || LoadError != null) return;
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "settings.json");
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
        else File.Move(temp, path);
    }
}
