using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;
using CmlLib.Core.VersionMetadata;
using Microsoft.Win32;

namespace TLauncherRemake;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = CreateHttpClient();
    private MinecraftPath _minecraftPath = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TealLauncher",
            "minecraft"));
    private MinecraftLauncher _minecraftLauncher = null!;
    private readonly ObservableCollection<PopularModpackItem> _popularModpacks = new();
    private readonly ObservableCollection<MinecraftUpdateItem> _minecraftUpdates = new();
    private readonly ObservableCollection<MinecraftVersionItem> _minecraftVersions = new();
    private readonly ObservableCollection<ModsProjectItem> _modsProjects = new();
    private readonly ObservableCollection<ModpackInstance> _instances = new();
    private string _modsProjectType = "modpack";
    private bool _modsLoading;
    private int _modsGeneration;
    private bool _loadingInstances;
    private bool _installingProject;
    private readonly List<string> _allMinecraftVersionNames = new();
    private Rect _restoreBounds;
    private bool _isMaximized;
    private bool _isLaunching;
    private Process? _gameProcess;
    private string? _customJavaPath;
    private MSession? _authenticatedSession;
    private JELoginHandler? _loginHandler;

    public MainWindow() : this(null) { }

    public MainWindow(string? preferencesPath)
    {
        _preferencesPath = preferencesPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TealLauncher", "preferences.json");
        InitializeComponent();
        InitializeSettings();
        InitializeNicknamePersistence();
        PopularModpacksList.ItemsSource = _popularModpacks;
        MinecraftUpdatesList.ItemsSource = _minecraftUpdates;
        VersionComboBox.ItemsSource = _minecraftVersions;
        System.Windows.Data.CollectionViewSource.GetDefaultView(_minecraftVersions).GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(MinecraftVersionItem.VersionGroup)));
        ModsProjectsList.ItemsSource = _modsProjects;
        ModsInstanceComboBox.ItemsSource = _instances;

        InitializeMinecraftLauncher();
        UpdateLaunchButtonState();
        Closing += (_, e) =>
        {
            if (_modsCancellation == null) return;
            e.Cancel = true;
            _closeAfterModsOperation = true;
            _modsCancellation.Cancel();
            ModsOperationDetail.Text = "Завершение загрузок перед закрытием...";
        };
    }

    private void InitializeMinecraftLauncher()
    {
        _minecraftLauncher = new MinecraftLauncher(_minecraftPath);
        _minecraftLauncher.ByteProgressChanged += MinecraftLauncher_ByteProgressChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Task.WhenAll(
            LoadPopularModpacksAsync(),
            LoadMinecraftUpdatesAsync(),
            LoadMinecraftVersionsAsync());
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            e.Handled = true;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PageScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NewsScrollViewer is not null)
        {
            NewsScrollViewer.ScrollToVerticalOffset(e.NewValue);
        }
    }

    private async void PopularModpackRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PopularModpackItem item, Tag: string url }
            && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string slug = uri.Segments.Last().Trim('/');
            _modsProjectType = "modpack";
            ModsOverlay.Visibility = Visibility.Visible;
            UpdateModsTabStyles();
            await LoadInstancesAsync(); UpdateProjectStates();
            await ShowProjectPageAsync(new ModrinthProjectHit { ProjectId = slug, Title = item.Title, Slug = slug }, "modpack", null);
        }
    }

    private async void OpenModrinthModpacks_Click(object sender, RoutedEventArgs e)
    {
        _modsProjectType = "modpack";
        ModsSearchTextBox.Text = "";
        UpdateModsTabStyles();
        SettingsOverlay.Visibility = Visibility.Collapsed;
        ModsOverlay.Visibility = Visibility.Visible;
        await LoadInstancesAsync();
        UpdateProjectStates();
        await LoadModsProjectsAsync();
    }

    private async Task LoadPopularModpacksAsync()
    {
        PopularModpacksStatus.Text = "Загрузка...";
        PopularModpacksStatus.Visibility = Visibility.Visible;

        try
        {
            const string url = "https://api.modrinth.com/v2/search?query=&facets=%5B%5B%22project_type%3Amodpack%22%5D%5D&index=downloads&limit=3";
            ModrinthSearchResponse? response = await Http.GetFromJsonAsync<ModrinthSearchResponse>(url);
            var hits = response?.Hits ?? new List<ModrinthSearchHit>();

            _popularModpacks.Clear();
            foreach (ModrinthSearchHit hit in hits)
            {
                string slug = string.IsNullOrWhiteSpace(hit.Slug) ? hit.ProjectId : hit.Slug;

                _popularModpacks.Add(new PopularModpackItem
                {
                    Title = hit.Title,
                    IconUrl = hit.IconUrl,
                    Description = hit.Description,
                    Details = string.IsNullOrWhiteSpace(hit.Author)
                        ? hit.Description
                        : $"{hit.Author} — {hit.Description}",
                    DownloadsText = FormatDownloads(hit.Downloads),
                    ProjectUrl = $"https://modrinth.com/modpack/{slug}"
                });
            }

            PopularModpacksStatus.Text = "Сборки не найдены";
            PopularModpacksStatus.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            PopularModpacksStatus.Text = "Не удалось загрузить сборки";
            PopularModpacksStatus.Visibility = Visibility.Visible;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TealLauncher/1.0");
        return client;
    }

    private static string FormatDownloads(long downloads)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo("ru-RU");

        if (downloads >= 1_000_000)
        {
            return $"{(downloads / 1_000_000d).ToString("0.#", culture)} млн";
        }

        if (downloads >= 1_000)
        {
            return $"{(downloads / 1_000d).ToString("0.#", culture)} тыс.";
        }

        return downloads.ToString(culture);
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void MinecraftUpdateLink_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            OpenUrl(url);
        }
    }

    private async Task LoadMinecraftUpdatesAsync()
    {
        bool hasCache = await TryLoadMinecraftUpdatesFromCacheAsync();

        if (hasCache)
        {
            MinecraftUpdatesStatus.Visibility = Visibility.Collapsed;
        }
        else
        {
            MinecraftUpdatesStatus.Text = "Загрузка обновлений...";
            MinecraftUpdatesStatus.Visibility = Visibility.Visible;
        }

        try
        {
            List<MinecraftUpdateItem> items = await FetchMinecraftUpdatesAsync();

            if (items.Count > 0)
            {
                ReplaceMinecraftUpdates(items);
                await SaveMinecraftUpdatesToCacheAsync(items);
                MinecraftUpdatesStatus.Visibility = Visibility.Collapsed;
            }
            else if (!hasCache)
            {
                MinecraftUpdatesStatus.Text = "Обновления не найдены";
                MinecraftUpdatesStatus.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            if (!hasCache)
            {
                MinecraftUpdatesStatus.Text = "Не удалось загрузить обновления";
                MinecraftUpdatesStatus.Visibility = Visibility.Visible;
            }
        }
    }

    private async Task<List<MinecraftUpdateItem>> FetchMinecraftUpdatesAsync()
    {
        const string url = "https://launchercontent.mojang.com/v2/javaPatchNotes.json";
        MojangPatchNotesResponse? response = await Http.GetFromJsonAsync<MojangPatchNotesResponse>(url);
        List<MojangPatchNote> entries = (response?.Entries ?? new List<MojangPatchNote>())
            .OrderByDescending(entry => entry.Date)
            .ToList();

        MojangPatchNote? latestRelease = entries.FirstOrDefault(entry => entry.Type == "release");
        List<MojangPatchNote> selected = entries
            .Where(entry => entry.Type == "snapshot")
            .Take(3)
            .ToList();

        if (latestRelease is not null)
        {
            selected.Add(latestRelease);
        }

        selected = selected
            .OrderByDescending(entry => entry.Date)
            .Take(4)
            .ToList();

        MinecraftUpdateItem[] items = await Task.WhenAll(selected.Select(CreateMinecraftUpdateItemAsync));
        return items.ToList();
    }

    private async Task<MinecraftUpdateItem> CreateMinecraftUpdateItemAsync(MojangPatchNote note)
    {
        string wikiTitle = GetWikiTitle(note.Version);
        string wikiUrl = $"https://ru.minecraft.wiki/{Uri.EscapeDataString(wikiTitle.Replace(' ', '_'))}";
        string summary = string.Empty;

        try
        {
            string extractUrl = $"https://ru.minecraft.wiki/api.php?action=query&titles={Uri.EscapeDataString(wikiTitle)}&prop=extracts&explaintext=1&exintro=1&format=json&utf8=1&redirects=1";
            WikiExtractResponse? wikiResponse = await Http.GetFromJsonAsync<WikiExtractResponse>(extractUrl);
            summary = wikiResponse?.Query?.Pages.Values
                .Select(page => page.Extract)
                .FirstOrDefault(extract => !string.IsNullOrWhiteSpace(extract))
                ?? string.Empty;
        }
        catch
        {
        }

        summary = NormalizeSummary(summary);

        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "Русское описание пока недоступно. Откройте страницу Minecraft Wiki.";
        }

        string? imageUrl = Uri.TryCreate(new Uri("https://launchercontent.mojang.com/"), note.Image?.Url, out var imageUri) && imageUri.Scheme == "https" ? imageUri.AbsoluteUri : null;
        return new MinecraftUpdateItem
        {
            Title = NormalizeMinecraftTitle(note),
            ImageUrl = imageUrl,
            ThumbnailPath = await CacheNewsImageAsync(imageUrl),
            DateText = FormatMinecraftDate(note.Date),
            Summary = summary,
            Url = wikiUrl
        };
    }


    private static async Task<string?> CacheNewsImageAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TealLauncher", "news-images");
            Directory.CreateDirectory(directory);
            string name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url!)));
            string path = Path.Combine(directory, name + ".jpg");
            if (File.Exists(path)) return path;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            byte[] bytes = await Http.GetByteArrayAsync(uri, timeout.Token);
            using (var data = new MemoryStream(bytes))
                System.Windows.Media.Imaging.BitmapFrame.Create(data, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, bytes, timeout.Token); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }
        catch { return null; }
    }

    private static string NormalizeMinecraftTitle(MojangPatchNote note)
    {
        string title = note.Title.Replace("Minecraft: Java Edition ", "Minecraft ");
        return string.IsNullOrWhiteSpace(title) ? $"Minecraft {note.Version}" : title;
    }

    private static string GetWikiTitle(string version)
    {
        string title = version
            .Replace("-snapshot-", " Snapshot ")
            .Replace("-pre-release-", " Pre-Release ")
            .Replace("-release-candidate-", " Release Candidate ");

        return $"{title} (Java Edition)";
    }

    private static string FormatMinecraftDate(DateTimeOffset date)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo("ru-RU");
        return $"Дата выпуска: {date.UtcDateTime.ToString("d MMMM yyyy", culture)} года";
    }

    private static string NormalizeSummary(string summary)
    {
        return Regex.Replace(summary, @"\s+", " ").Trim();
    }

    private static string MinecraftUpdatesCachePath
    {
        get
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TealLauncher");

            return Path.Combine(directory, "minecraft-updates.json");
        }
    }

    private async Task<bool> TryLoadMinecraftUpdatesFromCacheAsync()
    {
        try
        {
            if (!File.Exists(MinecraftUpdatesCachePath))
            {
                return false;
            }

            string json = await File.ReadAllTextAsync(MinecraftUpdatesCachePath);
            MinecraftUpdatesCache? cache = JsonSerializer.Deserialize<MinecraftUpdatesCache>(json);

            if (cache?.Items is not { Count: > 0 })
            {
                return false;
            }

            await Task.WhenAll(cache.Items.Select(async item => item.ThumbnailPath = await CacheNewsImageAsync(item.ImageUrl)));
            ReplaceMinecraftUpdates(cache.Items);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ReplaceMinecraftUpdates(IEnumerable<MinecraftUpdateItem> items)
    {
        _minecraftUpdates.Clear();

        foreach (MinecraftUpdateItem item in items)
        {
            _minecraftUpdates.Add(item);
        }
    }

    private static async Task SaveMinecraftUpdatesToCacheAsync(List<MinecraftUpdateItem> items)
    {
        try
        {
            string? directory = Path.GetDirectoryName(MinecraftUpdatesCachePath);

            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            var cache = new MinecraftUpdatesCache
            {
                FetchedAt = DateTimeOffset.UtcNow,
                Items = items
            };

            await File.WriteAllTextAsync(
                MinecraftUpdatesCachePath,
                JsonSerializer.Serialize(cache));
        }
        catch
        {
        }
    }

    private async Task LoadMinecraftVersionsAsync()
    {
        int listGeneration = ++_versionListGeneration;
        string? previousVersion = (VersionComboBox.SelectedItem as MinecraftVersionItem)?.Name;
        VersionComboBox.IsEnabled = false;

        try
        {
            var allVersions = await _minecraftLauncher.GetAllVersionsAsync();
            _allMinecraftVersionNames.Clear();
            _allMinecraftVersionNames.AddRange(allVersions
                .Where(v => v.GetVersionType() is MVersionType.Release or MVersionType.Snapshot or MVersionType.OldAlpha or MVersionType.OldBeta)
                .OrderByDescending(v => v.ReleaseTime).Select(v => v.Name).Distinct());
            List<IVersionMetadata> releases = allVersions
                .Where(version => version.GetVersionType() == MVersionType.Release)
                .GroupBy(version => version.Name)
                .Select(group => group.First())
                .OrderByDescending(version => version.ReleaseTime)
                .ToList();

            List<IVersionMetadata> allowedVersions = allVersions
                .Where(v => v.GetVersionType() is MVersionType.Release or MVersionType.Snapshot or MVersionType.OldBeta or MVersionType.OldAlpha)
                .GroupBy(v => v.Name).Select(g => g.First()).OrderByDescending(v => v.ReleaseTime).ToList();

            _minecraftVersions.Clear();

            foreach (IVersionMetadata version in allowedVersions)
            {
                _minecraftVersions.Add(new MinecraftVersionItem
                {
                    Name = version.Name,
                    VersionType = version.GetVersionType(),
                    DisplayName = _minecraftVersions.Count == 0
                        ? $"Новейшая версия {version.Name}"
                        : $"Версия {version.Name}"
                });
            }

            await LoadInstancesAsync();

            foreach (ModpackInstance instance in _instances.Where(item => !item.IsPlaceholder))
            {
                string loaderText = string.Equals(instance.ModLoader, "Vanilla", StringComparison.OrdinalIgnoreCase)
                    ? "Vanilla"
                    : $"{instance.ModLoader} {instance.LoaderVersion}";

                _minecraftVersions.Add(new MinecraftVersionItem
                {
                    Name = instance.Directory,
                    DisplayName = $"Модпак: {instance.Name} ({loaderText}, {instance.Version})",
                    Instance = instance
                });
            }

            VersionComboBox.SelectedItem = _minecraftVersions.FirstOrDefault(v => v.Name == previousVersion)
                ?? _minecraftVersions.FirstOrDefault(v => v.Instance?.Directory == GetSelectedInstance()?.Directory && v.Instance != null)
                ?? _minecraftVersions.FirstOrDefault();
            UpdateLaunchButtonState();
            RefreshVersionFilter();
            _ = LoadLoaderVersionChoicesAsync(releases.Select(v => v.Name).ToArray(), listGeneration);
        }
        catch (Exception ex)
        {
            await LoadInstancesAsync();
            await RefreshInstanceSelectionAsync(GetSelectedInstance()?.Directory ?? "");
            ModsStatusText.Text = $"Каталог версий Minecraft недоступен: {ex.Message}. Установленные сборки доступны локально.";
        }
        finally
        {
            VersionComboBox.IsEnabled = true;
        }
    }

    private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLaunchButtonState();
    }

    private void MinecraftLauncher_ByteProgressChanged(object? sender, ByteProgress e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => MinecraftLauncher_ByteProgressChanged(sender, e));
            return;
        }
        if (!_isLaunching)
        {
            return;
        }

        double percentage = e.ToRatio() * 100;

        if (percentage > 0)
        {
            LaunchButton.Content = $"Загрузка {percentage:0}%";
        }
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLaunching || _gameProcess is { HasExited: false })
        {
            return;
        }

        if (VersionComboBox.SelectedItem is not MinecraftVersionItem selectedVersion)
        {
            MessageBox.Show(
                this,
                "Сначала выберите версию Minecraft.",
                "Teal Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        if (selectedVersion.Instance is not null)
        {
            await HandleInstanceClickAsync(selectedVersion.Instance);
            return;
        }

        if (selectedVersion.Loader != "Vanilla")
        {
            var existing = FindInstalledLoaderChoice(selectedVersion);
            if (existing != null)
            {
                await RefreshInstanceSelectionAsync(existing.Directory);
                await HandleInstanceClickAsync(existing);
            }
            else await ShowCreateInstanceAsync(selectedVersion);
            return;
        }

        bool cleanInstall = UpdateClientCheckBox.IsChecked == true;

        if (!cleanInstall && !IsVersionInstalled(selectedVersion.Name))
        {
            await InstallVersionAsync(selectedVersion);
            return;
        }

        string nickname = EmailTextBox.Text.Trim();

        if (!Regex.IsMatch(nickname, "^[A-Za-z0-9_]{3,16}$"))
        {
            MessageBox.Show(
                this,
                "Введите никнейм из 3–16 символов: латинские буквы, цифры или _.",
                "Teal Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            EmailTextBox.Focus();
            return;
        }

        if (cleanInstall)
        {
            MessageBoxResult result = MessageBox.Show(
                this,
                "Будет выполнена чистая установка Minecraft. Все миры, настройки, моды и ресурспаки в папке игры будут удалены. Продолжить?",
                "Обновить клиент",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _isLaunching = true;
        SetLaunchButton(cleanInstall ? "Очистка..." : "Запуск...", false);

        try
        {
            if (cleanInstall)
            {
                string gameDirectory = _minecraftPath.BasePath;

                if (Directory.Exists(gameDirectory))
                {
                    Directory.Delete(gameDirectory, true);
                }

                _minecraftPath = new MinecraftPath(gameDirectory);
                InitializeMinecraftLauncher();
                SetLaunchButton("Установка...", false);
            }

            await _minecraftLauncher.InstallAsync(selectedVersion.Name);

            IVersion version = await _minecraftLauncher.GetVersionAsync(selectedVersion.Name);
            string? javaPath;

            if (SettingsJavaComboBox.SelectedIndex == 1)
            {
                if (string.IsNullOrWhiteSpace(_customJavaPath) || !File.Exists(_customJavaPath))
                {
                    throw new InvalidOperationException("Указанный путь к Java не найден.");
                }

                javaPath = _customJavaPath;
            }
            else
            {
                javaPath = _minecraftLauncher.GetJavaPath(version);

                if (string.IsNullOrWhiteSpace(javaPath))
                {
                    javaPath = _minecraftLauncher.GetDefaultJavaPath();
                }

                if (string.IsNullOrWhiteSpace(javaPath))
                {
                    throw new InvalidOperationException("Java не найдена. Установите Java и попробуйте снова.");
                }
            }

            int maximumRam = Math.Max(512, (int)Math.Round(SettingsMemorySlider.Value));
            int screenWidth = int.TryParse(SettingsWidthTextBox.Text.Trim(), out int parsedWidth) && parsedWidth > 0
                ? parsedWidth
                : 0;
            int screenHeight = int.TryParse(SettingsHeightTextBox.Text.Trim(), out int parsedHeight) && parsedHeight > 0
                ? parsedHeight
                : 0;

            var launchOption = new MLaunchOption
            {
                Session = _authenticatedSession ?? MSession.CreateOfflineSession(nickname),
                MaximumRamMb = maximumRam,
                MinimumRamMb = Math.Min(1024, maximumRam),
                ScreenWidth = screenWidth,
                ScreenHeight = screenHeight,
                FullScreen = SettingsFullscreenCheckBox.IsChecked == true,
                JavaPath = javaPath,
                GameLauncherName = "Teal Launcher",
                GameLauncherVersion = "1.0"
            };

            string extraArguments = SettingsArgumentsTextBox.Text.Trim();

            if (!string.IsNullOrWhiteSpace(extraArguments))
            {
                launchOption.ExtraGameArguments = new[] { MArgument.FromCommandLine(extraArguments) };
            }

            Process process = await _minecraftLauncher.BuildProcessAsync(selectedVersion.Name, launchOption);
            await PrepareGameOptionsAsync(_minecraftPath.BasePath, selectedVersion.Name);
            process.Start();
            if (_settings.MinimizeOnLaunch) WindowState = WindowState.Minimized;
            _gameProcess = process;

            if (cleanInstall)
            {
                UpdateClientCheckBox.IsChecked = false;
            }

            SetLaunchButton("В игре", false);
            _ = TrackGameProcessAsync(process);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Не удалось запустить Minecraft:\n{ex.Message}",
                "Teal Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isLaunching = false;

            if (_gameProcess is null || _gameProcess.HasExited)
            {
                UpdateLaunchButtonState();
            }
        }
    }

    private async Task HandleInstanceClickAsync(ModpackInstance instance)
    {
        if (!IsInstanceInstalled(instance))
        {
            await InstallInstanceAsync(instance);
            return;
        }

        string nickname = EmailTextBox.Text.Trim();

        if (!Regex.IsMatch(nickname, "^[A-Za-z0-9_]{3,16}$"))
        {
            MessageBox.Show(
                this,
                "Введите никнейм из 3–16 символов: латинские буквы, цифры или _.",
                "Teal Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            EmailTextBox.Focus();
            return;
        }

        _retryProject = null;
        BeginModsOperation($"Запуск: {instance.Name}");
        bool success = false;
        string result = "Запуск отменён.";

        try
        {
            await LaunchInstanceAsync(instance, nickname);
            success = true;
            result = "Minecraft запущен.";
        }
        catch (OperationCanceledException) { result = "Подготовка запуска отменена."; }
        catch (Exception ex)
        {
            result = $"Не удалось запустить сборку: {ex.Message}";
            MessageBox.Show(
                this,
                $"Не удалось запустить модпак:\n{ex.Message}",
                "Teal Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndModsOperation(result, success);
        }
    }

    private async Task InstallInstanceAsync(ModpackInstance instance)
    {
        _retryProject = null;
        BeginModsOperation($"Установка: {instance.Name}");
        ModsCancelButton.IsEnabled = true;
        bool success = false;
        string result;
        try
        {
            string loaderVersionName = await EnsureInstanceLoaderAsync(instance);
            SaveInstanceMetadata(instance, loaderVersionName);
            await RefreshInstanceSelectionAsync(instance.Directory);
            _operationDirectory = instance.Directory;
            success = true;
            result = "Сборка готова к запуску.";
        }
        catch (OperationCanceledException) { result = "Установка отменена."; }
        catch (Exception ex) { result = $"Ошибка установки: {ex.Message}"; }
        EndModsOperation(result, success);
    }

    private async Task<string> EnsureInstanceLoaderAsync(ModpackInstance instance)
    {
        ReportStage($"Подготовка {instance.ModLoader} {instance.LoaderVersion}, Minecraft {instance.Version}");
        ModsToken.ThrowIfCancellationRequested();
        if (instance.ModLoader is "Fabric" or "Quilt")
        {
            string host = instance.ModLoader == "Fabric" ? "meta.fabricmc.net" : "meta.quiltmc.org";
            string api = instance.ModLoader == "Fabric" ? "v2" : "v3";
            string url = $"https://{host}/{api}/versions/loader/{Uri.EscapeDataString(instance.Version)}/{Uri.EscapeDataString(instance.LoaderVersion)}/profile/json";
            string profileJson = await Http.GetStringAsync(url, ModsToken);

            using JsonDocument document = JsonDocument.Parse(profileJson);
            string versionName = document.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Fabric не вернул имя версии.");

            string versionDirectory = Path.Combine(_minecraftPath.Versions, versionName);
            Directory.CreateDirectory(versionDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(versionDirectory, versionName + ".json"),
                profileJson);

            await _minecraftLauncher.GetAllVersionsAsync(ModsToken);
            await InstallLoaderFilesAsync(versionName);
            return versionName;
        }

        if (string.Equals(instance.ModLoader, "Forge", StringComparison.OrdinalIgnoreCase))
        {
            string versionName = await InstallForgeWithoutBrowserAsync(instance);

            await InstallLoaderFilesAsync(versionName);
            return versionName;
        }

        if (instance.ModLoader == "NeoForge")
        {
            string versionName = await new NeoForgeInstaller(_minecraftLauncher).Install(
                instance.Version, instance.LoaderVersion, CreateForgeInstallOptions());
            await _minecraftLauncher.GetAllVersionsAsync(ModsToken);
            await InstallLoaderFilesAsync(versionName);
            return versionName;
        }

        if (instance.ModLoader != "Vanilla")
            throw new NotSupportedException($"Загрузчик {instance.ModLoader} не поддерживается.");

        await InstallLoaderFilesAsync(instance.Version);
        return instance.Version;
    }

    private void SaveInstanceMetadata(ModpackInstance instance, string loaderVersionName)
    {
        var metadata = new InstanceMetadata
        {
            ProjectId = instance.ProjectId,
            ProjectVersionId = instance.ProjectVersionId,
            Name = instance.Name,
            Version = instance.Version,
            ModLoader = instance.ModLoader,
            LoaderVersion = instance.LoaderVersion,
            LoaderVersionName = loaderVersionName
        };

        WriteMetadata(instance.Directory, metadata);
    }

    private async Task LaunchInstanceAsync(ModpackInstance instance, string nickname)
    {
        string versionName = instance.LoaderVersionName;

        if (string.IsNullOrWhiteSpace(versionName))
        {
            throw new InvalidOperationException("Модпак ещё не установлен.");
        }

        await InstallLoaderFilesAsync(versionName);

        IVersion version = await _minecraftLauncher.GetVersionAsync(versionName);
        string? javaPath = _minecraftLauncher.GetJavaPath(version);

        if (string.IsNullOrWhiteSpace(javaPath))
        {
            javaPath = _minecraftLauncher.GetDefaultJavaPath();
        }

        if (string.IsNullOrWhiteSpace(javaPath))
        {
            throw new InvalidOperationException("Java не найдена. Установите Java и попробуйте снова.");
        }

        string? configuredJavaPath = SettingsJavaComboBox.SelectedIndex == 1 && File.Exists(_customJavaPath)
            ? _customJavaPath
            : javaPath;

        int maximumRam = Math.Max(512, (int)Math.Round(SettingsMemorySlider.Value));
        int screenWidth = int.TryParse(SettingsWidthTextBox.Text.Trim(), out int parsedWidth) && parsedWidth > 0
            ? parsedWidth
            : 0;
        int screenHeight = int.TryParse(SettingsHeightTextBox.Text.Trim(), out int parsedHeight) && parsedHeight > 0
            ? parsedHeight
            : 0;

        var instancePath = new MinecraftPath(instance.Directory)
        {
            Assets = _minecraftPath.Assets,
            Library = _minecraftPath.Library,
            Runtime = _minecraftPath.Runtime,
            Versions = _minecraftPath.Versions,
            Resource = _minecraftPath.Resource
        };

        var launchOption = new MLaunchOption
        {
            Session = _authenticatedSession ?? MSession.CreateOfflineSession(nickname),
            Path = instancePath,
            NativesDirectory = _minecraftPath.GetNativePath(versionName),
            MaximumRamMb = maximumRam,
            MinimumRamMb = Math.Min(1024, maximumRam),
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            FullScreen = SettingsFullscreenCheckBox.IsChecked == true,
            JavaPath = configuredJavaPath,
            GameLauncherName = "Teal Launcher",
            GameLauncherVersion = "1.0"
        };

        string extraArguments = SettingsArgumentsTextBox.Text.Trim();

        if (!string.IsNullOrWhiteSpace(extraArguments))
        {
            launchOption.ExtraGameArguments = new[] { MArgument.FromCommandLine(extraArguments) };
        }

        Process process = await _minecraftLauncher.BuildProcessAsync(versionName, launchOption);
        await PrepareGameOptionsAsync(instance.Directory, instance.Version);
        StartInstanceProcess(process, instance);
        if (_settings.MinimizeOnLaunch) WindowState = WindowState.Minimized;
        _gameProcess = process;
        SetLaunchButton("В игре", false);
        _ = TrackGameProcessAsync(process);
    }

    private async Task InstallVersionAsync(MinecraftVersionItem selectedVersion)
    {
        _isLaunching = true;
        SetLaunchButton("Установка...", false);

        try
        {
            await _minecraftLauncher.InstallAsync(selectedVersion.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Не удалось установить Minecraft:\n{ex.Message}",
                "Teal Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isLaunching = false;
            UpdateLaunchButtonState();
        }
    }

    private async Task TrackGameProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 && _gameLogs.TryGetValue(process.Id, out string? log))
            {
                string latest = Path.Combine(Path.GetDirectoryName(log)!, "latest.log");
                _operationLog = File.Exists(latest) && new FileInfo(latest).Length > 0 ? latest : log;
                ModsOperationPanel.Visibility = Visibility.Visible;
                ModsOperationTitle.Text = "Игра завершилась с ошибкой";
                ModsOperationDetail.Text = $"Код выхода: {process.ExitCode}. Откройте журнал: в нём указаны ошибки Java или модов.";
                ModsOperationSummary.Text = Path.GetFileName(log);
                ModsOperationProgress.IsIndeterminate = false;
                ModsCancelButton.Visibility = Visibility.Collapsed;
                ModsDismissButton.Visibility = Visibility.Visible;
                ModsRetryButton.Visibility = Visibility.Collapsed;
            }
            _gameLogs.Remove(process.Id);
        }
        catch
        {
        }

        if (ReferenceEquals(_gameProcess, process))
        {
            _gameProcess = null;
        }

        UpdateLaunchButtonState();
        UpdateProjectStates();
    }

    private bool IsVersionInstalled(string versionName)
    {
        return File.Exists(_minecraftPath.GetVersionJsonPath(versionName))
            && File.Exists(_minecraftPath.GetVersionJarPath(versionName));
    }

    private void UpdateLaunchButtonState()
    {
        if (_isLaunching)
        {
            return;
        }

        if (_gameProcess is { HasExited: false })
        {
            SetLaunchButton("В игре", false);
            return;
        }

        if (VersionComboBox.SelectedItem is not MinecraftVersionItem selectedVersion)
        {
            SetLaunchButton("Играть", false);
            return;
        }

        if (selectedVersion.Loader != "Vanilla" && selectedVersion.Instance == null)
        {
            SetLaunchButton(FindInstalledLoaderChoice(selectedVersion) == null ? "Установить" : "Играть", true);
            return;
        }

        if (UpdateClientCheckBox.IsChecked == true && selectedVersion.Instance is null)
        {
            SetLaunchButton("Переустановить", true);
            return;
        }

        bool installed = selectedVersion.Instance is null
            ? IsVersionInstalled(selectedVersion.Name)
            : IsInstanceInstalled(selectedVersion.Instance);

        SetLaunchButton(installed ? "Играть" : "Установить", true);
    }

    private bool IsInstanceInstalled(ModpackInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.LoaderVersionName))
        {
            return false;
        }

        return File.Exists(_minecraftPath.GetVersionJsonPath(instance.LoaderVersionName))
            && IsVersionInstalled(instance.Version);
    }

    private void UpdateClientCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateLaunchButtonState();
    }

    private void SetLaunchButton(string text, bool enabled)
    {
        LaunchButton.Content = text;
        LaunchButton.IsEnabled = enabled;
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (VersionComboBox.SelectedItem is MinecraftVersionItem { Instance: { } instance })
        {
            Directory.CreateDirectory(instance.Directory);
            OpenUrl(instance.Directory);
            return;
        }
        Directory.CreateDirectory(_minecraftPath.BasePath);
        Directory.CreateDirectory(Path.Combine(_minecraftPath.BasePath, "mods"));
        Directory.CreateDirectory(Path.Combine(_minecraftPath.BasePath, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(_minecraftPath.BasePath, "shaderpacks"));

        OpenUrl(_minecraftPath.BasePath);
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/zerth444");
    }

    private void OpenSocialLink_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string url }) OpenUrl(url); }

    private async void AccountsCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        try
        {
            JELoginHandler loginHandler = GetLoginHandler();
            MSession? session = null;

            try
            {
                session = await loginHandler.AuthenticateSilently();
            }
            catch
            {
            }

            if (session is null || string.IsNullOrWhiteSpace(session.Username))
            {
                var loginWindow = new AccountLoginWindow(loginHandler)
                {
                    Owner = this
                };

                if (loginWindow.ShowDialog() != true || loginWindow.Session is null)
                {
                    AccountsCheckBox.IsChecked = false;
                    return;
                }

                session = loginWindow.Session;
            }

            if (!ApplyAuthenticatedSession(session))
            {
                AccountsCheckBox.IsChecked = false;
            }
        }
        catch (Exception ex)
        {
            AccountsCheckBox.IsChecked = false;

            MessageBox.Show(
                this,
                $"Не удалось восстановить аккаунт:\n{ex.Message}",
                "Аккаунты",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AccountsCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        _authenticatedSession = null;
        EmailTextBox.IsReadOnly = false;
    }

    private JELoginHandler GetLoginHandler()
    {
        return _loginHandler ??= JELoginHandlerBuilder.BuildDefault();
    }

    private bool ApplyAuthenticatedSession(MSession? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.Username))
        {
            return false;
        }

        _authenticatedSession = session;
        EmailTextBox.Text = session.Username;
        EmailTextBox.IsReadOnly = true;
        return true;
    }

    private async void OpenMods_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        ModsOverlay.Visibility = Visibility.Visible;
        UpdateModsTabStyles();

        await LoadInstancesAsync();
        UpdateProjectStates();
        await LoadModsProjectsAsync();
    }

    private void CloseMods_Click(object sender, RoutedEventArgs e)
    {
        ModsOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ModsTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string projectType })
        {
            return;
        }

        CloseProjectPage_Click(this, new RoutedEventArgs());
        _modsProjectType = projectType;
        UpdateModsTabStyles();
        await LoadModsProjectsAsync();
    }

    private void UpdateModsTabStyles()
    {
        RefreshInstalledContents();
        SetModsTabStyle(ModsTabModpacks, _modsProjectType == "modpack");
        SetModsTabStyle(ModsTabMods, _modsProjectType == "mod");
        SetModsTabStyle(ModsTabResourcepacks, _modsProjectType == "resourcepack");
        SetModsTabStyle(ModsTabMaps, _modsProjectType == "datapack");
        SetModsTabStyle(ModsTabShaders, _modsProjectType == "shader");
    }

    private static void SetModsTabStyle(Button button, bool active)
    {
        button.SetResourceReference(BackgroundProperty, active ? "PanelTabActive" : "PanelTabInactive");
        button.SetResourceReference(ForegroundProperty, active ? "PanelTabActiveInk" : "OnGlass");
    }

    private async void ModsSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModsOverlay is not null && ModsOverlay.Visibility == Visibility.Visible)
        {
            await LoadModsProjectsAsync();
        }
    }

    private async void ModsSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _searchTimer?.Stop();
            await LoadModsProjectsAsync();
            e.Handled = true;
        }
    }

    private async void ModsInstanceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingInstances || _installingProject) return;
        CloseProjectPage_Click(this, new RoutedEventArgs());
        UpdateProjectStates();
        var instance = GetSelectedInstance();
        File.WriteAllText(Path.Combine(_minecraftPath.BasePath, "selected-instance.txt"), instance?.Directory ?? "");
        if (instance != null)
            VersionComboBox.SelectedItem = _minecraftVersions.FirstOrDefault(v => v.Instance?.Directory == instance.Directory);
        if (ModsOverlay?.Visibility == Visibility.Visible) await LoadModsProjectsAsync();
    }

    private async void ModsCompatibility_Click(object sender, RoutedEventArgs e)
    {
        await LoadModsProjectsAsync();
    }

    private async Task LoadModsProjectsAsync(bool append = false)
    {
        if (append && _modsLoading) return;
        int generation = ++_modsGeneration;
        if (!append)
        {
            _catalogCancellation?.Cancel();
            _catalogCancellation?.Dispose();
            _catalogCancellation = new CancellationTokenSource();
        }
        var cancellation = _catalogCancellation?.Token ?? CancellationToken.None;

        _modsLoading = true;
        if (!append) _modsProjects.Clear();
        ModsStatusText.Text = append ? "Загрузка..." : "Загрузка...";
        ModsStatusText.Visibility = Visibility.Visible;
        ModsLoadMoreButton.Visibility = Visibility.Collapsed;

        try
        {
            string facetType = _modsProjectType;
            string query = Uri.EscapeDataString(ModsSearchTextBox.Text.Trim());
            var filters = new List<string[]> { new[] { $"project_type:{facetType}" } };
            var instance = GetSelectedInstance();
            if (instance != null && facetType != "modpack" && ModsCompatibleCheckBox.IsChecked == true)
            {
                filters.Add(new[] { $"versions:{instance.Version}" });
                if (facetType == "mod") filters.Add(new[] { $"categories:{instance.ModLoader.ToLowerInvariant()}" });
            }
            string facets = Uri.EscapeDataString(JsonSerializer.Serialize(filters));
            string index = ModsSortComboBox.SelectedIndex switch
            {
                1 => "newest",
                2 => "updated",
                _ => "downloads"
            };
            int offset = append ? _modsProjects.Count : 0;

            string url = $"https://api.modrinth.com/v2/search?query={query}&facets={facets}&index={index}&limit=20&offset={offset}";
            ModrinthProjectSearchResponse? response = await Http.GetFromJsonAsync<ModrinthProjectSearchResponse>(url, cancellation);
            if (generation != _modsGeneration) return;

            if (!append)
            {
                _modsProjects.Clear();
            }

            foreach (ModrinthProjectHit hit in response?.Hits ?? new List<ModrinthProjectHit>())
            {
                _modsProjects.Add(new ModsProjectItem
                {
                    Project = hit
                });
            }

            ModsStatusText.Text = _modsProjects.Count == 0 ? "Ничего не найдено" : string.Empty;
            ModsStatusText.Visibility = _modsProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateProjectStates();
            if (!append) ModsScrollViewer.ScrollToTop();
            long totalHits = response?.TotalHits ?? _modsProjects.Count;
            ModsLoadMoreButton.Visibility = _modsProjects.Count < totalHits
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (generation != _modsGeneration) return;
            if (ex is OperationCanceledException && cancellation.IsCancellationRequested) return;
            if (!append)
            {
                _modsProjects.Clear();
            }

            ModsStatusText.Text = $"Не удалось загрузить проекты: {ex.Message}";
            ModsStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (generation == _modsGeneration) _modsLoading = false;
        }
    }

    private async void ModsLoadMore_Click(object sender, RoutedEventArgs e)
    {
        await LoadModsProjectsAsync(true);
    }

    private async Task LoadInstancesAsync()
    {
        string previous = (ModsInstanceComboBox.SelectedItem as ModpackInstance)?.Directory ?? string.Empty;
        string selectionFile = Path.Combine(_minecraftPath.BasePath, "selected-instance.txt");
        if (string.IsNullOrEmpty(previous) && File.Exists(selectionFile)) previous = (await File.ReadAllTextAsync(selectionFile)).Trim();
        string root = Path.Combine(_minecraftPath.BasePath, "instances");
        Directory.CreateDirectory(root);

        var loaded = new List<ModpackInstance> { new ModpackInstance
        {
            Name = "Не выбрано",
            IsPlaceholder = true
        } };

        foreach (string directory in Directory.GetDirectories(root).Where(d => !Path.GetFileName(d).StartsWith(".")))
        {
            string metadataPath = Path.Combine(directory, "instance.json");
            if (!File.Exists(metadataPath)) continue;
            string name = Path.GetFileName(directory);
            string version = string.Empty;
            string modLoader = "Vanilla";
            string loaderVersion = string.Empty;
            string loaderVersionName = string.Empty;
            string projectId = "", projectVersionId = "";

            try
            {
                if (File.Exists(metadataPath))
                {
                    InstanceMetadata? metadata = JsonSerializer.Deserialize<InstanceMetadata>(
                        await File.ReadAllTextAsync(metadataPath));

                    if (!string.IsNullOrWhiteSpace(metadata?.Name))
                    {
                        name = metadata.Name;
                    }

                    version = metadata?.Version ?? string.Empty;
                    modLoader = string.IsNullOrWhiteSpace(metadata?.ModLoader) ? "Vanilla" : metadata.ModLoader;
                    loaderVersion = metadata?.LoaderVersion ?? string.Empty;
                    loaderVersionName = metadata?.LoaderVersionName ?? string.Empty;
                    projectId = metadata?.ProjectId ?? "";
                    projectVersionId = metadata?.ProjectVersionId ?? "";
                }
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(version)) continue;
            loaded.Add(new ModpackInstance
            {
                Name = name,
                Directory = directory,
                Version = version,
                ModLoader = modLoader,
                LoaderVersion = loaderVersion,
                LoaderVersionName = loaderVersionName, ProjectId = projectId, ProjectVersionId = projectVersionId
            });
        }

        _loadingInstances = true;
        _instances.Clear();
        foreach (var instance in loaded) _instances.Add(instance);
        ModpackInstance? selected = _instances
            .FirstOrDefault(instance => !instance.IsPlaceholder
                && string.Equals(instance.Directory, previous, StringComparison.OrdinalIgnoreCase));

        ModsInstanceComboBox.SelectedItem = selected ?? _instances[0];
        _loadingInstances = false;
    }

    private ModpackInstance? GetSelectedInstance()
    {
        if (ModsInstanceComboBox.SelectedItem is ModpackInstance instance && !instance.IsPlaceholder)
        {
            return instance;
        }

        return null;
    }

    private async void CreateInstance_Click(object sender, RoutedEventArgs e)
    {
        await ShowCreateInstanceAsync(VersionComboBox.SelectedItem as MinecraftVersionItem);
    }

    private async Task ShowCreateInstanceAsync(MinecraftVersionItem? selected)
    {
        if (_installingProject || _isLaunching || _gameProcess is { HasExited: false }) return;
        var versions = _allMinecraftVersionNames.Count > 0 ? _allMinecraftVersionNames.ToList()
            : _minecraftVersions.Where(v => v.Instance is null && v.Loader == "Vanilla").Select(v => v.Name).ToList();
        if (versions.Count == 0)
        {
            MessageBox.Show(this, "Список версий ещё загружается. Повторите через несколько секунд.");
            return;
        }
        string game = selected?.Instance?.Version ?? (selected?.Loader != "Vanilla" ? selected?.GameVersion : selected?.Name) ?? versions[0];
        string loader = selected?.Instance?.ModLoader ?? (selected?.Loader is null or "Vanilla" ? "Fabric" : selected.Loader);
        var window = new CreateInstanceWindow(versions, game, _minecraftLauncher, loader) { Owner = this };
        if (selected is { Loader: not "Vanilla" }) window.SetDefaultName($"{selected.Loader} {game}");
        if (window.ShowDialog() != true) return;
        _retryProject = null;
        BeginModsOperation($"Создание: {window.InstanceName}");
        ModsCancelButton.IsEnabled = true;
        ModpackInstance? instance = null;
        bool success = false;
        string result;
        try
        {
            instance = CreateInstance(window.InstanceName, window.MinecraftVersion, window.ModLoader, window.LoaderVersion);
            _operationDirectory = instance.Directory;
            string installedVersion = await EnsureInstanceLoaderAsync(instance);
            SaveInstanceMetadata(instance, installedVersion);
            success = true;
            result = "Сборка создана. Можно устанавливать моды и ресурспаки или запускать игру.";
        }
        catch (OperationCanceledException) { result = "Установка отменена. Чтобы завершить её, выберите сборку и нажмите «Установить» внизу."; }
        catch (Exception ex) { result = $"Ошибка установки: {ex.Message}. Можно повторить кнопкой «Установить» внизу."; }
        if (instance != null) await RefreshInstanceSelectionAsync(instance.Directory);
        EndModsOperation(result, success);
    }

    private ModpackInstance CreateInstance(
        string name,
        string version,
        string modLoader = "Vanilla",
        string loaderVersion = "")
    {
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("Не выбрана версия Minecraft.");
        string directory = UniqueInstanceDirectory(name);

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "mods"));
        Directory.CreateDirectory(Path.Combine(directory, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(directory, "shaderpacks"));
        Directory.CreateDirectory(Path.Combine(directory, "saves"));

        var metadata = new InstanceMetadata
        {
            Name = name,
            Version = version,
            ModLoader = modLoader,
            LoaderVersion = loaderVersion
        };

        WriteMetadata(directory, metadata);

        return new ModpackInstance
        {
            Name = name,
            Directory = directory,
            Version = version,
            ModLoader = modLoader,
            LoaderVersion = loaderVersion
        };
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Trim().TrimEnd('.', ' ');
    }

    private async void InstallModsProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModsProjectItem project })
            await RunProjectInstallAsync(project, _modsProjectType, true);
    }

    private async Task InstallContentAsync(ModrinthProjectHit project)
    {
        var instance = GetSelectedInstance() ?? throw new InvalidOperationException("Выберите сборку.");
        var version = SelectCompatibleVersion(await GetProjectVersionsAsync(project.ProjectId), instance, _modsProjectType)
            ?? throw new InvalidOperationException("Совместимая версия не найдена.");
        await InstallContentVersionAsync(instance, _modsProjectType, version);
    }

    private async Task InstallModpackAsync(ModrinthProjectHit project)
    {
        var versions = await GetProjectVersionsAsync(project.ProjectId);
        var version = versions.OrderByDescending(v => v.DatePublished)
            .First(v => v.Files.Any(f => f.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)));
        await InstallModpackVersionAsync(project, version);
    }

    private async Task InstallModpackVersionAsync(ModrinthProjectHit project, ModrinthVersion version)
    {
        var file = version.Files.First(f => f.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase));
        string instancesRoot = Path.Combine(_minecraftPath.BasePath, "instances");
        Directory.CreateDirectory(instancesRoot);
        // Stable staging location + verified content cache make retries resumable.
        string staging = Path.Combine(instancesRoot, ".install-" + project.ProjectId + "-" + version.Id);
        string archive = Path.Combine(_minecraftPath.BasePath, "cache", "packs", version.Id + ".mrpack");
        ReportStage("Скачивание архива сборки");
        await Downloader.DownloadAsync(new[] { file.Url }, archive, file.Hashes, FileProgress("archive"), ModsToken);
        Directory.CreateDirectory(staging);
        ReportStage("Скачивание содержимого сборки");
        var index = await ExtractModpackAsync(archive, staging)
            ?? throw new InvalidDataException("В архиве отсутствует modrinth.index.json.");
        var instance = InstanceFromIndex(project.Title, staging, index);
        ReportStage($"Установка Minecraft {instance.Version} и {instance.ModLoader} {instance.LoaderVersion}");
        string installedVersion = await EnsureInstanceLoaderAsync(instance);
        ModsToken.ThrowIfCancellationRequested();
        WriteMetadata(staging, new InstanceMetadata
        {
            Name = project.Title, Version = instance.Version, ModLoader = instance.ModLoader,
            LoaderVersion = instance.LoaderVersion, LoaderVersionName = installedVersion,
            ProjectId = project.ProjectId, ProjectVersionId = version.Id
        });
        string destination = UniqueInstanceDirectory(project.Title);
        Directory.Move(staging, destination);
        _operationDirectory = destination;
        await RefreshInstanceSelectionAsync(destination);
    }

    private async Task<ModpackIndex?> ExtractModpackAsync(string archivePath, string instanceDirectory)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("modrinth.index.json")
            ?? throw new InvalidDataException("В архиве отсутствует modrinth.index.json.");
        using var stream = entry.Open();
        var index = await JsonSerializer.DeserializeAsync<ModpackIndex>(stream, cancellationToken: ModsToken)
            ?? throw new InvalidDataException("Некорректный индекс модпака.");
        if (index.FormatVersion != 1 || index.Game != "minecraft")
            throw new NotSupportedException("Неподдерживаемый формат модпака.");
        _ = InstanceFromIndex("validation", instanceDirectory, index);
        var files = index.Files.Where(f => f.Env?.GetValueOrDefault("client") != "unsupported").ToArray();
        // Validate ALL archive paths before the first write.
        foreach (var file in files) _ = SafeContentPath(instanceDirectory, file.Path);
        foreach (string prefix in new[] { "overrides/", "client-overrides/" })
            foreach (var item in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
                _ = SafeContentPath(instanceDirectory, item.FullName[prefix.Length..]);
        _transfers.Clear();
        _completedFiles = 0; _totalFiles = files.Length;
        _expectedBytes = files.Sum(f => f.FileSize);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = _settings.ParallelDownloads, CancellationToken = ModsToken },
            async (file, token) =>
            {
                string target = SafeContentPath(instanceDirectory, file.Path);
                // Progress<T> must be created on the UI thread.
                IProgress<TransferProgress>? progress = null;
                await Dispatcher.InvokeAsync(() => progress = FileProgress(file.Path));
                await Downloader.DownloadAsync(file.Downloads, target, file.Hashes, progress, token);
                await Dispatcher.InvokeAsync(() => { _completedFiles++; UpdateOperationSummary(); });
            });
        ReportStage("Распаковка конфигурации и клиентских файлов");
        foreach (string prefix in new[] { "overrides/", "client-overrides/" })
        foreach (var item in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
        {
            ModsToken.ThrowIfCancellationRequested();
            string target = SafeContentPath(instanceDirectory, item.FullName[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            item.ExtractToFile(target, true);
        }
        // Record Modrinth project/version IDs embedded in official CDN links.
        var installed = new Dictionary<string, InstalledContent>();
        foreach (var file in files)
        {
            foreach (string url in file.Downloads)
            {
                var match = Regex.Match(url, @"https://cdn\.modrinth\.com/data/([^/]+)/versions/([^/]+)/");
                if (!match.Success) continue;
                installed[match.Groups[1].Value] = new InstalledContent
                { Path = file.Path, VersionId = match.Groups[2].Value };
                break;
            }
        }
        await File.WriteAllTextAsync(Path.Combine(instanceDirectory, "teal-content.json"), JsonSerializer.Serialize(installed), ModsToken);
        return index;
    }

    private static ModpackInstance InstanceFromIndex(string name, string directory, ModpackIndex index)
    {
        if (!index.Dependencies.TryGetValue("minecraft", out var minecraft) || string.IsNullOrWhiteSpace(minecraft))
            throw new InvalidDataException("В модпаке не указана версия Minecraft.");
        var loaders = new Dictionary<string, string>
        {
            ["fabric-loader"] = "Fabric", ["forge"] = "Forge",
            ["neoforge"] = "NeoForge", ["quilt-loader"] = "Quilt"
        };
        if (index.Dependencies.Keys.Any(k => k != "minecraft" && !loaders.ContainsKey(k)))
            throw new NotSupportedException("Модпак требует неизвестный загрузчик.");
        var selected = index.Dependencies.Where(d => loaders.ContainsKey(d.Key)).ToList();
        if (selected.Count > 1) throw new InvalidDataException("Модпак содержит несколько загрузчиков.");
        return new ModpackInstance
        {
            Name = name, Directory = directory, Version = minecraft,
            ModLoader = selected.Count == 0 ? "Vanilla" : loaders[selected[0].Key],
            LoaderVersion = selected.Count == 0 ? "" : selected[0].Value
        };
    }

    private async Task<List<ModrinthVersion>> GetProjectVersionsAsync(string projectId)
    {
        string url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}/version";
        return await Http.GetFromJsonAsync<List<ModrinthVersion>>(url, ModsToken) ?? new List<ModrinthVersion>();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLauncherAsync();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installingProject) return;
        ApplySettingsControls(_settings);
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsControls(_settings);
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private async void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SettingsDefault_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsControls(new LauncherSettings());
    }

    private void SettingsJavaBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Java|javaw.exe;java.exe|Все файлы|*.*",
            Title = "Выберите Java"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _customJavaPath = dialog.FileName;
            SettingsJavaComboBox.Items.Clear();
            SettingsJavaComboBox.Items.Add("По умолчанию");
            SettingsJavaComboBox.Items.Add(dialog.FileName);
            SettingsJavaComboBox.SelectedIndex = 1;
        }
    }

    private void SettingsResetAll_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsControls(new LauncherSettings());
        SettingsSave_Click(sender, e);
    }

    private void SettingsBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = _minecraftPath.BasePath,
            Title = "Выберите папку игры"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SettingsDirectoryTextBox.Text = dialog.FolderName;
        }
    }

    private void SettingsMemorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SettingsMemoryValue is not null)
        {
            SettingsMemoryValue.Text = $"{Math.Round(e.NewValue):0} МБ";
        }
    }

    private void ToggleMaximize()
    {
        if (_isMaximized)
        {
            _isMaximized = false;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
        }
        else
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);

            Rect workArea = SystemParameters.WorkArea;
            Left = workArea.Left;
            Top = workArea.Top;
            Width = workArea.Width;
            Height = workArea.Height;
            _isMaximized = true;
        }

        MaximizeRestoreButton.Content = _isMaximized ? "❐" : "□";
        MaximizeRestoreButton.ToolTip = _isMaximized ? "Восстановить" : "Развернуть";
    }
}

