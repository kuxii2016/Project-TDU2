using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Core.World;
using Paradise.Game.Gamemodes;
using Paradise.Game.Input;
using Paradise.Game.Online;
using AudioEngine = Paradise.Core.Audio.AudioEngine;

namespace Paradise.Game.Screens;

/// <summary>
/// Original-Pausemenü (ESC) aus frontend/&lt;res&gt;/main.bnk – Port von Unity TDU2PauseMenu:
///   - Öffnen: _root-Zustand setzen (Spielmodus, Sperren je Menüpunkt, Geld/Profil/Auto, Uhr, Inselkarte) und "pause_in" abspielen.
///     Die Menülogik (Quadrat-Menü, Optionen, Dialoge) steckt im Film selbst (ActionScript, VmfScript-VM).
///   - Eingabe wie die Spiel-Engine: Move("up"/"down"/"left"/"right"), A() (Enter), B() (ESC/Rücktaste), X/Y am obersten Clip.
///   - FSCommands: "paou" (Pause beendet), "exit:vali"/"quit"/"egam" (Spiel verlassen, vorher speichern), "dbxo" (Ja/Nein-Dialog).
///   - Spielerlisten (Lobby/Zuletzt gesehen …) aus TDU2GameData.
/// Während das Menü offen ist, steht das Spiel (Unity Time.timeScale = 0): Fahrzeuge werden angehalten.
/// Optionsseiten: Options (TDU2Options). Noch nicht portiert: Tastenbelegung, Spielerkarte/Freunde/Party, Renn-Modus.
/// </summary>
public sealed class PauseMenu
{
    const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
              VK_A = 0x41, VK_D = 0x44, VK_S = 0x53, VK_W = 0x57, VK_X = 0x58, VK_Y = 0x59, VK_F10 = 0x79;

    // Menüpunkte, die hier funktionieren (alles andere ist im Film gesperrt dargestellt)
    static readonly string[] Unlocked = { "LockResume", "LockOptions", "LockExitGame", "LockQuit", "LockControls", "LockVideo", "LockDisplay",
        "LockAudio", "LockGameSettings", "LockDevice", "LockMouse", "LockWheel", "LockLanguage", "LockSave", "LockPlayers", "LockJoin", "LockGameProgress", "LockInvite" };
    static readonly string[] Locked = { "LockSnapshot", "LockChangePosition", "LockInviteParty", "LockManageParty",
        "LockRestart", "LockEditor", "LockBookmarks", "LockProfiles", "LockNewGame", "LockContinueGame", "LockAchiev", "LockRankings",
        "LockOnline", "LockSocial", "LockStatus" };

    readonly IGameModeContext _ctx;
    VmfPlayer? _ui;
    Options? _options;
    Dictionary<string, string>? _texts;
    public Options? Settings => _options;
    bool _open, _pendingDialog;
    float _clock, _escAt = -1f;
    int _clockShown = -1, _clockDay = -1;
    public bool IsOpen => _open;
    public string? LastCommand { get; private set; }

    public PauseMenu(IGameModeContext ctx) => _ctx = ctx;

    /// <summary>Film übernehmen (einmal nach dem Laden von main.bnk).</summary>
    public void Attach(VmfPlayer? ui)
    {
        if (ui == null || ReferenceEquals(ui, _ui)) return;
        _ui = ui;
        ui.Visible = false;
        ui.FSCommand += OnCommand;
        ui.ListCreated += OnListCreated;
        EnsureOptions();
        _options!.Publish(ui);
    }

