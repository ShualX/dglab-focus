using System.Text.Json;
using DglabFocus.Windows;

internal static class BrowserSetupChecks
{
    public static void Run(Action<bool, string> assert)
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source != null && !File.Exists(Path.Combine(source.FullName, "extension", "manifest.json"))) source = source.Parent;
        if (source == null) throw new Exception("Extension source not found");
        var original = Path.Combine(source.FullName, "extension");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DglabFocus-browser-setup-" + Guid.NewGuid().ToString("N")));
        var token = new string('A', 48); var replacement = new string('B', 48);
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "source"); Directory.CreateDirectory(input);
            foreach (var name in BrowserExtensionSetup.Files) File.Copy(Path.Combine(original, name), Path.Combine(input, name));
            var setup = new BrowserExtensionSetup(Path.Combine(root, "data"));
            var result = setup.Prepare(input, token);
            assert(BrowserExtensionSetup.Files.All(name => File.Exists(Path.Combine(result, name))), "browser setup prepares every required module in a stable per-user directory");
            string ReadKey() => JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(result, "install-config.js")).Split('=', 2)[1].Trim().TrimEnd(';'))!;
            assert(ReadKey() == token && !File.ReadAllText(Path.Combine(input, "install-config.js")).Contains(token), "automatic pairing key is written only into the private installation copy");
            using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(result, "manifest.json"))))
                assert(manifest.RootElement.GetProperty("manifest_version").GetInt32() == 3 && !manifest.RootElement.TryGetProperty("host_permissions", out _),
                    "prepared extension retains its Manifest V3 permissions without expanding site access");
            assert(setup.Prepare(input, replacement) == result && ReadKey() == replacement, "reconfiguration refreshes the pairing key without changing the installed extension path");
            assert(!Directory.EnumerateFiles(result, "*.tmp-*").Any(), "completed extension setup leaves no temporary key files");
            var invalid = false; try { setup.Prepare(input, "not a key"); } catch (ArgumentException) { invalid = true; }
            assert(invalid && ReadKey() == replacement, "invalid keys cannot replace a working installation");
            var foreign = new BrowserExtensionSetup(Path.Combine(root, "foreign")); Directory.CreateDirectory(foreign.DirectoryPath);
            File.WriteAllText(Path.Combine(foreign.DirectoryPath, "unrelated.txt"), "preserve");
            invalid = false; try { foreign.Prepare(input, token); } catch (IOException) { invalid = true; }
            assert(invalid && File.ReadAllText(Path.Combine(foreign.DirectoryPath, "unrelated.txt")) == "preserve", "browser setup refuses a non-owned directory and preserves its files");
            File.Delete(Path.Combine(input, "pairing.js"));
            var missing = new BrowserExtensionSetup(Path.Combine(root, "missing"));
            invalid = false; try { missing.Prepare(input, token); } catch (IOException) { invalid = true; }
            var missingRejected = invalid && !Directory.Exists(missing.DirectoryPath);
            File.Copy(Path.Combine(original, "pairing.js"), Path.Combine(input, "pairing.js"));
            File.WriteAllText(Path.Combine(input, "manifest.json"), "{\"name\":\"broken manifest\"}");
            invalid = false; try { missing.Prepare(input, token); } catch (IOException) { invalid = true; }
            assert(missingRejected && invalid && !Directory.Exists(missing.DirectoryPath), "incomplete or malformed extension sources cannot produce an installation reported as ready");
            var chrome = BrowserExtensionSetup.BrowserStartInfo("Chrome"); var edge = BrowserExtensionSetup.BrowserStartInfo("Edge");
            assert(chrome.UseShellExecute && chrome.FileName == "chrome.exe" && chrome.Arguments == "chrome://extensions/" &&
                edge.FileName == "msedge.exe" && edge.Arguments == "edge://extensions/", "setup launch intents open only the normal browser management page, with no installation bypass flags");
            invalid = false; try { BrowserExtensionSetup.BrowserStartInfo("cmd.exe"); } catch (ArgumentException) { invalid = true; }
            assert(invalid, "browser setup cannot launch an arbitrary executable supplied as a browser name");
        }
        finally
        {
            var allowedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "DglabFocus-browser-setup-";
            if (!root.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture cleanup path");
            Directory.Delete(root, true);
        }
    }
}
