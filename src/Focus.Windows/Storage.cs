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
    public string BridgeToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public SessionSnapshot? Session { get; set; }
    public List<SessionEvent> History { get; set; } = [];

    public void InitializePlans(bool migrateLegacy = false)
    {
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
        foreach (var plan in Plans) plan.Validate();
        if (Plans.Select(x => x.Id).Distinct().Count() != Plans.Count)
            throw new ArgumentException("方案标识重复。");
        if (!Plans.Any(x => x.Id == SelectedPlanId)) SelectedPlanId = null;
    }
}
public sealed class Storage(bool smoke, string? directoryPath = null)
{
    public string DirectoryPath { get; } = directoryPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DglabFocus");
    public string? LoadError { get; private set; }
    public AppSettings Load()
    {
        if (smoke) return new() { Plans = [] };
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new() { Plans = [] };
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new JsonException();
            settings.Options.Validate();
            settings.InitializePlans(migrateLegacy: true);
            if (settings.BridgeToken.Length != 48) throw new JsonException("Invalid bridge token");
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException)
        {
            LoadError = "配置无法读取，原文件已保留。请检查本地配置后重启，以免丢失上次会话。";
            return new() { Plans = [] };
        }
    }
    public void Save(AppSettings settings)
    {
        if (smoke || LoadError != null) return;
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "settings.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
