using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DglabFocus.Core;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private async Task RunApplicationSelectionSmoke(string folder, string[] args)
    {
        File.WriteAllText(Path.Combine(folder, "app-selection-result.txt"), "");
        ShowPage("PlansPage"); ShowPlanTab("RulesEditor");
        var fixtureIndex = Array.IndexOf(args, "--app-picker-fixture");
        var pathIndex = Array.IndexOf(args, "--fixture-executable");
        smokeCaptureFixture = fixtureIndex >= 0 ? new nint(long.Parse(args[fixtureIndex + 1])) : 0;
        var planId = selectedPlanId;
        ChooseRunningAppClick(this, new RoutedEventArgs());
        var until = Environment.TickCount64 + 6000;
        while (loadingRunningApps && Environment.TickCount64 < until) await Task.Delay(20);
        if (loadingRunningApps || runningApps.Any(x => string.Equals(x.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("运行中的软件列表没有正确排除本软件或读取超时。");
        if (fixtureIndex >= 0)
        {
            var expected = args[pathIndex + 1];
            var fixture = runningApps.SingleOrDefault(x => string.Equals(x.Path, expected, StringComparison.OrdinalIgnoreCase));
            if (fixture == null || fixture.WindowCount < 2 || !fixture.Windows.Contains("自习室软件选择集成测试"))
                throw new InvalidOperationException("没有发现并合并两个独立进程的测试窗口。");
        }
        // Public screenshots use demonstration data, never titles from the user's open windows.
        runningApps = new[]
        {
            new RunningApplication("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "网课复习 · 课程主页", 2),
            new RunningApplication("StudyReader", @"C:\Program Files\StudyReader\StudyReader.exe", "学习资料 · 第二章", 1),
            new RunningApplication("Visual Studio Code", @"C:\Program Files\Microsoft VS Code\Code.exe", "复习笔记", 1)
        };
        FilterRunningApps(); RunningAppList.SelectedIndex = -1; RefreshApplicationSelectionControls();
        await CheckUnavailableAction(ConfirmAppSelectionButton, "还没有选中", folder, "UnavailableSoftwareSelection");
        RunningAppList.SelectedIndex = 1;
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); AppSelectionPanel.BringIntoView(); }, DispatcherPriority.ApplicationIdle);
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "AppSelectionRunning"); }, DispatcherPriority.ApplicationIdle);
        RunningAppSearch.Text = "课程";
        if (RunningAppList.Items.Count != 1) throw new InvalidOperationException("软件窗口标题搜索无效。");
        RunningAppList.SelectedIndex = 0;
        var whitePath = ((RunningApplication)RunningAppList.SelectedItem).Path;
        ConfirmAppSelectionClick(this, new RoutedEventArgs());
        if (!rules.Any(x => x.Kind == RuleKind.Application && x.Action == RuleAction.Allow && x.Value == whitePath))
            throw new InvalidOperationException("从列表确认后没有加入所选白名单。");
        var count = rules.Count;
        AddRule(new(RuleKind.Application, whitePath.ToUpperInvariant()));
        if (rules.Count != count) throw new InvalidOperationException("程序路径大小写变化添加了重复规则。");

        AppActionBox.SelectedIndex = 1;
        CaptureNextWindowClick(this, new RoutedEventArgs());
        CaptureActivatedWindow(new WindowInteropHelper(this).Handle);
        CaptureActivatedWindow(0);
        if (windowWatcher == null || capturedAppPath != null) throw new InvalidOperationException("捕获没有跳过本软件或无效窗口。");
        if (fixtureIndex >= 0)
        {
            File.WriteAllText(Path.Combine(folder, "window-capture-request"), "notify owned test window only");
            until = Environment.TickCount64 + 4500;
            while (capturedAppPath == null && Environment.TickCount64 < until) await Task.Delay(20);
            if (!string.Equals(capturedAppPath, args[pathIndex + 1], StringComparison.OrdinalIgnoreCase) || windowWatcher != null)
                throw new InvalidOperationException("真实 WinEvent 通知没有捕获测试窗口的准确程序路径。");
            ConfirmAppSelectionClick(this, new RoutedEventArgs());
            if (!rules.Any(x => x.Kind == RuleKind.Application && x.Action == RuleAction.Block && x.Value == args[pathIndex + 1]))
                throw new InvalidOperationException("捕获的软件没有加入所选黑名单。");
            File.AppendAllText(Path.Combine(folder, "app-selection-result.txt"), "PASS: native window activation notification, exact executable path, grouped fixture windows and blacklist rule.\n");
            CaptureNextWindowClick(this, new RoutedEventArgs());
        }
        AppSelectionPanel.BringIntoView();
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "AppSelectionWaiting"); }, DispatcherPriority.ApplicationIdle);
        CompleteWindowCapture(new(1, @"C:\Program Files\StudyReader\StudyReader.exe", "学习资料 · 第二章"));
        if (windowWatcher != null || rules.Count != (fixtureIndex >= 0 ? count + 1 : count) || ConfirmAppSelectionButton.IsEnabled != true)
            throw new InvalidOperationException("捕获后没有等待确认，或没有停止监听。");
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "AppSelectionCaptured"); }, DispatcherPriority.ApplicationIdle);
        Width = 920; Height = 680;
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); AppSelectionPanel.BringIntoView(); }, DispatcherPriority.ApplicationIdle);
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "AppSelectionCompact"); }, DispatcherPriority.ApplicationIdle);
        var confirmBottom = ConfirmAppSelectionButton.TranslatePoint(new System.Windows.Point(0, ConfirmAppSelectionButton.ActualHeight), PageScroll).Y;
        if (confirmBottom > PageScroll.ViewportHeight + 1) throw new InvalidOperationException("最小窗口里软件选择的确认按钮不可见。");
        Width = 1180; Height = 830;
        var beforeCancel = rules.Count;
        CancelAppSelectionClick(this, new RoutedEventArgs());
        if (rules.Count != beforeCancel || capturedAppPath != null) throw new InvalidOperationException("取消选择修改了规则。");
        CaptureNextWindowClick(this, new RoutedEventArgs());
        windowCaptureStarted -= Stopwatch.Frequency * 31;
        RefreshWindowCapture();
        if (windowWatcher != null || appSelectionPlan != null || rules.Count != beforeCancel)
            throw new InvalidOperationException("超时后仍然监听窗口或修改了规则。");
        BeginAppSelection(); var pending = LoadRunningApps(); CancelAppSelection(); await pending;
        if (RunningAppList.Items.Count != 0 || appSelectionPlan != null) throw new InvalidOperationException("过期的异步读取恢复了已取消的选择。");
        CaptureNextWindowClick(this, new RoutedEventArgs());
        CompleteWindowCapture(new(1, whitePath, "示例课程"));
        SelectPlan(null); ConfirmAppSelectionClick(this, new RoutedEventArgs()); SelectPlan(planId);
        if (rules.Count != beforeCancel || appSelectionPlan != null) throw new InvalidOperationException("切换方案后仍可确认旧的软件选择。");
        CaptureNextWindowClick(this, new RoutedEventArgs()); ShowPage("FocusPage");
        if (windowWatcher != null) throw new InvalidOperationException("离开方案页后没有停止捕获。");
        ShowPage("PlansPage"); ShowPlanTab("RulesEditor");
        rules.Clear(); Save(); AppActionBox.SelectedIndex = 0;
        File.AppendAllText(Path.Combine(folder, "app-selection-result.txt"), "PASS: list search and selection, duplicate paths, capture preview, confirmation, cancel, timeout, stale loading and plan change.\n");
    }
}
