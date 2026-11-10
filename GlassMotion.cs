using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TLauncherRemake;

public static class GlassMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(GlassMotion), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        element.Loaded -= Loaded;
        element.IsVisibleChanged -= VisibleChanged;
        if ((bool)args.NewValue)
        {
            element.Loaded += Loaded;
            element.IsVisibleChanged += VisibleChanged;
            if (element.IsLoaded) Reveal(element);
        }
        else element.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private static void Loaded(object sender, RoutedEventArgs args) => Reveal((FrameworkElement)sender);
    private static void VisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if ((bool)args.NewValue) Reveal((FrameworkElement)sender);
    }

    private static void Reveal(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation || !element.IsVisible) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(230))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }
}
