using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DglabFocus.Windows;

public sealed class BrowserExtensionSetup(string dataDirectory)
{
    private const string Marker = ".dglab-focus-extension";
    private const string Ownership = "DglabFocus browser extension v1";
    public static readonly IReadOnlyList<string> Files = Array.AsReadOnly(new[]
    { "background.js", "report.js", "pairing.js", "popup.js", "popup.html", "popup.css", "install-config.js", "manifest.json" });
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "BrowserExtension");

    public string Prepare(string sourceDirectory, string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 48 || !token.All(Uri.IsHexDigit)) throw new ArgumentException("本机配对码无效，无法准备扩展。");
        var source = Path.GetFullPath(sourceDirectory);
        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), DirectoryPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("请使用下载包中的原始扩展准备安装。");
        RefuseLinks(source); RefuseLinks(DirectoryPath);
        foreach (var name in Files)
        {
            var file = Path.Combine(source, name);
            if (!File.Exists(file)) throw new IOException("扩展文件不完整，请完整解压新版下载包。");
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("扩展来源不能包含链接文件。");
        }
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "manifest.json"))))
            if (manifest.RootElement.ValueKind != JsonValueKind.Object || !manifest.RootElement.TryGetProperty("manifest_version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 3) throw new IOException("扩展清单版本不正确。");
        if (Directory.Exists(DirectoryPath) && Directory.EnumerateFileSystemEntries(DirectoryPath).Any())
        {
            var marker = Path.Combine(DirectoryPath, Marker);
            if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(marker) != Ownership)
                throw new IOException("扩展目标目录含有其他文件，请先检查该目录，软件不会覆盖它。");
        }
        Directory.CreateDirectory(DirectoryPath);
        foreach (var name in Files.Append(Marker))
        {
            var target = Path.Combine(DirectoryPath, name);
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("扩展目标不能包含链接文件。");
        }
        // Mark ownership first. Manifest is committed last, after every required module is available.
        WriteAtomic(Path.Combine(DirectoryPath, Marker), Ownership);
        foreach (var name in Files)
        {
            var content = name == "install-config.js" ? "export const installationToken = " + JsonSerializer.Serialize(token) + ";\n"
                : File.ReadAllText(Path.Combine(source, name));
            WriteAtomic(Path.Combine(DirectoryPath, name), content);
        }
        return DirectoryPath;
    }
    private static void RefuseLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("扩展安装目录不能经过目录链接，请使用普通本机目录。");
    }
    private static void WriteAtomic(string target, string content)
    {
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ProcessStartInfo BrowserStartInfo(string browser)
    {
        if (browser is not ("Chrome" or "Edge")) throw new ArgumentException("请选择 Chrome 或 Edge。");
        var executable = browser == "Chrome" ? "chrome.exe" : "msedge.exe";
        return new ProcessStartInfo(executable, browser == "Chrome" ? "chrome://extensions/" : "edge://extensions/") { UseShellExecute = true };
    }
}
