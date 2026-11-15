using Paradise.Core.Net;
using Paradise.Core.Platform;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Paradise.Launcher;

public partial class MainWindow : Window
{
    private readonly LauncherConfig _config = LauncherConfig.Load();
    private readonly ObservableCollection<ServerRow> _rows = new();

    public MainWindow()
    {
        InitializeComponent();
        ServerGrid.ItemsSource = _rows;
        NameBox.Text = _config.PlayerName;
        _ = RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        StatusText.Text = "Lade Serverliste …";
        RefreshButton.IsEnabled = false;
        _rows.Clear();
        try
        {
            using var client = new MasterClient(_config.MasterUrl);
            var servers = await client.GetServersAsync();
            foreach (var s in servers) _rows.Add(new ServerRow(s));
            StatusText.Text = $"{servers.Count} Server";

            // Ping im Hintergrund nachtragen
            foreach (var row in _rows)
            {
                int ping = await MasterClient.PingAsync(row.Info.Host, row.Info.Port);
                row.PingMs = ping;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Master nicht erreichbar";
            MessageBox.Show($"Serverliste konnte nicht geladen werden:\n{ex.Message}",
                "Paradise Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private void OnConnect(object sender, RoutedEventArgs e) => Connect();
    private void OnConnect(object sender, MouseButtonEventArgs e) => Connect();

    private void Connect()
    {
        if (ServerGrid.SelectedItem is not ServerRow row)
        {
            MessageBox.Show("Bitte einen Server auswählen.", "Paradise Launcher");
            return;
        }

        string name = string.IsNullOrWhiteSpace(NameBox.Text) ? Environment.UserName : NameBox.Text.Trim();
        _config.PlayerName = name;
        _config.Save();

        var opts = LaunchOptionsBuilder.For(row.Info.Host, row.Info.Port, name);
        string exe = _config.ResolveGameExe();

        if (!System.IO.File.Exists(exe))
        {
            MessageBox.Show($"Spiel-exe nicht gefunden:\n{exe}\n\nUnter Einstellungen den Pfad setzen.",
                "Paradise Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = opts.ToCommandLine(),
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Start fehlgeschlagen:\n{ex.Message}", "Paradise Launcher",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_config) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _config.Save();
            _ = RefreshAsync();
        }
    }
}

/// <summary>Kleiner Helfer, weil LaunchOptions bewusst nur über Parse() befüllt wird.</summary>
internal static class LaunchOptionsBuilder
{
    public static LaunchOptions For(string host, int port, string name)
        => LaunchOptions.Parse(new[] { "--server", $"{host}:{port}", "--name", name });
}