public sealed class PopularModpackItem
{
    public string Description { get; init; } = "";
    public string? IconUrl { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string DownloadsText { get; init; } = string.Empty;
    public string ProjectUrl { get; init; } = string.Empty;
}

public sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")]
    public List<ModrinthSearchHit> Hits { get; init; } = new();
}

public sealed class ModrinthSearchHit
{
    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }
    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    [JsonPropertyName("downloads")]
    public long Downloads { get; init; }
}

public sealed class MinecraftVersionItem
{
    public MVersionType VersionType { get; init; } = MVersionType.Release;
    public string Loader { get; init; } = "Vanilla";
    public string GameVersion { get; init; } = "";
    public string VersionGroup => Instance != null ? "Мои сборки" : Loader != "Vanilla" ? Loader : VersionType switch
    { MVersionType.Snapshot => "Снапшоты", MVersionType.OldBeta => "Бета", MVersionType.OldAlpha => "Альфа", _ => "Minecraft" };
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ModpackInstance? Instance { get; init; }
}

public sealed class ModrinthProjectSearchResponse
{
    [JsonPropertyName("hits")]
    public List<ModrinthProjectHit> Hits { get; init; } = new();

    [JsonPropertyName("total_hits")]
    public long TotalHits { get; init; }
}

public sealed class ModrinthProjectHit
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    [JsonPropertyName("downloads")]
    public long Downloads { get; init; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    [JsonPropertyName("date_modified")]
    public DateTimeOffset DateModified { get; init; }

    [JsonPropertyName("versions")]
    public List<string> Versions { get; init; } = new();
}

