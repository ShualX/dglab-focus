using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DglabFocus.Core;

namespace DglabFocus.Windows;
public sealed class StrengthChart : FrameworkElement
{
    private StrengthPreviewOptions profile = new();
    public void Update(StrengthPreviewOptions value) { profile = value; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        var accent = (Brush)FindResource("Accent");
        var muted = (Brush)FindResource("Muted");
        var line = (Brush)FindResource("Line");
        var width = Math.Max(1, ActualWidth - 64);
        var height = Math.Max(1, ActualHeight - 32);
        var maximum = Math.Max(1, profile.Maximum);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Text(string value, Point at)
        {
            dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 11, muted, dpi), at);
        }
        for (int i = 0; i < 3; i++)
        {
            var y = height * i / 2 + 2;
            dc.DrawLine(new Pen(line, 1), new(34, y), new(34 + width, y));
            Text((maximum * (1 - i / 2d)).ToString("0.#"), new(0, y - 2));
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            Point previous = default;
            for (int i = 0; i <= 5; i++)
            {
                var level = profile.At(i * profile.IntervalSeconds, 0);
                var point = new Point(34 + width * i / 5, 2 + height * (1 - level / maximum));
                if (i == 0) g.BeginFigure(point, false, false);
                else
                {
                    g.LineTo(new Point(point.X, previous.Y), true, false);
                    g.LineTo(point, true, false);
                }
                previous = point;
                if (i is 0 or 2 or 5) Text($"+{i * profile.IntervalSeconds:0.#} 秒", new(point.X - 14, height + 10));
            }
        }
        dc.DrawGeometry(null, new Pen(accent, 2.5), geometry);
    }
}
