using System.IO;
using System.Text.Json;

namespace TLauncherRemake;

public sealed class LauncherPreferences
{
    public string Nickname { get; init; } = "";
    public LauncherSettings Settings { get; init; } = new();

    public static LauncherPreferences Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<LauncherPreferences>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public static void SaveNickname(string path, string nickname)
        => Save(path, new LauncherPreferences { Nickname = nickname.Trim(), Settings = Load(path).Settings });

    public static void Save(string path, LauncherPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
