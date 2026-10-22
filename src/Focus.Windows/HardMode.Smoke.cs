using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DglabFocus.Core;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private async Task RunHardModeSmoke(string folder, Guid? planId)
    {
        for (int i = 0; i < 301; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Allowed, 0);
        SelectPlan(planId);
        var original = SelectedPlan!;
        HardModeBox.IsChecked = true; HardModeChanged(this, new RoutedEventArgs());
        FocusMinutesBox.Text = "0"; SaveSettingsClick(this, new RoutedEventArgs());
        if (settings.Options.HardMode || !NoticeLabel.Text.Contains("有限"))
            throw new InvalidOperationException("狠心模式接受了无限时长。");
        FocusMinutesBox.Text = "1"; DeviceFeedbackBox.IsChecked = false; SaveSettingsClick(this, new RoutedEventArgs());
        if (!settings.Options.HardMode || RestMinutesBox.IsEnabled || RepeatBox.IsEnabled)
            throw new InvalidOperationException("狠心模式未保存或仍可设置无限循环。");
        ShowPage("PlansPage"); ShowPlanTab("RhythmEditor"); Width = 920; Height = 680;
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); PageScroll.ScrollToBottom(); HardModeBox.BringIntoView(); UpdateLayout(); Capture(folder, "HardModeSetupCompact"); }, DispatcherPriority.ApplicationIdle);
        StartClick(this, new RoutedEventArgs());
        ExitClick(this, new RoutedEventArgs()); BreakClick(this, new RoutedEventArgs()); CloseSoftware();
        var closingAttempt = new CancelEventArgs(); WindowClosing(this, closingAttempt);
        if (!closingAttempt.Cancel || !session.IsHardModeLocked || session.State != SessionState.Focusing || ExitButton.IsEnabled || ExitMenuItem.IsEnabled || BreakButton.IsEnabled)
            throw new InvalidOperationException("狠心模式的普通退出、关闭或休息入口未锁定。");
        if (floating?.FindName("ExitButton") is not System.Windows.Controls.Button { IsEnabled: false })
            throw new InvalidOperationException("狠心模式悬浮球仍允许提前退出。");
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "HardModeActiveCompact"); }, DispatcherPriority.ApplicationIdle);
        for (int i = 0; i < 10; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Outside, 0);
        Refresh();
        if (session.PreviewStrength <= 0) throw new InvalidOperationException("狠心模式没有遵循启动延迟。");
        TutorialMenuClick(this, new RoutedEventArgs());
        if (TutorialEmergencyButton.Visibility != Visibility.Visible || !session.IsHardModeLocked || session.State != SessionState.Focusing)
            throw new InvalidOperationException("锁定会话中的教程隐藏了急停入口或修改了会话。");
        TutorialEmergencyClick(this, new RoutedEventArgs());
        if (!TutorialLiveStatusLabel.Text.Contains("会话已暂停") || TutorialLiveStatusLabel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("教程急停结果没有在固定操作区显示。");
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "TutorialHardModeEmergency"); }, DispatcherPriority.ApplicationIdle);
        TutorialSkipClick(this, new RoutedEventArgs());
        if (session.State != SessionState.Suspended || session.PreviewStrength != 0 || device.IsArmed || !session.IsHardModeLocked)
            throw new InvalidOperationException("狠心模式阻止了急停或急停丢失了原会话。");
        closingAttempt = new CancelEventArgs(); WindowClosing(this, closingAttempt);
        if (!closingAttempt.Cancel) throw new InvalidOperationException("紧急暂停绕过了狠心模式的普通关闭限制。");
        StartClick(this, new RoutedEventArgs());
        for (int i = 0; i < 50; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Allowed, 0);
        Refresh();
        if (session.State != SessionState.Completed || session.IsHardModeLocked)
            throw new InvalidOperationException("狠心模式没有在固定时长后自动结束。");
        ReplacePlan(original); SelectPlan(planId); Width = 1180; Height = 830;
        File.WriteAllText(Path.Combine(folder, "hard-mode-result.txt"), "PASS: hard mode rejects unlimited duration and ordinary exit, rest, tray close and window close; tutorial preserves the locked session and its emergency stop; the original session resumes and ends at its finite deadline.\n");
    }
}
