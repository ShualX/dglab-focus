using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DglabFocus.Windows;
public partial class Theme : ResourceDictionary
{
    private void ButtonEnter(object sender, MouseEventArgs e) => Fade((UIElement)sender, .9);
    private void ButtonLeave(object sender, MouseEventArgs e) => Fade((UIElement)sender, 1);
    private static void Fade(UIElement element, double to)
    {
        if (System.Windows.Application.Current.MainWindow is not MainWindow { CanAnimate: true }) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
}
