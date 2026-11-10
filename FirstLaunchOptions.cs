using System.IO;
using System.IO.Compression;
using CmlLib.Core.Version;

namespace TealLauncher;

public static class FirstLaunchOptions
{
    public static void InitializeLanguage(string directory, bool enabled, string minecraftVersion)
    {
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, ".teal-language-initialized");
        if (File.Exists(marker)) return;
        string path = Path.Combine(directory, "options.txt");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        if (!enabled || File.Exists(Path.Combine(directory, "logs", "latest.log")))
        { File.WriteAllText(marker, "initialized"); return; }
        string language = Version.TryParse(minecraftVersion, out var version) && version < new Version(1, 11) ? "ru_RU" : "ru_ru";
        lines.RemoveAll(l => l.StartsWith("lang:", StringComparison.Ordinal));
        lines.Add("lang:" + language);
        string temporary = path + ".tmp";
        File.WriteAllLines(temporary, lines); File.Move(temporary, path, true);
        File.WriteAllText(marker, "initialized");
    }

    public static async Task BackupWorldsAsync(string directory, LauncherSettings settings, CancellationToken token)
    {
        string saves = Path.Combine(directory, "saves");
        if (!settings.Backups || !Directory.Exists(saves)) return;
        string folder = Path.Combine(directory, "backups");
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "worlds-*.zip"))
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-settings.BackupRetentionDays)) File.Delete(file);
        string archive = Path.Combine(folder, $"worlds-{DateTime.Now:yyyy-MM-dd}.zip");
        if (File.Exists(archive)) return;
        var sources = Directory.EnumerateFiles(saves, "*", SearchOption.AllDirectories).ToArray();
        if (sources.Length == 0) return;
        long limit = settings.BackupLimitMb * 1024L * 1024;
        if (sources.Sum(f => new FileInfo(f).Length) > limit) throw new IOException("Миры превышают лимит резервной копии. Увеличьте лимит в настройках или отключите копирование.");
        string partial = archive + ".part";
        if (File.Exists(partial)) File.Delete(partial);
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(partial, ZipArchiveMode.Create);
                foreach (string file in sources)
                { token.ThrowIfCancellationRequested(); zip.CreateEntryFromFile(file, Path.GetRelativePath(saves, file).Replace('\\', '/'), CompressionLevel.Fastest); }
            }, token);
            File.Move(partial, archive);
            var copies = Directory.GetFiles(folder, "worlds-*.zip").OrderBy(File.GetLastWriteTimeUtc).ToList();
            while (copies.Count > 1 && copies.Sum(f => new FileInfo(f).Length) > limit)
            { File.Delete(copies[0]); copies.RemoveAt(0); }
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}

public partial class MainWindow
{
    private async Task PrepareGameOptionsAsync(string directory, string version)
    {
        FirstLaunchOptions.InitializeLanguage(directory, _settings.RussianOnFirstLaunch, version);
        ReportStage("Проверка резервной копии миров");
        try { await FirstLaunchOptions.BackupWorldsAsync(directory, _settings, ModsToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModsOperationDetail.Text = "Резервная копия не создана: " + ex.Message;
            try
            {
                string logs = Path.Combine(directory, "logs"); Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, "backup.log"), $"{DateTime.Now:O} {ex.Message}\n");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
        }
    }
}
