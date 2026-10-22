using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DglabFocus.Windows;

public sealed record ApplicationWindow(nint Handle, string Path, string Title)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}
public sealed record RunningApplication(string Name, string Path, string Windows, int WindowCount)
{
    public string Summary => WindowCount == 1 ? Windows : $"{WindowCount} 个窗口 · {Windows}";
    public bool Matches(string search) => Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Path.Contains(search, StringComparison.OrdinalIgnoreCase) || Windows.Contains(search, StringComparison.OrdinalIgnoreCase);
}

public static class RunningApplications
{
    private delegate bool EnumWindowProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int length);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MultitaskingViewFrame", "TaskSwitcherWnd", "#32768" };
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    { "SearchHost", "SearchApp", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "LockApp" };

    private static string? ProcessPath(uint pid)
    {
        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid) return null;
        var text = new StringBuilder(32768); var size = text.Capacity;
        return QueryFullProcessImageName(process, 0, text, ref size) ? text.ToString() : null;
    }
    // The same resolver is used by rule selection and live foreground matching.
    public static string? ExecutableForWindow(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var path = ProcessPath(pid);
        if (!string.Equals(System.IO.Path.GetFileName(path), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return path;
        string? childPath = null;
        EnumChildWindows(hwnd, (child, _) =>
        {
            var name = new StringBuilder(256); GetClassName(child, name, name.Capacity);
            GetWindowThreadProcessId(child, out var childPid);
            if (childPid != pid && name.ToString() == "Windows.UI.Core.CoreWindow") childPath = ProcessPath(childPid);
            return childPath == null;
        }, 0);
        return childPath; // Never create a rule that accidentally allows every hosted Store app.
    }
    public static ApplicationWindow? Inspect(nint hwnd)
    {
        if (hwnd == 0 || !IsWindowVisible(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return null;
        var className = new StringBuilder(256); GetClassName(hwnd, className, className.Capacity);
        if (ShellClasses.Contains(className.ToString()) || DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        var title = new StringBuilder(4096); GetWindowText(hwnd, title, title.Capacity);
        if (string.IsNullOrWhiteSpace(title.ToString())) return null;
        var path = ExecutableForWindow(hwnd);
        if (path == null || !System.IO.Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || ShellProcesses.Contains(System.IO.Path.GetFileNameWithoutExtension(path))) return null;
        return new(hwnd, path, title.ToString().Trim());
    }
    public static IReadOnlyList<RunningApplication> Snapshot()
    {
        var windows = new List<ApplicationWindow>();
        EnumWindows((hwnd, _) => { if (Inspect(hwnd) is { } window) windows.Add(window); return true; }, 0);
        return windows.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var first = group.First(); var name = first.Name;
            try { name = FileVersionInfo.GetVersionInfo(first.Path).FileDescription?.Trim() is { Length: > 0 } description ? description : name; }
            catch (Exception ex) when (ex is IOException or Win32Exception or ArgumentException) { }
            return new RunningApplication(name, first.Path, string.Join(" · ", group.Select(x => x.Title).Distinct()), group.Count());
        }).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

public sealed class ForegroundWindowWatcher : IDisposable
{
    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    private readonly GCHandle callbackRoot;
    private nint hook;
    // Construct and dispose on the UI thread, which owns the WinEvent message loop.
    public ForegroundWindowWatcher(Action<nint> activated)
    {
        WinEventProc callback = (_, _, hwnd, _, _, _, _) => { if (hook != 0) activated(hwnd); };
        callbackRoot = GCHandle.Alloc(callback);
        hook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, 0, callback, 0, 0, 2 /* SKIPOWNPROCESS, OUTOFCONTEXT */);
        if (hook == 0) { callbackRoot.Free(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    public void Dispose()
    {
        if (hook == 0) return;
        UnhookWinEvent(hook); hook = 0; callbackRoot.Free();
    }
}
