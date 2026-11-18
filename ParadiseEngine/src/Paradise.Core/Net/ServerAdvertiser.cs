using Paradise.Core.Diagnostics;

namespace Paradise.Core.Net;

/// <summary>
/// Läuft auf einem (Host-)Server: meldet den Server beim MasterServer an und
/// hält ihn per Heartbeat in der Liste. Die aktuelle Spielerzahl liefert ein
/// Callback, damit der Advertiser nichts über das Spiel wissen muss.
/// </summary>
public sealed class ServerAdvertiser : IAsyncDisposable
{
    private readonly MasterClient _client;
    private readonly ServerInfo _info;
    private readonly Func<int> _currentPlayers;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _token = "";

    public ServerAdvertiser(string masterUrl, ServerInfo info, Func<int> currentPlayers)
    {
        _client = new MasterClient(masterUrl);
        _info = info;
        _currentPlayers = currentPlayers;
    }

    public async Task StartAsync()
    {
        // Eigene ID vor der Anmeldung festlegen, damit Heartbeats sie sicher kennen.
        if (string.IsNullOrEmpty(_info.Id))
            _info.Id = Guid.NewGuid().ToString("N");

        var resp = await _client.RegisterAsync(_info);
        if (!resp.Ok)
        {
            Log.Warn($"Master-Anmeldung abgelehnt: {resp.Error}");
            return;
        }
        _token = resp.Token;
        Log.Info($"Beim Master angemeldet ({_info.Name}).");

        _cts = new CancellationTokenSource();
        _loop = HeartbeatLoop(_cts.Token);
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _client.HeartbeatAsync(new HeartbeatRequest
                {
                    Id = _info.Id,
                    Token = _token,
                    Players = _currentPlayers(),
                    MaxPlayers = _info.MaxPlayers,
                    ModMessage = _info.ModMessage
                }, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Debug($"Heartbeat fehlgeschlagen: {ex.Message}");
            }

            try { await Task.Delay(MasterApi.HeartbeatInterval, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
        try
        {
            await _client.UnregisterAsync(new HeartbeatRequest { Id = _info.Id, Token = _token });
        }
        catch { /* Master evtl. weg – egal */ }
        _client.Dispose();
    }
}