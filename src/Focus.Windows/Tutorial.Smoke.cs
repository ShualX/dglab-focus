using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DglabFocus.Core;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private async Task RunTutorialSmoke(string folder)
    {
        var originalSettings = System.Text.Json.JsonSerializer.Serialize(settings);
        TryShowFirstRunTutorial();
        if (TutorialOverlay.Visibility != Visibility.Visible || settings.Tutorial.Step != 0 || MainContentPanel.IsEnabled || SessionMenu.IsEnabled || session.IsActive || device.IsArmed)
            throw new InvalidOperationException("首次启动教程没有弹出，或教程触发了专注及设备授权。");
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "TutorialWelcome"); }, DispatcherPriority.ApplicationIdle);
        TutorialNextClick(this, new RoutedEventArgs());
        TutorialBackClick(this, new RoutedEventArgs());
        if (settings.Tutorial.Step != 0) throw new InvalidOperationException("教程不能返回上一步。");
        Width = 920; Height = 680;
        for (int i = 1; i < TutorialGuide.Steps.Count; i++)
        {
            TutorialNextClick(this, new RoutedEventArgs());
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateLayout();
                if (TutorialNextButton.ActualHeight < 28 || TutorialNextButton.TranslatePoint(new Point(), RootGrid).Y + TutorialNextButton.ActualHeight > RootGrid.ActualHeight)
                    throw new InvalidOperationException("小窗口的教程按钮超出可见范围。");
                Capture(folder, $"TutorialStep{i + 1}Compact");
            }, DispatcherPriority.ApplicationIdle);
            TutorialActionClick(this, new RoutedEventArgs());
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            if (TutorialOverlay.Visibility != Visibility.Collapsed || TutorialDock.Visibility != Visibility.Visible || page != CurrentTutorialStep.Page || !MainContentPanel.IsEnabled)
                throw new InvalidOperationException("教程未能收成底部提示卡或跳转到对应设置。");
            if (i == 3)
                await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "TutorialBrowserDockCompact"); }, DispatcherPriority.ApplicationIdle);
            TutorialExpandClick(this, new RoutedEventArgs());
            if (settings.Tutorial.Step != i) throw new InvalidOperationException("恢复教程丢失了当前步骤。");
        }
        SetZoom(1.4);
        await Dispatcher.InvokeAsync(() =>
        {
            UpdateLayout(); Capture(folder, "TutorialZoomed");
            var nextBottom = TutorialNextButton.TranslatePoint(new Point(0, TutorialNextButton.ActualHeight), RootGrid).Y;
            if (nextBottom > RootGrid.ActualHeight) throw new InvalidOperationException("放大界面后教程操作区被遮挡。");
        }, DispatcherPriority.ApplicationIdle);
        SetZoom(1); Width = 1180; Height = 830;
        TutorialNextClick(this, new RoutedEventArgs());
        if (!settings.Tutorial.Completed || settings.Tutorial.ShowAutomatically || TutorialOverlay.Visibility != Visibility.Collapsed || TutorialDock.Visibility != Visibility.Collapsed || !SessionMenu.IsEnabled)
            throw new InvalidOperationException("教程完成状态或底层交互未恢复。");
        tutorialAutoChecked = false; TryShowFirstRunTutorial();
        if (TutorialOverlay.Visibility != Visibility.Collapsed) throw new InvalidOperationException("已完成的教程又自动弹出。");
        TutorialMenuClick(this, new RoutedEventArgs());
        if (settings.Tutorial.Step != 0 || TutorialOverlay.Visibility != Visibility.Visible) throw new InvalidOperationException("帮助菜单不能从头重开教程。");
        TutorialSkipClick(this, new RoutedEventArgs());
        tutorialAutoChecked = false; TryShowFirstRunTutorial();
        if (!settings.Tutorial.Dismissed || settings.Tutorial.Completed || TutorialOverlay.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("跳过教程后仍自动打扰用户，或被误记为完成。");
        var after = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        after.Tutorial = new(); after.Session = null;
        var before = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(originalSettings)!;
        before.Tutorial = new(); before.Session = null;
        if (System.Text.Json.JsonSerializer.Serialize(after) != System.Text.Json.JsonSerializer.Serialize(before) || session.IsActive || device.IsArmed || plans.Count != 0)
            throw new InvalidOperationException("教程修改了用户的方案、名单、偏好或设备授权。");
        settings.Tutorial.Restart(); settings.Tutorial.Finish();
        ShowPage("FocusPage"); Refresh();
        File.WriteAllText(Path.Combine(folder, "tutorial-result.txt"), "PASS: first-run tutorial, all eight steps, back/next, settings destinations, compact dock, completion, optional skip, help replay and zoomed layout. No plans, rules, preferences or output authorization changed.\n");
    }
}
