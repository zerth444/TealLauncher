using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    private Rect _restoreBounds;
    private bool _isMaximized;
    private bool _isLaunching;
    private Process? _gameProcess;
    private string? _customJavaPath;
    private MSession? _authenticatedSession;
    private JELoginHandler? _loginHandler;

    public MainWindow()
    {
        InitializeComponent();
        PopularModpacksList.ItemsSource = _popularModpacks;
        MinecraftUpdatesList.ItemsSource = _minecraftUpdates;
        VersionComboBox.ItemsSource = _minecraftVersions;

        InitializeMinecraftLauncher();
        UpdateLaunchButtonState();
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

    private void PopularModpackRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            OpenUrl(url);
        }
    }

    private void OpenModrinthModpacks_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://modrinth.com/modpacks");
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
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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

        return new MinecraftUpdateItem
        {
            Title = NormalizeMinecraftTitle(note),
            DateText = FormatMinecraftDate(note.Date),
            Summary = summary,
            Url = wikiUrl
        };
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
        VersionComboBox.IsEnabled = false;

        try
        {
            var allVersions = await _minecraftLauncher.GetAllVersionsAsync();
            List<IVersionMetadata> releases = allVersions
                .Where(version => version.GetVersionType() == MVersionType.Release)
                .GroupBy(version => version.Name)
                .Select(group => group.First())
                .OrderByDescending(version => version.ReleaseTime)
                .ToList();

            int latestIndex = releases.FindIndex(version => version.Name == "26.3");
            int oldestIndex = releases.FindIndex(version => version.Name == "1.0");

            if (latestIndex < 0)
            {
                latestIndex = 0;
            }

            if (oldestIndex < latestIndex)
            {
                oldestIndex = releases.Count - 1;
            }

            List<IVersionMetadata> allowedVersions = releases
                .Skip(latestIndex)
                .Take(oldestIndex - latestIndex + 1)
                .ToList();

            _minecraftVersions.Clear();

            foreach (IVersionMetadata version in allowedVersions)
            {
                _minecraftVersions.Add(new MinecraftVersionItem
                {
                    Name = version.Name,
                    DisplayName = _minecraftVersions.Count == 0
                        ? $"Новейшая версия {version.Name}"
                        : $"Версия {version.Name}"
                });
            }

            VersionComboBox.SelectedIndex = _minecraftVersions.Count > 0 ? 0 : -1;
            UpdateLaunchButtonState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Не удалось загрузить список версий Minecraft:\n{ex.Message}",
                "Teal Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
            process.Start();
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
        }
        catch
        {
        }

        if (ReferenceEquals(_gameProcess, process))
        {
            _gameProcess = null;
        }

        UpdateLaunchButtonState();
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

        if (UpdateClientCheckBox.IsChecked == true)
        {
            SetLaunchButton("Переустановить", true);
            return;
        }

        SetLaunchButton(
            IsVersionInstalled(selectedVersion.Name) ? "Играть" : "Установить",
            true);
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
        Directory.CreateDirectory(_minecraftPath.BasePath);
        Directory.CreateDirectory(Path.Combine(_minecraftPath.BasePath, "mods"));
        Directory.CreateDirectory(Path.Combine(_minecraftPath.BasePath, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(_minecraftPath.BasePath, "shaderpacks"));

        OpenUrl(_minecraftPath.BasePath);
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/");
    }

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

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLaunching)
        {
            return;
        }

        SettingsOverlay.Visibility = Visibility.Collapsed;

        await Task.WhenAll(
            LoadPopularModpacksAsync(),
            LoadMinecraftUpdatesAsync(),
            LoadMinecraftVersionsAsync());

        UpdateLaunchButtonState();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsDirectoryTextBox.Text = _minecraftPath.BasePath;
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private async void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        string selectedPath = SettingsDirectoryTextBox.Text.Trim();

        if (!string.IsNullOrWhiteSpace(selectedPath)
            && !string.Equals(selectedPath, _minecraftPath.BasePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _minecraftPath = new MinecraftPath(selectedPath);
                InitializeMinecraftLauncher();
                await LoadMinecraftVersionsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Не удалось изменить папку игры:\n{ex.Message}",
                    "Teal Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }
        }

        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void SettingsDefault_Click(object sender, RoutedEventArgs e)
    {
        SettingsWidthTextBox.Text = "925";
        SettingsHeightTextBox.Text = "530";
        SettingsFullscreenCheckBox.IsChecked = false;
        SettingsArgumentsTextBox.Text = string.Empty;
        SettingsMemorySlider.Value = 10871;

        _customJavaPath = null;
        SettingsJavaComboBox.Items.Clear();
        SettingsJavaComboBox.Items.Add("По умолчанию");
        SettingsJavaComboBox.SelectedIndex = 0;
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
        SettingsDefault_Click(sender, e);
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
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class MinecraftUpdateItem
{
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
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; init; }
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
