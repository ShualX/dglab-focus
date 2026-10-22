using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private async Task CheckUnavailableAction(FrameworkElement control, string expected, string folder, string capture)
    {
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        control.BringIntoView();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        if (control.IsEnabled || !control.IsVisible) throw new InvalidOperationException($"{control.Name} 没有处于可见禁用状态。");
        var before = (session.State, session.DebtSeconds, selectedPlanId, device.IsArmed, plans.Count, rules.Count);
        var root = PresentationSource.FromVisual(control)?.RootVisual ?? this;
        var point = control.TranslatePoint(new Point(control.ActualWidth / 2, control.ActualHeight / 2), (UIElement)root);
        if (!unavailableActions.TryExplainAt(root, point) || !unavailableActions.IsOpen || !unavailableActions.Message.Contains(expected))
        {
            Capture(folder, capture + "Failed");
            var hit = UnavailableActionFeedback.HitTarget(root, point);
            var path = string.Join(" > ", UnavailableActionFeedback.Ancestors(hit).OfType<FrameworkElement>().Select(x => $"{x.GetType().Name}:{x.Name}:{x.IsEnabled}"));
            throw new InvalidOperationException($"点击禁用的 {control.Name} 未弹出正确原因：{unavailableActions.Message}，位置 {point}，命中 {path}");
        }
        if (before != (session.State, session.DebtSeconds, selectedPlanId, device.IsArmed, plans.Count, rules.Count))
            throw new InvalidOperationException("查看不可用原因改变了会话、方案或设备授权。");
        CaptureReasonCard(unavailableActions.Card, Path.Combine(folder, capture + ".png"));
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        RaiseEvent(escape);
        if (unavailableActions.IsOpen || !escape.Handled) throw new InvalidOperationException("Esc 没有关闭原因说明。");
    }

    private static void CaptureReasonCard(Border card, string path)
    {
        card.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(card.ActualWidth)), Math.Max(1, (int)Math.Ceiling(card.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(card); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }

    private async Task CheckInitialUnavailableActions(string folder)
    {
        ShowPage("FocusPage");
        await CheckUnavailableAction(StartButton, "尚未选择", folder, "UnavailableNoPlan");
        ShowPage("PlansPage");
        await CheckUnavailableAction(CopyPlanButton, "选择", folder, "UnavailableCopyPlan");
        ShowPage("DevicePage");
        await CheckUnavailableAction(ArmDeviceButton, "选择", folder, "UnavailableAuthorizationNoPlan");
        ShowPage("FocusPage");
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        var enabled = RootGrid.TransformToVisual(this).Transform(new Point(240, 30));
        if (unavailableActions.TryExplainAt(this, enabled)) throw new InvalidOperationException("普通界面点击被误判为不可用。");
        CancelExitClick(this, new RoutedEventArgs());
        if (!unavailableActions.Message.Contains("无需取消")) throw new InvalidOperationException("没有退出申请时菜单仍无说明。");
        unavailableActions.Close();
        File.WriteAllText(Path.Combine(folder, "unavailable-actions-result.txt"), "PASS: raw clicks on disabled controls explain missing plan, copy and authorization prerequisites without changing session state; no-op menu actions explain their cause.\n");
    }
}
