using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace TLauncherRemake;

public partial class MainWindow
{
    private readonly ObservableCollection<InstalledContentRow> _installedRows = new();
    private ICollectionView? _installedView;
    private readonly Dictionary<string, (long Size, DateTime Time, string Name)> _contentNameCache = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshInstalledContents()
    {
        if (ModsInstalledList == null) return;
        if (_installedView == null)
        {
            _installedView = CollectionViewSource.GetDefaultView(_installedRows);
            _installedView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(InstalledContentRow.Group)));
            _installedView.Filter = value => value is InstalledContentRow row && row.Name.Contains(ModsInstalledSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            ModsInstalledList.ItemsSource = _installedView;
        }
        double offset = ModsInstalledScroll.VerticalOffset;
        _installedRows.Clear();
        var instance = GetSelectedInstance();
        if (instance == null)
        {
            ModsInstalledTitle.Text = "Содержимое сборки";
            ModsInstalledEmpty.Text = "Выберите или создайте сборку в списке сверху.";
            ModsInstalledEmpty.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            void AddFolder(string folder, string group, string type, string root)
            {
                if (_modsProjectType != "modpack" && _modsProjectType != type || !Directory.Exists(folder)) return;
                foreach (string path in Directory.EnumerateFileSystemEntries(folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    string name = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path;
                    if (!(name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || Directory.Exists(path) && type != "mod" && File.Exists(Path.Combine(path, "pack.mcmeta"))
                        || Directory.Exists(Path.Combine(path, "shaders")))) continue;
                    _installedRows.Add(new InstalledContentRow
                    {
                        Path = path, Root = root, Group = group, Type = type,
                        Name = GetContentName(path, type), Filename = Path.GetFileName(name),
                        Enabled = InstanceContentManager.IsEnabled(root, path), CanToggle = CanChangeInstalledContent()
                    });
                }
            }
            AddFolder(Path.Combine(instance.Directory, "mods"), "Моды", "mod", instance.Directory);
            AddFolder(Path.Combine(instance.Directory, "resourcepacks"), "Ресурспаки", "resourcepack", instance.Directory);
            AddFolder(Path.Combine(instance.Directory, "shaderpacks"), "Шейдеры", "shader", instance.Directory);
            string saves = Path.Combine(instance.Directory, "saves");
            if (Directory.Exists(saves))
                foreach (string world in Directory.EnumerateDirectories(saves))
                    AddFolder(Path.Combine(world, "datapacks"), "Датапаки · " + Path.GetFileName(world), "datapack", world);
            ModsInstalledTitle.Text = $"Установлено: {_installedRows.Count}";
            UpdateInstalledEmptyMessage();
            Dispatcher.BeginInvoke(() => ModsInstalledScroll.ScrollToVerticalOffset(offset));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ModsInstalledEmpty.Text = "Не удалось прочитать содержимое: " + ex.Message;
            ModsInstalledEmpty.Visibility = Visibility.Visible;
        }
    }

    private bool CanChangeInstalledContent() => !_isLaunching && !_installingProject && _gameProcess is not { HasExited: false };

    private string GetContentName(string path, string type)
    {
        string filename = Path.GetFileName(path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path);
        string fallback = System.IO.Path.GetFileNameWithoutExtension(filename);
        if (type != "mod" || !File.Exists(path)) return fallback;
        var file = new FileInfo(path);
        if (_contentNameCache.TryGetValue(path, out var cached) && cached.Size == file.Length && cached.Time == file.LastWriteTimeUtc) return cached.Name;
        string result = fallback;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.GetEntry("fabric.mod.json") is { } fabric)
            {
                using var document = JsonDocument.Parse(fabric.Open());
                if (document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    result = name.GetString() ?? fallback;
            }
            else if ((archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml")) is { } forge)
            {
                using var reader = new StreamReader(forge.Open());
                var match = Regex.Match(reader.ReadToEnd(), "(?m)^\\s*displayName\\s*=\\s*[\"']([^\"'\\r\\n]+)[\"']");
                if (match.Success) result = match.Groups[1].Value;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
        _contentNameCache[path] = (file.Length, file.LastWriteTimeUtc, result);
        return result;
    }

    private void ModsInstalledSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _installedView?.Refresh();
        if (_installedView != null) UpdateInstalledEmptyMessage();
    }

    private void UpdateInstalledEmptyMessage()
    {
        bool empty = _installedView?.IsEmpty != false;
        ModsInstalledEmpty.Text = ModsInstalledSearch.Text.Length > 0 ? "Ничего не найдено."
            : "В этой категории пока ничего не установлено. Выберите проект в каталоге справа.";
        ModsInstalledEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstalledContentToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: InstalledContentRow item } toggle) return;
        if (!CanChangeInstalledContent()) { toggle.IsChecked = item.Enabled; return; }
        try
        {
            item.Path = InstanceContentManager.SetEnabled(item.Root, item.Path, toggle.IsChecked == true);
            item.Enabled = InstanceContentManager.IsEnabled(item.Root, item.Path);
            if (item.Type == "shader")
                foreach (var row in _installedRows.Where(r => r.Type == "shader"))
                    row.Enabled = InstanceContentManager.IsEnabled(row.Root, row.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            toggle.IsChecked = item.Enabled;
            ModsSelectionInfo.Text = "Не удалось изменить состояние: " + ex.Message;
        }
    }
}

public sealed class InstalledContentRow : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Filename { get; init; } = "";
    public string Group { get; init; } = "";
    public string Type { get; init; } = "";
    public string Path { get; set; } = "";
    public string Root { get; init; } = "";
    public bool CanToggle { get; set; }
    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); PropertyChanged?.Invoke(this, new(nameof(StateText))); }
    }
    public string StateText => !CanToggle ? "Закройте игру или дождитесь завершения установки, чтобы изменить состояние."
        : Enabled ? "Включено. Нажмите, чтобы выключить." : "Выключено. Нажмите, чтобы включить.";
    public event PropertyChangedEventHandler? PropertyChanged;
}
