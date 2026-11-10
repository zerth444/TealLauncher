using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace TealLauncher;

public partial class InstanceContentsWindow : Window
{
    private readonly string _instanceDirectory;

    public InstanceContentsWindow(string instanceDirectory, string instanceName)
    {
        InitializeComponent();
        _instanceDirectory = instanceDirectory;
        TitleText.Text = $"Содержимое: {instanceName}";
        LoadContents();
    }

    private void LoadContents()
    {
        ContentsPanel.Children.Clear();
        AddGroup("Моды", "mods");
        AddGroup("Ресурспаки", "resourcepacks");
        AddGroup("Шейдеры", "shaderpacks");

        if (ContentsPanel.Children.Count == 0)
        {
            ContentsPanel.Children.Add(new TextBlock
            {
                Text = "В инстансе пока нет модов, ресурспаков или шейдеров.",
                FontFamily = new System.Windows.Media.FontFamily("Arial"),
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }
    }

    private void AddGroup(string title, string folderName)
    {
        string directory = Path.Combine(_instanceDirectory, folderName);

        if (!Directory.Exists(directory))
        {
            return;
        }

        string[] files = Directory.GetFileSystemEntries(directory)
            .Where(file => file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(file) && (File.Exists(Path.Combine(file, "pack.mcmeta")) || Directory.Exists(Path.Combine(file, "shaders"))))
            .OrderBy(file => file)
            .ToArray();

        if (files.Length == 0)
        {
            return;
        }

        ContentsPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new System.Windows.Media.FontFamily("Arial"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(34, 34, 34)),
            Margin = new Thickness(0, 8, 0, 4)
        });

        foreach (string file in files)
        {
            bool enabled = InstanceContentManager.IsEnabled(_instanceDirectory, file);
            var checkBox = new CheckBox
            {
                Content = GetDisplayName(file, enabled),
                IsChecked = enabled,
                Tag = file,
                FontFamily = new System.Windows.Media.FontFamily("Arial"),
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(34, 34, 34)),
                Margin = new Thickness(4, 7, 8, 7)
            };

            checkBox.Checked += ContentCheckBox_Changed;
            checkBox.Unchecked += ContentCheckBox_Changed;
            var row = new DockPanel();
            var remove = new Button { Content = "Удалить", Tag = file, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 3, 0, 3) };
            DockPanel.SetDock(remove, Dock.Right);
            remove.Click += (_, _) =>
            {
                string current = checkBox.Tag?.ToString() ?? file;
                if (MessageBox.Show(this, $"Удалить {Path.GetFileName(current)}? Зависимые моды могут перестать работать.", "Удаление содержимого", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                try
                {
                    string disabled = InstanceContentManager.SetEnabled(_instanceDirectory, current, false);
                    if (Directory.Exists(disabled)) Directory.Delete(disabled, true); else File.Delete(disabled);
                    LoadContents();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось удалить файл"); }
            };
            row.Children.Add(remove); row.Children.Add(checkBox);
            ContentsPanel.Children.Add(row);
        }
    }

    private static string GetDisplayName(string path, bool enabled)
    {
        string fileName = Path.GetFileName(path);

        if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^".disabled".Length];
        }

        return enabled ? fileName : $"{fileName} (выключен)";
    }

    private void ContentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string path } checkBox)
        {
            return;
        }

        bool wasEnabled = InstanceContentManager.IsEnabled(_instanceDirectory, path);
        try
        {
            bool enabled = checkBox.IsChecked == true;
            string targetPath = InstanceContentManager.SetEnabled(_instanceDirectory, path, enabled);
            checkBox.Tag = targetPath;

            checkBox.Content = GetDisplayName(targetPath, enabled);
            if (Path.GetFileName(Path.GetDirectoryName(targetPath)) == "shaderpacks") LoadContents();
        }
        catch (Exception ex)
        {
            checkBox.Checked -= ContentCheckBox_Changed;
            checkBox.Unchecked -= ContentCheckBox_Changed;
            checkBox.IsChecked = wasEnabled;
            checkBox.Checked += ContentCheckBox_Changed;
            checkBox.Unchecked += ContentCheckBox_Changed;
            MessageBox.Show(
                this,
                $"Не удалось изменить состояние файла:\n{ex.Message}",
                "Содержимое инстанса",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_instanceDirectory) { UseShellExecute = true });

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadContents();
}
