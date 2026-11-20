using Paradise.Core.Diagnostics;
using Paradise.Core.Net;
using Paradise.Core.Online;
using Paradise.Core.Online.Net;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using Paradise.Game.Input;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;

namespace Paradise.Game.Online;

/// <summary>
/// Spieler-Synchronisation über UDP (TDU2Server udpPort) – Port von Unity TDU2Multiplayer (Netzteil; Darstellung der
/// Mitspieler-Autos: <see cref="RemoteCars"/>, Chat-Oberfläche: <see cref="Chat"/>, Namen: <see cref="NameTags"/>).
///   Senden (~20/s): Position/Richtung/Geschwindigkeit (Spielkoordinaten), Lenkung, Drehzahl/Motormoment, Auto (DB-Id) + Lack,
///   Lichter (Licht, Bremse, Rückwärts, Blinker, Warnblinker, Lichthupe), Hupe, Sendezeit.
/// Paket (Client -> Server): [1][u16 Spieler][u32 Schlüssel][Zustand], Server -> Client: [2][u16 Spieler][Zustand].
/// Zustand: +0 u32 seq, +4 i32 carId, +8 u8 Lack, +9 u8 Flags (1 im Auto, 2 Bremse, 4 rückwärts, 8 Blinker l, 16 Blinker r,
/// 32 Warnblinker), +10 u8 Licht, +11 u8 Flags2 (1 Hupe, 2 Lichthupe), +12 Pos, +24 Vorne, +36 Oben, +48 Geschw. (je xyz),
/// +60 Lenkwinkel, +64 km/h, +68 Drehzahl, +72 Motormoment, +76 u32 Sendezeit (ms), +80 u8 Insel.
/// Erweiterung Lackierung/Beklebung: +81 u8 Version (1), +82 u32 Hash der codierten CarLivery (0 = keine), +86 u16 Gesamtlänge,
/// +88 u16 Versatz, +90 u8 Länge, +91 Daten (≤ 96 Byte) – jedes Paket trägt das nächste Stück (Karussell).
/// Danach Verschmutzung: u8 0xD1, u8 Schmutz, u8 Kratzer, u8 Flags (1 = nass).
/// Danach (optional, nur im Interieur) Raum: u8 0xA1, u32 Raumkennung (InteriorUtils.PresenceKey), f32 x,y,z (Füße im Raum der
/// Interieur-Szene), f32 Blickrichtung (Grad um Y), u8 Flags (1 = geht); danach Ausstellungsauto: u8 0xA2, i32 Auto (DB-Id),
/// u8 Zubehör-Bits, u8 Flags (1 sitzt, 2 Motor an, 4 Hupe) – Mitspieler im selben Raum erscheinen als Spielfigur (RoomAvatars).
/// Danach (optional, nur im Auto) Ausstattung: u8 0xE1, u8 Bremsscheiben-Wärme FL/FR/RL/RR, u8 Zubehör-Bits, i32 Felge, i32 Innenraum.
/// Koordinaten: Spielraum der Insel = Unity MapRoot-lokal = Engine "Game" (VehicleManager.PositionGame) – kompatibel zu Unity-Clients.
/// </summary>
public static class Multiplayer
{
    /// <summary>Private Sitzung (z.B. Rennschul-Prüfung): ohne Auto gemeldet, sieht selbst keine Mitspieler (Unity Hidden).</summary>
    public static bool Hidden;
    /// <summary>Mitspieler nicht anzeigen (z.B. Sticker-Shop), man selbst bleibt verbunden (Unity HideRemotes).</summary>
    public static bool HideRemotes;

    const int StateSize = 81;      // +80: Insel (1 Ibiza, 2 Hawaii)
    const int LiveryChunk = 96, LiveryExt = 10;
    internal const int RoomCarExt = 7; internal const byte RoomCarTag = 0xA2;
    internal const int ExtraExt = 14; internal const byte ExtraTag = 0xE1;
    internal const int RoomExt = 22; internal const byte RoomTag = 0xA1;
    internal const int DirtExt = 4; internal const byte DirtTag = 0xD1;

    static readonly byte[] _out = new byte[7 + StateSize + LiveryExt + LiveryChunk + DirtExt + RoomExt + RoomCarExt + ExtraExt];
    static readonly PlayerStatePacketIn _stateIn = new();
    static readonly ChatPacketIn _chatPk = new();
    static readonly TrafficPacketIn _trafficPk = new();
    static readonly HousePacketIn _housePk = new();
    static readonly StatusPacketIn _statusPk = new();
    static readonly StorePacketIn _storePk = new();
    static readonly PacketContext _storeCtx = new();
    static bool _packetsRegistered;
    static Vector3 _roomLast; static float _roomLastT; static bool _hasRoomLast;
    static PongPacketIn? _pong;
    static UdpDispatcher? _dispatch;
    static UdpClient? _udp;
    static Thread? _recv;
    static volatile bool _run;
    static IPEndPoint? _server;
    static string? _serverKey;
    static float _clockF, _nextSend, _nextPing, _nextServerState, _nextMeshLog;
    static int _lastMeshAll = -1, _lastMeshDirect = -1;
    static uint _seq;
    static Vector3 _lastPos; static float _lastPosT; static bool _hasLast;
    static int _livOffset;
    static string? _livCar; static byte[]? _livBlob; static uint _livHash;
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static double Now => _clock.Elapsed.TotalSeconds;
    static IGameModeContext? _ctx;