    /// <summary>
    /// Optionsseiten (Unity TDU2PauseMenu.EnsureLoaded: Texte in der gespeicherten Sprache laden, TDU2Options anlegen und
    /// ApplyAll) – Unity macht das beim Spielstart, nicht erst beim ersten Öffnen des Pausemenüs. Hier nach dem Anmelden
    /// (gespeicherte Optionen liegen im Spielstand), siehe ScreenModule.ImportProfile.
    /// </summary>
    public void EnsureOptions()
    {
        if (_options != null) return;
        Options.SetLanguage(Options.SavedLanguage());
        try { _texts = FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language); }
        catch (Exception ex) { Log.Warn("Pausemenü-Texte: " + ex.Message); _texts = new(); }
        _options = new Options(_ctx, k => _texts != null && _texts.TryGetValue(k, out var v) ? v : null);
        _options.LanguageChanged = t => { _texts = t; if (_ui != null) _options?.Publish(_ui); };
    }

    // ------------------------------------------------------------------ Öffnen / Schließen
    public void Open()
    {
        var ui = _ui;
        if (ui == null || _open) return;
        _open = true;
        _clockShown = -1; _clockDay = -1;
        ui.SetValue("/:WhatGameMode", 1);   // 1 Freie Fahrt (2 Rennen: folgt mit Racing)
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetVariable("/:MenuType", "pc");
        ui.SetValue("/:IsPC_X360", true);
        ui.SetValue("/:IsCasino", 0);
        ui.SetValue("/:isCasinoStandalone", 0);
        ui.SetValue("/:IsBootCasino", false);
        ui.SetValue("/:IsInOptions", false);
        ui.SetValue("/:ListPlayerCardOpened", false);
        ui.SetValue("/:Input", true);
        ui.ForceVisible("/pause_menu/data_extd/objective", false);
        foreach (var n in Unlocked) ui.SetValue("/:" + n, false);
        foreach (var n in Locked) ui.SetValue("/:" + n, true);
        ui.SetVariable("/:NbNewsUnread", "0");
        ui.SetVariable("/:Money", TDU2Session.LoggedIn
            ? TDU2Session.Save.money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $"
            : "999 999 999 $");
        if (TDU2Session.LoggedIn)
        {
            ui.SetVariable("/:CurrentProfile", TDU2Session.Nick);
            ui.SetVariable("/:NbCars", TDU2Session.OwnedCars.Count.ToString());
        }
        else ui.SetVariable("/:NbCars", "1");
        ui.SetVariable("/:NbHouses", "0");
        if (TDU2GameData.CarName.Length > 0) ui.SetVariable("/:VehicleName", TDU2GameData.CarName);
        // Insel/Gebiet für die Karte (initMap: erster Buchstabe h = Hawaii, i = Ibiza; Gebiete h1..h4, i1..i2)
        string area = CurrentArea() ?? (IsHawaii() ? "h1" : "i1");
        ui.SetVariable("/:CurrentArea", area);
        ui.SetVariable("/:AvatarName", TDU2GameData.PlayerName);
        ui.SetVariable("/:IsOnline", TDU2GameData.Online ? "1" : "0");
        ui.SetVariable("/:IsLoggedIn", TDU2Session.LoggedIn ? "1" : "0");
        // Ausweis: Flagge = Bild Ländernummer (Zahl), Online-Symbol über state_online, Titel NumTitle
        ui.SetNumber("/:Nationality", TDU2GameData.NationalityId);
        ui.SetNumber("/:state_online", TDU2GameData.Online ? 1 : 0);
        ui.SetNumber("/:NumTitle", 1);

        ui.Visible = true;
        bool ok = ui.GotoLabel("/", "pause_in", true);
        Log.Info($"Pausemenü offen (Gebiet {area}, pause_in={ok})");
        if (_ctx.Vehicles != null) _ctx.Vehicles.Frozen = true;   // Unity: Time.timeScale = 0
        AudioEngine.Paused = true;                                  // Unity: AudioListener.pause = true
    }

    public void Close()
    {
        _escAt = -1f;
        if (!_open) return;
        _open = false;
        if (_ui != null) { _ui.Visible = false; _ui.GotoLabel("/", "default", false); }
        if (_ctx.Vehicles != null) _ctx.Vehicles.Frozen = false;
        AudioEngine.Paused = false;
        Log.Info("Pausemenü geschlossen");
    }

    // ------------------------------------------------------------------ Jedes Bild
    public void Update(float dt)
    {
        _clock += dt;
        var ui = _ui;
        if (!_open || ui == null) return;
        UpdateClock();
        UpdateMapMarker();
        UpdatePlayerLists();
        if (UpdateBinder()) return;   // "Taste zuweisen" wartet auf eine Taste
        if (_pendingDialog) AnswerDialog();

        // Texteingabe (setInputFocus, z.B. "Spieler suchen"): Buchstaben gehören dem Feld, nur Enter bestätigt, ESC bricht ab
        bool typing = ui.InputFocus != null;
        if (!typing)
        {
            // Menü-Belegung (Game/Config/KeyConfig.cs, Unity TDU2Controls "menu_*")
            if (TDU2Controls.Pressed("menu_up")) ui.Input("Move", "up");
            if (TDU2Controls.Pressed("menu_down")) ui.Input("Move", "down");
            if (TDU2Controls.Pressed("menu_left")) ui.Input("Move", "left");
            if (TDU2Controls.Pressed("menu_right")) ui.Input("Move", "right");
        }
        // Texteingabe: nur Enter bestätigt (Buchstaben gehören dem Textfeld), sonst die belegbare Bestätigungstaste
        if (typing ? _ctx.Pressed(VK_RETURN) : TDU2Controls.ConfirmPressed()) ui.Input("A");
        bool esc = _ctx.Pressed(VK_ESCAPE);
        bool back = !typing && TDU2Controls.BackPressed();
        if (esc || back)
        {
            // Zurück; reagiert der Film nicht (keine B-Funktion), Menü direkt schließen – nie im Pausenzustand hängen bleiben
            if (!ui.Input("B")) { Log.Info("Pausemenü: B nicht verarbeitet -> schließen"); Close(); return; }
            if (esc) _escAt = _clock;
        }
        // ESC im Hauptmenü muss nach der Schließ-Animation "paou" liefern; blockiert der Film, trotzdem schließen
        // (im Optionsmenü führt ESC nur zurück und bleibt offen)
        if (_escAt > 0f && _clock - _escAt > 2f)
        {
            _escAt = -1f;
            var inOpt = ui.GetVariable("/:IsInOptions");
            if (!string.Equals(inOpt, "true", StringComparison.OrdinalIgnoreCase) && inOpt != "1") { Log.Info("Pausemenü: ESC ohne paou -> schließen"); Close(); }
        }
        if (_ctx.Pressed(VK_F10)) Close();   // Notausgang
        if (!typing && TDU2Controls.Pressed("menu_opt1")) ui.Input("X");
        if (!typing && TDU2Controls.Pressed("menu_opt2")) ui.Input("Y");
    }

    // ------------------------------------------------------------------ FSCommands
    void OnCommand(string cmd, string args)
    {
        if (GameMode.Current == Gamemode.GAME_MODE_LOGIN) return;   // Befehle des Profilmenüs behandelt der Login
        LastCommand = cmd + (string.IsNullOrEmpty(args) ? "" : " " + args);
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("sndp:")) return;                 // Menü-Sounds (FrontendSounds)
        if (c == "dbxo") { _pendingDialog = true; return; }
        if (c.StartsWith("mylf:scrl:")) return;            // Scrollbalken der Liste: behandelt VmfPlayer
        if (c == "plli") { ReloadPlayerLists(); return; }  // Spielerliste (Reiter) geöffnet
        if (c == "psch:vali") { SearchPlayer(); return; }  // "Spieler suchen": Name bestätigt
        if (c == "psch:canc") return;
        if (HandleControlsCommand(c)) return;                // Steuerung (Tastenbelegung)
        if (_options != null && _ui != null && _options.Handle(_ui, c)) return;   // Optionsseiten
        switch (c)
        {
            case "paou":           // Pause beendet (nach der Schließen-Animation)
                Close();
                break;
            case "exit:vali":      // "Spiel beenden" bestätigt
            case "quit":
            case "egam":
                Close();
                Log.Info("Pausemenü: Spiel beenden");
                _ctx.Quit();       // ScreenModule.Unload speichert und meldet ab (SessionAutosave.SaveAndLogout)
                break;
            default:
                Log.Debug("Pausemenü FSCommand: " + LastCommand);
                break;
        }
    }

    // ------------------------------------------------------------------ Bestätigungsdialoge ("dbxo")
    // Der Film setzt _root.DgbxCase/Msg1Txt … und meldet "dbxo"; das Spiel zeigt eine Ja/Nein-Box und ruft danach
    // DialogBox() mit _root.PlayerAnswer = "a" (Ja). Hier: Ja bestätigen.
    void AnswerDialog()
    {
        _pendingDialog = false;
        var ui = _ui!;
        string kase = ui.GetVariable("/:DgbxCase") ?? "";
        var owners = ui.FunctionOwners("DialogBox");
        string? target = null;
        foreach (var o in owners) if (o.IndexOf("options", StringComparison.OrdinalIgnoreCase) >= 0) { target = o; break; }
        if (target == null && owners.Count > 0) target = owners[owners.Count - 1];
        Log.Info($"Pausemenü: Dialog '{kase}' -> Ja ({target ?? "kein DialogBox"})");
        ui.SetVariable("/:PlayerAnswer", "a");
        if (target == null || !ui.CallClip(target, "DialogBox"))
        {
            ui.SetValue("/:Input", true);
            ui.SetValue("/:_buttons", true);
        }
    }

    // ------------------------------------------------------------------ Uhr (/pause_menu/clock/time)
    void UpdateClock()
    {
        var ui = _ui!;
        float h = WorldWeather.Hour;
        int hh = (int)MathF.Floor(h) % 24, mm = (int)MathF.Floor((h - MathF.Floor(h)) * 60f) % 60;
        int code = hh * 100 + mm;
        if (code != _clockShown)
        {
            _clockShown = code;
            ui.SetValue("/:HDecade", hh / 10); ui.SetValue("/:HUnit", hh % 10);
            ui.SetValue("/:MDecade", mm / 10); ui.SetValue("/:MUnit", mm % 10);
            ui.SetValue("/:SDecade", 0); ui.SetValue("/:SUnit", 0);
            ui.CallClip("/pause_menu/clock/time/disp", "setHour");
        }
        int day = WorldWeather.Night < 0.5f ? 1 : 0;
        if (day != _clockDay)
        {
            bool first = _clockDay < 0;
            _clockDay = day;
            ui.GotoLabel("/pause_menu/clock/time/day", day == 1 ? "on" : "off", !first);
            ui.GotoLabel("/pause_menu/clock/time/night", day == 1 ? "off" : "on", !first);
        }
    }

    // ------------------------------------------------------------------ Inselkarte
    // Marke: /pause_menu/data_extd/map/island/location/circle bekommt x = Breite(lure) * SpielX / (AreasX * 6144),
    // y = Höhe(lure) * SpielZ / (AreasZ * 6144). Gebiet: level/cruisingzones.bin (ein Polygon je Gebiet).
    List<(string label, (float x, float z)[] poly)>? _zones;
    string? _zonesFor;

    bool IsHawaii() => (_ctx.Terrain?.Streamer?.Index?.IslandName ?? "").StartsWith("h", StringComparison.OrdinalIgnoreCase);

    System.Numerics.Vector3? PlayerGamePos()
    {
        var vm = _ctx.Vehicles?.Manager;
        if (vm?.Current != null) return vm.PositionGame;
        var s = _ctx.Terrain?.Streamer; var cam = _ctx.Terrain?.Camera;
        return s != null && cam != null ? s.RenderToGame(cam.Position) : null;
    }

    void EnsureZones()
    {
        var idx = _ctx.Terrain?.Streamer?.Index;
        if (idx == null || _zonesFor == idx.LevelDir) return;
        _zonesFor = idx.LevelDir;
        _zones = new();
        try
        {
            string? f = string.IsNullOrEmpty(idx.LevelDir) ? null : Path.Combine(idx.LevelDir, "cruisingzones.bin");
            if (f == null || !GameFiles.Exists(f)) return;
            var b = GameFiles.ReadAllBytes(f);
            var polys = new List<List<(float, float)>>();
            var cur = new List<(float, float)>();
            var vals = new List<float>();
            void Flush()
            {
                for (int i = 0; i + 1 < vals.Count; i += 2) cur.Add((vals[i], vals[i + 1]));
                vals.Clear();
                if (cur.Count >= 3) polys.Add(cur);
                cur = new();
            }
            // ab Offset 36: Floats, Gruppen getrennt durch 0xEFBEADDE; Polygone = Gruppen mit >= 3 Punkten
            for (int o = 36; o + 4 <= b.Length; o += 4)
            {
                uint u = BitConverter.ToUInt32(b, o);
                if (u == 0xEFBEADDEu) { if (vals.Count > 0) Flush(); continue; }
                vals.Add(BitConverter.ToSingle(b, o));
            }
            if (vals.Count > 0) Flush();
            // Reihenfolge der Polygone -> Gebiets-Label (Lage der Plaketten h1..h4 / i1..i2 im Film)
            string[] labels = IsHawaii() ? new[] { "h4", "h1", "h3", "h2" } : new[] { "i1", "i2" };
            for (int i = 0; i < polys.Count && i < labels.Length; i++) _zones.Add((labels[i], polys[i].ToArray()));
        }
        catch (Exception ex) { Log.Warn("cruisingzones.bin: " + ex.Message); }
    }

    static bool InPolygon((float x, float z)[] poly, float px, float pz)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i]; var c = poly[j];
            if ((a.z > pz) != (c.z > pz) && px < (c.x - a.x) * (pz - a.z) / (c.z - a.z) + a.x) inside = !inside;
        }
        return inside;
    }

    /// <summary>Gebiet (Flash-Label h1..h4 / i1..i2) an der Spielerposition; null außerhalb aller Gebiete.</summary>
    public string? CurrentArea()
    {
        var gp = PlayerGamePos();
        return gp == null ? null : AreaAt(gp.Value.X, gp.Value.Z);
    }

    /// <summary>Gebiet (h1..h4 / i1..i2) an einer Position der aktuellen Insel (Spielkoordinaten); null ohne Gebiete.
    /// Auch für die Garage (Fahrzeugliste/Platz wechseln: Gebiet des Hauses, Original /:CurHouseArea).</summary>
    public string? AreaAt(float px, float pz)
    {
        EnsureZones();
        if (_zones == null || _zones.Count == 0) return null;
        foreach (var z in _zones) if (InPolygon(z.poly, px, pz)) return z.label;
        // knapp außerhalb (Küste/Meer): nächstes Gebiet
        string? best = null; float bd = float.MaxValue;
        foreach (var z in _zones)
        {
            float cx = 0, cz = 0; foreach (var q in z.poly) { cx += q.x; cz += q.z; }
            cx /= z.poly.Length; cz /= z.poly.Length;
            float d = (cx - px) * (cx - px) + (cz - pz) * (cz - pz);
            if (d < bd) { bd = d; best = z.label; }
        }
        return best;
    }

    void UpdateMapMarker()
    {
        var ui = _ui!;
        var gp = PlayerGamePos();
        if (gp == null) return;
        const string loc = "/pause_menu/data_extd/map/island/location";
        var lure = ui.Find(loc + "/lure");
        if (lure == null) return;
        var r = ui.LocalBounds(lure);   // im Koordinatensystem von location (wie GetBounds im Spiel)
        if (!r.HasValue) return;
        bool hawaii = IsHawaii();
        float ax = (hawaii ? 11 : 7) * 6144f, az = (hawaii ? 9 : 6) * 6144f;
        ui.SetPosition(loc + "/circle", new Vector2(r.Value.width * gp.Value.X / ax, r.Value.height * gp.Value.Z / az));
    }

    // ------------------------------------------------------------------ Spielerlisten (Lobby, Zuletzt gesehen …)
    // Liste "players_list" (ASFlashList, Name = list_name): Zeilen line1..6 mit PlayerName / PlayerTitle / GlobalLevel,
    // Online-Symbol line%d/content/state (online/offline).
    static readonly HashSet<string> PlayerLists = new(StringComparer.OrdinalIgnoreCase) { "friends", "club_members", "current_players", "recent_players", "black_list" };
    readonly List<VmfPlayer.FlashList> _playerLists = new();
    readonly Dictionary<VmfPlayer.FlashList, List<PlayerInfo>> _listPlayers = new();
    float _nextListRefresh;

    void OnListCreated(VmfPlayer.FlashList l)
    {
        if (string.Equals(l.Name, "controls_list", StringComparison.OrdinalIgnoreCase))
        {
            _controlsList = l;
            TDU2Controls.Load();
            FillControls(l);
            // wie Unity OnListCreated: Liste zeichnen und als geladen melden (sonst bleibt die Seite in der Ladeanimation)
            _ui?.ListRefresh(l);
            _ui?.ListLoaded(l);
            return;
        }
        if (!PlayerLists.Contains(l.Name ?? "")) return;
        var ui = _ui!;
        FillPlayers(l);
        foreach (var old in _playerLists.FindAll(x => x.Owner == l.Owner)) _listPlayers.Remove(old);
        _playerLists.RemoveAll(x => x.Owner == l.Owner || x.Owner == null);
        _playerLists.Add(l);
        ui.ListRefresh(l);
        ui.ListLoaded(l);
        ApplyPlayerLineStates(l);
    }

    /// <summary>Spieler einer Liste: Lobby = alle online (eigene Insel zuerst); Freunde/Club/Zuletzt gesehen folgen mit dem Multiplayer.</summary>
    static List<PlayerInfo> ListFor(string? name)
    {
        var r = new List<PlayerInfo>();
        if (string.Equals(name, "current_players", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in TDU2GameData.Players.Values) if (p.Online) r.Add(p);
            r.Sort((a, b) => (b.SameIsland ? 1 : 0).CompareTo(a.SameIsland ? 1 : 0) != 0 ? (b.SameIsland ? 1 : 0).CompareTo(a.SameIsland ? 1 : 0) : string.Compare(a.Nick, b.Nick, StringComparison.OrdinalIgnoreCase));
        }
        return r;
    }

    void FillPlayers(VmfPlayer.FlashList l)
    {
        var players = ListFor(l.Name);
        _listPlayers[l] = players;
        l.ColumnVars = new[] { "PlayerName", "PlayerTitle", "GlobalLevel" };
        l.Rows.Clear();
        foreach (var p in players) l.Rows.Add(new[] { p.Nick, p.CarName, System.Math.Max(1, p.NbCars).ToString() });
        if (l.Selected >= l.Rows.Count) { l.Selected = System.Math.Max(0, l.Rows.Count - 1); l.Top = 0; }
    }

    void ApplyPlayerLineStates(VmfPlayer.FlashList l)
    {
        var ui = _ui!;
        if (!_listPlayers.TryGetValue(l, out var players)) return;
        string op = VmfPlayer.PathOf(l.Owner);
        for (int i = 1; i <= l.ViewSize; i++)
        {
            int r = l.Top + i - 1;
            if (r < 0 || r >= players.Count) continue;
            string line = (op == "/" ? "" : op) + "/line" + i;
            ui.ForceVisible(line + "/content/forbidden", false);
            ui.GotoLabel(line + "/content/state", players[r].Online ? "online" : "offline", false);
            if (players[r].Nationality > 0) ui.SetFrame(line + "/content/nationality", players[r].Nationality);   // Flagge = Bild Ländernummer
        }
    }

    /// <summary>Offene Spielerliste regelmäßig aktualisieren (Spieler kommen/gehen) – nur bei geändertem Inhalt neu zeichnen.</summary>
    void UpdatePlayerLists()
    {
        if (_playerLists.Count == 0 || _clock < _nextListRefresh) return;
        _nextListRefresh = _clock + 1f;
        var ui = _ui!;
        foreach (var l in _playerLists)
        {
            if (l.Owner == null || !ui.IsShown(l.Owner)) continue;
            string before = RowsKey(l);
            FillPlayers(l);
            if (RowsKey(l) == before) continue;
            ui.ListRefresh(l);
            ui.ListLoaded(l);
            ApplyPlayerLineStates(l);
        }
    }

    static string RowsKey(VmfPlayer.FlashList l)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in l.Rows) { foreach (var c in r) sb.Append(c).Append('|'); sb.Append('\n'); }
        return sb.ToString();
    }

    void ReloadPlayerLists()
    {
        var ui = _ui!;
        foreach (var l in _playerLists)
        {
            if (l.Owner == null) continue;
            FillPlayers(l);
            ui.ListRefresh(l);
            ui.ListLoaded(l);
            ApplyPlayerLineStates(l);
        }
    }

    /// <summary>Spielersuche (/search_player, _root.PlayerSearched): gefunden -> endLoading(), sonst timeOutLoading().</summary>
    void SearchPlayer()
    {
        var ui = _ui!;
        string name = (ui.GetVariable("/:PlayerSearched") ?? "").Trim();
        PlayerInfo? hit = null;
        if (name.Length > 0)
            foreach (var p in TDU2GameData.Players.Values)
                if (string.Equals(p.Nick, name, StringComparison.OrdinalIgnoreCase)) { hit = p; break; }
        // Freundschaftsanfrage über den Server (Unity TDU2Friends) folgt mit dem Multiplayer
        ui.SetValue("/:PlayerFound", hit != null);
        ui.CallClip("/search_player", hit != null ? "endLoading" : "timeOutLoading");
        Log.Info($"Pausemenü: Spieler '{name}' {(hit != null ? "gefunden" : "nicht gefunden")}");
    }

    // ------------------------------------------------------------------ Steuerung: Tastenbelegungs-Liste (Unity TDU2PauseMenu)
    // Liste je Reiter wie im Original (_root.WhatListe: "driving" Fahren, "walkmode" Laufen/Avatar, "menu" Menü).
    VmfPlayer.FlashList? _controlsList;
    List<TDU2Controls.Action> _shownControls = new();
    float _binderSince = -1f;

    void FillControls(VmfPlayer.FlashList l)
    {
        bool de = FlashHost.Language == "ge";
        var cat = TDU2Controls.CategoryOf(_ui?.GetVariable("/:WhatListe"));
        _shownControls = TDU2Controls.InCategory(cat);
        l.Rows.Clear();
        // dritte Spalte: Belegung am Gamepad/Lenkrad (folgt mit TDU2InputDevices)
        foreach (var a in _shownControls) l.Rows.Add(new[] { de ? a.Name : a.NameEn, TDU2Controls.BindText(a), TDU2InputDevices.BindingText(a.Id) });
        if (l.Selected >= l.Rows.Count) { l.Selected = 0; l.Top = 0; }
    }

    TDU2Controls.Action? SelectedControl()
    {
        var l = _controlsList;
        if (l == null || l.Selected < 0 || l.Selected >= _shownControls.Count) return null;
        return _shownControls[l.Selected];
    }

    /// <summary>Steht der Film zwischen den Bildmarken binder_in und binder_out ("Taste drücken …")?</summary>
    bool InBinder()
    {
        var o = _controlsList?.Owner;
        if (o == null || _ui == null || !_ui.IsShown(o)) return false;
        var lb = o.Timeline.Labels;
        return lb.TryGetValue("binder_in", out int a) && lb.TryGetValue("binder_out", out int b) && o.Frame >= a && o.Frame < b;
    }

    /// <summary>Wartet das Menü auf eine Taste: nächste Taste zuweisen (ESC = abbrechen). true = Eingabe verbraucht.</summary>
    bool UpdateBinder()
    {
        if (!InBinder()) { _binderSince = -1f; return false; }
        // erst nach dem Öffnen der Eingabe-Box zuweisen (die Taste, die sie geöffnet hat, zählt nicht)
        if (_binderSince < 0f) { _binderSince = _clock; TDU2InputDevices.BeginAxisCapture(); return true; }
        if (_clock - _binderSince < 0.15f) return true;
        var act = SelectedControl();
        // Gas/Bremse/Kupplung/Lenken: Pedal treten bzw. Lenkrad drehen belegt die Achse (Gerät + Achse + Richtung)
        if (act != null && _controlsList != null && TDU2InputDevices.IsAnalogAction(act.Id) && TDU2InputDevices.CaptureAxis(act.Id, out var axisText))
        {
            _binderSince = -1f;
            FillControls(_controlsList);
            _ui!.ListRefresh(_controlsList);
            Log.Info($"Steuerung: Achse {axisText} -> {act.Id}");
            _ui.GotoLabel(VmfPlayer.PathOf(_controlsList.Owner), "binder_out", true);
            return true;
        }
        var key = TDU2Controls.AnyKeyDown();
        if (key == 0) return true;
        _binderSince = -1f;
        if (key != VK_ESCAPE && act != null && _controlsList != null)
        {
            TDU2Controls.Bind(act, key);
            FillControls(_controlsList);
            _ui!.ListRefresh(_controlsList);
            Log.Info($"Steuerung: Taste {TDU2Controls.KeyName(key)} -> {act.Id}");
        }
        if (_controlsList != null) _ui!.GotoLabel(VmfPlayer.PathOf(_controlsList.Owner), "binder_out", true);
        return true;
    }

    bool HandleControlsCommand(string c)
    {
        switch (c)
        {
            case "opts:cont:vadt":            // Y: übernehmen (Unity: speichern – hier in den Spielstand)
                TDU2Controls.Save();
                SessionAutosave.Schedule(0.5f);
                return true;
            case "opts:cont:canc":            // verwerfen
                TDU2Controls.Load();
                return true;
            case "opts:cont:bind:open":       // Liste (Fahren/Laufen/Menü) geöffnet: frisch füllen, oben beginnen
                if (_controlsList != null && _ui != null)
                {
                    TDU2Controls.Load();
                    FillControls(_controlsList);
                    _controlsList.Selected = 0; _controlsList.Top = 0;
                    _ui.ListRefresh(_controlsList);
                    _ui.ListLoaded(_controlsList);
                }
                return true;
            case "widg:kmap:dflt":            // Standardbelegung (Hilfeleiste)
            case "opts:cont:dflt":
                TDU2Controls.ResetDefaults();
                TDU2InputDevices.ClearOverrides();   // eigene Achsen-Belegungen des Geräts auch zurück auf das Profil
                if (_controlsList != null && _ui != null) { FillControls(_controlsList); _ui.ListRefresh(_controlsList); }
                return true;
            case "opts:cont:bind:delt":       // Belegung löschen
                {
                    var act = SelectedControl();
                    if (act != null && _controlsList != null && _ui != null) { TDU2Controls.Unbind(act); FillControls(_controlsList); _ui.ListRefresh(_controlsList); }
                    return true;
                }
        }
        return false;
    }
}
