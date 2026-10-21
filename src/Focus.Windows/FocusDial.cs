using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DglabFocus.Windows;
public sealed class FocusDial : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(FocusDial), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public string TimeText { get; set; } = "50:00";
    public string StateText { get; set; } = "等待开始";
    public bool Motion { get; set; } = true;
    public void Update(double progress, string time, string state)
    {
        TimeText = time; StateText = state;
        progress = Math.Clamp(progress, 0, 1);
        if (Motion) BeginAnimation(ProgressProperty, new DoubleAnimation(progress, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        else { BeginAnimation(ProgressProperty, null); Progress = progress; }
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 16;
        var accent = (Brush)FindResource("Accent");
        var line = (Brush)FindResource("Line");
        var muted = (Brush)FindResource("Muted");
        var text = (Brush)FindResource("Text");
        dc.DrawEllipse(null, new Pen(line, 1), center, radius - 28, radius - 28);
        dc.DrawEllipse(null, new Pen(line, 1), center, radius - 36, radius - 36);
        for (int i = 0; i < 60; i++)
        {
            var angle = (i * 6 - 90) * Math.PI / 180;
            var inner = radius - (i % 5 == 0 ? 13 : 7);
            dc.DrawLine(new Pen(i / 60d <= Progress && Progress > 0 ? accent : line, i % 5 == 0 ? 2 : 1),
                new Point(center.X + inner * Math.Cos(angle), center.Y + inner * Math.Sin(angle)),
                new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }
        var end = (-90 + Math.Min(Progress, .9999) * 360) * Math.PI / 180;
        if (Progress > 0)
        {
            var arcRadius = radius - 28;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(center.X, center.Y - arcRadius), false, false);
                g.ArcTo(new Point(center.X + arcRadius * Math.Cos(end), center.Y + arcRadius * Math.Sin(end)), new Size(arcRadius, arcRadius), 0, Progress > .5, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, new Pen(accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void CenterText(string value, double size, Brush brush, double y, string family, FontWeight weight)
        {
            var format = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily(family), FontStyles.Normal, weight, FontStretches.Normal), size, brush, dpi);
            dc.DrawText(format, new Point(center.X - format.Width / 2, center.Y + y));
        }
        CenterText("FOCUS SESSION", 12, muted, -76, "Bahnschrift", FontWeights.Normal);
        CenterText(TimeText, TimeText.Length > 6 ? 42 : 62, text, -40, "Bahnschrift", FontWeights.SemiBold);
        CenterText(StateText, 14, accent, 42, "Microsoft YaHei UI", FontWeights.Normal);
        dc.DrawLine(new Pen(accent, 2), new Point(center.X - 12, center.Y + 80), new Point(center.X + 12, center.Y + 80));
    }
}
