using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Game.Gamemodes;
using System.Globalization;
using System.Numerics;

namespace Paradise.Game.Online;

/// <summary>Mitspieler (Unity TDU2GameData.PlayerInfo).</summary>
public sealed class PlayerInfo
{
    public int Id;                  // Server-Spielernummer (0 = nicht online)
    public string Nick = "";
    public int CarId, Color;
    public long Money;
    public int NbCars;
    public int Nationality;
    public bool Online, InCar, HasPos;
    public int Island;              // 1 Ibiza, 2 Hawaii, 0 unbekannt
    public bool SameIsland => TDU2GameData.SameIsland(Island);
    public Vector3 GamePos;
    public int RideWith, Passenger;
    public DateTime LastSeen;
    public CarPhysics? Car;
    public string Avatar = "", Mood = "", MoodTxt = "";
    public int PhotoVer;
    public string CarName => Car == null ? "" : ((Car.Brand ?? "") + " " + Car.ModelName).Trim();
}

/// <summary>
/// Online-Daten für die Original-Oberflächen (Port von Unity TDU2GameData, ohne P2P/Party/Freunde/News – folgen mit dem Multiplayer):
///   * Status-Abfrage beim Server (op "status"): Online, Spielerzahl der eigenen Insel, Mitspielerliste, Radio-Zeit.
///   * Flash-Variablen in allen geladenen Filmen: /:IsOnline, /:NbPlayers, /:PlayerName, /:Money, /:VehicleName …, Clips "ic_status".
///   * Minimap: Online-Symbol und Spielerzahl. general.bnk: Spieler online/offline, Verbindung verloren.
/// Offline-Profil: kein Server – alles offline, Name/Geld trotzdem in die Filme.
/// </summary>
public static class TDU2GameData
{
    /// <summary>Spielerliste/Spielerzahl abfragen (Sekunden, Unity StatusSeconds).</summary>
    public static float StatusSeconds = 3f;
    public static bool ShowPlayerEvents = true;

    public static bool Online { get; private set; } = true;
    public static int PlayerCount { get; private set; }
    public static int Lobby { get; private set; }
    public static int LobbyCount { get; private set; }
    public static int LobbyMax { get; private set; }
    public static readonly Dictionary<int, PlayerInfo> Players = new();
    public static event Action? Changed;

    /// <summary>Eigene Insel (1 Ibiza, 2 Hawaii, 0 unbekannt) – Unity TDU2Multiplayer.LocalIsland.</summary>
    public static int LocalIsland { get; private set; }
    public static bool SameIsland(int other) => other == 0 || LocalIsland == 0 || other == LocalIsland;
    public static int IslandCode(string? island) => string.IsNullOrEmpty(island) ? 0 : island.StartsWith("ib", StringComparison.OrdinalIgnoreCase) ? 1 : island.StartsWith("ha", StringComparison.OrdinalIgnoreCase) ? 2 : 0;

    static Task<NetResponse>? _status;
    static float _clock, _nextStatus, _serverStatusSeconds, _nextPush;
    static int _failCount;
    static bool _wasOnline, _firstList = true;
    static VehicleManager? _vm;
    static readonly Dictionary<VmfPlayer, Dictionary<string, string>> _pushed = new();

    /// <summary>Ländernummer der Flagge (0 = aus der Windows-Region).</summary>
    public static int Nationality { get; set; } = 24;
    public static int NationalityId => Nationality > 0 ? Nationality : TDU2CountryHelper.GetSystemCountryId();

    public static long Money => TDU2Session.Save?.money ?? 0;
    public static string PlayerName => TDU2Session.LoggedIn ? TDU2Session.Nick ?? "" : "";
    public static int NbCars => TDU2Session.Save?.ownedCars?.Count ?? 0;
    public static string CarName { get { var c = _vm?.CurrentCar; return c == null ? "" : ((c.Brand ?? "") + " " + c.ModelName).Trim(); } }

    /// <summary>Jedes Bild (ScreenModule).</summary>
    public static void Update(IGameModeContext ctx, float dt)
    {
        _clock += dt;
        _vm = ctx.Vehicles?.Manager;
        LocalIsland = IslandCode(ctx.Terrain?.Streamer?.Index?.IslandName);
        PollStatus();
        if (_clock >= _nextPush) { _nextPush = _clock + 0.5f; PushToFlash(); }
    }

