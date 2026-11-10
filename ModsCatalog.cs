using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TLauncherRemake;

public partial class MainWindow
{
    private ModsProjectItem? _retryProject;
    private string _retryType = "modpack";
    private ModrinthVersion? _retryVersion;
    private DispatcherTimer? _searchTimer;
    private CancellationTokenSource? _catalogCancellation;

    private async Task RunProjectInstallAsync(ModsProjectItem project, string type, bool chooseVersion, bool force = false)
    {
        if (_installingProject || _isLaunching || _gameProcess is { HasExited: false }) return;
        if (!force && type == "modpack" && project.InstalledDirectory != null)
        {
            await RefreshInstanceSelectionAsync(project.InstalledDirectory);
            return;
        }
        var instance = GetSelectedInstance();
        if (type != "modpack" && instance == null)
        {
            ModsSelectionInfo.Text = "Сначала создайте или выберите сборку в списке сверху.";
            return;
        }
        _retryProject = project;
        _retryType = type;
        if (chooseVersion) _retryVersion = null;
        BeginModsOperation($"Установка: {project.Title}");
        ModsCancelButton.IsEnabled = true;
        bool success = false;
        string result;
        try
        {
            ReportStage("Получение доступных версий");
            var versions = await GetProjectVersionsAsync(project.Project.ProjectId);
            ModrinthVersion? version = _retryVersion;
            if (chooseVersion)
            {
                var picker = new ProjectVersionWindow(project.Project, versions, instance, type) { Owner = this };
                if (picker.ShowDialog() != true) throw new OperationCanceledException();
                version = picker.SelectedVersion;
            }
            version ??= type == "modpack" ? versions.FirstOrDefault(v => v.Files.Any(f => f.Filename.EndsWith(".mrpack")))
                : SelectCompatibleVersion(versions, instance!, type);
            if (version == null) throw new InvalidOperationException("Подходящая версия не найдена.");
            _retryVersion = version;
            ModsToken.ThrowIfCancellationRequested();
            if (type == "modpack") await InstallModpackVersionAsync(project.Project, version);
            else await InstallContentVersionAsync(instance!, type, version);
            success = true;
            result = type == "modpack" ? "Сборка установлена. Нажмите «Играть» для запуска."
                : "Содержимое установлено. Переключатели включения находятся в списке слева.";
        }
        catch (OperationCanceledException)
        { result = "Установка отменена. Скачанные файлы сохранены в кэше; можно продолжить кнопкой «Повторить»."; }
        catch (Exception ex)
        {
            result = $"Ошибка установки: {ex.Message}";
            if (_operationLog != null) File.AppendAllText(_operationLog, ex + "\n");
        }
        EndModsOperation(result, success);
    }

    private async void ModsRetry_Click(object sender, RoutedEventArgs e)
    { if (_retryProject != null) await RunProjectInstallAsync(_retryProject, _retryType, _retryVersion == null, true); }

    private void ModsSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ModsOverlay?.Visibility != Visibility.Visible) return;
        _searchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchTimer.Stop();
        _searchTimer.Tick -= SearchTimer_Tick;
        _searchTimer.Tick += SearchTimer_Tick;
        _searchTimer.Start();
    }

    private async void SearchTimer_Tick(object? sender, EventArgs e)
    { _searchTimer?.Stop(); await LoadModsProjectsAsync(); }

    private void UpdateProjectStates()
    {
        RefreshInstalledContents();
        var instance = GetSelectedInstance();
        ModsSelectionInfo.Text = instance == null
            ? "Модпаки устанавливаются отдельно. Для модов и ресурспаков выберите или создайте свою сборку."
            : $"{instance.Name}  ·  Minecraft {instance.Version}  ·  {instance.ModLoader} {instance.LoaderVersion}";
        var installed = new Dictionary<string, InstalledContent>();
        if (instance != null)
        {
            string manifest = Path.Combine(instance.Directory, "teal-content.json");
            try
            {
                if (File.Exists(manifest)) installed = JsonSerializer.Deserialize<Dictionary<string, InstalledContent>>(File.ReadAllText(manifest)) ?? installed;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            { ModsSelectionInfo.Text += "  ·  Не удалось прочитать список содержимого; проверьте папку сборки."; }
            if (_modsProjectType == "datapack")
            {
                string saves = Path.Combine(instance.Directory, "saves");
                foreach (string world in Directory.Exists(saves) ? Directory.GetDirectories(saves) : Array.Empty<string>())
                {
                    string worldManifest = Path.Combine(world, "teal-content.json");
                    try
                    {
                        if (!File.Exists(worldManifest)) continue;
                        var entries = JsonSerializer.Deserialize<Dictionary<string, InstalledContent>>(File.ReadAllText(worldManifest));
                        foreach (var (id, entry) in entries ?? new())
                        {
                            string absolute = SafeContentPath(world, entry.Path);
                            if (File.Exists(absolute) || File.Exists(absolute + ".disabled"))
                                installed[id] = new InstalledContent { Path = Path.GetRelativePath(instance.Directory, absolute), VersionId = entry.VersionId };
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { }
                }
            }
        }
        foreach (var item in _modsProjects)
        {
            item.InstalledDirectory = _modsProjectType == "modpack"
                ? _instances.FirstOrDefault(i => i.ProjectId == item.Project.ProjectId)?.Directory : null;
            bool exists = false;
            try
            {
                exists = installed.TryGetValue(item.Project.ProjectId, out var content)
                    && (File.Exists(SafeContentPath(instance!.Directory, content.Path))
                        || File.Exists(SafeContentPath(instance.Directory, content.Path + ".disabled")));
            }
            catch (InvalidDataException) { /* Invalid manifest entries must not become file operations. */ }
            item.ActionText = _modsProjectType == "modpack" ? item.InstalledDirectory != null ? "Выбрать" : "Установить"
                : instance == null ? "Выбрать сборку" : exists ? "Установлено" : "Установить";
            item.CanInstall = !_installingProject && !_isLaunching && _gameProcess is not { HasExited: false }
                && (_modsProjectType == "modpack" || instance != null && !exists);
        }
        RefreshProjectPageState();
    }

    private async Task InstallContentVersionAsync(ModpackInstance instance, string type, ModrinthVersion version)
    {
        if (SelectCompatibleVersion(new[] { version }, instance, type) == null)
            throw new InvalidOperationException($"Версия несовместима с Minecraft {instance.Version}, {instance.ModLoader}.");
        string folder = type switch
        {
            "mod" => "mods", "resourcepack" => "resourcepacks", "shader" => "shaderpacks", "datapack" => "datapacks",
            _ => throw new NotSupportedException("Неизвестный тип проекта.")
        };
        string root = instance.Directory;
        if (type == "datapack")
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            { Title = "Выберите мир для установки датапака", InitialDirectory = Path.Combine(root, "saves") };
            if (dialog.ShowDialog(this) != true) throw new OperationCanceledException();
            if (!File.Exists(Path.Combine(dialog.FolderName, "level.dat"))) throw new InvalidOperationException("В выбранной папке нет level.dat.");
            root = dialog.FolderName;
        }
        ReportStage("Проверка обязательных зависимостей");
        var plan = new Dictionary<string, (ModrinthVersion Version, string Folder)>();
        await ResolveContentAsync(version, instance, type, folder, plan, new HashSet<string>());
        ReportStage($"Скачивание содержимого и зависимостей ({plan.Count})");
        _completedFiles = 0; _totalFiles = plan.Count; _expectedBytes = 0;
        await InstallContentPlanAsync(root, plan.Values);
        _operationDirectory = instance.Directory;
    }
}
