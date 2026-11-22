using System.Net;
using System.Net.Sockets;
using TDU2Server.Core;
using TDU2Server.Net.Packets;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Udp;

/// <summary>
/// UDP-Server (Standard-Port 7778): empfängt Pakete und verteilt sie nach Typ (Byte 0) an ihre Klasse (je Pakettyp eine Datei,
/// <see cref="UdpPacketIds"/>). Der Server ist das Relay zwischen den Spielern: Clients senden nur an den Server (kein P2P,
/// keine offenen Ports, funktioniert hinter NAT/CGNAT). Weitergeleitet wird nur innerhalb der Lobby, entfernte Spieler
/// bekommen seltener Updates. Senden ist synchron und nicht blockierend (vorher wartete die Empfangsschleife auf jedes
/// einzelne Senden an jeden Spieler – bei mehreren Spielern stauten sich die Pakete = starke Laggs).
/// </summary>
public sealed class UdpServer
{
    const int SIO_UDP_CONNRESET = -1744830452;

    readonly ServerContext _ctx;
    readonly Dictionary<byte, UdpPacketHandler> _handlers = new();
    UdpClient _udp;
    volatile bool _running;

    public UdpServer(ServerContext ctx)
    {
        _ctx = ctx;
        Add(new PlayerStatePacket(ctx, this));
        Add(new ChatPacket(ctx, this));
        Add(new TrafficPacket(ctx, this));
        Add(new PingPacket(ctx, this));
        Add(new RelayPacket(ctx, this));
        Add(new StorePacket(ctx, this));
        // Store-Pakete: Instanzen für [PacketHandler]-Konstruktoren, Sende-Helfer, Handler-Klassen
        Injector.RegisterInstance(ctx);
        Injector.RegisterInstance(this);
        PacketSender.Init(ctx, this);
        PacketRouter.RegisterHandlers<ServerPackets>();
    }

    // Datenmenge (Konsole "traffic"): gesendet gesamt und davon Relay (Mitspieler ohne Direktverbindung)
    long _bytesOut, _packetsOut, _relayBytes;
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public long PacketsOut => Interlocked.Read(ref _packetsOut);
    public long RelayBytes => Interlocked.Read(ref _relayBytes);
    public void CountRelay(int bytes) => Interlocked.Add(ref _relayBytes, bytes + 28);
    public DateTime Since { get; } = DateTime.UtcNow;

    void Add(UdpPacketHandler h) => _handlers[h.Type] = h;

