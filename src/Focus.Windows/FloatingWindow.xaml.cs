using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using DglabFocus.Core;

namespace DglabFocus.Windows;
public partial class FloatingWindow : Window
{
    public event Action? OpenRequested, ExitRequested, CancelRequested, StopRequested, CheckInRequested;
    private SessionState state;
    private bool hardModeLocked;
    internal UnavailableActionFeedback UnavailableActions { get; }
    public FloatingWindow()
    {
        InitializeComponent();
        UnavailableActions = new(this, _ => hardModeLocked
            ? "狠心模式会在约定时间结束，不能提前退出。需要停止输出时，请点击「立即停电」。"
            : "当前没有可退出的专注会话。请先开始或继续专注。", SetMessage);
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24; Top = area.Bottom - Height - 24;
    }
    public void Update(string label, string time, SessionState value, bool needsCheckIn, bool hardMode)
    {
        state = value;
        hardModeLocked = hardMode;
        StateLabel.Text = label + (hardMode ? " · 狠心" : ""); TimeLabel.Text = BallTimeLabel.Text = time;
        ExitButton.Content = hardMode ? "到时自动结束" : value == SessionState.ExitPending ? "取消退出" : "申请退出";
        ExitButton.IsEnabled = !hardMode && value is SessionState.Focusing or SessionState.Resting or SessionState.ExitPending;
        CheckInButton.Visibility = needsCheckIn ? Visibility.Visible : Visibility.Collapsed;
    }
    public void UpdateFeedback(OutputDisplay display)
    {
        FeedbackStatusLabel.Text = display.Status; FeedbackValueLabel.Text = display.Value; FeedbackDetailLabel.Text = display.Detail;
        BallOutputLabel.Text = display.BallStatus; BallStrengthLabel.Text = display.BallValue;
        var description = $"{display.Status}，{display.Detail}：{display.Value}。点击展开，Ctrl+Alt+F10 立即停电。";
        Ball.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(Ball, description);
        Progress.Maximum = Math.Max(1, display.Maximum); Progress.Value = Math.Min(display.Level, Progress.Maximum);
        Progress.ToolTip = $"{display.Detail}：{display.Value}";
    }
    public void SetMessage(string message) => MessageLabel.Text = message;
    private void OpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    private void ExitClick(object sender, RoutedEventArgs e) { if (state == SessionState.ExitPending) CancelRequested?.Invoke(); else ExitRequested?.Invoke(); }
    private void CheckInClick(object sender, RoutedEventArgs e) => CheckInRequested?.Invoke();
    private void ExpandClick(object sender, RoutedEventArgs e) => Expand();
    public void Expand()
    {
        Width = 300; Height = 280; Ball.Visibility = Visibility.Collapsed; ControlsPanel.Visibility = Visibility.Visible;
        var area = SystemParameters.WorkArea; Left = Math.Min(Left, area.Right - Width); Top = Math.Min(Top, area.Bottom - Height);
        if (System.Windows.Application.Current.MainWindow is MainWindow { CanAnimate: true })
            ControlsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(.2, 1, TimeSpan.FromMilliseconds(180)));
    }
    private void CollapseClick(object sender, RoutedEventArgs e)
    { Width = Height = 80; Ball.Visibility = Visibility.Visible; ControlsPanel.Visibility = Visibility.Collapsed; }
    private void BallKeyDown(object sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Space) { Expand(); e.Handled = true; } }
    private void DragBall(object sender, MouseButtonEventArgs e)
    {
        var left = Left; var top = Top;
        DragHeader(sender, e);
        if (Math.Abs(Left - left) + Math.Abs(Top - top) < 4) Expand();
        e.Handled = true;
    }
    private void StopClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        try { DragMove(); } catch (InvalidOperationException) { }
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left, area.Right - Width); Top = Math.Clamp(Top, area.Top, area.Bottom - Height);
    }
    public void Capture(string path)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
}
