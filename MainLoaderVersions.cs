using System.Collections.Generic;
using System.Text.Json;
using System.Xml.Linq;
using CmlLib.Core.Installer.NeoForge;

namespace TealLauncher;

public partial class MainWindow
{
    private ModpackInstance? FindInstalledLoaderChoice(MinecraftVersionItem choice)
        => _instances.FirstOrDefault(i => !i.IsPlaceholder && string.IsNullOrEmpty(i.ProjectId)
            && i.ModLoader == choice.Loader && i.Version == choice.GameVersion && IsInstanceInstalled(i));

    private int _versionListGeneration;

    private async Task LoadLoaderVersionChoicesAsync(string[] releases, int generation)
    {
        async Task<HashSet<string>> MetaGames(string host, string api)
        {
            using var document = JsonDocument.Parse(await Http.GetStringAsync($"https://{host}/{api}/versions/game"));
            return document.RootElement.EnumerateArray().Select(e => e.GetProperty("version").GetString()!).ToHashSet();
        }
        async Task<HashSet<string>> ForgeGames()
        {
            string xml = await Http.GetStringAsync("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
            return XDocument.Parse(xml).Descendants("version").Select(e => e.Value.Split('-')[0])
                .Where(v => Version.TryParse(v, out var number) && number >= new Version(1, 7, 10)).ToHashSet();
        }
        async Task<HashSet<string>> NeoGames()
            => (await new NeoForgeInstaller(_minecraftLauncher).GetNeoForgeVersions()).Select(v => v.MinecraftVersionName).ToHashSet();
        async Task<(string Loader, HashSet<string> Games)> Read(string loader, Func<Task<HashSet<string>>> read)
        {
            try { return (loader, await read()); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A failed loader catalog must not prevent vanilla or installed instances from loading.
                System.Diagnostics.Debug.WriteLine($"{loader} catalog: {ex.Message}");
                return (loader, new HashSet<string>());
            }
        }
        var catalogs = await Task.WhenAll(
            Read("Fabric", () => MetaGames("meta.fabricmc.net", "v2")),
            Read("Forge", ForgeGames), Read("NeoForge", NeoGames),
            Read("Quilt", () => MetaGames("meta.quiltmc.org", "v3")));
        if (generation != _versionListGeneration) return;
        int index = _minecraftVersions.ToList().FindIndex(v => v.Instance != null);
        if (index < 0) index = _minecraftVersions.Count;
        foreach (var (loader, games) in catalogs)
            foreach (string game in releases.Where(games.Contains))
                _minecraftVersions.Insert(index++, new MinecraftVersionItem
                {
                    Name = loader.ToLowerInvariant() + ":" + game,
                    DisplayName = $"{loader} {game}", Loader = loader, GameVersion = game
                });
        RefreshVersionFilter();
    }
}
