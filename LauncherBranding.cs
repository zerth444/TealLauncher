using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TealLauncher;

public static class LauncherBranding
{
    public static ImageSource WindowIcon { get; } = LoadIcon();

    private static ImageSource LoadIcon()
    {
        var icon = BitmapFrame.Create(new Uri("pack://application:,,,/TealLauncher;component/Assets/launcher.ico", UriKind.Absolute));
        icon.Freeze();
        return icon;
    }
}
