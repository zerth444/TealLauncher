using System.IO;
using System.IO.Compression;
using TLauncherRemake;
using CmlLib.Core.VersionMetadata;

internal static class SettingsTests
{
    public static async Task Run(string root)
    {
        string folder = Path.Combine(root, "settings-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string preferences = Path.Combine(folder, "preferences.json");
        var settings = new LauncherSettings { GameDirectory = folder, Width = 1280, Height = 720, MemoryMb = 3072, ShowBeta = true, ShowSnapshots = true, ShowModified = false, ParallelDownloads = 8, MinimizeOnLaunch = true };
        LauncherPreferences.Save(preferences, new LauncherPreferences { Nickname = "Before", Settings = settings });
        LauncherPreferences.SaveNickname(preferences, "After");
        var restored = LauncherPreferences.Load(preferences);
        Check(restored.Nickname == "After" && restored.Settings.Width == 1280 && restored.Settings.ShowBeta && restored.Settings.ParallelDownloads == 8, "nickname updates preserve saved launcher settings");
        var all = new LauncherSettings { ShowAlpha = true, ShowBeta = true, ShowSnapshots = true, ShowModified = true, ShowOldReleases = true };
        var samples = new[]
        {
            new MinecraftVersionItem { Name = "1.20.1" }, new MinecraftVersionItem { Name = "1.5.2" },
            new MinecraftVersionItem { Name = "snapshot", VersionType = MVersionType.Snapshot },
            new MinecraftVersionItem { Name = "b1.7.3", VersionType = MVersionType.OldBeta },
            new MinecraftVersionItem { Name = "a1.2.6", VersionType = MVersionType.OldAlpha },
            new MinecraftVersionItem { Name = "fabric:1.20.1", Loader = "Fabric" }
        };
        Check(samples.All(v => MainWindow.IsVersionVisible(v, all)), "all version categories can be enabled");
        all.ShowAlpha = all.ShowBeta = all.ShowSnapshots = all.ShowModified = all.ShowOldReleases = false;
        Check(samples.Count(v => MainWindow.IsVersionVisible(v, all)) == 1, "disabled categories hide snapshots, old releases, beta, alpha and modified versions");
        string game = Path.Combine(folder, "game"); Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "music:0.5\nlang:en_us\n");
        FirstLaunchOptions.InitializeLanguage(game, true, "1.20.1");
        Check((await File.ReadAllTextAsync(Path.Combine(game, "options.txt"))).Contains("lang:ru_ru"), "Russian is applied on first launch including a modpack preset");
        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "lang:en_us\n");
        FirstLaunchOptions.InitializeLanguage(game, true, "1.20.1");
        Check((await File.ReadAllTextAsync(Path.Combine(game, "options.txt"))).Contains("lang:en_us"), "later in-game language choice is preserved");
        string off = Path.Combine(folder, "language-off");
        FirstLaunchOptions.InitializeLanguage(off, false, "1.20.1");
        Check(!File.Exists(Path.Combine(off, "options.txt")), "language option disabled leaves Minecraft defaults untouched");
        string old = Path.Combine(folder, "old-game");
        FirstLaunchOptions.InitializeLanguage(old, true, "1.5.2");
        Check((await File.ReadAllTextAsync(Path.Combine(old, "options.txt"))).Contains("lang:ru_RU"), "legacy Minecraft receives legacy language code");
        string world = Path.Combine(game, "saves", "Test World"); Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "world fixture");
        await FirstLaunchOptions.BackupWorldsAsync(game, new LauncherSettings(), CancellationToken.None);
        string archive = Directory.GetFiles(Path.Combine(game, "backups"), "*.zip").Single();
        using (var zip = ZipFile.OpenRead(archive)) Check(zip.GetEntry("Test World/level.dat") != null, "world backup contains actual save files");
        await FirstLaunchOptions.BackupWorldsAsync(game, new LauncherSettings(), CancellationToken.None);
        Check(Directory.GetFiles(Path.Combine(game, "backups"), "*.zip").Length == 1, "daily backup is not duplicated");
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