public sealed class ModsProjectItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private string _actionText = "Установить";
    private bool _canInstall = true;
    public string? InstalledDirectory { get; set; }
    public string ActionText { get => _actionText; set { _actionText = value; PropertyChanged?.Invoke(this, new(nameof(ActionText))); } }
    public bool CanInstall { get => _canInstall; set { _canInstall = value; PropertyChanged?.Invoke(this, new(nameof(CanInstall))); } }
    public ModrinthProjectHit Project { get; init; } = new();
    public string Title => Project.Title;
    public string Description => Project.Description;
    public string AuthorText => $"Автор: {Project.Author}";
    public string? IconUrl => Project.IconUrl;
    public string DownloadsText => $"Загрузки: {FormatCount(Project.Downloads)}";
    public string UpdatedText => $"Обновлено: {Project.DateModified.UtcDateTime.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("ru-RU"))}";
    public string GameVersionText => $"Minecraft: {string.Join(", ", Project.Versions.TakeLast(3).Reverse())}";

    private static string FormatCount(long count)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo("ru-RU");

        if (count >= 1_000_000)
        {
            return $"{(count / 1_000_000d).ToString("0.#", culture)} млн";
        }

        if (count >= 1_000)
        {
            return $"{(count / 1_000d).ToString("0.#", culture)} тыс.";
        }

        return count.ToString(culture);
    }
}

