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
using System.Windows.Input;

namespace TLauncherRemake;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly ObservableCollection<PopularModpackItem> _popularModpacks = new();
    private readonly ObservableCollection<MinecraftUpdateItem> _minecraftUpdates = new();
    private Rect _restoreBounds;
    private bool _isMaximized;

    public MainWindow()
    {
        InitializeComponent();
        PopularModpacksList.ItemsSource = _popularModpacks;
        MinecraftUpdatesList.ItemsSource = _minecraftUpdates;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Task.WhenAll(LoadPopularModpacksAsync(), LoadMinecraftUpdatesAsync());
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
