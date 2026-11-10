using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TLauncherRemake;

public static class LauncherBranding
{
    public static ImageSource WindowIcon { get; } = LoadIcon();

    private static ImageSource LoadIcon()
    {
        var icon = BitmapFrame.Create(new Uri("pack://application:,,,/TLauncherRemake;component/Assets/launcher.ico", UriKind.Absolute));
        icon.Freeze();
        return icon;
    }
}
