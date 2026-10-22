using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DglabFocus.Core;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private IReadOnlyList<RunningApplication> runningApps = Array.Empty<RunningApplication>();
    private ForegroundWindowWatcher? windowWatcher;
    private DispatcherTimer? windowCaptureTimer;
    private Guid? appSelectionPlan;
    private RuleAction appSelectionAction;
    private string? capturedAppPath;
    private long windowCaptureStarted;
    private int appSelectionGeneration;
    private bool loadingRunningApps;
    private nint smokeCaptureFixture;
    private bool CanSelectApplication => !closing && !session.IsActive && SelectedPlan != null;

    private void CancelAppSelection()
    {
        if (AppSelectionPanel == null) return;
        appSelectionGeneration++;
        windowWatcher?.Dispose(); windowWatcher = null;
        windowCaptureTimer?.Stop(); windowCaptureTimer = null;
        appSelectionPlan = null; capturedAppPath = null; runningApps = Array.Empty<RunningApplication>();
        loadingRunningApps = false;
        AppSelectionPanel.Visibility = RunningPickerPanel.Visibility = CapturedAppPanel.Visibility = ConfirmAppSelectionButton.Visibility = Visibility.Collapsed;
        RunningAppList.ItemsSource = null; RunningAppSearch.Clear();
        CapturedAppPath.Clear(); CapturedAppName.Text = CapturedAppTitle.Text = AppSelectionStatus.Text = "";
        RefreshApplicationSelectionControls();
    }
    private void BeginAppSelection()
    {
        CancelAppSelection();
        appSelectionPlan = selectedPlanId;
        appSelectionAction = AppActionBox.SelectedIndex == 1 ? RuleAction.Block : RuleAction.Allow;
        ConfirmAppSelectionButton.Content = appSelectionAction == RuleAction.Block ? "加入黑名单" : "加入白名单";
        ConfirmAppSelectionButton.IsEnabled = false;
        AppSelectionPanel.Visibility = Visibility.Visible;
        RefreshApplicationSelectionControls();
        RevealAppSelection();
    }
    private void RevealAppSelection() => Dispatcher.InvokeAsync(() =>
    {
        if (appSelectionPlan != null && page == "PlansPage" && RulesEditor.Visibility == Visibility.Visible)
        { UpdateLayout(); AppSelectionPanel.BringIntoView(); }
    }, DispatcherPriority.Loaded);
    private void RefreshApplicationSelectionControls()
    {
        if (AppSelectionPanel == null) return;
        AppActionBox.IsEnabled = appSelectionPlan == null;
        RefreshRunningAppsButton.IsEnabled = !loadingRunningApps;
        ConfirmAppSelectionButton.IsEnabled = CanSelectApplication && appSelectionPlan == selectedPlanId &&
            (capturedAppPath != null || RunningAppList.SelectedItem is RunningApplication);
    }
    private async void ChooseRunningAppClick(object sender, RoutedEventArgs e)
    {
        if (!CanSelectApplication) { ExplainUnavailableAction(sender, PlanEditReason); return; }
        BeginAppSelection(); RunningPickerPanel.Visibility = ConfirmAppSelectionButton.Visibility = Visibility.Visible;
        await LoadRunningApps();
        if (RunningPickerPanel.Visibility == Visibility.Visible) RunningAppSearch.Focus();
    }
    private async Task LoadRunningApps()
    {
        if (!CanSelectApplication || appSelectionPlan != selectedPlanId || loadingRunningApps) return;
        var generation = appSelectionGeneration;
        var previous = (RunningAppList.SelectedItem as RunningApplication)?.Path;
        loadingRunningApps = true; RunningAppList.IsEnabled = false;
        AppSelectionStatus.Text = "正在读取有窗口的软件…"; RefreshApplicationSelectionControls();
        try
        {
            var result = await Task.Run(RunningApplications.Snapshot);
            if (generation != appSelectionGeneration || !CanSelectApplication || appSelectionPlan != selectedPlanId) return;
            runningApps = result; FilterRunningApps(previous);
            AppSelectionStatus.Text = "选择软件后，确认加入" + (appSelectionAction == RuleAction.Block ? "黑名单" : "白名单") + "。";
            RevealAppSelection();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { if (generation == appSelectionGeneration) AppSelectionStatus.Text = "暂时无法读取列表，请刷新或直接选择 .exe 文件。"; }
        finally
        {
            if (generation == appSelectionGeneration)
            { loadingRunningApps = false; RunningAppList.IsEnabled = true; RefreshApplicationSelectionControls(); }
        }
    }
    private void FilterRunningApps(string? selected = null)
    {
        var search = RunningAppSearch.Text.Trim();
        var filtered = runningApps.Where(x => x.Matches(search)).ToList();
        RunningAppList.ItemsSource = filtered;
        RunningAppList.SelectedItem = filtered.FirstOrDefault(x => string.Equals(x.Path, selected, StringComparison.OrdinalIgnoreCase));
        RunningAppCountLabel.Text = filtered.Count > 0 ? $"{filtered.Count} 个软件 · 同一程序的多个窗口已合并。可搜索名称、窗口标题或路径。"
            : runningApps.Count == 0 ? "没有可读取的软件窗口。可打开目标软件后刷新，或直接选择 .exe 文件。" : "没有匹配的软件，请换个关键词。";
        RefreshApplicationSelectionControls();
    }
    private void RunningAppSearchChanged(object sender, TextChangedEventArgs e)
    { if (!initializing && appSelectionPlan != null) FilterRunningApps(); }
    private void RunningAppSelected(object sender, SelectionChangedEventArgs e) => RefreshApplicationSelectionControls();
    private async void RefreshRunningAppsClick(object sender, RoutedEventArgs e) => await LoadRunningApps();
    private void RunningAppDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(RunningAppList, source) is ListBoxItem)
            ConfirmAppSelectionClick(sender, new RoutedEventArgs());
    }
    private void AppActionChanged(object sender, SelectionChangedEventArgs e)
    { if (!initializing && appSelectionPlan != null) CancelAppSelection(); }
    private void CancelAppSelectionClick(object sender, RoutedEventArgs e)
    { CancelAppSelection(); Notice("已取消软件选择，名单未修改。"); }
    private void CaptureNextWindowClick(object sender, RoutedEventArgs e)
    {
        if (!CanSelectApplication) { ExplainUnavailableAction(sender, PlanEditReason); return; }
        BeginAppSelection(); windowCaptureStarted = Stopwatch.GetTimestamp();
        try
        {
            var generation = appSelectionGeneration;
            windowWatcher = new ForegroundWindowWatcher(hwnd => Dispatcher.InvokeAsync(() =>
            { if (generation == appSelectionGeneration) CaptureActivatedWindow(hwnd); }));
            windowCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            windowCaptureTimer.Tick += (_, _) => RefreshWindowCapture();
            windowCaptureTimer.Start(); RefreshWindowCapture();
        }
        catch (System.ComponentModel.Win32Exception)
        { CancelAppSelection(); Notice("无法监听窗口切换，请使用运行中的软件列表或选择 .exe。"); }
    }
    private void RefreshWindowCapture()
    {
        if (!CanSelectApplication || appSelectionPlan != selectedPlanId) { CancelAppSelection(); return; }
        var seconds = 30 - Stopwatch.GetElapsedTime(windowCaptureStarted).TotalSeconds;
        if (seconds <= 0) { CancelAppSelection(); Notice("30 秒内未选中软件窗口，已取消捕获，名单未修改。"); return; }
        AppSelectionStatus.Text = $"请激活目标软件窗口 · 剩余 {Math.Ceiling(seconds)} 秒。桌面、任务栏和本软件会跳过。";
    }
    private void CaptureActivatedWindow(nint hwnd)
    {
        if (windowWatcher == null) return;
        if (smoke && (hwnd == 0 || hwnd != smokeCaptureFixture)) return;
        if (!CanSelectApplication || appSelectionPlan != selectedPlanId || Stopwatch.GetElapsedTime(windowCaptureStarted).TotalSeconds >= 30)
        { CancelAppSelection(); return; }
        if (RunningApplications.Inspect(hwnd) is not { } app) return;
        CompleteWindowCapture(app);
    }
    private void CompleteWindowCapture(ApplicationWindow app)
    {
        if (windowWatcher == null) return;
        windowWatcher.Dispose(); windowWatcher = null; windowCaptureTimer?.Stop(); windowCaptureTimer = null;
        capturedAppPath = app.Path;
        CapturedAppName.Text = app.Name; CapturedAppTitle.Text = app.Title; CapturedAppPath.Text = app.Path;
        CapturedAppPanel.Visibility = ConfirmAppSelectionButton.Visibility = Visibility.Visible;
        AppSelectionStatus.Text = "已捕获软件，请核对程序路径后加入" + (appSelectionAction == RuleAction.Block ? "黑名单。" : "白名单。");
        RefreshApplicationSelectionControls();
        RevealAppSelection();
        if (!smoke) { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Show(); if (Activate()) ConfirmAppSelectionButton.Focus(); }
    }
    private void RunningAppListKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && RunningAppList.SelectedItem != null) { ConfirmAppSelectionClick(sender, new RoutedEventArgs()); e.Handled = true; } }
    private void ConfirmAppSelectionClick(object sender, RoutedEventArgs e)
    {
        if (!CanSelectApplication || appSelectionPlan != selectedPlanId)
        { var reason = UnavailableReason([ConfirmAppSelectionButton]); CancelAppSelection(); ExplainUnavailableAction(sender, reason); return; }
        var path = capturedAppPath ?? (RunningAppList.SelectedItem as RunningApplication)?.Path;
        if (path == null) { ExplainUnavailableAction(sender, UnavailableReason([ConfirmAppSelectionButton])); return; }
        var action = appSelectionAction;
        CancelAppSelection();
        if (Rules.TryCreate(RuleKind.Application, path, false, out var rule)) AddRule(rule! with { Action = action });
        else Notice("无法识别该程序路径，请直接选择对应的 .exe 文件。");
    }
}
