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
    public Vector3 GameForward = Vector3.UnitZ;
    /// <summary>Spieluhr (Multiplayer.FrameClock) der letzten Position aus dem UDP-Zustand.</summary>
    public float LastUpdate;
    public int RideWith, Passenger;
    public DateTime LastSeen;
    public CarPhysics? Car;
    public string Avatar = "", Mood = "", MoodTxt = "";
    public int PhotoVer;                     // MyTDULife: Version des Avatar-Fotos (0 = keins)
    public MyLifeData? Life;                 // MyTDULife komplett (op "profile"), null = noch nicht geladen
    public Texture2D? Photo;                 // Avatar-Foto (aus Life.photo)
    public int PhotoTexVer = -1;
    /// <summary>My House: Änderungszähler der Einrichtung (Server-Spielerliste, houseVer).</summary>
    public int HouseVer;
    /// <summary>Spielerlevel für die Namensschilder (general.bnk GlobalLevel); −1 = unbekannt (Server/Spielstand haben noch kein Level).</summary>
    public int Level = -1;
    public string CarName => Car == null ? "" : ((Car.Brand ?? "") + " " + Car.ModelName).Trim();
}

/// <summary>
/// Online-Daten für die Original-Oberflächen (Port von Unity TDU2GameData; UDP/P2P: Multiplayer, Freunde: Friends, Chat: Chat,
/// Namen: NameTags, Einladungen: Party, News: TDU2News; Umfragen ausgelassen):
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
    /// <summary>Zuletzt gesehen (je Profil gespeichert), Freunde (Friends.Apply), Club (Server hat noch keine Clubs – leer wie in Unity).</summary>
    public static readonly List<PlayerInfo> Recent = new();
    public static readonly List<PlayerInfo> Friends = new();
    public static readonly List<PlayerInfo> Club = new();
    public static event Action? Changed;
    /// <summary>Listen haben sich geändert (z.B. Freundesliste vom Server) – offene Menüs neu füllen.</summary>
    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>Eigene Insel (1 Ibiza, 2 Hawaii, 0 unbekannt) – Unity TDU2Multiplayer.LocalIsland.</summary>
    public static int LocalIsland { get; private set; }
    public static bool SameIsland(int other) => other == 0 || LocalIsland == 0 || other == LocalIsland;
    public static int IslandCode(string? island) => string.IsNullOrEmpty(island) ? 0 : island.StartsWith("ib", StringComparison.OrdinalIgnoreCase) ? 1 : island.StartsWith("ha", StringComparison.OrdinalIgnoreCase) ? 2 : 0;

    static Task<NetResponse>? _status;
    static float _clock, _nextStatus, _serverStatusSeconds, _nextPush;
    static int _failCount;
    static bool _wasOnline, _firstList = true;
    static VehicleManager? _vm;
    /// <summary>Fahrzeug-Verwaltung (Datenbank für Autonamen in Listen; Friends).</summary>
    internal static VehicleManager? Vehicles => _vm;
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
        // Kleidung der Mitspieler (Fahrer-Avatare in ihren Autos, Core AvatarConfigs)
        Paradise.Core.Avatars.AvatarConfigs.RemoteCodeProvider ??= id => Players.TryGetValue(id, out var pl) ? pl.Avatar : "";
        PollLife();
        PollRecent();
        PollStatus();
        UpdatePlayerDots();
        Paradise.Game.Online.Friends.Update(ctx, dt);
        TDU2News.Tick(dt);
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
            _status = TDU2Session.Client.Send(new NetRequest
            {
                op = "status", session = TDU2Session.SessionId,
                p2p = Paradise.Game.Config.OnlineConfig.UseP2P && Multiplayer.Mesh != null, localEp = Multiplayer.LocalEndpoint,
            });
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
            // P2P: Endpunkte/Tokens der Lobby-Mitspieler an die Peer-Verwaltung (Hole-Punching, Relay-Rückfall)
            Multiplayer.Mesh?.Update(Paradise.Game.Config.OnlineConfig.UseP2P && r.p2pEnabled, r.playerId, r.p2pToken, r.players);
            Paradise.Game.Online.Friends.OnStatus(r);
            Party.OnStatus(r);   // Einladungen, Antworten (joined/declined/accepted), Mitfahrt
            TDU2News.OnStatus(r.newsLatest);   // News-Abgleich (höchste Id auf dem Server)
            if (r.statusIntervalMs > 0) _serverStatusSeconds = System.Math.Clamp(r.statusIntervalMs / 1000f, 0.5f, 30f);
            ApplyPlayers(r);
            // Spielerzahl wie im Spiel = Spieler der eigenen Lobby (inkl. einem selbst); ohne Lobby-Angabe die der Insel
            PlayerCount = r.lobbyCount > 0 ? r.lobbyCount : IslandPlayerCount(r.online);
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
            else if (p.Nick.StartsWith("Spieler ") && !_firstList && ShowPlayerEvents) GeneralOverlay.PlayerEvent(e.nick, true);   // zuerst per UDP gesehen
            p.Nick = e.nick; p.Money = e.money; p.NbCars = e.nbCars; p.Online = true; p.Nationality = e.nationality;
            if (e.carId != 0) { p.CarId = e.carId; p.Color = e.color; }
            p.Island = e.island;
            p.RideWith = e.rideWith; p.Passenger = e.passenger;
            if (!string.IsNullOrEmpty(e.avatar)) p.Avatar = e.avatar;
            p.Mood = e.mood ?? ""; p.MoodTxt = e.moodTxt ?? ""; p.PhotoVer = e.photoVer;
            if (e.houseVer != p.HouseVer) { int old = p.HouseVer; p.HouseVer = e.houseVer; if (old != 0 || e.houseVer != 0) HouseVersionChanged?.Invoke(e.id, e.houseVer); }
            p.InCar = e.inCar && p.SameIsland;
            if (!p.SameIsland) p.HasPos = false;   // andere Insel: keine Position (Karte/Minimap)
            else if (!p.HasPos || Multiplayer.FrameClock - p.LastUpdate > 3f) { p.GamePos = new Vector3(e.px, p.GamePos.Y, e.pz); p.HasPos = e.inCar || p.HasPos; }
            p.Car = db != null && db.ById.TryGetValue(p.CarId, out var c) ? c : null;
            p.LastSeen = DateTime.Now;
            Remember(p);
        }
        var gone = new List<int>();
        foreach (var kv in Players) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            var p = Players[id];
            p.Online = false; p.LastSeen = DateTime.Now;
            Remember(p);
            Players.Remove(id);
            if (ShowPlayerEvents && !p.Nick.StartsWith("Spieler ")) GeneralOverlay.PlayerEvent(p.Nick, false);
        }
        if (gone.Count > 0 || _firstList) SaveRecent();
        _firstList = false;
    }

    /// <summary>
    /// Minimap: Mitspieler als Punkte (Unity TDU2Minimap.CollectDots – eigene Lobby, gleiche Insel, im Auto), Farbe je Beziehung
    /// (weiß / Freund grün / Club orange, Friends.ColorOf).
    /// </summary>
    static void UpdatePlayerDots()
    {
        var dots = Paradise.Core.Vehicles.Minimap.PlayerDots;
        dots.Clear();
        if (!Online) return;
        foreach (var pl in Players.Values)
        {
            if (!pl.Online || !pl.InCar || !pl.HasPos) continue;
            dots.Add((pl.GamePos, Paradise.Game.Online.Friends.ColorOf(pl.Nick)));
        }
    }

    /// <summary>Server meldet neue Einladung/Antwort (UDP [12]): nächste Abfrage sofort.</summary>
    public static void RequestStatusNow() => _nextStatus = 0f;

    /// <summary>My House: Änderungszähler eines Spielers aus der Spielerliste geändert (falls UDP [11] verloren ging).</summary>
    public static event Action<int, int>? HouseVersionChanged;

    /// <summary>Spieler aus der Lobby (UDP) – unbekannte Nummern bekommen einen Platzhalter (Unity Player(id)).</summary>
    public static PlayerInfo Player(int id)
    {
        if (Players.TryGetValue(id, out var p)) return p;
        p = new PlayerInfo { Id = id, Nick = "Spieler " + id, Online = true };
        Players[id] = p;
        return p;
    }

    /// <summary>Avatar-Foto (Base64-JPG, MyTDULife) -> Flash-Textur; null bei leer/fehlerhaft (Unity DecodePhoto).</summary>
    public static Texture2D? DecodePhoto(string? b64) => DecodeImage(b64, false);

    /// <summary>JPEG/PNG (Base64) -> Flash-Textur (Unity Texture2D.LoadImage); alpha = Transparenz behalten (News-Bilder RGBA32).</summary>
    public static Texture2D? DecodeImage(string? b64, bool alpha)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            var rgba = Paradise.Core.Formats.WicImage.Decode(Convert.FromBase64String(b64), out int w, out int h);
            if (rgba == null || w <= 0 || h <= 0) return null;
            // WIC: Zeile 0 = oben; Flash-Texturen wie Unity LoadImage (Zeile 0 = unten) -> umdrehen
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = ((h - 1 - y) * w + x) * 4;
                    px[y * w + x] = new Color32(rgba[s], rgba[s + 1], rgba[s + 2], alpha ? rgba[s + 3] : (byte)255);
                }
            var tex = new Texture2D(w, h) { wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
        catch (Exception ex) { Log.Warn("Bild: " + ex.Message); return null; }
    }

    // ------------------------------------------------------------------ MyTDULife anderer Spieler (Spielerkarte, Listen)
    static readonly Dictionary<int, (Task<NetResponse> task, PlayerInfo p, List<Action<PlayerInfo>> done)> _lifeReq = new();
    static readonly Dictionary<int, (int ver, float at)> _lifeTried = new();

    /// <summary>
    /// MyTDULife (Stimmung, Profil, Avatar-Foto) eines Spielers holen (op "profile"); done läuft im Hauptthread, sobald
    /// p.Life/p.Photo da sind (sofort, wenn schon aktuell). Offline-Spieler (Id 0) werden über den Namen gesucht.
    /// </summary>
    public static void FetchLife(PlayerInfo? p, Action<PlayerInfo>? done)
    {
        if (p == null) return;
        if (p.Life != null && p.PhotoTexVer == p.PhotoVer && p.PhotoVer >= 0 && (p.PhotoVer == 0 || p.Photo != null)) { done?.Invoke(p); return; }
        if (!TDU2Session.LoggedIn || TDU2Session.Offline || TDU2Session.Client == null) return;
        int key = p.Id != 0 ? p.Id : -System.Math.Abs(StableHash(p.Nick ?? "") | 1);
        if (_lifeReq.TryGetValue(key, out var pend)) { if (done != null) pend.done.Add(done); return; }
        // nicht dauernd neu fragen (Spielerliste aktualisiert jede Sekunde): je Spieler und Foto-Version höchstens alle 15 s
        if (_lifeTried.TryGetValue(key, out var tried) && tried.ver == p.PhotoVer && _clock - tried.at < 15f) return;
        _lifeTried[key] = (p.PhotoVer, _clock);
        var t = TDU2Session.Client.Send(new NetRequest { op = "profile", session = TDU2Session.SessionId, target = p.Id, nick = p.Id == 0 ? p.Nick : "" });
        _lifeReq[key] = (t, p, done != null ? new List<Action<PlayerInfo>> { done } : new List<Action<PlayerInfo>>());
    }

    // Unity nahm string.GetHashCode (je Prozess gleich); .NET würfelt ihn je Start – für den Schlüssel egal, aber stabil ist sauberer
    static int StableHash(string s) { unchecked { int h = (int)2166136261; foreach (char c in s.ToLowerInvariant()) h = (h ^ c) * 16777619; return h; } }

    static void PollLife()
    {
        if (_lifeReq.Count == 0) return;
        List<int>? fin = null;
        foreach (var kv in _lifeReq) if (kv.Value.task.IsCompleted) (fin ??= new List<int>()).Add(kv.Key);
        if (fin == null) return;
        foreach (int k in fin)
        {
            var (task, p, done) = _lifeReq[k];
            _lifeReq.Remove(k);
            var r = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            if (r == null || !r.ok || r.life == null) { Log.Info("MyTDULife von " + p.Nick + ": " + (r?.error ?? "keine Antwort")); continue; }
            p.Life = r.life;
            if (!string.IsNullOrEmpty(r.avatar)) p.Avatar = r.avatar;
            p.Mood = r.life.mood ?? ""; p.MoodTxt = r.life.moodText ?? ""; p.PhotoVer = r.life.photoVer;
            p.Photo = DecodePhoto(r.life.photo);
            p.PhotoTexVer = r.life.photoVer;
            foreach (var d in done) { try { d(p); } catch (Exception ex) { Log.Warn("MyTDULife: " + ex.Message); } }
        }
    }

    // ------------------------------------------------------------------ Zuletzt gesehen (im Spielstand des Profils: SaveData.recentPlayers,
    // Original-DATA als Umgebungsvariable U3D_Recent)
    static string? _recentFor;

    static void EnsureRecentLoaded()
    {
        string nick = TDU2Session.Nick ?? "";
        if (_recentFor == nick) return;
        _recentFor = nick;
        Recent.Clear();
        if (nick.Length == 0) return;
        var l = TDU2Session.Save?.recentPlayers;
        if (l != null)
            foreach (var e in l)
            {
                if (e == null || string.IsNullOrEmpty(e.nick)) continue;
                DateTime.TryParse(e.lastSeen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen);
                Recent.Add(new PlayerInfo { Nick = e.nick, CarId = e.carId, Color = e.color, Money = e.money, NbCars = e.nbCars, Nationality = e.nationality, LastSeen = seen.Kind == DateTimeKind.Utc ? seen.ToLocalTime() : seen });
            }
    }

    static void Remember(PlayerInfo p)
    {
        if (p == null || string.IsNullOrEmpty(p.Nick) || p.Nick.StartsWith("Spieler ")) return;
        EnsureRecentLoaded();
        int i = Recent.FindIndex(x => string.Equals(x.Nick, p.Nick, StringComparison.OrdinalIgnoreCase));
        var e = i >= 0 ? Recent[i] : new PlayerInfo { Nick = p.Nick };
        if (i >= 0) Recent.RemoveAt(i);
        e.CarId = p.CarId; e.Color = p.Color; e.Money = p.Money; e.NbCars = p.NbCars; e.Nationality = p.Nationality; e.LastSeen = DateTime.Now; e.Car = p.Car;
        Recent.Insert(0, e);
        if (Recent.Count > 50) Recent.RemoveRange(50, Recent.Count - 50);
        SaveRecent();   // in den Spielstand (gespeichert mit dem nächsten Autosave)
    }

    /// <summary>Zuletzt gesehen speichern (auch beim Beenden, Unity OnApplicationQuit -> ScreenModule.Unload).</summary>
    public static void SaveRecent()
    {
        if (string.IsNullOrEmpty(TDU2Session.Nick) || _recentFor == null) return;
        var save = TDU2Session.Save;
        if (save == null) return;
        var l = new List<RecentEntry>();
        foreach (var p in Recent)
            l.Add(new RecentEntry { nick = p.Nick, carId = p.CarId, color = p.Color, money = p.Money, nbCars = p.NbCars, nationality = p.Nationality,
                                    lastSeen = p.LastSeen.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) });
        save.recentPlayers = l;
    }

    /// <summary>Liste für das Pausemenü (Spielermenü): friends, club_members, current_players, recent_players, black_list (Unity ListFor).</summary>
    public static List<PlayerInfo> ListFor(string? listName)
    {
        switch ((listName ?? "").ToLowerInvariant())
        {
            case "current_players": return new List<PlayerInfo>(Players.Values);
            case "friends": return new List<PlayerInfo>(Friends);
            case "black_list": return new List<PlayerInfo>(Friends);
            case "recent_players":
                {
                    EnsureRecentLoaded();
                    var db = _vm?.Database;
                    foreach (var p in Recent)
                    {
                        if (p.Car == null && db != null && db.ById.TryGetValue(p.CarId, out var c)) p.Car = c;
                        p.Online = false;
                        // gespeicherte Einträge haben keine Sitzungsnummer: online -> die aktuelle übernehmen (Einladen/Beitreten brauchen sie)
                        foreach (var o in Players.Values) if (string.Equals(o.Nick, p.Nick, StringComparison.OrdinalIgnoreCase)) { p.Online = true; p.Id = o.Id; }
                    }
                    return new List<PlayerInfo>(Recent);
                }
            case "club_members": return new List<PlayerInfo>(Club);
        }
        return new List<PlayerInfo>();
    }

    // ------------------------------------------------------------------ Zuletzt gesehen vom Server (op "recent")
    static Task<NetResponse>? _recentReq;
    static float _recentAt = -100f;

    /// <summary>Zuletzt gesehene Spieler beim Server anfragen (höchstens alle 15 s); Ergebnis landet in <see cref="Recent"/>.</summary>
    public static void RequestRecent()
    {
        if (!TDU2Session.LoggedIn || TDU2Session.Offline || TDU2Session.Client == null || _recentReq != null || _clock - _recentAt < 15f) return;
        _recentAt = _clock;
        _recentReq = TDU2Session.Client.Send(new NetRequest { op = "recent", session = TDU2Session.SessionId });
    }

    static void PollRecent()
    {
        if (_recentReq == null || !_recentReq.IsCompleted) return;
        var r = _recentReq.Status == TaskStatus.RanToCompletion ? _recentReq.Result : null;
        _recentReq = null;
        if (r == null || !r.ok || r.recent == null) return;
        EnsureRecentLoaded();
        var db = _vm?.Database;
        foreach (var e in r.recent)
        {
            if (string.IsNullOrEmpty(e.nick) || string.Equals(e.nick, TDU2Session.Nick, StringComparison.OrdinalIgnoreCase)) continue;
            DateTime.TryParse(e.lastSeen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen);
            if (seen.Kind == DateTimeKind.Utc) seen = seen.ToLocalTime();
            var p = Recent.Find(x => string.Equals(x.Nick, e.nick, StringComparison.OrdinalIgnoreCase));
            if (p == null) { p = new PlayerInfo { Nick = e.nick }; Recent.Add(p); }
            if (seen > p.LastSeen) p.LastSeen = seen;
            if (e.carId != 0 && p.CarId == 0) { p.CarId = e.carId; p.Color = e.color; }
            if (p.NbCars == 0) p.NbCars = e.nbCars;
            if (p.Money == 0) p.Money = e.money;
            if (p.Nationality == 0) p.Nationality = e.nationality;
            if (p.Car == null && db != null && db.ById.TryGetValue(p.CarId, out var c)) p.Car = c;
        }
        Recent.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
        if (Recent.Count > 50) Recent.RemoveRange(50, Recent.Count - 50);
        SaveRecent();
        Changed?.Invoke();
    }

    /// <summary>Spielerzahl der eigenen Insel (inkl. einem selbst). Server ohne Inselangabe: alle Online-Spieler.</summary>
    static int IslandPlayerCount(int serverOnline)
    {
        // Players enthält nur die eigene Lobby (Server "status"). Früher: ohne Mitspieler die Zahl ALLER Spieler des Servers
        // (r.online) -> zwei Lobbys mit je 1 Spieler zeigten 2. Jetzt: man selbst + Mitspieler der Lobby auf derselben Insel.
        int n = 1;
        foreach (var p in Players.Values) if (p.Online && p.SameIsland && !p.Nick.StartsWith("Spieler ")) n++;
        return n;
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