    // Echte Traffic-Zähler (Dev-Menü): Server-UDP und direkte P2P-Pakete. Relay/Punch zählt TDU2PeerMesh.
    static long _serverPacketsIn, _serverBytesIn, _serverPacketsOut, _serverBytesOut;
    static long _directPacketsIn, _directBytesIn, _udpSendErrors, _udpRecvErrors;

    /// <summary>Laufzeit zum Server (geglättet, ms); -1 = unbekannt.</summary>
    public static double PingMs => _pong != null ? _pong.SmoothRttMs : -1;
    /// <summary>Lobby laut Server (aus dem Ping).</summary>
    public static int PingLobby => _pong?.Lobby ?? 0;
    /// <summary>Letzte gemessene UDP-Laufzeit zum Server (ms); -1 = noch keine Antwort.</summary>
    public static double PingLastMs => _pong?.RttMs ?? -1;
    /// <summary>UDP-Socket und Server-Endpunkt stehen.</summary>
    public static bool ServerSocketUp => _udp != null && _server != null;
    /// <summary>IP:Port des UDP-Servers; "-" solange nicht verbunden.</summary>
    public static string ServerEndpoint => _server?.ToString() ?? "-";
    /// <summary>Server-UDP: Pakete und Bytes seit Verbindung.</summary>
    public static long ServerPacketsIn => Interlocked.Read(ref _serverPacketsIn);
    public static long ServerBytesIn => Interlocked.Read(ref _serverBytesIn);
    public static long ServerPacketsOut => Interlocked.Read(ref _serverPacketsOut);
    public static long ServerBytesOut => Interlocked.Read(ref _serverBytesOut);
    /// <summary>Direkt von Mitspielern empfangene P2P-Pakete (nicht über den Relay-Umschlag).</summary>
    public static long DirectPacketsIn => Interlocked.Read(ref _directPacketsIn);
    public static long DirectBytesIn => Interlocked.Read(ref _directBytesIn);
    /// <summary>Fehlgeschlagene UDP-Sendements / Empfangsfehler des Receive-Loops.</summary>
    public static long UdpSendErrors => Interlocked.Read(ref _udpSendErrors);
    public static long UdpRecvErrors => Interlocked.Read(ref _udpRecvErrors);
    /// <summary>Pakete, die der UdpDispatcher angenommen bzw. verworfen hat.</summary>
    public static long DispatchReceived => _dispatch?.Received ?? 0;
    public static long DispatchDropped => _dispatch?.Dropped ?? 0;
    /// <summary>Aktuell wartende Verkehrspakete im Empfangs-Queue.</summary>
    public static int TrafficInboxCount => _trafficPk.Queue.Count;
    /// <summary>P2P zu den Lobby-Mitspielern (Hole-Punching, Relay des Servers als Rückfall).</summary>
    public static TDU2PeerMesh? Mesh { get; private set; }
    /// <summary>Lokaler UDP-Endpunkt "ip:port" (für Mitspieler im selben LAN), leer bis zum Verbinden.</summary>
    public static string LocalEndpoint { get; private set; } = "";
    /// <summary>Spieluhr (Summe der Bildzeiten) – Alter der Pakete (Remote.LastPacket).</summary>
    internal static float FrameClock => _clockF;
    /// <summary>Uhr dieses Clients (Sekunden) – Zeitbasis der Pakete.</summary>
    public static double Clock => Now;
    public static bool CanSend => _udp != null && _server != null && TDU2Session.LoggedIn && TDU2Session.PlayerId != 0;
    /// <summary>Empfangene Verkehrspakete ([6][u16 Absender][Nutzdaten]) mit Ankunftszeit (<see cref="Clock"/>).</summary>
    public static ConcurrentQueue<(byte[] data, double t)> TrafficInbox => _trafficPk.Queue;
    /// <summary>Chatnachricht empfangen (Spieler-Nr., Name, Text) – auch die eigene (Bestätigung vom Server).</summary>
    public static event Action<int, string, string>? ChatReceived;
    /// <summary>My House: Einrichtung von Spieler (Nummer) geändert, Änderungszähler (UDP [11]).</summary>
    public static event Action<int, uint>? HouseChanged;

