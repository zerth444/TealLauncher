using System.IO;
using System.Windows.Threading;

namespace TLauncherRemake;

public partial class MainWindow
{
    private readonly string _preferencesPath;
    private DispatcherTimer? _nicknameSaveTimer;

    private void InitializeNicknamePersistence()
    {
        EmailTextBox.Text = LauncherPreferences.Load(_preferencesPath).Nickname;
        _nicknameSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _nicknameSaveTimer.Tick += (_, _) => { _nicknameSaveTimer.Stop(); SaveNickname(); };
        EmailTextBox.TextChanged += (_, _) => { _nicknameSaveTimer.Stop(); _nicknameSaveTimer.Start(); };
        Closed += (_, _) => { _nicknameSaveTimer.Stop(); SaveNickname(); };
    }

    private void SaveNickname()
    {
        try { LauncherPreferences.SaveNickname(_preferencesPath, EmailTextBox.Text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ModsSelectionInfo.Text = "Не удалось сохранить никнейм: " + ex.Message; }
    }
}
