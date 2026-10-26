using System.Windows;
using System.Windows.Input;

namespace TLauncherRemake;

public partial class MainWindow : Window
{
    private Rect _restoreBounds;
    private bool _isMaximized;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            e.Handled = true;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PageScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NewsScrollViewer is not null)
        {
            NewsScrollViewer.ScrollToVerticalOffset(e.NewValue);
        }
    }

    private void ToggleMaximize()
    {
        if (_isMaximized)
        {
            _isMaximized = false;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
        }
        else
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);

            Rect workArea = SystemParameters.WorkArea;
            Left = workArea.Left;
            Top = workArea.Top;
            Width = workArea.Width;
            Height = workArea.Height;
            _isMaximized = true;
        }

        MaximizeRestoreButton.Content = _isMaximized ? "❐" : "□";
        MaximizeRestoreButton.ToolTip = _isMaximized ? "Восстановить" : "Развернуть";
    }
}
