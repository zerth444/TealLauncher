using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CmlLib.Core;
using CmlLib.Core.VersionMetadata;

namespace TLauncherRemake;

public partial class MainWindow
{
    private LauncherSettings _settings = new();
    private bool _settingsInitialized;

    private void InitializeSettings()
    {
        _settings = LauncherPreferences.Load(_preferencesPath).Settings;
        _minecraftPath = new MinecraftPath(_settings.GameDirectory);
        ApplySettingsControls(_settings);
        _settingsInitialized = true;
        CollectionViewSource.GetDefaultView(_minecraftVersions).Filter = value => value is MinecraftVersionItem v && IsVersionVisible(v, _settings);
    }

    private void ApplySettingsControls(LauncherSettings s)
    {
        bool ready = _settingsInitialized;
        _settingsInitialized = false;
        SettingsDirectoryTextBox.Text = s.GameDirectory;
        SettingsWidthTextBox.Text = s.Width.ToString(); SettingsHeightTextBox.Text = s.Height.ToString();
        SettingsFullscreenCheckBox.IsChecked = s.Fullscreen;
        SettingsMemorySlider.Value = Math.Clamp(s.MemoryMb, 512, 16307);
        SettingsArgumentsTextBox.Text = s.Arguments;
        _customJavaPath = s.JavaPath;
        SettingsJavaComboBox.Items.Clear(); SettingsJavaComboBox.Items.Add("По умолчанию");
        if (!string.IsNullOrEmpty(s.JavaPath)) SettingsJavaComboBox.Items.Add(s.JavaPath);
        SettingsJavaComboBox.SelectedIndex = string.IsNullOrEmpty(s.JavaPath) ? 0 : 1;
        SettingsSnapshotsCheckBox.IsChecked = s.ShowSnapshots; SettingsModifiedCheckBox.IsChecked = s.ShowModified;
        SettingsOldReleasesCheckBox.IsChecked = s.ShowOldReleases; SettingsBetaCheckBox.IsChecked = s.ShowBeta;
        SettingsAlphaCheckBox.IsChecked = s.ShowAlpha; SettingsRussianCheckBox.IsChecked = s.RussianOnFirstLaunch;
        SettingsLaunchBehaviorCombo.SelectedIndex = s.MinimizeOnLaunch ? 1 : 0;
        SettingsParallelCombo.SelectedIndex = s.ParallelDownloads == 2 ? 0 : s.ParallelDownloads == 8 ? 2 : 1;
        SettingsBackupsCheckBox.IsChecked = s.Backups;
        SettingsBackupLimitTextBox.Text = s.BackupLimitMb.ToString(); SettingsBackupDaysTextBox.Text = s.BackupRetentionDays.ToString();
        _settingsInitialized = ready;
    }

    internal static bool IsVersionVisible(MinecraftVersionItem item, LauncherSettings settings)
    {
        if (item.Instance != null || item.Loader != "Vanilla") return settings.ShowModified;
        return item.VersionType switch
        {
            MVersionType.Snapshot => settings.ShowSnapshots,
            MVersionType.OldBeta => settings.ShowBeta,
            MVersionType.OldAlpha => settings.ShowAlpha,
            _ => settings.ShowOldReleases || !Version.TryParse(item.Name, out var number) || number > new Version(1, 5, 2)
        };
    }

