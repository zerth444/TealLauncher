using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TLauncherRemake;

public partial class MainWindow
{
    private ModrinthProjectHit? _pageProject;
    private InstalledContentRow? _pageRow;
    private string _pageType = "mod";
    private int _pageGeneration;

    private async void ModsProjectDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModsProjectItem project }) await ShowProjectPageAsync(project.Project, _modsProjectType, null);
    }

    private async void InstalledContentName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledContentRow row }) return;
        string id = "";
        string manifest = Path.Combine(row.Root, "teal-content.json");
        if (File.Exists(manifest))
        {
            try
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, InstalledContent>>(await File.ReadAllTextAsync(manifest));
                id = entries?.FirstOrDefault(p => BaseContentPath(SafeContentPath(row.Root, p.Value.Path)).Equals(BaseContentPath(row.Path), StringComparison.OrdinalIgnoreCase)).Key ?? "";
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
        }
        await ShowProjectPageAsync(string.IsNullOrEmpty(id) ? null : new ModrinthProjectHit { ProjectId = id, Title = row.Name }, row.Type, row);
    }

    private async Task ShowProjectPageAsync(ModrinthProjectHit? project, string type, InstalledContentRow? row)
    {
        int generation = ++_pageGeneration;
        _pageProject = project; _pageType = type; _pageRow = row;
        ModsDetailsPanel.Visibility = Visibility.Visible;
        ModsScrollViewer.Visibility = Visibility.Collapsed;
        ModsStatusText.Visibility = Visibility.Collapsed;
        ModsDetailsTitle.Text = project?.Title ?? row?.Name ?? "Проект";
        ModsDetailsSubtitle.Text = row?.Filename ?? project?.Description ?? "";
        SetProjectDescription(project == null ? "Локальный файл. Описание Modrinth для него не привязано.\n\nМожно включить, выключить или удалить его из сборки." : "Загрузка описания...");
        RefreshProjectPageState();
        if (project == null) return;
        try
        {
            var details = await Http.GetFromJsonAsync<ModrinthProjectDetails>($"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(project.ProjectId)}");
            if (generation != _pageGeneration || details == null) return;
            _pageProject = new ModrinthProjectHit { ProjectId = details.Id, Title = details.Title, Description = details.Description, Slug = details.Slug };
            ModsDetailsTitle.Text = details.Title;
            ModsDetailsSubtitle.Text = details.Description;
            SetProjectDescription(string.IsNullOrWhiteSpace(details.Body) ? details.Description : details.Body);
            RefreshProjectPageState();
        }
        catch (Exception ex)
        {
            if (generation == _pageGeneration) SetProjectDescription((project.Description ?? "") + "\n\nНе удалось загрузить полное описание: " + ex.Message);
        }
    }

    private void SetProjectDescription(string markdown)
    {
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 13, PagePadding = new Thickness(0) };
        foreach (string source in markdown.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            string text = Regex.Replace(source, "<[^>]+>", "");
            if (Regex.IsMatch(text.Trim(), @"^[-*_]{3,}$")) continue;
            text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*\)", "$1");
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
            text = text.Replace("**", "").Replace("__", "");
            if (text.StartsWith("* ") || text.StartsWith("- ")) text = "• " + text[2..];
            else text = text.Trim('*');
            bool heading = text.StartsWith('#');
            var paragraph = new Paragraph(new Run(heading ? text.TrimStart('#', ' ') : text)) { Margin = new Thickness(0, heading ? 12 : 3, 0, 6) };
            if (heading) { paragraph.FontWeight = FontWeights.SemiBold; paragraph.FontSize = 17; }
            document.Blocks.Add(paragraph);
        }
        ModsDetailsBody.Document = document;
    }

    private static string BaseContentPath(string path) => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path;

    private InstalledContentRow? FindPageInstalledRow()
    {
        if (_pageRow != null)
        {
            string basePath = BaseContentPath(_pageRow.Path);
            string actual = File.Exists(basePath) || Directory.Exists(basePath) ? basePath : basePath + ".disabled";
            if (File.Exists(actual) || Directory.Exists(actual))
            { _pageRow.Path = actual; return _pageRow; }
        }
        if (_pageProject == null || GetSelectedInstance() is not { } instance) return null;
        try
        {
            string manifest = Path.Combine(instance.Directory, "teal-content.json");
            if (!File.Exists(manifest)) return null;
            var entries = JsonSerializer.Deserialize<Dictionary<string, InstalledContent>>(File.ReadAllText(manifest));
            if (entries == null || !entries.TryGetValue(_pageProject.ProjectId, out var content) || content == null) return null;
            string path = SafeContentPath(instance.Directory, content.Path);
            if (!File.Exists(path) && !Directory.Exists(path)) path += ".disabled";
            return File.Exists(path) || Directory.Exists(path) ? new InstalledContentRow { Path = path, Root = instance.Directory, Name = _pageProject.Title, Type = _pageType, Filename = Path.GetFileName(path) } : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return null; }
    }

    private void RefreshProjectPageState()
    {
        if (ModsDetailsPanel?.Visibility != Visibility.Visible) return;
        var installed = FindPageInstalledRow();
        bool editable = CanChangeInstalledContent();
        ModsDetailsRemove.Visibility = installed == null ? Visibility.Collapsed : Visibility.Visible;
        ModsDetailsRemove.IsEnabled = editable;
        ModsDetailsToggle.Visibility = installed == null ? Visibility.Collapsed : Visibility.Visible;
        ModsDetailsToggle.IsEnabled = editable;
        ModsDetailsToggle.Content = installed == null || !InstanceContentManager.IsEnabled(installed.Root, installed.Path) ? "Включить" : "Выключить";
        ModsDetailsInstall.Visibility = _pageProject == null ? Visibility.Collapsed : Visibility.Visible;
        ModsDetailsInstall.IsEnabled = editable && (_pageType == "modpack" || GetSelectedInstance() != null);
        ModsDetailsInstall.Content = installed == null ? "Установить" : "Выбрать другую версию";
    }

    private void CloseProjectPage_Click(object sender, RoutedEventArgs e)
    { ++_pageGeneration; ModsDetailsPanel.Visibility = Visibility.Collapsed; ModsScrollViewer.Visibility = Visibility.Visible; _pageRow = null; _pageProject = null; }

    private async void ProjectPageInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_pageProject == null) return;
        await RunProjectInstallAsync(new ModsProjectItem { Project = _pageProject }, _pageType, true, true);
        RefreshProjectPageState();
    }

    private void ProjectPageToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeInstalledContent() || FindPageInstalledRow() is not { } row) return;
        try
        {
            row.Path = InstanceContentManager.SetEnabled(row.Root, row.Path, !InstanceContentManager.IsEnabled(row.Root, row.Path));
            _pageRow = row; UpdateProjectStates(); RefreshProjectPageState();
        }
        catch (Exception ex) { ModsDetailsSubtitle.Text = ex.Message; }
    }

    internal static void RemoveInstalledContent(InstalledContentRow row)
    {
        string original = BaseContentPath(row.Path);
        string disabled = InstanceContentManager.SetEnabled(row.Root, row.Path, false);
        if (Directory.Exists(disabled)) Directory.Delete(disabled, true); else File.Delete(disabled);
        string manifest = Path.Combine(row.Root, "teal-content.json");
        if (!File.Exists(manifest)) return;
        var entries = JsonSerializer.Deserialize<Dictionary<string, InstalledContent>>(File.ReadAllText(manifest)) ?? new();
        foreach (string id in entries.Where(p => SafeContentPath(row.Root, p.Value.Path).Equals(original, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray()) entries.Remove(id);
        string temporary = manifest + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries)); File.Move(temporary, manifest, true);
    }

    private void ProjectPageRemove_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeInstalledContent() || FindPageInstalledRow() is not { } row) return;
        if (MessageBox.Show(this, $"Удалить «{row.Name}» из сборки? Зависимые моды могут перестать работать.", "Удаление содержимого", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { RemoveInstalledContent(row); _pageRow = null; ModsDetailsSubtitle.Text = "Удалено из сборки."; }
        catch (Exception ex) { ModsDetailsSubtitle.Text = "Не удалось удалить: " + ex.Message; }
        UpdateProjectStates(); RefreshProjectPageState();
    }
}

public sealed class ModrinthProjectDetails
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("body")] public string Body { get; init; } = "";
}
