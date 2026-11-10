using System.IO;
using CmlLib.Core.Installer.Forge.Versions;

namespace TealLauncher;

public partial class MainWindow
{
    private async Task<string> InstallForgeWithoutBrowserAsync(ModpackInstance instance)
    {
        // Use the public installer engine directly. ForgeInstaller.Install adds a browser-ad callback.
        var versions = await new ForgeVersionLoader(Http).GetForgeVersions(instance.Version);
        ModsToken.ThrowIfCancellationRequested();
        var version = versions.FirstOrDefault(v => v.ForgeVersionName == instance.LoaderVersion)
            ?? throw new InvalidOperationException($"Forge {instance.LoaderVersion} для Minecraft {instance.Version} не найден.");
        var installer = new ForgeInstallerVersionMapper().CreateInstaller(version);
        var options = CreateForgeInstallOptions();
        if (options.SkipIfAlreadyInstalled && File.Exists(_minecraftPath.GetVersionJsonPath(installer.VersionName)))
            return installer.VersionName;
        await InstallLoaderFilesAsync(instance.Version);
        var vanilla = await _minecraftLauncher.GetVersionAsync(instance.Version, ModsToken);
        options.JavaPath ??= _minecraftLauncher.GetJavaPath(vanilla) ?? _minecraftLauncher.GetDefaultJavaPath();
        if (string.IsNullOrWhiteSpace(options.JavaPath)) throw new InvalidOperationException("Java для установки Forge не найдена.");
        ReportStage($"Установка Forge {instance.LoaderVersion}");
        await installer.Install(_minecraftPath, _minecraftLauncher.GameInstaller, options);
        await _minecraftLauncher.GetAllVersionsAsync(ModsToken);
        return installer.VersionName;
    }
}
