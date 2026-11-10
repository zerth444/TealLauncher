using System.Diagnostics;
using System.IO;

namespace TealLauncher;

public partial class MainWindow
{
    private readonly Dictionary<int, string> _gameLogs = new();

    private void StartInstanceProcess(Process process, ModpackInstance instance)
    {
        string logs = Path.Combine(instance.Directory, "logs");
        Directory.CreateDirectory(logs);
        string log = Path.Combine(logs, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(log, $"Запуск Minecraft {instance.Version}; {instance.ModLoader} {instance.LoaderVersion}\n");
        object gate = new();
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        void Write(string? line)
        {
            if (line == null) return;
            lock (gate) File.AppendAllText(log, line + Environment.NewLine);
        }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Start();
        _gameLogs[process.Id] = log;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }
}
