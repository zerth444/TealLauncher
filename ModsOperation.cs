using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CmlLib.Core.Installers;
using CmlLib.Core;

namespace TLauncherRemake;

public partial class MainWindow
{
    private CancellationTokenSource? _modsCancellation;
    private readonly Dictionary<string, (long Bytes, long Total)> _transfers = new();
    private readonly Stopwatch _operationWatch = new();
    private DispatcherTimer? _operationTimer;
    private string _operationStage = "";
    private string? _operationLog;
    private int _completedFiles;
    private int _totalFiles;
    private long _expectedBytes;
    private long _networkBytes;
    private readonly HashSet<string> _networkTransfers = new();
    private string? _operationDirectory;
    private bool _closeAfterModsOperation;
    private CancellationToken ModsToken => _modsCancellation?.Token ?? CancellationToken.None;
    private ContentDownloader Downloader => new(Path.Combine(_minecraftPath.BasePath, "cache", "content"));

    private async Task InstallLoaderFilesAsync(string version)
    {
        ReportStage($"Minecraft, Java и библиотеки: {version}");
        _transfers.Clear(); _expectedBytes = 0;
        var files = new Progress<InstallerProgressChangedEventArgs>(p =>
        {
            if (_modsCancellation == null) return;
            ModsOperationDetail.Text = $"Minecraft / Java: {p.Name}";
            _completedFiles = p.ProgressedTasks; _totalFiles = p.TotalTasks;
            UpdateOperationSummary();
        });
        var bytes = new Progress<ByteProgress>(p =>
        {
            if (_modsCancellation == null) return;
            ModsOperationProgress.IsIndeterminate = false;
            ModsOperationProgress.Value = p.ToRatio() * 100;
        });
        await _minecraftLauncher.InstallAsync(version, files, bytes, ModsToken);
    }

    private void BeginModsOperation(string title)
    {
        _modsCancellation?.Dispose();
        _modsCancellation = new CancellationTokenSource();
        _installingProject = true;
        _isLaunching = true;
        _transfers.Clear();
        _completedFiles = _totalFiles = 0;
        _expectedBytes = 0;
        _networkBytes = 0;
        _networkTransfers.Clear();
        _operationDirectory = null;
        _operationWatch.Restart();
        string logs = Path.Combine(_minecraftPath.BasePath, "logs");
        Directory.CreateDirectory(logs);
        _operationLog = Path.Combine(logs, $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        ModsOperationTitle.Text = title;
        ModsOperationPanel.Visibility = Visibility.Visible;
        ModsDismissButton.Visibility = Visibility.Visible;
        ModsShowProgressButton.Visibility = Visibility.Collapsed;
        ModsShowProgressButton.Content = "Идёт установка — показать прогресс";
        ModsCancelButton.Visibility = Visibility.Visible;
        ModsCancelButton.IsEnabled = true;
        ModsRetryButton.Visibility = Visibility.Collapsed;
        ModsInstanceComboBox.IsEnabled = false;
        ModsCreateButton.IsEnabled = false;
        ModsInstalledList.IsEnabled = false;
        ModsProjectsList.IsEnabled = false;
        RefreshProjectPageState();
        SetLaunchButton("Установка...", false);
        _operationTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateOperationSummary(), Dispatcher);
        _operationTimer.Start();
        ReportStage("Подготовка установки");
    }