    private void VersionVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsInitialized) return;
        _settings.ShowSnapshots = SettingsSnapshotsCheckBox.IsChecked == true;
        _settings.ShowModified = SettingsModifiedCheckBox.IsChecked == true;
        _settings.ShowOldReleases = SettingsOldReleasesCheckBox.IsChecked == true;
        _settings.ShowBeta = SettingsBetaCheckBox.IsChecked == true;
        _settings.ShowAlpha = SettingsAlphaCheckBox.IsChecked == true;
        RefreshVersionFilter();
        PersistSettings();
    }

    private void RefreshVersionFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_minecraftVersions);
        view.Refresh();
        if (VersionComboBox.SelectedItem is not MinecraftVersionItem selected || !IsVersionVisible(selected, _settings))
            VersionComboBox.SelectedItem = view.Cast<MinecraftVersionItem>().FirstOrDefault();
        UpdateLaunchButtonState();
    }

    private void PersistSettings()
    {
        try { LauncherPreferences.Save(_preferencesPath, new LauncherPreferences { Nickname = EmailTextBox.Text.Trim(), Settings = _settings }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "Не удалось сохранить настройки: " + ex.Message); }
    }

    private LauncherSettings ReadSettingsControls()
    {
        if (!int.TryParse(SettingsWidthTextBox.Text, out int width) || width < 320 || width > 16384
            || !int.TryParse(SettingsHeightTextBox.Text, out int height) || height < 240 || height > 16384)
            throw new InvalidOperationException("Укажите размер окна от 320×240 до 16384×16384.");
        if (!int.TryParse(SettingsBackupLimitTextBox.Text, out int limit) || limit < 1 || limit > 1000000
            || !int.TryParse(SettingsBackupDaysTextBox.Text, out int days) || days < 1 || days > 3650)
            throw new InvalidOperationException("Проверьте лимит резервных копий и срок хранения (1–3650 дней).");
        string directory = Path.GetFullPath(SettingsDirectoryTextBox.Text.Trim());
        Directory.CreateDirectory(directory);
        string? java = SettingsJavaComboBox.SelectedIndex == 1 ? _customJavaPath : null;
        if (java != null && !File.Exists(java)) throw new FileNotFoundException("Выбранный файл Java не найден.");
        return new LauncherSettings
        {
            GameDirectory = directory, Width = width, Height = height, Fullscreen = SettingsFullscreenCheckBox.IsChecked == true,
            MemoryMb = (int)SettingsMemorySlider.Value, Arguments = SettingsArgumentsTextBox.Text.Trim(), JavaPath = java,
            ShowSnapshots = SettingsSnapshotsCheckBox.IsChecked == true, ShowModified = SettingsModifiedCheckBox.IsChecked == true,
            ShowOldReleases = SettingsOldReleasesCheckBox.IsChecked == true, ShowBeta = SettingsBetaCheckBox.IsChecked == true,
            ShowAlpha = SettingsAlphaCheckBox.IsChecked == true, RussianOnFirstLaunch = SettingsRussianCheckBox.IsChecked == true,
            MinimizeOnLaunch = SettingsLaunchBehaviorCombo.SelectedIndex == 1,
            ParallelDownloads = SettingsParallelCombo.SelectedIndex switch { 0 => 2, 2 => 8, _ => 4 },
            Backups = SettingsBackupsCheckBox.IsChecked == true, BackupLimitMb = limit, BackupRetentionDays = days
        };
    }

    private async Task SaveSettingsAsync()
    {
        var next = ReadSettingsControls();
        bool changed = !string.Equals(next.GameDirectory, _minecraftPath.BasePath, StringComparison.OrdinalIgnoreCase);
        if (changed && (_isLaunching || _gameProcess is { HasExited: false })) throw new InvalidOperationException("Закройте игру перед сменой папки Minecraft.");
        _settings = next;
        if (changed) { _minecraftPath = new MinecraftPath(next.GameDirectory); InitializeMinecraftLauncher(); await LoadMinecraftVersionsAsync(); }
        RefreshVersionFilter(); PersistSettings(); SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void SettingsSupportLog_Click(object sender, RoutedEventArgs e)
    {
        string? log = _operationLog;
        string directory = (VersionComboBox.SelectedItem as MinecraftVersionItem)?.Instance?.Directory ?? _minecraftPath.BasePath;
        string latest = Path.Combine(directory, "logs", "latest.log");
        if (File.Exists(latest)) log = latest;
        if (log != null && File.Exists(log)) OpenUrl(log);
        else { Directory.CreateDirectory(Path.Combine(directory, "logs")); OpenUrl(Path.Combine(directory, "logs")); }
    }
}
