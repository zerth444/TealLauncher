using System.IO;
using System.Windows;
using System.Windows.Controls;
using TLauncherRemake;

internal static class ThemeTests
{
    public static void Run(string root)
    {
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        string path = Path.Combine(root, "theme-preferences.json");
        File.WriteAllText(path, "{\"Settings\":{}}");
        Check(!LauncherPreferences.Load(path).Settings.LegacyDesign, "existing preferences default to modern theme");
        var window = new MainWindow(path);
        ((FrameworkElement)window.Content).Measure(new Size(1066, 694)); ((FrameworkElement)window.Content).Arrange(new Rect(0, 0, 1066, 694));
        var play = (Button)window.FindName("LaunchButton");
        var size = play.RenderSize;
        var location = play.TranslatePoint(new Point(), window);
        var modern = Application.Current.Resources.MergedDictionaries[0];
        Check((CornerRadius)modern["SurfaceRadius"] == new CornerRadius(14), "modern surfaces loaded");
        Check(play.Template != null, "play template resolves");
        foreach (bool legacy in new[] { true, false, true, false })
        {
            ThemeManager.Apply(legacy);
            ((FrameworkElement)window.Content).Measure(new Size(1066, 694)); ((FrameworkElement)window.Content).Arrange(new Rect(0, 0, 1066, 694)); ((FrameworkElement)window.Content).UpdateLayout();
            var dictionary = Application.Current.Resources.MergedDictionaries[0];
            Check(modern.Keys.OfType<string>().Where(key => key != "GlassButtonTemplate" && key != "SectionFlatButtonTemplate").All(dictionary.Contains), "all shared resources available");
            Check((CornerRadius)dictionary["SurfaceRadius"] == new CornerRadius(legacy ? 0 : 14), "theme switches live");
            Check(legacy ? play.ActualWidth == 240 && play.ActualHeight == 49 : play.RenderSize == size && play.TranslatePoint(new Point(), window) == location, $"play geometry restored for each theme: legacy={legacy}, width={play.ActualWidth}, height={play.ActualHeight}, requested={play.Height}");
            if (!legacy) { var left = (Border)window.FindName("WelcomeCard"); var right = (Border)window.FindName("PopularCard"); Check(Math.Abs(right.TranslatePoint(new Point(), (UIElement)left.Parent).X - left.TranslatePoint(new Point(left.ActualWidth, 0), (UIElement)left.Parent).X - 16) < 0.1, $"top cards gap: left={left.ActualWidth}, leftX={left.TranslatePoint(new Point(), (UIElement)left.Parent).X}, rightX={right.TranslatePoint(new Point(), (UIElement)left.Parent).X}"); }
            Check(window.FontFamily.Source.Contains(legacy ? "Segoe UI" : "Segoe UI Variable"), "window font updates");
            var mods = (Button)window.FindName("ModsButton"); mods.ApplyTemplate();
            if (!legacy) { var hitSurface = (Grid)System.Windows.Media.VisualTreeHelper.GetChild(mods, 0); Check(hitSurface.Background != null && mods.ActualWidth == 36 && mods.ActualHeight == 36, "mods button has a full transparent hit surface"); }
        }
        ThemeManager.Apply(false);
        var picker = new ProjectVersionWindow(new ModrinthProjectHit { Title = "Fixture" }, Array.Empty<ModrinthVersion>(), null, "modpack");
        picker.Measure(new Size(650, 520));
        Check(picker.Background != null && picker.Foreground != null, "version picker loads modern section resources");
        var launcher = (CmlLib.Core.MinecraftLauncher)typeof(MainWindow).GetField("_minecraftLauncher", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        var create = new CreateInstanceWindow(new[] { "1.20.1" }, "1.20.1", launcher);
        create.Measure(new Size(560, 700));
        Check(create.FindName("VersionComboBox") is ComboBox && create.FindName("LoaderComboBox") is ComboBox, "creation dialog loads themed selectors");
        ThemeManager.Apply(true);
        create.Measure(new Size(560, 700));
        Check(create.Background != null && picker.Background != null, "open dialogs resolve legacy resources");
        create.Close(); picker.Close();
        LauncherPreferences.Save(path, new() { Settings = new() { LegacyDesign = true } });
        Check(LauncherPreferences.Load(path).Settings.LegacyDesign, "legacy choice survives reload");
        ThemeManager.Apply(LauncherPreferences.Load(path).Settings.LegacyDesign);
        window.Close();
        ThemeManager.Apply(false);
        Console.WriteLine("SUCCESS: theme switching and persistence");
    }
}