    private void ReportStage(string stage)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ReportStage(stage)); return; }
        _operationStage = stage;
        if (_modsCancellation == null) return;
        ModsOperationDetail.Text = stage;
        ModsOperationProgress.IsIndeterminate = true;
        if (_operationLog != null) File.AppendAllText(_operationLog, $"{DateTime.Now:HH:mm:ss} {stage}\n");
        UpdateOperationSummary();
    }

    private IProgress<TransferProgress> FileProgress(string key)
        => new Progress<TransferProgress>(p =>
        {
            if (_modsCancellation == null) return;
            long previous = _transfers.GetValueOrDefault(key).Bytes;
            if (p.Activity.StartsWith("Подключение") || p.Activity.StartsWith("Повтор")) _networkTransfers.Add(key);
            if (_networkTransfers.Contains(key) && p.Activity is "Скачивание" or "Готово")
                _networkBytes += Math.Max(0, p.Received - previous);
            _transfers[key] = (Math.Max(previous, p.Received), Math.Max(_transfers.GetValueOrDefault(key).Total, p.Total));
            ModsOperationDetail.Text = $"{p.Activity}: {p.File}";
            long total = Math.Max(_expectedBytes, _transfers.Values.Sum(v => v.Total));
            long bytes = _transfers.Values.Sum(v => v.Bytes);
            ModsOperationProgress.IsIndeterminate = total == 0;
            if (total > 0) ModsOperationProgress.Value = Math.Min(100, bytes * 100d / total);
            UpdateOperationSummary();
        });

    private void UpdateOperationSummary()
    {
        if (_modsCancellation == null) return;
        long bytes = _transfers.Values.Sum(v => v.Bytes);
        long total = Math.Max(_expectedBytes, _transfers.Values.Sum(v => v.Total));
        string files = _totalFiles > 0 ? $"Файлы: {_completedFiles}/{_totalFiles}  ·  " : "";
        string size = total > 0 ? $"{SizeText(bytes)} / {SizeText(total)}  ·  " : "";
        string speed = _networkBytes > 0 && _operationWatch.Elapsed.TotalSeconds > 0
            ? $"{SizeText((long)(_networkBytes / _operationWatch.Elapsed.TotalSeconds))}/с  ·  " : "";
        ModsOperationSummary.Text = $"{files}{size}{speed}Время: {_operationWatch.Elapsed:mm\\:ss}";
    }

    private static string SizeText(long bytes) => bytes >= 1_048_576
        ? $"{bytes / 1048576d:0.0} МБ" : $"{bytes / 1024d:0} КБ";

    private void EndModsOperation(string result, bool success)
    {
        UpdateOperationSummary();
        _operationTimer?.Stop();
        _operationWatch.Stop();
        ModsOperationDetail.Text = result;
        ModsOperationProgress.IsIndeterminate = false;
        if (success) ModsOperationProgress.Value = 100;
        ModsCancelButton.Visibility = Visibility.Collapsed;
        ModsDismissButton.Visibility = Visibility.Visible;
        if (ModsShowProgressButton.Visibility == Visibility.Visible)
            ModsShowProgressButton.Content = success ? "Установка завершена — показать результат" : "Установка остановлена — показать результат";
        ModsRetryButton.Visibility = !success && _retryProject != null ? Visibility.Visible : Visibility.Collapsed;
        if (_operationLog != null) File.AppendAllText(_operationLog, result + "\n");
        _modsCancellation?.Dispose();
        _modsCancellation = null;
        _installingProject = false;
        _isLaunching = false;
        ModsInstanceComboBox.IsEnabled = true;
        ModsCreateButton.IsEnabled = true;
        ModsInstalledList.IsEnabled = true;
        ModsProjectsList.IsEnabled = true;
        UpdateLaunchButtonState();
        UpdateProjectStates();
        if (_closeAfterModsOperation) Dispatcher.BeginInvoke(Close);
    }

    private void CancelModsOperation_Click(object sender, RoutedEventArgs e)
    {
        _modsCancellation?.Cancel();
        ModsCancelButton.IsEnabled = false;
        ModsOperationDetail.Text = "Отмена установки...";
    }

    private void DismissModsOperation_Click(object sender, RoutedEventArgs e)
    {
        ModsOperationPanel.Visibility = Visibility.Collapsed;
        ModsShowProgressButton.Visibility = _modsCancellation != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowModsOperation_Click(object sender, RoutedEventArgs e)
    {
        ModsOperationPanel.Visibility = Visibility.Visible;
        ModsShowProgressButton.Visibility = Visibility.Collapsed;
    }

    private void ModsLog_Click(object sender, RoutedEventArgs e)
    { if (_operationLog != null && File.Exists(_operationLog)) OpenUrl(_operationLog); }
}
