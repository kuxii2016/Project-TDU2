using Microsoft.Win32;
using System.Windows;

namespace Paradise.Launcher;

public partial class SettingsWindow : Window
{
    private readonly LauncherConfig _config;

    public SettingsWindow(LauncherConfig config)
    {
        InitializeComponent();
        _config = config;
        MasterBox.Text = config.MasterUrl;
        NameBox.Text = config.PlayerName;
        ExeBox.Text = config.GameExe;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Programm (*.exe)|*.exe|Alle Dateien|*.*" };
        if (dlg.ShowDialog() == true) ExeBox.Text = dlg.FileName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _config.MasterUrl = MasterBox.Text.Trim();
        _config.PlayerName = NameBox.Text.Trim();
        _config.GameExe = ExeBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
