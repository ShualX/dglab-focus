using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DglabFocus.Core;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private bool tutorialAutoChecked;
    private IInputElement? tutorialPreviousFocus;
    private TutorialStep CurrentTutorialStep => TutorialGuide.Steps[settings.Tutorial.Step];

    private void TryShowFirstRunTutorial()
    {
        if (tutorialAutoChecked) return;
        tutorialAutoChecked = true;
        if (storage.LoadError == null && settings.Tutorial.ShowAutomatically) OpenTutorial(false);
    }
    private void TutorialMenuClick(object sender, RoutedEventArgs e) => OpenTutorial(true);
    private void TutorialSizeChanged(object sender, SizeChangedEventArgs e)
    { if (TutorialCard != null) TutorialCard.MaxHeight = Math.Max(240, e.NewSize.Height - 48); }
    private void TutorialExpandClick(object sender, RoutedEventArgs e) => OpenTutorial(false);
    private void OpenTutorial(bool restart)
    {
        if (TutorialOverlay.Visibility == Visibility.Visible) { TutorialNextButton.Focus(); return; }
        if (restart) settings.Tutorial.Restart();
        settings.Tutorial.Normalize();
        tutorialPreviousFocus = Keyboard.FocusedElement;
        TutorialDock.Visibility = Visibility.Collapsed;
        SetTutorialOverlay(true);
        RenderTutorial(); Save();
        TutorialNextButton.Focus();
    }
    private void SetTutorialOverlay(bool visible)
    {
        TutorialOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NavigationPanel.IsEnabled = MainContentPanel.IsEnabled = SessionMenu.IsEnabled = !visible;
        TutorialNextButton.IsDefault = visible;
        if (visible) TutorialCard.MaxHeight = Math.Max(240, RootGrid.ActualHeight - 48);
        else if (tutorialPreviousFocus is UIElement { IsVisible: true, IsEnabled: true } previous) previous.Focus();
    }
    private void RenderTutorial()
    {
        settings.Tutorial.Normalize();
        var step = CurrentTutorialStep;
        var counter = $"第 {settings.Tutorial.Step + 1} / {TutorialGuide.Steps.Count} 步";
        TutorialCounter.Text = counter + " · 可随时跳过";
        TutorialTitle.Text = step.Title; TutorialSummary.Text = step.Summary;
        TutorialInstructions.Text = string.Join("\n\n", step.Instructions.Select((text, index) => $"{index + 1}. {text}"));
        TutorialTip.Text = step.Tip;
        TutorialActionButton.Content = step.Action;
        TutorialActionButton.Visibility = step.Action.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TutorialBackButton.IsEnabled = settings.Tutorial.Step > 0;
        TutorialNextButton.Content = TutorialDockNext.Content = settings.Tutorial.Step == TutorialGuide.Steps.Count - 1 ? "完成教程" : "下一步";
        TutorialProgressBar.Maximum = TutorialGuide.Steps.Count;
        TutorialProgressBar.Value = settings.Tutorial.Step + 1;
        TutorialDockTitle.Text = $"教程 {settings.Tutorial.Step + 1}/{TutorialGuide.Steps.Count} · {step.Title}";
        TutorialBody.ScrollToTop(); RefreshTutorialStatus(); Pulse(TutorialCard);
    }
    private void RefreshTutorialStatus()
    {
        if (TutorialStatusLabel == null || TutorialOverlay.Visibility != Visibility.Visible && TutorialDock.Visibility != Visibility.Visible) return;
        var plan = SelectedPlan;
        var text = session.IsActive ? session.State == SessionState.Suspended
            ? "会话已暂停，设置仍锁定；真实反馈继续前需重新授权。"
            : "会话进行中，设置已锁定；立即停电始终可用。"
            : settings.Tutorial.Step switch
            {
                0 => "可先体验模拟模式。",
                1 => plan == null ? "尚未选择方案，请先新建或选择。" : $"当前方案：{plan.Name}。",
                2 => plan == null ? "请先创建或选择方案。" : $"已保存 {plan.Rules.Count} 条规则。",
                3 => bridge.ConnectionCount(DateTimeOffset.Now) > 0 ? "扩展已连接，请到控制台核对网页判断。" : "尚未收到扩展报告，请检查浏览器加载。",
                4 => plan == null ? "请先创建或选择方案。" : $"已保存：延迟 {plan.Options.GraceSeconds:0.##} 秒，{(plan.Options.HardMode ? "狠心模式" : "普通模式")}。",
                5 => plan == null ? "请先创建或选择方案。" : plan.Options.DeviceFeedback ? "当前方案使用真实反馈，开始前需授权。" : "当前方案只模拟。",
                6 => device.IsArmed ? "本次设备已授权。" : device.Devices.Count > 0 ? "已发现设备，核对通道和上限后授权。" : "未发现设备，可以先跳过。",
                _ => plan == null ? "请选择方案后开始。" : "完成教程后可开始专注。"
            };
        TutorialStatusLabel.Text = TutorialDockStatus.Text = text;
        TutorialEmergencyButton.Visibility = session.IsActive || device.IsArmed ? Visibility.Visible : Visibility.Collapsed;
        TutorialLiveStatusLabel.Visibility = TutorialEmergencyButton.Visibility;
        var output = OutputDisplay.Create(session.IsActive ? session.Options : plan?.Options, session.PreviewStrength, device.OutputState);
        TutorialLiveStatusLabel.Text = (session.State == SessionState.Suspended ? "会话已暂停 · " : "") + output.Status + " · " + output.Value;
    }
    private void TutorialNextClick(object sender, RoutedEventArgs e)
    {
        if (settings.Tutorial.Step == TutorialGuide.Steps.Count - 1)
        {
            settings.Tutorial.Finish(); SetTutorialOverlay(false); TutorialDock.Visibility = Visibility.Collapsed;
            Notice("教程已完成。以后可从「帮助 → 新手教程」重新开始。"); Save(); Refresh(); return;
        }
        settings.Tutorial.Step++; RenderTutorial(); Save();
        if (TutorialDock.Visibility == Visibility.Visible) NavigateTutorialTarget();
        else TutorialNextButton.Focus();
    }
    private void TutorialBackClick(object sender, RoutedEventArgs e)
    {
        if (settings.Tutorial.Step == 0) return;
        settings.Tutorial.Step--; RenderTutorial(); Save(); TutorialNextButton.Focus();
    }
    private void TutorialActionClick(object sender, RoutedEventArgs e)
    {
        SetTutorialOverlay(false); TutorialDock.Visibility = Visibility.Visible;
        RenderTutorial(); NavigateTutorialTarget(); Save();
    }
    private void NavigateTutorialTarget()
    {
        var step = CurrentTutorialStep;
        ShowPage(step.Page);
        if (step.Tab != null) ShowPlanTab(step.Tab);
        Dispatcher.InvokeAsync(() =>
        {
            if (step.Target != null && FindName(step.Target) is FrameworkElement { IsVisible: true } target)
            { target.BringIntoView(); if (target.Focusable && target.IsEnabled) target.Focus(); Pulse(target); }
        }, DispatcherPriority.Loaded);
        RefreshTutorialStatus();
    }
    private void TutorialSkipClick(object sender, RoutedEventArgs e)
    {
        settings.Tutorial.Dismissed = true;
        SetTutorialOverlay(false); TutorialDock.Visibility = Visibility.Collapsed;
        Notice("已暂时跳过教程，可从「帮助 → 新手教程」重新开始。"); Save(); Refresh();
    }
    private void TutorialEmergencyClick(object sender, RoutedEventArgs e)
    {
        EmergencyClick(sender, e); RefreshTutorialStatus();
    }
}
