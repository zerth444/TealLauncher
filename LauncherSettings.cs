using System.IO;

namespace TealLauncher;

public sealed class LauncherSettings
{
    public bool LegacyDesign { get; set; }
    public string GameDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TealLauncher", "minecraft");
    public int Width { get; set; } = 925;
    public int Height { get; set; } = 530;
    public bool Fullscreen { get; set; }
    public int MemoryMb { get; set; } = 4096;
    public string Arguments { get; set; } = "";
    public string? JavaPath { get; set; }
    public bool ShowSnapshots { get; set; }
    public bool ShowModified { get; set; } = true;
    public bool ShowOldReleases { get; set; } = true;
    public bool ShowBeta { get; set; }
    public bool ShowAlpha { get; set; }
    public bool RussianOnFirstLaunch { get; set; } = true;
    public bool MinimizeOnLaunch { get; set; }
    public int ParallelDownloads { get; set; } = 4;
    public bool Backups { get; set; } = true;
    public int BackupLimitMb { get; set; } = 2000;
    public int BackupRetentionDays { get; set; } = 30;
}