public sealed class ModpackInstance
{
    public string ProjectId { get; init; } = "";
    public string ProjectVersionId { get; init; } = "";
    public string Name { get; init; } = string.Empty;
    public string Directory { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ModLoader { get; init; } = string.Empty;
    public string LoaderVersion { get; init; } = string.Empty;
    public string LoaderVersionName { get; init; } = string.Empty;
    public bool IsPlaceholder { get; init; }

    public override string ToString() => Name;
}

public sealed class InstanceMetadata
{
    public string ProjectId { get; init; } = "";
    public string ProjectVersionId { get; init; } = "";
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ModLoader { get; init; } = string.Empty;
    public string LoaderVersion { get; init; } = string.Empty;
    public string LoaderVersionName { get; init; } = string.Empty;
}

public sealed class ModpackIndex
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }
    [JsonPropertyName("game")]
    public string Game { get; init; } = "";
    [JsonPropertyName("files")]
    public List<ModpackIndexFile> Files { get; init; } = new();

    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; init; } = new();
}

public sealed class ModpackIndexFile
{
    [JsonPropertyName("fileSize")]
    public long FileSize { get; init; }
    [JsonPropertyName("hashes")]
    public Dictionary<string, string> Hashes { get; init; } = new();
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("downloads")]
    public List<string> Downloads { get; init; } = new();
}

