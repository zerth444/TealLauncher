using System.Windows;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;

namespace TLauncherRemake;

public partial class AccountLoginWindow : Window
{
    private readonly JELoginHandler _loginHandler;

    public MSession? Session { get; private set; }

    public AccountLoginWindow(JELoginHandler loginHandler)
    {
        _loginHandler = loginHandler;
        InitializeComponent();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        StatusText.Text = "Открываю окно входа Microsoft...";

        try
        {
            Session = await _loginHandler.AuthenticateInteractively();

            if (Session is null || string.IsNullOrWhiteSpace(Session.Username))
            {
                throw new InvalidOperationException("Microsoft не вернул профиль Minecraft.");
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось войти. Проверьте аккаунт и повторите попытку.";
            LoginButton.IsEnabled = true;

            MessageBox.Show(
                this,
                $"Не удалось авторизоваться:\n{ex.Message}\n\nДля входа нужен установленный Microsoft Edge WebView2 Runtime.",
                "Вход в аккаунт Minecraft",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
