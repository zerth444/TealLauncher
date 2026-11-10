using System.IO;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace TealLauncher;

public partial class MainWindow
{
    private CmlLib.Core.Installer.Forge.ForgeInstallOptions CreateForgeInstallOptions()
        => new()
        {
            CancellationToken = ModsToken,
            InstallerOutput = new Progress<string>(line => { if (_operationLog != null) File.AppendAllText(_operationLog, line + "\n"); }),
            JavaPath = SettingsJavaComboBox.SelectedIndex == 1 && File.Exists(_customJavaPath)
                ? _customJavaPath : null
        };

    internal static string SafeContentPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is ".." or "."
                || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"Недопустимый путь: {relative}");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            || result.Equals(Path.Combine(fullRoot, "instance.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Недопустимый путь: {relative}");
        return result;
    }

    internal static Task DownloadVerifiedAsync(IEnumerable<string> urls, string target, Dictionary<string, string> hashes)
        => new ContentDownloader(Path.Combine(Path.GetTempPath(), "TealLauncher-verified-cache"))
            .DownloadAsync(urls, target, hashes);

    internal static async Task VerifyHashesAsync(string path, Dictionary<string, string> hashes)
    {
        bool verified = false;
        foreach (var (algorithm, expected) in hashes)
        {
            if (algorithm is not ("sha512" or "sha1")) continue;
            using var input = File.OpenRead(path);
            byte[] hash = algorithm == "sha512"
                ? await SHA512.HashDataAsync(input) : await SHA1.HashDataAsync(input);
            if (!Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Контрольная сумма {Path.GetFileName(path)} не совпадает.");
            verified = true;
        }
        if (!verified) throw new InvalidDataException("Сервер не предоставил контрольную сумму файла.");
    }

    internal static ModrinthVersion? SelectCompatibleVersion(IEnumerable<ModrinthVersion> versions, ModpackInstance instance, string type)
        => versions.Where(v => IsCompatible(v, instance, type))
            .OrderByDescending(v => v.DatePublished).FirstOrDefault();

    private static bool IsCompatible(ModrinthVersion version, ModpackInstance instance, string type)
        => version.Files.Count > 0 && version.GameVersions.Contains(instance.Version)
            && (type != "mod" || version.Loaders.Contains(instance.ModLoader.ToLowerInvariant()))
            && (type != "resourcepack" || version.Loaders.Contains("minecraft"))
            && (type != "shader" || version.Files.Any(f => f.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            && (type != "datapack" || version.Loaders.Contains("datapack"));

    private async Task ResolveContentAsync(ModrinthVersion version, ModpackInstance instance, string type, string folder,
        Dictionary<string, (ModrinthVersion Version, string Folder)> plan, HashSet<string> visiting)
    {
        if (plan.ContainsKey(version.Id) || !visiting.Add(version.Id)) return;
        if (!IsCompatible(version, instance, type))
            throw new InvalidOperationException($"Зависимость {version.Name} несовместима с {instance.Version}, {instance.ModLoader}.");
        foreach (var dependency in version.Dependencies.Where(d => d.DependencyType == "required"))
        {
            ModrinthVersion? required;
            if (!string.IsNullOrWhiteSpace(dependency.VersionId))
                required = await Http.GetFromJsonAsync<ModrinthVersion>(
                    $"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(dependency.VersionId)}");
            else if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
                required = SelectCompatibleVersion(await GetProjectVersionsAsync(dependency.ProjectId), instance, type);
            else
                throw new InvalidDataException("У обязательной зависимости нет идентификатора Modrinth.");
            if (required is null) throw new InvalidOperationException("Нет совместимой версии обязательной зависимости.");
            await ResolveContentAsync(required, instance, type, folder, plan, visiting);
        }
        if (!string.IsNullOrWhiteSpace(version.ProjectId)
            && plan.Values.Any(v => v.Version.ProjectId == version.ProjectId && v.Version.Id != version.Id))
            throw new InvalidOperationException($"Зависимости требуют разные версии {version.Name}.");
        plan[version.Id] = (version, folder);
        visiting.Remove(version.Id);
    }

    private static void WriteMetadata(string directory, InstanceMetadata metadata)
    {
        string target = Path.Combine(directory, "instance.json");
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(metadata));
        File.Move(temporary, target, true);
    }

    private async Task InstallContentPlanAsync(string root, IEnumerable<(ModrinthVersion Version, string Folder)> plan)
    {
        string manifestPath = Path.Combine(root, "teal-content.json");
        var installed = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<Dictionary<string, InstalledContent>>(await File.ReadAllTextAsync(manifestPath))
                ?? throw new InvalidDataException("Повреждён список установленного содержимого.")
            : new Dictionary<string, InstalledContent>();
        string staging = Path.Combine(root, ".content-" + Guid.NewGuid().ToString("N"));
        var downloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var obsolete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var committed = new List<string>();
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var item in plan)
            {
                var file = item.Version.Files.FirstOrDefault(f => f.Primary) ?? item.Version.Files.First();
                // A remote filename must be a single filename, never a relative path.
                if (file.Filename != Path.GetFileName(file.Filename)) throw new InvalidDataException("Некорректное имя файла.");
                string relative = Path.Combine(item.Folder, file.Filename);
                string target = SafeContentPath(root, relative);
                string key = string.IsNullOrEmpty(item.Version.ProjectId) ? item.Version.Id : item.Version.ProjectId;
                if (installed.TryGetValue(key, out var old))
                {
                    obsolete.Add(SafeContentPath(root, old.Path));
                    obsolete.Add(SafeContentPath(root, old.Path + ".disabled"));
                }
                if (downloads.ContainsKey(target)) throw new InvalidDataException("Несколько проектов используют одно имя файла.");
                string staged = SafeContentPath(staging, Path.Combine(item.Version.Id, file.Filename));
                ModsToken.ThrowIfCancellationRequested();
                await Downloader.DownloadAsync(new[] { file.Url }, staged, file.Hashes, FileProgress(file.Filename), ModsToken);
                _completedFiles++;
                downloads[target] = staged;
                installed[key] = new InstalledContent { Path = relative, VersionId = item.Version.Id };
            }
            ModsToken.ThrowIfCancellationRequested();
            ReportStage("Сохранение установленных файлов");
            string stagedManifest = Path.Combine(staging, "manifest.json");
            await File.WriteAllTextAsync(stagedManifest, JsonSerializer.Serialize(installed));
            downloads[manifestPath] = stagedManifest;
            // Keep old files until every download has passed validation. Roll back failed commits.
            try
            {
                foreach (string target in downloads.Keys.Concat(obsolete).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(target)) continue;
                    string backup = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".backup");
                    File.Move(target, backup);
                    backups[target] = backup;
                }
                foreach (var (target, source) in downloads)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(source, target);
                    committed.Add(target);
                }
                foreach (string target in downloads.Keys.Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == "resourcepacks"))
                    InstanceContentManager.ActivateResourcePack(root, target);
            }
            catch
            {
                foreach (var target in committed) File.Delete(target);
                foreach (var (target, backup) in backups) File.Move(backup, target, true);
                throw;
            }
        }
        finally { Directory.Delete(staging, true); }
    }

    private string UniqueInstanceDirectory(string name)
    {
        string root = Path.Combine(_minecraftPath.BasePath, "instances");
        Directory.CreateDirectory(root);
        string safe = SanitizeFileName(name);
        // A prefix also avoids Windows device names (CON, AUX, COM1, ...).
        safe = "pack-" + (string.IsNullOrWhiteSpace(safe) ? "instance" : safe);
        if (safe.Length > 80) safe = safe[..80];
        string candidate = Path.Combine(root, safe);
        for (int suffix = 2; Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(root, $"{safe}-{suffix}");
        return candidate;
    }

    private async Task RefreshInstanceSelectionAsync(string directory)
    {
        // Refresh local entries without depending on a successful Mojang manifest request.
        await LoadInstancesAsync();
        foreach (var item in _minecraftVersions.Where(v => v.Instance != null).ToList())
            _minecraftVersions.Remove(item);
        foreach (var instance in _instances.Where(i => !i.IsPlaceholder))
            _minecraftVersions.Add(new MinecraftVersionItem
            {
                Name = instance.Directory, Instance = instance,
                DisplayName = $"Модпак: {instance.Name} ({instance.ModLoader} {instance.LoaderVersion}, {instance.Version})"
            });
        ModsInstanceComboBox.SelectedItem = _instances.FirstOrDefault(i => i.Directory == directory) ?? _instances[0];
        if (Directory.Exists(directory)) File.WriteAllText(Path.Combine(_minecraftPath.BasePath, "selected-instance.txt"), directory);
        VersionComboBox.SelectedItem = _minecraftVersions.FirstOrDefault(i => i.Instance?.Directory == directory);
        UpdateLaunchButtonState();
    }
}

public sealed class InstalledContent
{
    public string Path { get; init; } = "";
    public string VersionId { get; init; } = "";
}

public sealed class ModrinthDependency
{
    [System.Text.Json.Serialization.JsonPropertyName("version_id")]
    public string? VersionId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("dependency_type")]
    public string DependencyType { get; init; } = "";
}