    // ------------------------------------------------------------------ jedes Bild (ScreenModule, läuft in jedem Modus)
    public static void Update(IGameModeContext ctx, float dt)
    {
        _ctx = ctx;
        _clockF += dt;
        EnsureSocket();
        Receive(ctx);
        ReceiveChat();
        ReceiveHouse();
        if (_statusPk.Queue.TryDequeue(out _)) { while (_statusPk.Queue.TryDequeue(out _)) { } TDU2GameData.RequestStatusNow(); }   // Einladung/Antwort
        ReceiveStorePackets();
        Mesh?.Tick();
        if (Mesh != null && Mesh.Active && _clockF >= _nextMeshLog)
        {
            _nextMeshLog = _clockF + 30f;
            int all = Mesh.PeerCount, direct = Mesh.DirectCount;
            if (all != _lastMeshAll || direct != _lastMeshDirect)
            {
                _lastMeshAll = all; _lastMeshDirect = direct;
                Log.Info($"P2P: {direct}/{all} Mitspieler direkt, {all - direct} über das Relay des Servers");
            }
        }
        // Ping alle 2 s: Laufzeit messen, NAT/CGNAT-Zuordnung offen halten (auch ohne Zustandspakete, z.B. im Menü)
        if (_udp != null && _server != null && TDU2Session.LoggedIn && TDU2Session.PlayerId != 0 && _clockF >= _nextPing)
        {
            _nextPing = _clockF + 2f;
            var ping = PingPacketOut.Build(TDU2Session.PlayerId, TDU2Session.UdpKey, (uint)(Now * 1000.0));
            UdpSend(ping, ping.Length, _server, true);
        }
        if (_udp != null && _server != null && TDU2Session.LoggedIn && TDU2Session.PlayerId != 0 && _clockF >= _nextSend)
        {
            _nextSend = _clockF + 1f / MathF.Max(2f, OnlineConfig.SendRate);
            SendState(ctx);
        }
        RemoteCars.Update(ctx, dt);
    }

    // ------------------------------------------------------------------ Netz
    /// <summary>
    /// Zentraler UDP-Sendepfad zum Server. Zählt echte Pakete/Bytes für das Dev-Menü und meldet Sendefehler,
    /// statt sie still zu schlucken. P2P-Direkt- und Relay-Pakete zählt TDU2PeerMesh selbst.
    /// </summary>
    static bool UdpSend(byte[] b, int len, IPEndPoint to, bool toServer)
    {
        if (_udp == null || to == null) return false;
        try { _udp.Send(b, len, to); }
        catch (Exception) { Interlocked.Increment(ref _udpSendErrors); return false; }
        if (toServer || to.Equals(_server))
        {
            Interlocked.Increment(ref _serverPacketsOut);
            Interlocked.Add(ref _serverBytesOut, len);
        }
        return true;
    }

