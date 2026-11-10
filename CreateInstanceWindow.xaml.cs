using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;

namespace TealLauncher;

public partial class CreateInstanceWindow : Window
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly MinecraftLauncher _launcher;
    private int _loadGeneration;

    public string InstanceName { get; private set; } = string.Empty;
    public string MinecraftVersion { get; private set; } = string.Empty;
    public string ModLoader { get; private set; } = string.Empty;
    public string LoaderVersion { get; private set; } = string.Empty;

    public CreateInstanceWindow(
        IEnumerable<string> versions,
        string defaultVersion,
        MinecraftLauncher launcher,
        string initialLoader = "Fabric")
    {
        _launcher = launcher;
        InitializeComponent();

        foreach (string version in versions)
        {
            VersionComboBox.Items.Add(version);
        }

        if (VersionComboBox.Items.Count == 0)
        {
            VersionComboBox.Items.Add(defaultVersion);
        }

        VersionComboBox.SelectedItem = VersionComboBox.Items.Contains(defaultVersion)
            ? defaultVersion
            : VersionComboBox.Items[0];

        LoaderComboBox.SelectedItem = LoaderComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Content?.ToString() == initialLoader)
            ?? LoaderComboBox.Items[0];
        VersionComboBox.SelectionChanged += VersionComboBox_SelectionChanged;
        Loaded += async (_, _) => await RefreshLoaderVersionsAsync();
    }

    public void SetDefaultName(string name) => InstanceNameTextBox.Text = name;

    private async void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RefreshLoaderVersionsAsync();
    }

    private async void LoaderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RefreshLoaderVersionsAsync();
    }

    private async Task RefreshLoaderVersionsAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        string minecraftVersion = VersionComboBox.SelectedItem?.ToString() ?? string.Empty;
        string loader = GetSelectedLoader();
        int generation = ++_loadGeneration;

        LoaderVersionComboBox.Items.Clear();
        CreateButton.IsEnabled = false;
        StatusText.Text = "Загрузка версий модлоадера...";
        LoaderVersionComboBox.IsEnabled = false;

        if (loader == "Vanilla")
        {
            LoaderVersionComboBox.Items.Add("—");
            LoaderVersionComboBox.SelectedIndex = 0;
            LoaderVersionComboBox.IsEnabled = true;
            StatusText.Text = string.Empty;
            CreateButton.IsEnabled = true;
            return;
        }

        try
        {
            if (loader is "Fabric" or "Quilt")
            {
                string host = loader == "Fabric" ? "meta.fabricmc.net" : "meta.quiltmc.org";
                string api = loader == "Fabric" ? "v2" : "v3";
                using var document = JsonDocument.Parse(await Http.GetStringAsync(
                    $"https://{host}/{api}/versions/loader/{Uri.EscapeDataString(minecraftVersion)}"));

                if (generation != _loadGeneration)
                {
                    return;
                }

                foreach (var version in document.RootElement.EnumerateArray())
                {
                    string? name = version.GetProperty("loader").GetProperty("version").GetString();
                    if (name != null) LoaderVersionComboBox.Items.Add(name);
                }
            }
            else if (loader == "NeoForge")
            {
                var versions = await new NeoForgeInstaller(_launcher).GetNeoForgeVersions(minecraftVersion);
                if (generation != _loadGeneration) return;
                foreach (var version in versions)
                    LoaderVersionComboBox.Items.Add(version.NeoForgeVersionName);
            }
            else if (loader == "Forge")
            {
                var forgeInstaller = new ForgeInstaller(_launcher);
                var versions = await forgeInstaller.GetForgeVersions(minecraftVersion);

                if (generation != _loadGeneration)
                {
                    return;
                }

                foreach (var version in versions
                    .OrderByDescending(item => item.IsRecommendedVersion)
                    .ThenByDescending(item => item.IsLatestVersion)
                    .ThenByDescending(item => item.Time))
                {
                    LoaderVersionComboBox.Items.Add(version.ForgeVersionName);
                }
            }

            if (generation != _loadGeneration)
            {
                return;
            }

            if (LoaderVersionComboBox.Items.Count > 0)
            {
                LoaderVersionComboBox.SelectedIndex = 0;
                StatusText.Text = string.Empty;
            }
            else
            {
                StatusText.Text = $"Для Minecraft {minecraftVersion} нет доступных версий {loader}.";
            }
        }
        catch (Exception ex)
        {
            if (generation == _loadGeneration)
            {
                StatusText.Text = $"Не удалось получить версии {loader}: {ex.Message}";
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                LoaderVersionComboBox.IsEnabled = true;
                CreateButton.IsEnabled = LoaderVersionComboBox.SelectedItem != null;
            }
        }
    }

    private string GetSelectedLoader()
    {
        return (LoaderComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Fabric";
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        string name = InstanceNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(
                this,
                "Введите название модпака.",
                "Создать модпак",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            InstanceNameTextBox.Focus();
            return;
        }

        string loader = GetSelectedLoader();
        string loaderVersion = LoaderVersionComboBox.SelectedItem?.ToString() ?? string.Empty;

        if (loader != "Vanilla" && string.IsNullOrWhiteSpace(loaderVersion))
        {
            MessageBox.Show(
                this,
                "Выберите версию модлоадера.",
                "Создать модпак",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        InstanceName = name;
        MinecraftVersion = VersionComboBox.SelectedItem?.ToString() ?? "26.3";
        ModLoader = loader;
        LoaderVersion = loader == "Vanilla" ? string.Empty : loaderVersion;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TealLauncher/1.0");
        return client;
    }
}

public sealed class FabricLoaderEntry
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("stable")]
    public bool Stable { get; init; }
}