    // ------------------------------------------------------------------ Spielerliste vom Server
    static void PollStatus()
    {
        if (!TDU2Session.LoggedIn || TDU2Session.Offline || TDU2Session.Client == null)
        {
            if (Online) { Online = false; PlayerCount = 0; Changed?.Invoke(); }
            return;
        }
        if (_status == null && _clock >= _nextStatus)
        {
            _nextStatus = _clock + MathF.Max(1f, _serverStatusSeconds > 0f ? _serverStatusSeconds : StatusSeconds);
            _status = TDU2Session.Client.Send(new NetRequest { op = "status", session = TDU2Session.SessionId });
        }
        if (_status == null || !_status.IsCompleted) return;
        var r = _status.Status == TaskStatus.RanToCompletion ? _status.Result : null;
        _status = null;
        bool ok = r != null && r.ok;
        if (ok && r!.playerId == 0) { TDU2Session.SaveAsync(); ok = false; }   // Sitzung verloren (Server neu gestartet?) -> mit Token neu anmelden
        if (!ok) _failCount++; else _failCount = 0;
        bool online = ok || (_failCount < 2 && Online);   // einen Aussetzer tolerieren
        if (online && r != null && r.ok)
        {
            Lobby = r.lobby; LobbyCount = r.lobbyCount; LobbyMax = r.lobbyMax;
            if (r.statusIntervalMs > 0) _serverStatusSeconds = System.Math.Clamp(r.statusIntervalMs / 1000f, 0.5f, 30f);
            ApplyPlayers(r);
            PlayerCount = IslandPlayerCount(r.online);
            TDU2Session.Online = r.online;
            TDU2Session.ApplyRadio(r);
        }
        if (online != Online)
        {
            Online = online;
            Log.Info("Online-Status: " + (online ? "verbunden" : "getrennt"));
            if (!online)
            {
                PlayerCount = 0;
                if (_wasOnline) GeneralOverlay.ShowDisconnected();
                Players.Clear();
            }
        }
        if (online) _wasOnline = true;
        Changed?.Invoke();
    }

