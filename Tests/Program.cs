using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using TLauncherRemake;

internal static class Program
{
    static int assertions;
    static readonly string Root = Path.GetFullPath(Path.Combine("Tests", "work"));
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        assertions++;
        Console.WriteLine("PASS " + label);
    }
    static async Task Throws(Func<Task> action, string label)
    {
        try { await action(); } catch (Exception) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    static async Task<object?> Invoke(MainWindow window, string name, params object[] args)
    {
        object? result = typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        if (result is Task task)
        {
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }
        return result;
    }
    static async Task<string> ReadLiveLog(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return await reader.ReadToEndAsync();
    }
    [STAThread]
    static int Main(string[] args)
    {
        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            try { await Run(args); Console.WriteLine($"SUCCESS: {assertions} assertions"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exit;
    }
    static async Task Run(string[] args)
    {
        Directory.CreateDirectory(Root);
        if (args.Contains("--themes")) { await SettingsTests.Run(Root); ThemeTests.Run(Root); return; }
        await SettingsTests.Run(Root);
        if (!args.Contains("--sodiumplus")) await DownloaderTests.Run(Root);
        string toggles = Path.Combine(Root, "toggles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(toggles, "mods"));
        Directory.CreateDirectory(Path.Combine(toggles, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(toggles, "shaderpacks"));
        string toggleMod = Path.Combine(toggles, "mods", "example.jar");
        await File.WriteAllTextAsync(toggleMod, "fixture");
        string disabledMod = InstanceContentManager.SetEnabled(toggles, toggleMod, false);
        Check(!File.Exists(toggleMod) && File.Exists(disabledMod), "disabled mod removed from loader scan");
        Check(InstanceContentManager.SetEnabled(toggles, disabledMod, true) == toggleMod && File.Exists(toggleMod), "mod can be enabled again");
        string resource = Path.Combine(toggles, "resourcepacks", "pack.zip");
        await File.WriteAllTextAsync(resource, "fixture");
        InstanceContentManager.SetEnabled(toggles, resource, true);
        Check(InstanceContentManager.IsEnabled(toggles, resource) && (await File.ReadAllTextAsync(Path.Combine(toggles, "options.txt"))).Contains("file/pack.zip"), "resource pack activation persisted in Minecraft options");
        string disabledResource = InstanceContentManager.SetEnabled(toggles, resource, false);
        Check(!InstanceContentManager.IsEnabled(toggles, disabledResource) && !(await File.ReadAllTextAsync(Path.Combine(toggles, "options.txt"))).Contains("file/pack.zip"), "resource pack deactivation removes selected option");
        string shader1 = Path.Combine(toggles, "shaderpacks", "one.zip"), shader2 = Path.Combine(toggles, "shaderpacks", "two.zip");
        await File.WriteAllTextAsync(shader1, "fixture"); await File.WriteAllTextAsync(shader2, "fixture");
        InstanceContentManager.SetEnabled(toggles, shader1, true);
        InstanceContentManager.SetEnabled(toggles, shader2, true);
        Check(!InstanceContentManager.IsEnabled(toggles, shader1) && InstanceContentManager.IsEnabled(toggles, shader2), "only one shader pack is selected at a time");
        string unpacked = Path.Combine(toggles, "resourcepacks", "unpacked");
        Directory.CreateDirectory(unpacked);
        await File.WriteAllTextAsync(Path.Combine(unpacked, "pack.mcmeta"), "{}");
        InstanceContentManager.SetEnabled(toggles, unpacked, true);
        string disabledFolder = InstanceContentManager.SetEnabled(toggles, unpacked, false);
        Check(Directory.Exists(disabledFolder) && !Directory.Exists(unpacked), "unpacked resource pack can be disabled");
        InstanceContentManager.SetEnabled(toggles, disabledFolder, true);
        Check(Directory.Exists(unpacked) && InstanceContentManager.IsEnabled(toggles, unpacked), "unpacked resource pack can be reenabled");
        foreach (var path in new[] { "../escape.jar", "..\\escape.jar", "C:\\escape.jar", "/escape.jar", "a:stream", "instance.json", "instance.json.", "mods/.. /escape.jar" })
            await Throws(() => { MainWindow.SafeContentPath(Root, path); return Task.CompletedTask; }, "reject " + path);
        Check(MainWindow.SafeContentPath(Root, "mods/test.jar").StartsWith(Root), "safe nested path");
        var instance = new ModpackInstance { Version = "1.20.1", ModLoader = "Fabric" };
        ModrinthVersion Version(string id, string mc, string loader, int year) => new()
        {
            Id = id, GameVersions = new() { mc }, Loaders = new() { loader },
            DatePublished = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Files = new() { new ModrinthVersionFile() }
        };
        var compatible = Version("correct", "1.20.1", "fabric", 2024);
        Check(MainWindow.SelectCompatibleVersion(new[] { Version("wrong-loader", "1.20.1", "forge", 2026), Version("wrong-mc", "1.21", "fabric", 2026), compatible }, instance, "mod")?.Id == "correct", "compatible mod selected over newer incompatible releases");
        Check(MainWindow.SelectCompatibleVersion(new[] { compatible }, new ModpackInstance { Version = "1.20.1", ModLoader = "Vanilla" }, "mod") == null, "Vanilla rejects Fabric mods");
        string hashFile = Path.Combine(Root, "hash.bin");
        await File.WriteAllTextAsync(hashFile, "fixture");
        string sha = Convert.ToHexString(SHA512.HashData(await File.ReadAllBytesAsync(hashFile)));
        await MainWindow.VerifyHashesAsync(hashFile, new() { ["sha512"] = sha });
        Check(true, "valid hash accepted");
        await Throws(() => MainWindow.VerifyHashesAsync(hashFile, new() { ["sha512"] = "00" }), "corrupt file rejected");
        string nicknamePreferences = Path.Combine(Root, "nickname-reopen-" + Guid.NewGuid().ToString("N") + ".json");
        var nicknameWindow = new MainWindow(nicknamePreferences);
        ((System.Windows.Controls.TextBox)nicknameWindow.FindName("EmailTextBox")).Text = "Persistence_7";
        nicknameWindow.Close();
        var reopenedNicknameWindow = new MainWindow(nicknamePreferences);
        Check(((System.Windows.Controls.TextBox)reopenedNicknameWindow.FindName("EmailTextBox")).Text == "Persistence_7", "nickname survives closing and reopening launcher window");
        reopenedNicknameWindow.Close();
        string controlsPath = Path.Combine(Root, "settings-controls-" + Guid.NewGuid().ToString("N") + ".json");
        LauncherPreferences.Save(controlsPath, new LauncherPreferences { Settings = new LauncherSettings { GameDirectory = Path.Combine(Root, "settings-game"), ShowBeta = true } });
        var controlsWindow = new MainWindow(controlsPath);
        var controlsVersions = (System.Collections.ObjectModel.ObservableCollection<MinecraftVersionItem>)typeof(MainWindow).GetField("_minecraftVersions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controlsWindow)!;
        controlsVersions.Add(new MinecraftVersionItem { Name = "1.20.1" });
        controlsVersions.Add(new MinecraftVersionItem { Name = "b1.7.3", VersionType = CmlLib.Core.VersionMetadata.MVersionType.OldBeta });
        ((System.Windows.Controls.CheckBox)controlsWindow.FindName("SettingsBetaCheckBox")).IsChecked = false;
        Check(System.Windows.Data.CollectionViewSource.GetDefaultView(controlsVersions).Cast<MinecraftVersionItem>().Count() == 1, "version checkbox immediately filters actual main list");
        ((System.Windows.Controls.CheckBox)controlsWindow.FindName("SettingsBetaCheckBox")).IsChecked = true;
        Check(System.Windows.Data.CollectionViewSource.GetDefaultView(controlsVersions).Cast<MinecraftVersionItem>().Count() == 2, "version checkbox restores beta to actual main list");
        ((System.Windows.Controls.TextBox)controlsWindow.FindName("SettingsWidthTextBox")).Text = "bad";
        await Throws(() => Invoke(controlsWindow, "ReadSettingsControls"), "invalid window dimensions rejected");
        ((System.Windows.Controls.TextBox)controlsWindow.FindName("SettingsWidthTextBox")).Text = "1280";
        ((System.Windows.Controls.TextBox)controlsWindow.FindName("SettingsHeightTextBox")).Text = "720";
        ((System.Windows.Controls.Slider)controlsWindow.FindName("SettingsMemorySlider")).Value = 3072;
        await Invoke(controlsWindow, "SaveSettingsAsync"); controlsWindow.Close();
        var reopenedControls = new MainWindow(controlsPath);
        Check(((System.Windows.Controls.TextBox)reopenedControls.FindName("SettingsWidthTextBox")).Text == "1280"
            && ((System.Windows.Controls.Slider)reopenedControls.FindName("SettingsMemorySlider")).Value == 3072
            && ((System.Windows.Controls.CheckBox)reopenedControls.FindName("SettingsBetaCheckBox")).IsChecked == true,
            "saved game settings restore after launcher restart");
        reopenedControls.Close();
        var window = new MainWindow(Path.Combine(Root, "preferences.json"));
        string minecraftRoot = args.Contains("--user-root")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TealLauncher", "minecraft")
            : Path.Combine(Root, "minecraft");
        typeof(MainWindow).GetField("_minecraftPath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new MinecraftPath(minecraftRoot));
        typeof(MainWindow).GetMethod("InitializeMinecraftLauncher", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        if (args.Contains("--sodiumplus"))
        {
            using var httpSodium = new System.Net.Http.HttpClient();
            httpSodium.DefaultRequestHeaders.UserAgent.ParseAdd("TealLauncher/1.0");
            var sodiumVersions = JsonSerializer.Deserialize<List<ModrinthVersion>>(await httpSodium.GetStringAsync("https://api.modrinth.com/v2/project/sodiumplus/version"))!;
            var sodiumVersion = sodiumVersions.First(v => v.Files.Any(f => f.Filename.EndsWith(".mrpack")));
            await Invoke(window, "BeginModsOperation", "Sodium Plus — интеграционная проверка");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                var detail = (System.Windows.Controls.TextBlock)window.FindName("ModsOperationDetail");
                var summary = (System.Windows.Controls.TextBlock)window.FindName("ModsOperationSummary");
                Console.WriteLine(detail.Text + " | " + summary.Text);
            };
            timer.Start();
            try
            {
                string? existing = Directory.Exists(Path.Combine(minecraftRoot, "instances"))
                    ? Directory.GetDirectories(Path.Combine(minecraftRoot, "instances")).FirstOrDefault(d =>
                        File.Exists(Path.Combine(d, "instance.json")) && JsonSerializer.Deserialize<InstanceMetadata>(File.ReadAllText(Path.Combine(d, "instance.json")))?.ProjectVersionId == sodiumVersion.Id)
                    : null;
                if (existing != null && args.Contains("--launch-sodium")) await Invoke(window, "RefreshInstanceSelectionAsync", existing);
                else await Invoke(window, "InstallModpackVersionAsync", new ModrinthProjectHit { ProjectId = "ch7UHY2J", Title = "Sodium Plus", Slug = "sodiumplus" }, sodiumVersion);
                var selected = (ModpackInstance)((System.Windows.Controls.ComboBox)window.FindName("ModsInstanceComboBox")).SelectedItem;
                Check(selected.Name == "Sodium Plus", "Sodium Plus selected after installation");
                var metadata = JsonSerializer.Deserialize<InstanceMetadata>(await File.ReadAllTextAsync(Path.Combine(selected.Directory, "instance.json")))!;
                Check(metadata.ProjectVersionId == sodiumVersion.Id && metadata.LoaderVersionName.Contains("fabric"), "Sodium Plus exact profile persisted");
                Check(Directory.GetFiles(Path.Combine(selected.Directory, "mods"), "*.jar").Length > 20, "Sodium Plus real mod files installed");
                Check((bool)(await Invoke(window, "IsInstanceInstalled", selected))!, "Sodium Plus ready to play");
                await Invoke(window, "EndModsOperation", "Тест завершён", true);
                if (args.Contains("--launch-sodium"))
                {
                    ((System.Windows.Controls.Slider)window.FindName("SettingsMemorySlider")).Value = 4096;
                    await Invoke(window, "LaunchInstanceAsync", selected, "TealTest");
                    var game = (System.Diagnostics.Process)typeof(MainWindow).GetField("_gameProcess", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                    string logs = Path.Combine(selected.Directory, "logs");
                    bool ready = false;
                    for (int second = 0; second < 180 && !game.HasExited; second++)
                    {
                        await Task.Delay(1000);
                        string? log = Directory.GetFiles(logs, "launcher-*.log").OrderDescending().FirstOrDefault();
                        if (log == null) continue;
                        string text = await ReadLiveLog(log);
                        string latest = Path.Combine(logs, "latest.log");
                        if (File.Exists(latest) && File.GetLastWriteTimeUtc(latest) >= game.StartTime.ToUniversalTime())
                            text += await ReadLiveLog(latest);
                        if (text.Contains("Sound engine started") && text.Contains("textures/atlas/blocks.png-atlas")
                            || text.Contains("OpenAL initialized") || text.Contains("Created: 1024x1024x4 minecraft:textures/atlas/blocks.png-atlas")
                            || text.Contains("Reloading ResourceManager") && second > 50)
                        { ready = true; Console.WriteLine("GAME READY: " + log); break; }
                    }
                    Check(ready && !game.HasExited, "Sodium Plus launched through launcher into running Minecraft client");
                    game.CloseMainWindow();
                    if (!game.HasExited && !game.WaitForExit(10000)) game.Kill();
                }
            }
            finally { timer.Stop(); }
            return;
        }
        var created = (ModpackInstance)(await Invoke(window, "CreateInstance", "CON...", "1.20.1", "Fabric", "0.16.10"))!;
        Check(File.Exists(Path.Combine(created.Directory, "instance.json")), "instance persisted");
        var duplicate = (ModpackInstance)(await Invoke(window, "CreateInstance", "CON...", "1.20.1", "Fabric", "0.16.10"))!;
        Check(created.Directory != duplicate.Directory, "duplicate instance name isolated");
        string sidebarMod = Path.Combine(created.Directory, "mods", "sidebar-fixture.jar");
        await File.WriteAllTextAsync(sidebarMod, "fixture");
        await Invoke(window, "RefreshInstanceSelectionAsync", created.Directory);
        await Invoke(window, "RefreshInstalledContents");
        var sidebarRows = (System.Collections.ObjectModel.ObservableCollection<InstalledContentRow>)typeof(MainWindow).GetField("_installedRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var sidebarRow = sidebarRows.First(r => r.Path == sidebarMod);
        var toggle = new System.Windows.Controls.Primitives.ToggleButton { Tag = sidebarRow, IsChecked = false };
        await Invoke(window, "InstalledContentToggle_Click", toggle, new RoutedEventArgs());
        Check(!sidebarRow.Enabled && File.Exists(sidebarMod + ".disabled"), "left sidebar switch disables actual mod file");
        toggle.IsChecked = true;
        await Invoke(window, "InstalledContentToggle_Click", toggle, new RoutedEventArgs());
        Check(sidebarRow.Enabled && File.Exists(sidebarMod), "left sidebar switch reenables actual mod file");
        await Invoke(window, "BeginModsOperation", "Test result panel");
        await Invoke(window, "DismissModsOperation_Click", window, new RoutedEventArgs());
        var activeCancellation = (CancellationTokenSource)typeof(MainWindow).GetField("_modsCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        Check(((System.Windows.Controls.Button)window.FindName("ModsShowProgressButton")).Visibility == Visibility.Visible && !activeCancellation.IsCancellationRequested,
            "hiding active progress keeps installation running and provides restore action");
        await Invoke(window, "ShowModsOperation_Click", window, new RoutedEventArgs());
        await Invoke(window, "EndModsOperation", "Done", true);
        await Invoke(window, "DismissModsOperation_Click", window, new RoutedEventArgs());
        Check(((System.Windows.Controls.Border)window.FindName("ModsOperationPanel")).Visibility == Visibility.Collapsed, "completed operation notification can be dismissed");
        await Invoke(window, "ShowProjectPageAsync", null!, "mod", sidebarRow);
        Check(((System.Windows.Controls.Grid)window.FindName("ModsDetailsPanel")).Visibility == Visibility.Visible
            && typeof(MainWindow).GetField("_modsCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) == null,
            "opening local project details does not start installation");
        var removalManifest = new Dictionary<string, InstalledContent> { ["sidebar-test"] = new() { Path = "mods/sidebar-fixture.jar", VersionId = "v1" } };
        await File.WriteAllTextAsync(Path.Combine(created.Directory, "teal-content.json"), JsonSerializer.Serialize(removalManifest));
        MainWindow.RemoveInstalledContent(sidebarRow);
        Check(!File.Exists(sidebarMod) && !File.Exists(sidebarMod + ".disabled")
            && !JsonSerializer.Deserialize<Dictionary<string, InstalledContent>>(await File.ReadAllTextAsync(Path.Combine(created.Directory, "teal-content.json")))!.ContainsKey("sidebar-test"),
            "project removal deletes file and installed-content entry");
        await Invoke(window, "CloseProjectPage_Click", window, new RoutedEventArgs());
        string archive = Path.Combine(Root, "fixture.mrpack");
        if (File.Exists(archive)) File.Delete(archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            void Entry(string name, string value) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(value); }
            Entry("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","files":[],"dependencies":{"minecraft":"1.20.1","fabric-loader":"0.16.10"}}""");
            Entry("overrides/config/test.txt", "common");
            Entry("client-overrides/config/test.txt", "client");
            Entry("server-overrides/config/server.txt", "server");
        }
        string extracted = Path.Combine(Root, "extracted");
        await Invoke(window, "ExtractModpackAsync", archive, extracted);
        Check(await File.ReadAllTextAsync(Path.Combine(extracted, "config/test.txt")) == "client", "client overrides applied last");
        Check(!File.Exists(Path.Combine(extracted, "config/server.txt")), "server overrides excluded");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("overrides/../escape.jar").Open());
            writer.Write("escape");
        }
        await Throws(() => Invoke(window, "ExtractModpackAsync", archive, extracted), "archive traversal rejected");
        Check(!File.Exists(Path.Combine(Root, "escape.jar")), "archive never writes outside instance");
        if (args.Contains("--network"))
        {
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TealLauncher/1.0");
            await Invoke(window, "ShowProjectPageAsync", new ModrinthProjectHit { ProjectId = "lithium", Title = "Lithium" }, "mod", null!);
            Check(((System.Windows.Controls.TextBlock)window.FindName("ModsDetailsTitle")).Text.Contains("Lithium")
                && ((System.Windows.Controls.FlowDocumentScrollViewer)window.FindName("ModsDetailsBody")).Document.Blocks.Count > 0
                && typeof(MainWindow).GetField("_modsCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) == null,
                "live project page loads description without installing");
            await Invoke(window, "CloseProjectPage_Click", window, new RoutedEventArgs());
            await Invoke(window, "LoadLoaderVersionChoicesAsync", new[] { "1.20.1", "1.20.2", "1.21.1", "26.3" }, 0);
            var mainChoices = (System.Collections.ObjectModel.ObservableCollection<MinecraftVersionItem>)typeof(MainWindow).GetField("_minecraftVersions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            foreach (string loader in new[] { "Fabric", "Forge", "NeoForge", "Quilt" })
                Check(mainChoices.Any(v => v.Loader == loader), loader + " choices available in main version list");
            string json = await http.GetStringAsync("https://api.modrinth.com/v2/project/fabric-api/version");
            var versions = JsonSerializer.Deserialize<List<ModrinthVersion>>(json)!;
            var version = MainWindow.SelectCompatibleVersion(versions, instance, "mod");
            Check(version != null, "live Modrinth compatibility selection");
            var file = version!.Files.First();
            await MainWindow.DownloadVerifiedAsync(new[] { file.Url }, Path.Combine(Root, file.Filename), file.Hashes);
            Check(true, "live Modrinth download and hashes");
            var plan = new Dictionary<string, (ModrinthVersion Version, string Folder)>();
            var sodium = JsonSerializer.Deserialize<List<ModrinthVersion>>(await http.GetStringAsync("https://api.modrinth.com/v2/project/iris/version"))!;
            await Invoke(window, "ResolveContentAsync", MainWindow.SelectCompatibleVersion(sodium, instance, "mod")!, instance, "mod", "mods", plan, new HashSet<string>());
            Check(plan.Count >= 2, "live required dependencies resolved for Iris");
            if (args.Contains("--content"))
            {
                var contentInstance = (ModpackInstance)(await Invoke(window, "CreateInstance", "Content regression", "26.3", "Fabric", "0.20.1"))!;
                foreach (var (slug, type, folder) in new[]
                {
                    ("lithium", "mod", "mods"), ("fresh-animations", "resourcepack", "resourcepacks"),
                    ("complementary-reimagined", "shader", "shaderpacks"), ("terralith", "datapack", "datapacks")
                })
                {
                    var candidates = JsonSerializer.Deserialize<List<ModrinthVersion>>(await http.GetStringAsync($"https://api.modrinth.com/v2/project/{slug}/version"))!;
                    var candidate = MainWindow.SelectCompatibleVersion(candidates, contentInstance, type);
                    Check(candidate != null, $"live compatible {type} version selected");
                    if (type == "datapack")
                    {
                        var entries = new[] { (Version: candidate!, Folder: folder) };
                        await Invoke(window, "InstallContentPlanAsync", contentInstance.Directory, entries);
                    }
                    else await Invoke(window, "InstallContentVersionAsync", contentInstance, type, candidate!);
                    Check(Directory.GetFiles(Path.Combine(contentInstance.Directory, folder)).Length > 0, $"live {type} installed into correct folder");
                    if (type == "resourcepack")
                        Check(InstanceContentManager.IsEnabled(contentInstance.Directory, Directory.GetFiles(Path.Combine(contentInstance.Directory, folder))[0]), "installed resource pack selected in options.txt");
                }
                var lithium = JsonSerializer.Deserialize<List<ModrinthVersion>>(await http.GetStringAsync("https://api.modrinth.com/v2/project/lithium/version"))!;
                var selectedLithium = MainWindow.SelectCompatibleVersion(lithium, contentInstance, "mod")!;
                int before = Directory.GetFiles(Path.Combine(contentInstance.Directory, "mods")).Length;
                await Invoke(window, "InstallContentVersionAsync", contentInstance, "mod", selectedLithium);
                Check(Directory.GetFiles(Path.Combine(contentInstance.Directory, "mods")).Length == before, "reinstall creates no duplicate mod jars");
            }
            if (args.Contains("--install"))
            {
                string installed = (string)(await Invoke(window, "EnsureInstanceLoaderAsync", created))!;
                Check(installed.Contains("fabric"), "Fabric installed with correct launch profile");
            }
            if (args.Contains("--loaders"))
            {
                var launcher = new MinecraftLauncher(new MinecraftPath(Path.Combine(Root, "minecraft")));
                var forge = (await new ForgeInstaller(launcher).GetForgeVersions("1.20.1")).First();
                string installed = (string)(await Invoke(window, "EnsureInstanceLoaderAsync", new ModpackInstance
                { Version = "1.20.1", ModLoader = "Forge", LoaderVersion = forge.ForgeVersionName }))!;
                Check(installed.Contains("forge", StringComparison.OrdinalIgnoreCase), "Forge installed with processors");
                var neoVersions = await new NeoForgeInstaller(launcher).GetNeoForgeVersions("1.20.2");
                Check(neoVersions.Count > 0, "NeoForge catalog for Minecraft 1.20.2");
                installed = (string)(await Invoke(window, "EnsureInstanceLoaderAsync", new ModpackInstance
                { Version = "1.20.2", ModLoader = "NeoForge", LoaderVersion = neoVersions.First().NeoForgeVersionName }))!;
                Check(installed.Contains("forge", StringComparison.OrdinalIgnoreCase), "NeoForge installed with processors");
                using var quilt = JsonDocument.Parse(await http.GetStringAsync("https://meta.quiltmc.org/v3/versions/loader/1.20.1"));
                string quiltVersion = quilt.RootElement[0].GetProperty("loader").GetProperty("version").GetString()!;
                installed = (string)(await Invoke(window, "EnsureInstanceLoaderAsync", new ModpackInstance
                { Version = "1.20.1", ModLoader = "Quilt", LoaderVersion = quiltVersion }))!;
                Check(installed.Contains("quilt"), "Quilt installed with correct profile");
            }
            if (args.Contains("--forge-clean"))
            {
                var forgeLauncher = new MinecraftLauncher(new MinecraftPath(Path.Combine(Root, "minecraft")));
                var forgeVersions = await new ForgeInstaller(forgeLauncher).GetForgeVersions("1.20.1");
                var mapper = new CmlLib.Core.Installer.Forge.Versions.ForgeInstallerVersionMapper();
                var fresh = forgeVersions.First(v => !File.Exists(Path.Combine(Root, "minecraft", "versions", mapper.CreateInstaller(v).VersionName, mapper.CreateInstaller(v).VersionName + ".json")));
                string installed = (string)(await Invoke(window, "EnsureInstanceLoaderAsync", new ModpackInstance { Version = "1.20.1", ModLoader = "Forge", LoaderVersion = fresh.ForgeVersionName }))!;
                Check(installed.Contains(fresh.ForgeVersionName), "fresh Forge processor installation uses browser-free engine");
            }
            if (args.Contains("--refresh"))
            {
                ((System.Windows.Controls.TextBox)window.FindName("EmailTextBox")).Text = "Refresh_User";
                ((System.Windows.Controls.Grid)window.FindName("ModsOverlay")).Visibility = Visibility.Visible;
                await Invoke(window, "RefreshLauncherAsync");
                Check(((System.Windows.Controls.Button)window.FindName("RefreshButton")).IsEnabled
                    && ((System.Windows.Controls.TextBox)window.FindName("EmailTextBox")).Text == "Refresh_User",
                    "refresh completes and preserves nickname");
                Check(((System.Collections.ObjectModel.ObservableCollection<ModsProjectItem>)typeof(MainWindow).GetField("_modsProjects", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Count > 0,
                    "refresh reloads open mods catalog");
            }
        }
        if (args.Contains("--legacy"))
        {
            var legacyLauncher = new MinecraftLauncher(new MinecraftPath(Path.Combine(Root, "minecraft")));
            using var legacyTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            foreach (string name in new[] { "b1.7.3", "a1.2.6", "1.5.2" })
            {
                Console.WriteLine("Installing legacy " + name);
                await legacyLauncher.InstallAsync(name, legacyTimeout.Token);
                Check(File.Exists(Path.Combine(Root, "minecraft", "versions", name, name + ".jar")), "legacy version downloaded: " + name);
            }
        }
    }
}
