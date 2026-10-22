using System.Runtime.InteropServices;
using DglabFocus.Windows;
using Forms = System.Windows.Forms;

internal sealed class ApplicationWindowFixture : IDisposable
{
    private sealed class InactiveForm : Forms.Form { protected override bool ShowWithoutActivation => true; }
    [DllImport("user32.dll")] private static extern void NotifyWinEvent(uint eventType, nint hwnd, int objectId, int childId);
    private readonly Thread thread;
    private Forms.Form? first, second;
    public nint Handle { get; private set; }
    public const string Title = "自习室软件选择集成测试";
    public ApplicationWindowFixture()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try
            {
                first = new InactiveForm { Text = Title, Width = 420, Height = 180, ShowInTaskbar = false };
                second = new InactiveForm { Text = Title + " · 第二个窗口", Width = 420, Height = 180, ShowInTaskbar = false };
                first.Shown += (_, _) => { second.Show(); Handle = first.Handle; ready.SetResult(); };
                Forms.Application.Run(first);
                second.Dispose(); first.Dispose();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
    // Exercise the real WinEvent queue using our own window, without switching the user's foreground app.
    public void NotifyActivation() => first!.BeginInvoke(new Action(() => NotifyWinEvent(3, Handle, 0, 0)));
    public static void CheckNative(ApplicationWindowFixture fixture, Action<bool, string> assert)
    {
        assert(string.Equals(RunningApplications.ExecutableForWindow(fixture.Handle), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
            "native window resolver returns the exact executable path without reading process modules");
        assert(RunningApplications.Inspect(fixture.Handle) == null, "selection excludes windows owned by this process");
        assert(RunningApplications.ExecutableForWindow(0) == null && RunningApplications.Inspect(0) == null,
            "closed or missing native windows cannot become application rules");
        var app = new RunningApplication("Microsoft Edge", @"C:\Browser\msedge.exe", "课程页面 · 学习文档", 2);
        assert(app.Matches("edge") && app.Matches("课程") && app.Matches(@"c:\browser") && !app.Matches("unrelated"),
            "running-software search accepts names, window titles and case-insensitive executable paths");
    }
    public void Dispose()
    {
        if (first is { IsDisposed: false }) first.BeginInvoke(new Action(() => { second?.Close(); first.Close(); }));
        thread.Join(TimeSpan.FromSeconds(3));
    }
}
