using System.Security.Cryptography;
using System.Text.Json;
using DglabFocus.Core;

namespace DglabFocus.Windows;
public sealed class AppSettings
{
    public SessionOptions Options { get; set; } = new();
    public List<AllowRule> Rules { get; set; } = [];
    public bool Sound { get; set; }
    public bool ReducedMotion { get; set; }
    public string BridgeToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public SessionSnapshot? Session { get; set; }
    public List<SessionEvent> History { get; set; } = [];
}
public sealed class Storage(bool smoke)
{
    public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DglabFocus");
    public string? LoadError { get; private set; }
    public AppSettings Load()
    {
        if (smoke) return new();
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new JsonException();
            settings.Options.Validate();
            if (settings.BridgeToken.Length != 48) throw new JsonException("Invalid bridge token");
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException)
        {
            LoadError = "配置无法读取，原文件已保留。请检查本地配置后重启，以免丢失上次会话。";
            return new();
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
