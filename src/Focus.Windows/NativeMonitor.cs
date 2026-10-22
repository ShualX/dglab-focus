using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DglabFocus.Windows;
public sealed record ForegroundInfo(string? Path, string Name, bool IsOwn, string? Browser, double IdleSeconds);
public static class NativeMonitor
{
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size; public uint Time; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    public static ForegroundInfo Read()
    {
        var last = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        var idle = GetLastInputInfo(ref last) ? unchecked((uint)Environment.TickCount - last.Time) / 1000d : 0;
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new(null, "无法读取前台窗口", false, null, idle);
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return new(Environment.ProcessPath, "郊狼自习室", true, null, idle);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = RunningApplications.ExecutableForWindow(hwnd);
            var name = path == null ? process.ProcessName : System.IO.Path.GetFileNameWithoutExtension(path);
            var browser = name.Equals("chrome", StringComparison.OrdinalIgnoreCase) ? "Chrome"
                : name.Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "Edge" : null;
            return new(path, name, false, browser, idle);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { return new(null, "窗口访问受限", false, null, idle); }
    }
}
