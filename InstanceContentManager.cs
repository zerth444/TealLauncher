using System.IO;
using System.Text.Json;

namespace TLauncherRemake;

public static class InstanceContentManager
{
    public static bool IsEnabled(string root, string path)
    {
        if (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return false;
        if (Path.GetFileName(Path.GetDirectoryName(path)) == "resourcepacks")
            return ReadResourcePacks(root).Contains("file/" + Path.GetFileName(path));
        if (Path.GetFileName(Path.GetDirectoryName(path)) == "shaderpacks")
        {
            string iris = Path.Combine(root, "config", "iris.properties");
            return File.Exists(iris)
                ? ReadSetting(iris, "enableShaders", '=') != "false" && ReadSetting(iris, "shaderPack", '=') == Path.GetFileName(path)
                : ReadSetting(Path.Combine(root, "optionsshaders.txt"), "shaderPack", '=') == Path.GetFileName(path);
        }
        return true;
    }

    public static string SetEnabled(string root, string path, bool enabled)
    {
        string basePath = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path;
        string target = enabled ? basePath : basePath + ".disabled";
        bool moved = !path.Equals(target, StringComparison.OrdinalIgnoreCase);
        if (moved && (File.Exists(target) || Directory.Exists(target))) throw new IOException("Файл или папка с таким именем уже существует.");
        string kind = Path.GetFileName(Path.GetDirectoryName(basePath)) ?? "";
        string filename = Path.GetFileName(basePath);
        var settings = new[] { Path.Combine(root, "options.txt"), Path.Combine(root, "optionsshaders.txt"), Path.Combine(root, "config", "iris.properties") }
            .ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        if (moved) Move(path, target);
        try
        {
            if (kind == "resourcepacks")
            {
                var packs = ReadResourcePacks(root);
                packs.RemoveAll(p => p == "file/" + filename);
                if (enabled) packs.Add("file/" + filename);
                WriteSetting(Path.Combine(root, "options.txt"), "resourcePacks", JsonSerializer.Serialize(packs), ':');
            }
            else if (kind == "shaderpacks")
            {
                string iris = Path.Combine(root, "config", "iris.properties");
                if (enabled || ReadSetting(iris, "shaderPack", '=') == filename)
                {
                    WriteSetting(iris, "shaderPack", enabled ? filename : "", '=');
                    WriteSetting(iris, "enableShaders", enabled ? "true" : "false", '=');
                }
                string optifine = Path.Combine(root, "optionsshaders.txt");
                if (enabled || ReadSetting(optifine, "shaderPack", '=') == filename)
                    WriteSetting(optifine, "shaderPack", enabled ? filename : "OFF", '=');
            }
            return target;
        }
        catch
        {
            if (moved) Move(target, path);
            foreach (var (setting, original) in settings)
            {
                if (original == null) { if (File.Exists(setting)) File.Delete(setting); }
                else File.WriteAllBytes(setting, original);
            }
            throw;
        }
    }

    public static void ActivateResourcePack(string root, string path)
        => SetEnabled(root, path, true);

    private static void Move(string source, string target)
    { if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target); }

    private static List<string> ReadResourcePacks(string root)
    {
        string value = ReadSetting(Path.Combine(root, "options.txt"), "resourcePacks", ':');
        return string.IsNullOrEmpty(value) ? new() : JsonSerializer.Deserialize<List<string>>(value)
            ?? throw new InvalidDataException("Некорректный resourcePacks в options.txt.");
    }

    private static string ReadSetting(string path, string name, char separator)
        => !File.Exists(path) ? "" : File.ReadLines(path).LastOrDefault(l => l.StartsWith(name + separator, StringComparison.Ordinal))?[(name.Length + 1)..] ?? "";

    private static void WriteSetting(string path, string name, string value, char separator)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        lines.RemoveAll(l => l.StartsWith(name + separator, StringComparison.Ordinal));
        lines.Add(name + separator + value);
        string temporary = path + ".tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, true);
    }
}
