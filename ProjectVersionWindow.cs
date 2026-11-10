using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TealLauncher;

public sealed class ProjectVersionWindow : Window
{
    private readonly List<ModrinthVersion> _versions;
    private readonly ComboBox _game = new();
    private readonly ListBox _list = new();
    private readonly Button _install = new() { Content = "Установить", MinWidth = 120, Padding = new Thickness(14, 7, 14, 7) };
    public ModrinthVersion? SelectedVersion { get; private set; }

    public ProjectVersionWindow(ModrinthProjectHit project, IEnumerable<ModrinthVersion> versions, ModpackInstance? instance, string type)
    {
        Icon = LauncherBranding.WindowIcon;
        Title = project.Title + " — выбор версии";
        Width = 650; Height = 520; MinWidth = 560; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelDialog");
        SetResourceReference(ForegroundProperty, "PanelText");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        _versions = versions.Where(v => v.Files.Count > 0
            && (type == "modpack" ? v.Files.Any(f => f.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                : instance != null && MainWindow.SelectCompatibleVersion(new[] { v }, instance, type) != null))
            .OrderByDescending(v => v.DatePublished).ToList();
        var grid = new Grid { Margin = new Thickness(22) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        void Add(UIElement item, int row) { Grid.SetRow(item, row); grid.Children.Add(item); }
        Add(new TextBlock { Text = project.Title, FontSize = 21, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap }, 0);
        var description = new TextBlock
        {
            Text = type == "modpack" ? project.Description + "\nСборка будет установлена отдельно, со своей версией Minecraft и загрузчиком."
                : $"Установка в «{instance?.Name}»: Minecraft {instance?.Version}, {instance?.ModLoader}. Показаны только совместимые версии.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14)
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "PanelMuted");
        Add(description, 1);
        var filter = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        filter.Children.Add(new TextBlock { Text = "Minecraft:", Width = 100, VerticalAlignment = VerticalAlignment.Center });
        _game.MinHeight = 30;
        _game.SetResourceReference(StyleProperty, "PanelComboStyle");
        _game.Items.Add("Все версии");
        foreach (string game in _versions.SelectMany(v => v.GameVersions).Distinct()) _game.Items.Add(game);
        _game.SelectedIndex = 0;
        _game.SelectionChanged += (_, _) => Refresh();
        filter.Children.Add(_game);
        Add(filter, 2);
        _list.SetResourceReference(Control.BorderBrushProperty, "SectionBorder");
        _list.SetResourceReference(Control.BackgroundProperty, "PanelInput");
        _list.SetResourceReference(Control.ForegroundProperty, "PanelText");
        _list.SelectionChanged += (_, _) => _install.IsEnabled = _list.SelectedItem is VersionChoice;
        Add(_list, 3);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _install.SetResourceReference(StyleProperty, "SectionButtonStyle");
        _install.SetResourceReference(Control.BackgroundProperty, "SectionActionBrush");
        _install.SetResourceReference(Control.ForegroundProperty, "Brush.White");
        var cancel = new Button { Content = "Отмена", MinWidth = 100, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "SectionButtonStyle");
        cancel.SetResourceReference(Control.BackgroundProperty, "PanelSecondary");
        cancel.SetResourceReference(Control.ForegroundProperty, "PanelText");
        _install.Click += (_, _) => { if (_list.SelectedItem is VersionChoice choice) { SelectedVersion = choice.Version; DialogResult = true; } };
        buttons.Children.Add(_install); buttons.Children.Add(cancel);
        Add(buttons, 4);
        Content = grid;
        Refresh();
    }

    private void Refresh()
    {
        string game = _game.SelectedItem?.ToString() ?? "Все версии";
        _list.Items.Clear();
        foreach (var version in _versions.Where(v => game == "Все версии" || v.GameVersions.Contains(game)))
            _list.Items.Add(new VersionChoice(version));
        _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1;
        _install.IsEnabled = _list.SelectedIndex >= 0;
        if (_list.Items.Count == 0) _list.Items.Add("Совместимых версий нет. Выберите другую сборку или версию Minecraft.");
    }

    private sealed record VersionChoice(ModrinthVersion Version)
    {
        public override string ToString() => $"{Version.Name}\nMinecraft {string.Join(", ", Version.GameVersions)}  ·  {string.Join(", ", Version.Loaders)}  ·  {Version.VersionType}  ·  {Version.DatePublished:dd.MM.yyyy}";
    }
}