    static void ApplyPlayers(NetResponse r)
    {
        var db = _vm?.Database;
        var seen = new HashSet<int>();
        foreach (var e in r.players ?? new List<PlayerEntry>())
        {
            if (e.id == r.playerId || e.id == 0) continue;
            seen.Add(e.id);
            if (!Players.TryGetValue(e.id, out var p))
            {
                p = new PlayerInfo { Id = e.id };
                Players[e.id] = p;
                if (!_firstList && ShowPlayerEvents) GeneralOverlay.PlayerEvent(e.nick, true);
            }
            p.Nick = e.nick; p.Money = e.money; p.NbCars = e.nbCars; p.Online = true; p.Nationality = e.nationality;
            if (e.carId != 0) { p.CarId = e.carId; p.Color = e.color; }
            p.Island = e.island;
            p.RideWith = e.rideWith; p.Passenger = e.passenger;
            if (!string.IsNullOrEmpty(e.avatar)) p.Avatar = e.avatar;
            p.Mood = e.mood ?? ""; p.MoodTxt = e.moodTxt ?? ""; p.PhotoVer = e.photoVer;
            p.InCar = e.inCar && p.SameIsland;
            if (!p.SameIsland) p.HasPos = false;
            else { p.GamePos = new Vector3(e.px, p.GamePos.Y, e.pz); p.HasPos = e.inCar || p.HasPos; }
            p.Car = db != null && db.ById.TryGetValue(p.CarId, out var c) ? c : null;
            p.LastSeen = DateTime.Now;
        }
        var gone = new List<int>();
        foreach (var kv in Players) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            var p = Players[id];
            Players.Remove(id);
            if (ShowPlayerEvents) GeneralOverlay.PlayerEvent(p.Nick, false);
        }
        _firstList = false;
    }

    /// <summary>Spielerzahl der eigenen Insel (inkl. einem selbst). Server ohne Inselangabe: alle Online-Spieler.</summary>
    static int IslandPlayerCount(int serverOnline)
    {
        bool any = false; int n = 1;
        foreach (var p in Players.Values) { if (p.Island != 0) any = true; if (p.Online && p.SameIsland) n++; }
        return any ? n : System.Math.Max(1, serverOnline);
    }

    // ------------------------------------------------------------------ Flash-Variablen
    static void PushToFlash()
    {
        var mm = _vm?.Minimap;
        if (mm != null && TDU2Session.LoggedIn) { mm.OnlineStatus = Online; mm.PlayerCount = System.Math.Max(1, PlayerCount); }

        var vars = new Dictionary<string, string>
        {
            ["/:IsOnline"] = Online ? "1" : "0",
            ["/:NbPlayers"] = PlayerCount.ToString(),
            ["/:nb_players"] = PlayerCount.ToString(),
            ["/:nb_FRplayers"] = PlayerCount.ToString(),
        };
        if (TDU2Session.LoggedIn)
        {
            string money = Money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $";
            vars["/:PlayerName"] = PlayerName;
            vars["/:CurrentProfile"] = PlayerName;
            vars["/:Money"] = money;
            vars["/:money"] = money;
            vars["/:MyMoneyStr"] = money;
            vars["/:NbCars"] = NbCars.ToString();
            vars["/:AvatarName"] = PlayerName;
        }
        string car = CarName;
        if (car.Length > 0) { vars["/:VehicleName"] = car; vars["/:player_car"] = car; }
        var nums = new Dictionary<string, double> { ["/:state_online"] = Online ? 1 : 0, ["/:Nationality"] = NationalityId };

        var films = FlashHost.Players;
        var alive = new HashSet<VmfPlayer>(films);
        var dead = new List<VmfPlayer>();
        foreach (var k in _pushed.Keys) if (!alive.Contains(k)) dead.Add(k);
        foreach (var k in dead) _pushed.Remove(k);

        // Login läuft: Profilmenü setzt /:CurrentProfile selbst
        bool login = Screens.GameMode.Current == Screens.Gamemode.GAME_MODE_LOGIN;
        foreach (var f in films)
        {
            if (f == null || f.Root == null) continue;
            if (!_pushed.TryGetValue(f, out var last)) { last = new Dictionary<string, string>(); _pushed[f] = last; }
            bool hud = mm != null && f.Find("/HUD/minimap") != null;   // Film der Minimap
            foreach (var kv in vars)
            {
                if (hud && (kv.Key == "/:IsOnline" || kv.Key == "/:NbPlayers" || kv.Key == "/:nb_players" || kv.Key == "/:nb_FRplayers")) continue;   // macht die Minimap
                if (login && kv.Key == "/:CurrentProfile") continue;
                if (last.TryGetValue(kv.Key, out var v) && v == kv.Value) continue;   // nur bei Änderung
                f.SetVariable(kv.Key, kv.Value);
                last[kv.Key] = kv.Value;
            }
            foreach (var kv in nums)
            {
                string sv = "#" + kv.Value.ToString(CultureInfo.InvariantCulture);
                if (last.TryGetValue(kv.Key, out var v) && v == sv) continue;
                f.SetNumber(kv.Key, kv.Value);
                last[kv.Key] = sv;
            }
            ApplyStatusIcons(f, f.Root);
        }
    }

    /// <summary>Jeder Clip "ic_status" mit connected/disconnected bzw. online/offline zeigt den eigenen Online-Status.</summary>
    static void ApplyStatusIcons(VmfPlayer f, VmfPlayer.Instance inst)
    {
        if (inst?.List == null) return;
        foreach (var d in inst.List.Values)
        {
            if (d.Sprite == null) continue;
            if (d.Name == "player_card" || d.Name == "online_evt" || d.Name == "players_list" || d.Name == "minimap") continue;   // Status anderer Spieler / Minimap selbst
            if (d.Name == "ic_status")
            {
                var lb = d.Sprite.Timeline?.Labels;
                string? want = null;
                if (lb != null)
                {
                    if (lb.ContainsKey("connected")) want = Online ? "connected" : "disconnected";
                    else if (lb.ContainsKey("online")) want = Online ? "online" : "offline";
                }
                if (want != null && lb!.TryGetValue(want, out int fr) && d.Sprite.Frame != fr) f.GotoLabel(VmfPlayer.PathOf(d.Sprite), want, false);
                continue;
            }
            ApplyStatusIcons(f, d.Sprite);
        }
    }


    ///Native Player names general.bnk -> av_name_08 bis av_name_01
    ///werte club_hierarchy <- Disable
    ///GlobalLevel <- Playerlevel
    ///distance <- Entfernung zum Spieler (km)
    ///av_name <- Playername
    ///av_name_08 <> av_name_01 states: frame 65 == friends, frame 63 == Clubmember same Club, frame 23 == Club2, frame 16 == Club1, frame 8 == player/default
    ///Problem, UI ausrichtung muss manuell gemacht werden
}
