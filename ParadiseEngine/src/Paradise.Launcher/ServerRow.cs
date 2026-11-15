using Paradise.Core.Net;
using System.ComponentModel;

namespace Paradise.Launcher;

/// <summary>Zeilen-Ansichtsmodell für die Serverliste (DataGrid-Bindung).</summary>
public sealed class ServerRow : INotifyPropertyChanged
{
    public ServerInfo Info { get; }

    public ServerRow(ServerInfo info) { Info = info; }

    public string Name => Info.Name;
    public string ModMessage => Info.ModMessage;
    public string Players => $"{Info.Players} / {Info.MaxPlayers}";
    public string Version => Info.Version;
    public string Endpoint => Info.Endpoint;
    public bool Locked => Info.Locked;

    private int _ping = -1;
    public int PingMs
    {
        get => _ping;
        set { _ping = value; OnChanged(nameof(PingMs)); OnChanged(nameof(PingText)); }
    }

    public string PingText => _ping < 0 ? "–" : $"{_ping} ms";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