public sealed class ModrinthVersionFile
{
    [JsonPropertyName("hashes")]
    public Dictionary<string, string> Hashes { get; init; } = new();
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; init; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool Primary { get; init; }
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("version_type")]
    public string VersionType { get; init; } = "release";
    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = "";
    [JsonPropertyName("dependencies")]
    public List<ModrinthDependency> Dependencies { get; init; } = new();
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; init; } = string.Empty;

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; init; } = new();

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; init; } = new();

    [JsonPropertyName("date_published")]
    public DateTimeOffset DatePublished { get; init; }

    [JsonPropertyName("files")]
    public List<ModrinthVersionFile> Files { get; init; } = new();
}

public sealed class MinecraftUpdateItem
{
    public string? ImageUrl { get; init; }
    public string? ThumbnailPath { get; set; }
    public string Title { get; init; } = string.Empty;
    public string DateText { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed class MinecraftUpdatesCache
{
    public DateTimeOffset FetchedAt { get; init; }
    public List<MinecraftUpdateItem> Items { get; init; } = new();
}

public sealed class MojangPatchNotesResponse
{
    [JsonPropertyName("entries")]
    public List<MojangPatchNote> Entries { get; init; } = new();
}

public sealed class MojangPatchNote
{
    [JsonPropertyName("image")]
    public MojangNewsImage? Image { get; init; }
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; init; }
}

public sealed class MojangNewsImage
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed class WikiExtractResponse
{
    [JsonPropertyName("query")]
    public WikiQuery? Query { get; init; }
}

public sealed class WikiQuery
{
    [JsonPropertyName("pages")]
    public Dictionary<string, WikiPage> Pages { get; init; } = new();
}

public sealed class WikiPage
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("extract")]
    public string Extract { get; init; } = string.Empty;
}
