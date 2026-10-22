using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DglabFocus.Windows;

// Disabled controls stay disabled. Raw visual hit testing lets a click explain the restriction
// without re-enabling a command, changing a setting or capturing the next emergency-stop click.
public sealed class UnavailableActionFeedback
{
    private readonly Window owner;
    private readonly Func<IReadOnlyList<DependencyObject>, string> reason;
    private readonly Action<string> notice;
    private readonly Func<double> zoom;
    private readonly Popup popup;
    private readonly TextBlock message;
    internal Border Card { get; }
    internal bool IsOpen => popup.IsOpen;
    internal string Message => message.Text;

    public UnavailableActionFeedback(Window owner, Func<IReadOnlyList<DependencyObject>, string> reason,
        Action<string> notice, Func<double>? zoom = null)
    {
        this.owner = owner; this.reason = reason; this.notice = notice; this.zoom = zoom ?? (() => 1);
        message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 22 };
        message.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Assertive);
        var title = new TextBlock { Text = "暂时无法操作", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 10, 10) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var close = new Button { Content = "×", Width = 32, Height = 32, MinHeight = 32, Padding = new(0), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(close, "关闭原因说明");
        close.Click += (_, _) => Close();
        var heading = new DockPanel(); DockPanel.SetDock(close, Dock.Right);
        heading.Children.Add(close); heading.Children.Add(title);
        var content = new StackPanel(); content.Children.Add(heading); content.Children.Add(message);
        Card = new Border { Child = content, Padding = new(16), CornerRadius = new(12), BorderThickness = new(1), Width = 340 };
        TextElement.SetFontFamily(Card, owner.FontFamily);
        Card.SetResourceReference(Border.BackgroundProperty, "Panel");
        Card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        popup = new Popup { Child = Card, Placement = PlacementMode.Bottom, VerticalOffset = 8,
            AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.None };
        Card.PreviewKeyDown += DismissKey;
        owner.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PointerDown), true);
        owner.PreviewKeyDown += DismissKey;
        owner.Deactivated += (_, _) => Close();
        owner.LocationChanged += (_, _) => Close();
        owner.SizeChanged += (_, _) => Close();
        owner.Closed += (_, _) => Close();
    }

    public void IncludeMenu(MenuItem menu) => menu.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PointerDown), true);

    private void DismissKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsOpen) { Close(); e.Handled = true; }
    }
    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ChangedButton != MouseButton.Left) return;
        Close();
        var visual = e.OriginalSource as Visual ?? owner;
        var root = PresentationSource.FromVisual(visual)?.RootVisual ?? owner;
        if (TryExplainAt(root, e.GetPosition(root as IInputElement ?? owner))) e.Handled = true;
    }
    internal bool TryExplainAt(Visual root, Point point)
    {
        // InputHitTest skips disabled controls; VisualTreeHelper.HitTest deliberately does not.
        var chain = Ancestors(HitTarget(root, point));
        var control = chain.OfType<Control>().FirstOrDefault(x => !x.IsEnabled && x is ButtonBase or MenuItem or ComboBox or TextBox or ListBox);
        if (control == null) return false;
        Show(control, reason(chain)); return true;
    }
    internal static DependencyObject? HitTarget(Visual root, Point point)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(root,
            visual => visual is UIElement element && (!element.IsVisible || !element.IsHitTestVisible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
        return hit;
    }
    internal static IReadOnlyList<DependencyObject> Ancestors(DependencyObject? target)
    {
        var result = new List<DependencyObject>();
        while (target != null && !result.Contains(target))
        {
            result.Add(target);
            target = target is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(target) ?? LogicalTreeHelper.GetParent(target)
                : LogicalTreeHelper.GetParent(target);
        }
        return result;
    }
    public void Show(FrameworkElement anchor, string text)
    {
        Close(); message.Text = text; notice(text);
        Card.LayoutTransform = new ScaleTransform(zoom(), zoom());
        popup.PlacementTarget = anchor; popup.IsOpen = true;
        Card.UpdateLayout();
        var peer = UIElementAutomationPeer.FromElement(message) ?? UIElementAutomationPeer.CreatePeerForElement(message);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    public void Close() => popup.IsOpen = false;
}
