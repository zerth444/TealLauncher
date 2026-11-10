using System.Windows;

namespace TLauncherRemake;

/// <summary>Application resources also update open dialogs and existing control templates.</summary>
internal static class ThemeManager
{
    public static void Apply(bool legacy)
    {
        if (Application.Current is not { } app) return;
        var source = new Uri($"/TLauncherRemake;component/Themes/{(legacy ? "Legacy" : "Modern")}.xaml", UriKind.Relative);
        var dictionaries = app.Resources.MergedDictionaries;
        if (dictionaries.Count == 1 && dictionaries[0].Source == source) return;
        var next = new ResourceDictionary { Source = source };
        // Load first: a resource error must never leave the app without a theme.
        dictionaries.Clear();
        dictionaries.Add(next);
    }
}