    static void EnsureSocket()
    {
        if (!TDU2Session.LoggedIn || TDU2Session.Offline || TDU2Session.Client == null) { CloseSocket(); return; }

        RegisterPacketHandlers();

        string host = TDU2Session.Client.Host;
        int port = TDU2Session.UdpPort > 0 ? TDU2Session.UdpPort : 7778;
        string key = host + ":" + port;
        if (key == _serverKey) return;
        CloseSocket();
        _serverKey = key;
        try
        {
            var addrs = Dns.GetHostAddresses(host);
            IPAddress? ip = null;
            foreach (var a in addrs) if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
            if (ip == null && addrs.Length > 0) ip = addrs[0];
            if (ip == null) return;
            _server = new IPEndPoint(ip, port);
            // fester lokaler Port (derselbe für Server und Mitspieler – sonst klappt Hole-Punching nicht)
            var udp = new UdpClient(new IPEndPoint(ip.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            try { udp.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch (Exception) { }   // Windows: ICMP "Port nicht erreichbar" ignorieren
            try { udp.Client.ReceiveBufferSize = 1 << 20; udp.Client.SendBufferSize = 256 * 1024; } catch (Exception) { }
            _udp = udp;
            Mesh ??= new TDU2PeerMesh(() => Now);
            Mesh.Bind(udp);
            LocalEndpoint = DetectLocalEndpoint(ip, ((IPEndPoint)udp.Client.LocalEndPoint!).Port);
            if (_dispatch == null)
            {
                _pong = new PongPacketIn(() => Now * 1000.0);
                _dispatch = new UdpDispatcher().Register(_stateIn).Register(_chatPk).Register(_trafficPk).Register(_pong).Register(_housePk).Register(_statusPk).Register(_storePk);
            }
            _run = true;
            _recv = new Thread(() => ReceiveLoop(udp)) { IsBackground = true, Name = "TDU2 UDP" };
            _recv.Start();
            Log.Info($"UDP: verbunden mit {_server} (lokal {LocalEndpoint})");
        }
        catch (Exception ex) { Log.Warn("UDP: " + ex.Message); _udp = null; }
    }

    // ------------------------------------------------------------------ Store-Pakete (Core/Net Store + PacketRouter)
    /// <summary>[PacketHandler]-Klassen des Clients einmal anmelden (Instanzen über den Injector).</summary>
    static void RegisterPacketHandlers()
    {
        if (_packetsRegistered) return;
        _packetsRegistered = true;
        PacketRouter.RegisterHandlers<CustomPacket>();
    }

    /// <summary>Empfangene Store-Pakete im Hauptthread an ihre Handler (PacketRouter.Handle).</summary>
    static void ReceiveStorePackets()
    {
        int n = 0;
        while (n++ < 256 && _storePk.Queue.TryDequeue(out var p))
        {
            var d = p.data;
            _storeCtx.EndPoint = _server;
            _storeCtx.SenderId = d[1] | (d[2] << 8);
            _storeCtx.Sender = null;
            PacketRouter.HandleRaw(d, UdpPacketId.ServerHeader, d.Length - UdpPacketId.ServerHeader, _storeCtx);
        }
    }

    /// <summary>
    /// Store-Paket senden (Kennung steht am Anfang des Stores: <c>new Store(magic)</c>): Ziel <see cref="UdpPacketId.ToServer"/>,
    /// <see cref="UdpPacketId.ToLobby"/> (Server leitet an alle anderen der Lobby weiter) und/oder <see cref="UdpPacketId.ToPlayer"/>
    /// (nur an <paramref name="toPlayer"/>). false = nicht verbunden oder zu groß.
    /// </summary>
    internal static bool SendStore(Store store, byte target, int toPlayer = 0)
    {
        if (store == null || !CanSend || _udp == null || _server == null) return false;
        var seg = store.AsSegment();
        if (seg.Count < 2 || seg.Count > UdpPacketId.MaxStorePayload)
        {
            Log.Warn($"Store-Paket {store.Magic}: {seg.Count} Byte – erlaubt sind 2..{UdpPacketId.MaxStorePayload}");
            return false;
        }
        var b = new byte[UdpPacketId.ClientHeader + 3 + seg.Count];
        UdpPacketId.WriteClientHeader(b, UdpPacketId.PacketIn, TDU2Session.PlayerId, TDU2Session.UdpKey);
        b[7] = target;
        b[8] = (byte)toPlayer; b[9] = (byte)(toPlayer >> 8);
        Buffer.BlockCopy(seg.Array!, seg.Offset, b, 10, seg.Count);
        return UdpSend(b, b.Length, _server, true);
    }

    static void ReceiveLoop(UdpClient udp)
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (_run)
        {
            try
            {
                var d = udp.Receive(ref any);
                if (d == null || d.Length == 0) continue;
                var from = any;
                if (from.Equals(_server))
                {
                    Interlocked.Increment(ref _serverPacketsIn);
                    Interlocked.Add(ref _serverBytesIn, d.Length);
                    _dispatch?.Dispatch(d, Now);
                    continue;
                }
                // direkt von einem Mitspieler (P2P): Punch oder Zustand/Verkehr von einem bekannten Endpunkt
                Interlocked.Increment(ref _directPacketsIn);
                Interlocked.Add(ref _directBytesIn, d.Length);
                var mesh = Mesh;
                if (mesh == null) continue;
                if (d[0] == UdpPacketId.PeerPunch) { mesh.OnPunch(d, from); continue; }
                if ((d[0] == UdpPacketId.StateOut || d[0] == UdpPacketId.TrafficOut) && d.Length >= 3 && mesh.AcceptDirect(from, d[1] | (d[2] << 8), d.Length))
                    _dispatch?.Dispatch(d, Now);
            }
            catch (SocketException) { if (_run) Interlocked.Increment(ref _udpRecvErrors); if (!_run) break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception) { if (_run) Interlocked.Increment(ref _udpRecvErrors); if (!_run) break; }
        }
    }

    static void CloseSocket()
    {
        _run = false;
        try { _udp?.Close(); } catch (Exception) { }
        _udp = null; _recv = null; _serverKey = null; _server = null;
    }

    /// <summary>Beim Beenden (ScreenModule.Unload).</summary>
    public static void Shutdown()
    {
        CloseSocket();
        RemoteCars.Clear();
    }

    /// <summary>Lokale IP, über die der Server erreicht wird (UDP "verbinden" sendet nichts), mit dem eigenen Port.</summary>
    static string DetectLocalEndpoint(IPAddress server, int port)
    {
        try
        {
            using var s = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(server, 9);
            var ip = ((IPEndPoint)s.LocalEndPoint!).Address;
            return (ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString()) + ":" + port;
        }
        catch (Exception) { return ""; }
    }

    // ------------------------------------------------------------------ Senden
    static void SendState(IGameModeContext ctx)
    {
        var vm = ctx.Vehicles?.Manager;
        var veh = vm?.Current;
        var car = vm?.CurrentCar;
        var lights = vm?.Lights;
        bool inCar = vm != null && veh != null && car != null && vm.Model != null && !InteriorUtils.Inside && !Hidden
                     && GameModeAllowsCar();
        var b = _out;
        b[0] = UdpPacketId.StateIn;
        int pid = TDU2Session.PlayerId;
        b[1] = (byte)pid; b[2] = (byte)(pid >> 8);
        WriteU32(b, 3, TDU2Session.UdpKey);
        int o = 7;
        WriteU32(b, o, ++_seq);
        WriteI32(b, o + 4, inCar ? car!.Id : 0);
        b[o + 8] = (byte)System.Math.Clamp(vm?.ColorIndex ?? 0, 0, 255);
        byte flags = 0, flags2 = 0;
        if (inCar) flags |= 1;
        if (inCar && veh!.Brake > 0.05f) flags |= 2;
        if (inCar && veh!.Gear < 0) flags |= 4;
        if (lights != null && lights.IndicatorLeft) flags |= 8;
        if (lights != null && lights.IndicatorRight) flags |= 16;
        if (lights != null && lights.Hazard) flags |= 32;
        if (inCar && TDU2Controls.Held("horn")) flags2 |= 1;
        if (lights != null && lights.Flash) flags2 |= 2;
        b[o + 9] = flags;
        b[o + 10] = (byte)(lights != null ? System.Math.Clamp(lights.Headlights, 0, 2) : 0);
        b[o + 11] = flags2;
        Vector3 p = Vector3.Zero, f = Vector3.UnitZ, u = Vector3.UnitY, v = Vector3.Zero;
        float steer = 0f, kmh = 0f, rpm = 0f, torque = 0f;
        if (inCar)
        {
            p = vm!.PositionGame;
            f = SafeNorm(vm.ForwardGame, Vector3.UnitZ);
            u = SafeNorm(U.Mirror(veh!.transform.up), Vector3.UnitY);
            float now = _clockF;
            if (_hasLast && now - _lastPosT > 1e-3f) v = (p - _lastPos) / (now - _lastPosT);
            if (v.LengthSquared() > 200f * 200f) v = Vector3.Zero;   // Teleport
            _lastPos = p; _lastPosT = now; _hasLast = true;
            steer = veh.SteeringAngle; kmh = veh.SpeedKmh; rpm = veh.Rpm;
            torque = veh.EngineTorque * (veh.Gear == 0 ? 0.3f : 1f);
        }
        else
        {
            _hasLast = false;
            // Im Interieur: Einfahrt des Spots als Position, Blick zum Spot – Mitspieler sehen einen auf der großen Karte am Haus/Laden
            // und können dorthin beitreten (vorher 0,0,0: unsichtbar, Beitreten landete außerhalb der Insel)
            var spot = InteriorUtils.Inside ? InteriorUtils.CurrentSpot : null;
            if (spot != null)
            {
                bool p2 = spot.Position2.LengthSquared() > 1f && !float.IsNaN(spot.Position2.X);
                p = p2 ? spot.Position2 : spot.Position;
                var look = spot.Position - p; look.Y = 0f;
                f = look.LengthSquared() > 1f ? Vector3.Normalize(look) : Vector3.UnitZ;
            }
        }
        WriteV3(b, o + 12, p); WriteV3(b, o + 24, f); WriteV3(b, o + 36, u); WriteV3(b, o + 48, v);
        WriteF(b, o + 60, steer); WriteF(b, o + 64, kmh); WriteF(b, o + 68, rpm); WriteF(b, o + 72, torque);
        WriteU32(b, o + 76, (uint)(Now * 1000.0));
        b[o + 80] = (byte)TDU2GameData.LocalIsland;
        int len = o + StateSize;
        len = WriteLivery(b, len, inCar && OnlineConfig.SyncLiveries ? car!.FileName : null);
        // Verschmutzung des eigenen Autos
        var dirt = inCar && OnlineConfig.SyncDirt ? vm!.Dirt : null;
        b[len] = DirtTag;
        b[len + 1] = (byte)MathF.Round(System.Math.Clamp(dirt?.Level ?? 0f, 0f, 1f) * 255f);
        b[len + 2] = (byte)MathF.Round(System.Math.Clamp(dirt?.Scratches ?? 0f, 0f, 1f) * 255f);
        b[len + 3] = (byte)(dirt != null && dirt.Raining ? 1 : 0);
        len += DirtExt;
        // Raum im Interieur (Spielfigur für Mitspieler, Unity TDU2Multiplayer.SendState)
        if (!inCar && InteriorUtils.Inside && !Hidden && OnlineConfig.ShowPlayersInInteriors)
        {
            uint key = InteriorUtils.PresenceKey;
            if (key != 0 && InteriorUtils.LocalPresence(out var lp, out float yaw))
            {
                float now2 = _clockF;
                bool walk = _hasRoomLast && now2 > _roomLastT && (lp - _roomLast).Length() / (now2 - _roomLastT) > 0.4f;
                _roomLast = lp; _roomLastT = now2; _hasRoomLast = true;
                b[len] = RoomTag;
                WriteU32(b, len + 1, key);
                WriteV3(b, len + 5, lp);
                WriteF(b, len + 17, yaw);
                b[len + 21] = (byte)(walk ? 1 : 0);
                len += RoomExt;
                // Ausstellungsauto, an dem man Türen/Fenster bedient (Mitspieler im selben Raum sehen es)
                if (InteriorUtils.LocalCarState(out int rc, out int rbits, out int rflags))
                {
                    b[len] = RoomCarTag;
                    WriteI32(b, len + 1, rc);
                    b[len + 5] = (byte)rbits; b[len + 6] = (byte)rflags;
                    len += RoomCarExt;
                }
            }
        }
        else _hasRoomLast = false;
        // eigenes Auto: Bremsscheiben-Wärme (Engine: eigenes Auto ohne BrakeGlow -> 0), Türen/Fenster/Verdeck, Felge und Innenraum
        if (inCar)
        {
            var acc = CarAccessories.Existing(vm!.Model);
            b[len] = ExtraTag;
            for (int i = 0; i < 4; i++) b[len + 1 + i] = 0;
            b[len + 5] = (byte)(acc != null ? acc.Bits : 0);
            WriteI32(b, len + 6, vm.RimId);
            WriteI32(b, len + 10, vm.InteriorId);
            len += ExtraExt;
        }
        SendToLobby(UdpPacketId.StateOut, b, len, true);
    }

    /// <summary>Im Auto unterwegs (Freifahrt/Karte/Pause) – nicht in Login/Ladebildschirm.</summary>
    static bool GameModeAllowsCar()
    {
        var m = Screens.GameMode.Current;
        return m != Screens.Gamemode.GAME_MODE_LOGIN && m != Screens.Gamemode.GAME_MODE_BOOTMENU;
    }

    static Vector3 SafeNorm(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : fallback;

    /// <summary>
    /// Client-Paket [Typ][pid][key][Daten] an die Lobby: mit P2P direkt an erreichbare Mitspieler und über den Relay-Umschlag
    /// an den Rest; ohne P2P (Server erlaubt es nicht) komplett über den Server (Unity SendToLobby).
    /// </summary>
    static void SendToLobby(byte inner, byte[] b, int len, bool isState)
    {
        var mesh = Mesh;
        if (_udp == null || _server == null) return;
        if (mesh == null || !mesh.Active)
        {
            UdpSend(b, len, _server, true);
            return;
        }
        int plen = len - UdpPacketId.ClientHeader;
        var peer = new byte[UdpPacketId.ServerHeader + plen];
        peer[0] = inner;
        peer[1] = b[1]; peer[2] = b[2];
        Buffer.BlockCopy(b, UdpPacketId.ClientHeader, peer, UdpPacketId.ServerHeader, plen);
        Vector3 me = _lastPos;
        uint seq = _seq;
        Func<int, bool> include = id =>
        {
            var r = RemoteCars.Get(id);
            if (r == null || !r.HasVis) return true;
            float range = isState ? OnlineConfig.PeerFarRange : OnlineConfig.PeerTrafficRange;
            bool near = (r.Cur.P - me).LengthSquared() < range * range;
            return near || (isState && seq % 4 == 0);   // weit weg: Zustand nur jedes 4. Mal, Verkehr gar nicht
        };
        // Zustand ~2/s auch an den Server (Spielerliste, Karte, Lobby), selbst wenn alle Mitspieler direkt erreichbar sind
        bool force = isState && _clockF >= _nextServerState;
        if (force) _nextServerState = _clockF + 0.5f;
        mesh.Send(inner, peer, peer.Length, _server, TDU2Session.PlayerId, TDU2Session.UdpKey, include, force);
    }

    /// <summary>Nächstes Stück der eigenen Lackierung anhängen (Karussell). Liefert die neue Paketlänge.</summary>
    static int WriteLivery(byte[] b, int o, string? carFile)
    {
        byte[]? blob = carFile != null ? Liveries.Blob(carFile) : null;
        if (!string.Equals(carFile, _livCar, StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(blob, _livBlob))
        {
            _livCar = carFile; _livBlob = blob; _livHash = CarLivery.Hash(blob); _livOffset = 0;
        }
        int total = blob != null ? blob.Length : 0;
        int off = total > 0 ? _livOffset % total : 0;
        int n = System.Math.Min(LiveryChunk, total - off);
        b[o] = 1;
        WriteU32(b, o + 1, _livHash);
        b[o + 5] = (byte)total; b[o + 6] = (byte)(total >> 8);
        b[o + 7] = (byte)off; b[o + 8] = (byte)(off >> 8);
        b[o + 9] = (byte)n;
        if (n > 0) Buffer.BlockCopy(blob!, off, b, o + LiveryExt, n);
        _livOffset = total > 0 ? (off + n) % total : 0;
        return o + LiveryExt + n;
    }

    static void ReadLivery(RemoteCars.Remote r, byte[] d, int o)
    {
        if (d.Length < o + LiveryExt || d[o] != 1) return;
        uint hash = BitConverter.ToUInt32(d, o + 1);
        int total = d[o + 5] | (d[o + 6] << 8), off = d[o + 7] | (d[o + 8] << 8), n = d[o + 9];
        if (hash == 0 || total == 0) { r.LiveryHash = 0; r.Livery = null; r.Buf = null; return; }
        if (hash == r.LiveryHash) return;   // schon vollständig
        if (r.BufHash != hash || r.Buf == null || r.Buf.Length != total) { r.BufHash = hash; r.Buf = new byte[total]; r.Got = new bool[total]; r.GotCount = 0; }
        if (n <= 0 || off + n > total || d.Length < o + LiveryExt + n) return;
        Buffer.BlockCopy(d, o + LiveryExt, r.Buf, off, n);
        for (int i = off; i < off + n; i++) if (!r.Got![i]) { r.Got[i] = true; r.GotCount++; }
        if (r.GotCount < total) return;
        if (CarLivery.Hash(r.Buf) != hash) { r.Buf = null; r.BufHash = 0; return; }
        r.Livery = CarLivery.Decode(r.Buf);
        r.LiveryHash = hash;
        r.Buf = null; r.Got = null;
    }

    // ------------------------------------------------------------------ Empfang (Zustände)
    static void Receive(IGameModeContext ctx)
    {
        var db = ctx.Vehicles?.Manager?.Database;
        while (_stateIn.Queue.TryDequeue(out var pk))
        {
            var d = pk.data;
            int id = d[1] | (d[2] << 8);
            if (id == TDU2Session.PlayerId) continue;
            int o = 3;
            var s = new RemoteCars.Snap
            {
                Seq = BitConverter.ToUInt32(d, o),
                Flags = d[o + 9],
                Head = d[o + 10],
                Flags2 = d[o + 11],
                P = ReadV3(d, o + 12),
                F = ReadV3(d, o + 24),
                U = ReadV3(d, o + 36),
                V = ReadV3(d, o + 48),
                Steer = BitConverter.ToSingle(d, o + 60),
                Kmh = BitConverter.ToSingle(d, o + 64),
                Rpm = BitConverter.ToSingle(d, o + 68),
                Torque = BitConverter.ToSingle(d, o + 72),
                T = BitConverter.ToUInt32(d, o + 76) / 1000.0,   // Sendezeit (Uhr des Senders)
            };
            int carId = BitConverter.ToInt32(d, o + 4), color = d[o + 8];
            int island = d.Length >= o + 81 ? d[o + 80] : 0;
            var r = RemoteCars.GetOrAdd(id);
            if (s.Seq <= r.LastSeq && r.LastSeq - s.Seq < 1000) continue;   // alt/doppelt
            if (s.Seq < r.LastSeq) { r.Snaps.Clear(); r.Lag.Clear(); r.HasOffset = false; }   // Neustart des Senders
            r.LastSeq = s.Seq;
            r.LastPacket = _clockF;

            // Uhrversatz/Jitter
            double lag = pk.t - s.T;
            r.Lag.Enqueue(lag);
            while (r.Lag.Count > 60) r.Lag.Dequeue();
            double min = double.MaxValue; foreach (var l in r.Lag) if (l < min) min = l;
            r.Offset = min; r.HasOffset = true;
            r.Jitter = r.Jitter * 0.9 + (lag - min) * 0.1;

            if (d.Length >= o + StateSize + LiveryExt)
            {
                ReadLivery(r, d, o + StateSize);
                // Verschmutzung hinter dem Lackierungs-Stück
                int de = d[o + StateSize] == 1 ? o + StateSize + LiveryExt + d[o + StateSize + 9] : -1;
                if (de > 0 && d.Length >= de + DirtExt && d[de] == DirtTag)
                {
                    r.Dirt = d[de + 1] / 255f; r.Scratch = d[de + 2] / 255f; r.Wet = (d[de + 3] & 1) != 0;
                    int ro = de + DirtExt;
                    if (d.Length >= ro + RoomExt && d[ro] == RoomTag)
                    {
                        r.Room = BitConverter.ToUInt32(d, ro + 1);
                        r.RoomPos = ReadV3(d, ro + 5);
                        r.RoomYaw = BitConverter.ToSingle(d, ro + 17);
                        r.RoomWalk = (d[ro + 21] & 1) != 0;
                        r.RoomPacket = _clockF;
                        ro += RoomExt;
                    }
                    else r.Room = 0;
                    if (d.Length >= ro + RoomCarExt && d[ro] == RoomCarTag)
                    {
                        r.RoomCar = BitConverter.ToInt32(d, ro + 1);
                        r.RoomCarBits = d[ro + 5]; r.RoomCarFlags = d[ro + 6];
                        ro += RoomCarExt;
                    }
                    else r.RoomCar = 0;
                    if (d.Length >= ro + ExtraExt && d[ro] == ExtraTag)
                    {
                        for (int i = 0; i < 4; i++) r.Heat[i] = d[ro + 1 + i] / 255f;
                        r.AccBits = d[ro + 5];
                        r.Rim = BitConverter.ToInt32(d, ro + 6);
                        r.Interior = BitConverter.ToInt32(d, ro + 10);
                        r.HasExtra = true;
                    }
                }
            }
            r.Snaps.Add(s);
            if (r.Snaps.Count > 40) r.Snaps.RemoveRange(0, r.Snaps.Count - 40);
            bool inCar = (s.Flags & 1) != 0 && carId != 0 && TDU2GameData.SameIsland(island);   // andere Insel: nicht zeigen
            if (inCar) { r.CarId = carId; r.Color = color; } else r.CarId = 0;

            var p = TDU2GameData.Player(id);
            if (island != 0) p.Island = island;
            p.InCar = inCar;
            if (!TDU2GameData.SameIsland(island)) p.HasPos = false;
            if (inCar) { p.CarId = carId; p.Color = color; p.GamePos = s.P; p.GameForward = s.F; p.HasPos = true; p.LastUpdate = _clockF; }
            else if (TDU2GameData.SameIsland(island) && s.P.LengthSquared() > 1f) { p.GamePos = s.P; p.GameForward = s.F; p.HasPos = true; p.LastUpdate = _clockF; }   // im Interieur: Einfahrt des Spots
            if (db != null && p.CarId != 0 && (p.Car == null || p.Car.Id != p.CarId)) p.Car = db.ById.TryGetValue(p.CarId, out var c) ? c : null;
        }
    }

    // ------------------------------------------------------------------ Chat (UDP, über den Server an alle)
    // Client -> Server: [4][u16 Spieler][u32 Schlüssel][u32 Nachricht-Nr][UTF-8 Text]   (zweimal gesendet gegen Paketverlust)
    // Server -> Client: [3][u16 Spieler][u32 Nachricht-Nr][UTF-8 Name][0][UTF-8 Text]
    static readonly Queue<(int pid, uint msg)> _chatSeen = new();
    static uint _chatSeq, _chatBase;
    static float _chatResendAt = -1f;
    static byte[]? _chatResend;

    public static bool SendChat(string text)
    {
        if (_udp == null || _server == null || !TDU2Session.LoggedIn || TDU2Session.PlayerId == 0 || string.IsNullOrWhiteSpace(text)) return false;
        var t = System.Text.Encoding.UTF8.GetBytes(text.Trim());
        int len = System.Math.Min(t.Length, 300);
        var b = new byte[11 + len];
        b[0] = UdpPacketId.ChatIn;
        int pid = TDU2Session.PlayerId;
        b[1] = (byte)pid; b[2] = (byte)(pid >> 8);
        WriteU32(b, 3, TDU2Session.UdpKey);
        if (_chatBase == 0) _chatBase = (uint)Random.Shared.Next(1, 0xFFFF);
        WriteU32(b, 7, (_chatBase << 16) | (++_chatSeq & 0xFFFF));   // Nachricht-Nr. (Empfänger verwerfen Doppelte)
        Buffer.BlockCopy(t, 0, b, 11, len);
        if (!UdpSend(b, b.Length, _server, true)) return false;
        _chatResend = b; _chatResendAt = _clockF + 0.15f;
        return true;
    }

    static void ReceiveChat()
    {
        if (_chatResend != null && _chatResendAt > 0f && _clockF >= _chatResendAt)
        {
            try { if (_server != null) _udp?.Send(_chatResend, _chatResend.Length, _server); } catch (Exception) { }
            _chatResend = null; _chatResendAt = -1f;
        }
        while (_chatPk.Queue.TryDequeue(out var chatPk))
        {
            var d = chatPk.data;
            int pid = d[1] | (d[2] << 8);
            uint msg = BitConverter.ToUInt32(d, 3);
            bool dup = false;
            foreach (var x in _chatSeen) if (x.pid == pid && x.msg == msg) { dup = true; break; }
            if (dup) continue;
            _chatSeen.Enqueue((pid, msg));
            while (_chatSeen.Count > 128) _chatSeen.Dequeue();
            int z = Array.IndexOf(d, (byte)0, 7);
            if (z < 0) continue;
            string nick = System.Text.Encoding.UTF8.GetString(d, 7, z - 7);
            string text = System.Text.Encoding.UTF8.GetString(d, z + 1, d.Length - z - 1);
            try { ChatReceived?.Invoke(pid, nick, text); } catch (Exception ex) { Log.Warn("Chat: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ My House (Server-Erweiterung, UDP [11])
    static void ReceiveHouse()
    {
        while (_housePk.Queue.TryDequeue(out var pk))
        {
            var d = pk.data;
            int owner = d[1] | (d[2] << 8);
            uint ver = BitConverter.ToUInt32(d, 3);
            try { HouseChanged?.Invoke(owner, ver); } catch (Exception ex) { Log.Warn("My House (Sync): " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ Verkehr (Unity TDU2Traffic-Pakete)
    // Client -> Server: [5][u16 Spieler][u32 Schlüssel][Nutzdaten], Server -> Clients: [6][u16 Absender][Nutzdaten]
    public static bool SendTraffic(byte[] payload, int len)
    {
        if (!CanSend || payload == null || len <= 0 || len > 1250) return false;
        var b = new byte[7 + len];
        b[0] = UdpPacketId.TrafficIn;
        int pid = TDU2Session.PlayerId;
        b[1] = (byte)pid; b[2] = (byte)(pid >> 8);
        WriteU32(b, 3, TDU2Session.UdpKey);
        Buffer.BlockCopy(payload, 0, b, 7, len);
        SendToLobby(UdpPacketId.TrafficOut, b, b.Length, false);
        return true;
    }

    // ------------------------------------------------------------------ Binärhilfen
    static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    static void WriteI32(byte[] b, int o, int v) => WriteU32(b, o, (uint)v);
    static void WriteF(byte[] b, int o, float f) => BitConverter.TryWriteBytes(new Span<byte>(b, o, 4), f);
    static void WriteV3(byte[] b, int o, Vector3 v) { WriteF(b, o, v.X); WriteF(b, o + 4, v.Y); WriteF(b, o + 8, v.Z); }
    static Vector3 ReadV3(byte[] d, int o) => new(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));
}
