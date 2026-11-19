using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace Paradise.Core.Net;

/// <summary>
/// Client für den MasterServer. Wird vom Launcher (Liste holen) und vom
/// Game-Server (anmelden + Heartbeat) genutzt.
/// </summary>
public sealed class MasterClient : IDisposable
{
    private readonly HttpClient _http;

    public MasterClient(string masterBaseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(masterBaseUrl), Timeout = TimeSpan.FromSeconds(8) };
    }

    // ---- Launcher-Seite ----

    public async Task<List<ServerInfo>> GetServersAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetFromJsonAsync<ServerListResponse>(MasterApi.Servers, ct);
        return resp?.Servers ?? new List<ServerInfo>();
    }

    // ---- Game-Server-Seite ----

    public async Task<RegisterResponse> RegisterAsync(ServerInfo info, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync(MasterApi.Register, info, ct);
        return (await r.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken: ct))
               ?? new RegisterResponse { Ok = false, Error = "leere Antwort" };
    }

    public async Task HeartbeatAsync(HeartbeatRequest hb, CancellationToken ct = default)
        => await _http.PostAsJsonAsync(MasterApi.Heartbeat, hb, ct);

    public async Task UnregisterAsync(HeartbeatRequest hb, CancellationToken ct = default)
        => await _http.PostAsJsonAsync(MasterApi.Unregister, hb, ct);

    public void Dispose() => _http.Dispose();

    // ---- Ping direkt gegen den Game-Server (nicht über den Master) ----

    /// <summary>
    /// Grobe Latenzmessung per TCP-Connect-Zeit. Platzhalter: sobald der
    /// UDP-Ping des Spielprotokolls (PingPacketOut/PongPacketIn) steht, hier
    /// darauf umstellen. Gibt -1 bei Timeout/Fehler.
    /// </summary>
    public static async Task<int> PingAsync(string host, int port, int timeoutMs = 1500)
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var sw = Stopwatch.StartNew();
            var connect = sock.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect) return -1;
            await connect; // wirft, falls fehlgeschlagen
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return -1; }
    }
}