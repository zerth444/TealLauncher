using System.Windows;

namespace TealLauncher;

public partial class MainWindow
{
    private bool _refreshing;

    private async Task RefreshLauncherAsync()
    {
        if (_isLaunching || _refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        RefreshButton.ToolTip = "Обновление...";
        SettingsOverlay.Visibility = Visibility.Collapsed;
        try
        {
            InitializeMinecraftLauncher();
            await Task.WhenAll(LoadPopularModpacksAsync(), LoadMinecraftUpdatesAsync(), LoadMinecraftVersionsAsync());
            if (ModsOverlay.Visibility == Visibility.Visible)
            {
                await LoadModsProjectsAsync();
                UpdateProjectStates();
                if (ModsDetailsPanel.Visibility == Visibility.Visible && _pageProject != null)
                    await ShowProjectPageAsync(_pageProject, _pageType, FindPageInstalledRow());
            }
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
            RefreshButton.ToolTip = "Обновить интерфейс";
            UpdateLaunchButtonState();
        }
    }
}