    public void Start()
    {
        int port = _ctx.Config.udpPort;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            try { _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }   // Windows: ICMP-Fehler ignorieren
            int buf = Math.Max(256 * 1024, _ctx.Config.udpBufferBytes);
            try { _udp.Client.ReceiveBufferSize = buf; _udp.Client.SendBufferSize = buf; } catch { }
            _udp.Client.Blocking = false;
            _running = true;
            _ = Task.Run(LoopAsync);
            Log.Info($"UDP-Synchronisation auf Port {port}");
        }
        catch (Exception ex) { Log.Info("UDP-Port " + port + " nicht nutzbar: " + ex.Message); }
    }

    public void Stop()
    {
        _running = false;
        try { _udp?.Dispose(); } catch { }
    }

    async Task LoopAsync()
    {
        while (_running)
        {
            UdpReceiveResult rr;
            try { rr = await _udp.ReceiveAsync(); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            var d = rr.Buffer;
            if (d.Length == 0 || !_handlers.TryGetValue(d[0], out var h)) continue;
            if (d.Length < h.MinLength || d.Length > h.MaxLength) continue;
            try { await h.HandleAsync(d, rr.RemoteEndPoint); }
            catch (Exception ex) { Log.Info($"UDP {rr.RemoteEndPoint}: {ex.Message}"); }
        }
    }

    /// <summary>Ein Paket senden (synchron, blockiert nicht; Fehler/volle Puffer werden ignoriert – UDP darf verlieren).</summary>
    public void Send(ReadOnlyMemory<byte> data, IPEndPoint to)
    {
        if (to == null || _udp == null) return;
        try
        {
            _udp.Client.SendTo(data.Span, SocketFlags.None, to);
            Interlocked.Add(ref _bytesOut, data.Length + 28);   // + IP/UDP-Kopf
            Interlocked.Increment(ref _packetsOut);
        }
        catch (Exception) { }
    }

    /// <summary>Kompatibilität: wie <see cref="Send"/>.</summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint to) { Send(data, to); return Task.CompletedTask; }

    /// <summary>
    /// An alle anderen aktiven Spieler derselben Lobby weiterleiten. range &gt; 0: Spieler weiter weg bekommen nur jedes
    /// divider-te Paket (rangeOnlyOrDrop: gar keins).
    /// </summary>
    public void Relay(Session sender, ReadOnlyMemory<byte> data, double activeSeconds, float range = 0f, int divider = 1, bool dropFar = false)
    {
        var now = DateTime.UtcNow;
        bool farTurn = divider <= 1 || sender.RelayCount % (uint)divider == 0;
        float r2 = range * range;
        foreach (var o in _ctx.Sessions.Players)
        {
            if (o == sender || o.UdpEndPoint == null || !o.UdpActive(now, activeSeconds) || !LobbyManager.SameLobby(sender, o)) continue;
            if (range > 0f && sender.InCar && o.InCar)
            {
                float dx = o.Px - sender.Px, dz = o.Pz - sender.Pz;
                if (dx * dx + dz * dz > r2 && (dropFar || !farTurn)) continue;
            }
            Send(data, o.UdpEndPoint);
        }
    }

    /// <summary>
    /// My House: Einrichtung von <paramref name="owner"/> geändert -> [11][u16 Besitzer][u32 Änderungszähler] an alle Spieler seiner
    /// Lobby (zweimal, UDP darf verlieren). Gäste in seinem Haus holen die Einrichtung dann per "houseget"; wer das Paket verpasst,
    /// sieht den neuen Zähler mit dem nächsten "status" (PlayerEntry.houseVer).
    /// </summary>
    public void NotifyHouse(Session owner)
    {
        var o = new byte[7];
        o[0] = UdpPacketIds.HouseOut;
        o[1] = (byte)owner.PlayerId; o[2] = (byte)(owner.PlayerId >> 8);
        BitConverter.TryWriteBytes(new Span<byte>(o, 3, 4), (uint)owner.HouseVer);
        var now = DateTime.UtcNow;
        foreach (var x in _ctx.Sessions.Players)
        {
            if (x == owner || x.UdpEndPoint == null || !x.UdpActive(now, 30)) continue;
            if (!LobbyManager.SameLobby(owner, x) && x.RideWith != owner.PlayerId && x.Passenger != owner.PlayerId) continue;
            for (int i = 0; i < 2; i++) Send(o, x.UdpEndPoint);
        }
    }

    /// <summary>
    /// Neue Einladung, Antwort (joined/declined/accepted) oder Meldung für <paramref name="target"/>: [12][u16 0] zweimal (UDP kann
    /// verloren gehen; sonst kommt sie mit dem nächsten regulären "status"). Im Original kommen Einladung und Antwort sofort per P2P.
    /// </summary>
    public void NotifyStatus(Session target)
    {
        if (target.UdpEndPoint == null || !target.UdpActive(DateTime.UtcNow, 30)) return;
        var o = new byte[3] { UdpPacketIds.StatusOut, 0, 0 };
        for (int i = 0; i < 2; i++) Send(o, target.UdpEndPoint);
    }

    /// <summary>Kompatibilität (alte Aufrufer): ohne Entfernungsfilter.</summary>
    public Task RelayAsync(Session sender, ReadOnlyMemory<byte> data, double activeSeconds) { Relay(sender, data, activeSeconds); return Task.CompletedTask; }
}
