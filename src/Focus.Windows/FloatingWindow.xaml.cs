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
    public FloatingWindow() { InitializeComponent(); }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24; Top = area.Bottom - Height - 24;
    }
    public void Update(string label, string time, SessionState value, bool needsCheckIn, double strength, double maximum)
    {
        state = value;
        StateLabel.Text = label; TimeLabel.Text = BallTimeLabel.Text = time;
        Progress.Maximum = Math.Max(1, maximum); Progress.Value = Math.Min(strength, Progress.Maximum);
        Progress.ToolTip = $"当前曲线 {strength:0.#}% / 上限 {maximum:0.#}%";
        ExitButton.Content = value == SessionState.ExitPending ? "取消退出" : "申请退出";
        ExitButton.IsEnabled = value is SessionState.Focusing or SessionState.Resting or SessionState.ExitPending;
        CheckInButton.Visibility = needsCheckIn ? Visibility.Visible : Visibility.Collapsed;
    }
    public void SetMessage(string message) => MessageLabel.Text = message;
    private void OpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    private void ExitClick(object sender, RoutedEventArgs e) { if (state == SessionState.ExitPending) CancelRequested?.Invoke(); else ExitRequested?.Invoke(); }
    private void CheckInClick(object sender, RoutedEventArgs e) => CheckInRequested?.Invoke();
    private void ExpandClick(object sender, RoutedEventArgs e) => Expand();
    public void Expand()
    {
        Width = 300; Height = 204; Ball.Visibility = Visibility.Collapsed; ControlsPanel.Visibility = Visibility.Visible;
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
