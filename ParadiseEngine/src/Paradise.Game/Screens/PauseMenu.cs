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
/// Optionsseiten: Options (TDU2Options). Spielerkarte (pcrd:open), Freunde (frre/delf), Beitreten/Einladen (join:wait, pjoi,
/// pcrd:pinv -> Game/Online/Party). Spielerlisten Freunde/Club/Lobby/Zuletzt gesehen mit MyTDULife-Fotos (TDU2GameData.ListFor,
/// FetchLife), eigenes Foto im Ausweis. Renn-Modus (WhatGameMode 2: Neu starten/Beenden) über RegisterRace.
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

    // ------------------------------------------------------------------ Renn-Modus (Unity TDU2PauseMenu.RegisterRace)
    /// <summary>Rennen läuft: Menü im Rennmodus (WhatGameMode 2) mit "Neu starten" und "Beenden" = Rennen abbrechen.</summary>
    bool _raceMode;
    Action? _raceQuit, _raceRestart;   // Anbieter, der beim Öffnen aktiv war

    /// <summary>Anbieter für das Renn-ESC-Menü (Rennschul-Prüfung, Meisterschaft, Missionen …).</summary>
    sealed class RaceProvider { public object Owner = null!; public Func<bool>? Active; public Action? Restart, Quit; }
    static readonly List<RaceProvider> _raceProviders = new();

    /// <summary>
    /// Renn-Modus des ESC-Menüs anmelden (WhatGameMode 2: "Neu starten" / "Beenden"). Mehrere Anbieter möglich; beim Öffnen
    /// gilt der erste aktive. Gleicher Besitzer ersetzt seine alte Anmeldung.
    /// </summary>
    public static void RegisterRace(object owner, Func<bool> active, Action restart, Action quit)
    {
        _raceProviders.RemoveAll(p => p.Owner == owner);
        _raceProviders.Add(new RaceProvider { Owner = owner, Active = active, Restart = restart, Quit = quit });
    }

    public static void UnregisterRace(object owner) => _raceProviders.RemoveAll(p => p.Owner == owner);

    static bool FindRace(out Action? restart, out Action? quit)
    {
        restart = null; quit = null;
        foreach (var p in _raceProviders)
        {
            bool on = false;
            try { on = p.Active != null && p.Active(); } catch (Exception) { }
            if (on) { restart = p.Restart; quit = p.Quit; return true; }
        }
        return false;
    }

    /// <summary>Rennen neu starten/abbrechen: Menü schließen, dann die Aktion des Anbieters.</summary>
    void RaceAction(Action? a) { Close(); _raceMode = false; a?.Invoke(); }
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
        ui.ListValidated += OnListValidated;   // A auf einer Spielerzeile -> Spielerkarte
        ui.ListLineDrawn += OnListLineDrawn;   // Zeile neu beschrieben (auch beim Blättern): Flagge, Online-Symbol, Foto
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
        _raceMode = FindRace(out _raceRestart, out _raceQuit);
        ui.SetValue("/:WhatGameMode", _raceMode ? 2 : 1);   // 1 Freie Fahrt, 2 Rennen (Fortsetzen/Neu starten/…/Beenden)
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
        if (_raceMode) { ui.SetValue("/:LockRestart", false); ui.SetValue("/:LockQuit", false); }
        // exe 0x9eaccb: Editor frei, sobald die Geschichte durch ist (Schritt >= 11) und das Entwickler-Flag DISABLEGME
        // (0x1b0e384, Konsolenbefehl) nicht gesetzt ist; der Film zeigt den Punkt nur in der Freifahrt (WhatGameMode 1).
        ui.SetValue("/:LockEditor", StoryProgress.Get(TDU2Session.Nick) < 11);
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
        ui.ForceVisible("/options/controls_menu/content_menu/pc/binder_key/#2", false);
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
        if (ui != null) UpdateOwnPhoto();   // Unity: jedes Bild, sobald der Film geladen ist
        if (!_open || ui == null) return;
        UpdateClock();
        UpdateMapMarker();
        UpdatePlayerLists();
        if (UpdateBinder()) return;   // "Taste zuweisen" wartet auf eine Taste
        if (_pendingDialog) AnswerDialog();
        if (Paradise.Game.Gamemodes.FlashDialog.Active) return;   // Bestätigungsbox offen: Tasten gehören ihr

        // Texteingabe (setInputFocus, z.B. "Spieler suchen"): Buchstaben gehören dem Feld, nur Enter bestätigt, ESC bricht ab
        bool typing = ui.InputFocus != null;
        if (!typing)
        {
            // Menü-Belegung (Game/Config/KeyConfig.cs, Unity TDU2Controls "menu_*")
            // Tasten-Buttons des Films zuerst, sonst Move (Gamemodes/Utils/FlashNav, wie der Flash Player des Spiels)
            FlashNav.Directions(ui);
        }
        // Texteingabe: nur Enter bestätigt (Buchstaben gehören dem Textfeld), sonst die belegbare Bestätigungstaste
        if (typing ? _ctx.Pressed(VK_RETURN) : TDU2Controls.ConfirmPressed()) FlashNav.Send(ui, FlashKey.Confirm);
        bool esc = _ctx.Pressed(VK_ESCAPE);
        bool back = !typing && TDU2Controls.BackPressed();
        if (esc || back)
        {
            // Zurück; reagiert der Film nicht (keine B-Funktion), Menü direkt schließen – nie im Pausenzustand hängen bleiben
            if (!FlashNav.Send(ui, FlashKey.Back)) { Log.Info("Pausemenü: B nicht verarbeitet -> schließen"); Close(); return; }
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
        if (!typing && FlashNav.Pressed(FlashKey.X)) FlashNav.Send(ui, FlashKey.X);
        if (!typing && FlashNav.Pressed(FlashKey.Y)) FlashNav.Send(ui, FlashKey.Y);
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
        if (c == "pcrd:open") { OpenPlayerCard(); return; }  // Spielerkarte: Daten liefern
        if (c == "plli") { TDU2GameData.RequestRecent(); ReloadPlayerLists(); return; }  // Spielerliste (Reiter) geöffnet
        if (c == "psch:vali") { SearchPlayer(); return; }  // "Spieler suchen": Name bestätigt
        if (c == "psch:canc") return;
        if (HandleControlsCommand(c)) return;                // Steuerung (Tastenbelegung)
        if (HandleFriendCommand(c)) return;                  // Freund hinzufügen / entfernen (Spielerkarte)
        if (HandlePartyCommand(c)) return;                   // Beitreten / Einladen (Spielerkarte, Party)
        if (_options != null && _ui != null && _options.Handle(_ui, c)) return;   // Optionsseiten
        switch (c)
        {
            case "paou":           // Pause beendet (nach der Schließen-Animation)
                Close();
                break;
            case "goed":           // Menüpunkt "Editor" (valid_menu, exe 0x9ec1bb): nach der Pause in den Challenge-Editor
                // Die exe schließt das Pausemenü dabei selbst (kein paou: der Film sendet nach goed keins, das Menü blieb offen,
                // bis ESC kam – das war das lange Warten).
                GAME_MODE_PAUSE.Next = Gamemode.GAME_MODE_EDITOR;
                Close();
                break;
            case "rest" when _raceMode:   // Rennen neu starten
                RaceAction(_raceRestart);
                break;
            case "quit" when _raceMode:   // im Rennen: "Beenden" = Rennen abbrechen (nicht das Spiel)
            case "fain" when _raceMode:
                RaceAction(_raceQuit);
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
    // DialogBox() mit _root.PlayerAnswer ("a" Ja, "b" Nein). Box aus general.bnk (FlashDialog), wie im Spiel.
    void AnswerDialog()
    {
        _pendingDialog = false;
        var ui = _ui!;
        Paradise.Game.Gamemodes.FlashDialog.Open(ui, ans => DeliverAnswer(ui, ans));
    }

    void DeliverAnswer(VmfPlayer ui, string ans)
    {
        string kase = ui.GetVariable("/:DgbxCase") ?? "";
        // Im Rennen: "Beenden"/"Neu starten" bestätigt -> direkt ausführen (die Antwortkette des Films kommt nicht immer an);
        // Anbieter, der beim Öffnen aktiv war (Rennschule, Meisterschaft …)
        if (_raceMode && ans == "a" && string.Equals(kase, "confirm_quit_race", StringComparison.OrdinalIgnoreCase)) { RaceAction(_raceQuit); return; }
        if (_raceMode && ans == "a" && string.Equals(kase, "confirm_restart", StringComparison.OrdinalIgnoreCase)) { RaceAction(_raceRestart); return; }
        var owners = ui.FunctionOwners("DialogBox");
        string? target = null;
        foreach (var o in owners) if (o.IndexOf("options", StringComparison.OrdinalIgnoreCase) >= 0) { target = o; break; }
        if (target == null && owners.Count > 0) target = owners[owners.Count - 1];
        Log.Info($"Pausemenü: Dialog '{kase}' -> {ans} ({target ?? "kein DialogBox"})");
        ui.SetVariable("/:PlayerAnswer", ans);
        if (target == null || !ui.CallClip(target, "DialogBox"))
        {
            ui.SetValue("/:Input", true);
            ui.SetValue("/:_buttons", true);
        }
        //This is why the Windows was still Open.
        if (kase == "confirm_exit_game" && ans == "a")
        {
            _ctx.Quit();
            ProcessEnd.Now();
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
        TDU2GameData.RequestRecent();   // "Zuletzt gesehen" vom Server nachladen
        FillPlayers(l);
        foreach (var old in _playerLists.FindAll(x => x.Owner == l.Owner)) _listPlayers.Remove(old);
        _playerLists.RemoveAll(x => x.Owner == l.Owner || x.Owner == null);
        _playerLists.Add(l);
        ui.ListRefresh(l);
        ui.ListLoaded(l);
        ApplyPlayerLineStates(l);
    }

    void FillPlayers(VmfPlayer.FlashList l)
    {
        var players = TDU2GameData.ListFor(l.Name);   // Unity TDU2GameData.ListFor: Freunde, Club, Lobby, Zuletzt gesehen, Sperrliste
        _listPlayers[l] = players;
        l.ColumnVars = new[] { "PlayerName", "PlayerTitle", "GlobalLevel" };
        l.Rows.Clear();
        foreach (var p in players)
        {
            string title = p.CarName;
            if (l.Name == "recent_players" && !p.Online && p.LastSeen != default) title = (title.Length > 0 ? title + "  –  " : "") + p.LastSeen.ToString("dd.MM. HH:mm");
            l.Rows.Add(new[] { p.Nick, title, System.Math.Max(1, p.NbCars).ToString() });
        }
        if (l.Selected >= l.Rows.Count) { l.Selected = System.Math.Max(0, l.Rows.Count - 1); l.Top = 0; }
    }

    void ApplyPlayerLineStates(VmfPlayer.FlashList l)
    {
        if (!_listPlayers.ContainsKey(l)) return;
        for (int i = 1; i <= l.ViewSize; i++) ApplyPlayerLine(l, i, l.Top + i - 1);
    }

    /// <summary>Rückruf der nativen Liste je Zeile (exe 0x9ef1e0) – nur Spielerlisten.</summary>
    void OnListLineDrawn(VmfPlayer.FlashList l, int line, int row)
    {
        if (_ui != null && _listPlayers.ContainsKey(l)) ApplyPlayerLine(l, line, row);
    }

    /// <summary>
    /// Eine Spielerzeile wie das Spiel (exe 0x9ef1e0): content/forbidden aus, content/nationality = Bild Ländernummer (1..113,
    /// sonst 135), content/state online/offline, Foto in content/photo/lure. Bild/Label festgehalten (PinFrame/PinLabel), weil der
    /// Film die Zeilen-Clips beim Aufbau/Blättern neu anlegt – sonst fehlte die Flagge.
    /// </summary>
    void ApplyPlayerLine(VmfPlayer.FlashList l, int i, int r)
    {
        var ui = _ui!;
        if (!_listPlayers.TryGetValue(l, out var players) || r < 0 || r >= players.Count) return;
        string op = VmfPlayer.PathOf(l.Owner);
        string line = (op == "/" ? "" : op) + "/line" + i;
        var pl = players[r];
        ui.ForceVisible(line + "/content/forbidden", false);
        ui.PinLabel(line + "/content/state", pl.Online ? "online" : "offline");
        ui.PinFrame(line + "/content/nationality", pl.Nationality >= 1 && pl.Nationality <= 113 ? pl.Nationality : 135);   // Flagge = Bild Ländernummer
        // MyTDULife-Foto des Spielers (kommt per op "profile" nach, Version aus der Spielerliste)
        ui.SetClipTexture(line + "/content/photo/lure", pl.Photo);
        SetPhotoState(line + "/content/photo", pl.Photo != null, IsFemale(pl));
        // Profil noch nie geholt (Freunde/Club/Zuletzt gesehen kennen die Foto-Version nicht) -> wie beim Öffnen der Karte holen
        if (pl.Life == null || (pl.PhotoVer > 0 && (pl.Photo == null || pl.PhotoTexVer != pl.PhotoVer)))
            TDU2GameData.FetchLife(pl, _ => { if (_ui != null && _listPlayers.ContainsKey(l)) ApplyPlayerLineStates(l); });
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
        // unter allen bekannten Spielern suchen (online, Freunde, Club, zuletzt gesehen)
        if (name.Length > 0)
            foreach (var ln in PlayerLists)
            {
                foreach (var p in TDU2GameData.ListFor(ln))
                    if (p != null && string.Equals(p.Nick, name, StringComparison.OrdinalIgnoreCase)) { hit = p; break; }
                if (hit != null) break;
            }
        if (hit != null) _cardPlayer = hit;
        // Wie im Original: "Nach Spieler suchen" schickt dem Spieler eine Freundschaftsanfrage (Erfolg = Dialog search_player_ok
        // "Freundschaftsanfrage gesendet"). Der Server kennt alle Konten – auch Spieler, die nie in einer Liste standen (Unity).
        if (TDU2Session.LoggedIn && name.Length > 0)
        {
            Friends.Request(name, r =>
            {
                bool ok = r != null && r.ok;
                Log.Info($"Pausemenü: Freundschaftsanfrage '{name}' -> {(ok ? r!.friendResult : (r?.error ?? "keine Antwort"))}");
                if (_ui == null) return;
                _ui.SetValue("/:PlayerFound", ok);
                _ui.CallClip("/search_player", ok ? "endLoading" : "timeOutLoading");
            });
            return;
        }
        ui.SetValue("/:PlayerFound", hit != null);
        ui.CallClip("/search_player", hit != null ? "endLoading" : "timeOutLoading");
        Log.Info($"Pausemenü: Spieler '{name}' {(hit != null ? "gefunden" : "nicht gefunden")}");
    }

    // ------------------------------------------------------------------ Spielerkarte (Unity TDU2PauseMenu.OpenPlayerCard)
    // A auf einer Zeile -> FSCommand "pcrd:open", Daten in _root.listAvatarName/listMoney/…, dann _root.Clip.IsCardLoaded = true.
    PlayerInfo? _cardPlayer;

    void OnListValidated(VmfPlayer.FlashList l)
    {
        _cardPlayer = null;
        if (l != null && _listPlayers.TryGetValue(l, out var players) && l.Selected >= 0 && l.Selected < players.Count) _cardPlayer = players[l.Selected];
    }

    string CardPath
    {
        get
        {
            string? card = _ui?.GetVariable("/:PlayerCardPath");
            return string.IsNullOrEmpty(card) ? "/players/player_card" : card;
        }
    }

    void OpenPlayerCard()
    {
        var ui = _ui!;
        var p = _cardPlayer;
        // Menü der Karte baut der Film nach _root.PlayerType (setMenu, main.bnk): "current" (Aktuelle Spieler) hat im Original nur
        // Link/Freund/Chat … – gewünscht sind dort Beitreten + Einladen auf Platz 1 und 2 wie bei Freunde/Zuletzt gesehen ("recent").
        if (string.Equals(ui.GetVariable("/:PlayerType"), "current", StringComparison.OrdinalIgnoreCase)) ui.SetVariable("/:PlayerType", "recent");
        ui.SetVariable("/:listAvatarName", p?.Nick ?? "");
        ui.SetNumber("/:listNationality", p != null && p.Nationality >= 1 && p.Nationality <= 113 ? p.Nationality : 135);   // unbekannt = 135 (exe 0xc09030)
        ui.SetVariable("/:listMoney", p != null ? p.Money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $" : "");
        ui.SetVariable("/:listNbCars", (p?.NbCars ?? 0).ToString());
        ui.SetVariable("/:listNbHouses", "0");
        // MyTDULife: Smiley, Stimmungstext und Avatar-Foto (Foto/Profil kommen per op "profile" nach)
        ui.SetVariable("/:listMood", p != null && !string.IsNullOrEmpty(p.Mood) ? p.Mood : "neutral");
        ui.SetVariable("/:listMoodTxt", p?.MoodTxt ?? "");
        ApplyCardPhoto(p);
        if (p != null) TDU2GameData.FetchLife(p, OnCardLife);
        ui.SetValue("/:listIsConnected", p != null && p.Online);
        ApplyCardFriendState(p);
        // Kreise (Wettbewerb/Sozial/Entdeckung/Sammlung) und Globales Level: Standard wie Unity PlayerCardStats.Default
        ui.SetValue("/:listGlobalLevel", 1);
        ui.SetValue("/:listGlobalPoints", 0);
        ui.SetValue("/:listGlobalLvlPoints", 0);
        ui.SetVariable("/:GlobalLevelCount", "1");
        ui.SetValue("/:listRacingPoints", 0);
        ui.SetValue("/:listSocialPoints", 0);
        ui.SetValue("/:listCruisingPoints", 0);
        ui.SetValue("/:listCollectionPoints", 0);
        ui.SetVariable("/:listRacingLevelCount", "0");
        ui.SetVariable("/:listCollectionLevelCount", "0");
        ui.SetValue(CardPath + ":IsCardLoaded", true);
        // Der Lader der Karte fragt "_root.Clip.IsCardLoaded" ab (Clip = Verweis auf das offene Menü)
        ui.SetValue("_root.Clip.IsCardLoaded", true);
    }

    // ------------------------------------------------------------------ Eigenes MyTDULife-Foto (Ausweis im Pausemenü / Spielfortschritt)
    static readonly string[] OwnPhotoClips = { "/pause_menu/data_extd/idcard/avatar_photo/lure", "/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/idcard/avatar_photo/lure" };
    Texture2D? _ownPhoto; int _ownPhotoVer = -1;

    void UpdateOwnPhoto()
    {
        var life = TDU2Session.Save?.myLife;
        int ver = life != null && !string.IsNullOrEmpty(life.photo) ? life.photoVer : 0;
        if (ver == _ownPhotoVer) return;
        _ownPhotoVer = ver;
        // altes Foto nicht sofort freigeben (Unity Destroy): der Film zeichnet es evtl. noch in diesem Bild
        _ownPhoto = ver > 0 ? TDU2GameData.DecodePhoto(life!.photo) : null;
        foreach (var c in OwnPhotoClips) _ui!.SetClipTexture(c, _ownPhoto);
    }

    /// <summary>Avatar-Foto des Spielers in die Karte (content/photo/lure); ohne Foto leer.</summary>
    void ApplyCardPhoto(PlayerInfo? p)
    {
        if (_ui == null) return;
        _ui.SetClipTexture(CardPath + "/content/photo/lure", p?.Photo);
        SetPhotoState(CardPath + "/content/photo", p != null && p.Photo != null, IsFemale(p));
    }

    /// <summary>MyTDULife ist angekommen: offene Karte desselben Spielers aktualisieren.</summary>
    void OnCardLife(PlayerInfo p)
    {
        if (_ui == null || p == null || p != _cardPlayer) return;
        _ui.SetVariable("/:listMood", !string.IsNullOrEmpty(p.Mood) ? p.Mood : "neutral");
        _ui.SetVariable("/:listMoodTxt", p.MoodTxt ?? "");
        ApplyCardPhoto(p);
        var ci = _ui.Find(CardPath)?.Sprite;
        if (ci != null && _ui.IsShown(ci)) _ui.CallClip(CardPath, "setPlayerCard");
    }

    /// <summary>Foto-Clip wie im Spiel: "loaded", ohne Foto der Platzhalter nach Geschlecht ("empty_male"/"empty_female").</summary>
    void SetPhotoState(string photoClip, bool loaded, bool female)
    {
        var ui = _ui!;
        string want = loaded ? "loaded" : female ? "empty_female" : "empty_male";
        if (ui.Find(photoClip) != null && ui.Label(photoClip) == want) return;
        ui.PinLabel(photoClip, want);   // festhalten: Zeilen-/Karten-Clips werden vom Film neu angelegt
    }

    static bool IsFemale(PlayerInfo? p)
    {
        if (p == null || string.IsNullOrEmpty(p.Avatar)) return false;
        var c = Paradise.Core.Avatars.AvatarConfig.Decode(p.Avatar);
        return c != null && c.Sex == 1;
    }

    /// <summary>Spieler der Karte als aktueller Online-Eintrag (über den Namen); null = nicht online.</summary>
    PlayerInfo? LiveCardPlayer()
    {
        string? nick = _cardPlayer?.Nick;
        if (string.IsNullOrEmpty(nick)) nick = _ui?.GetVariable("/:listAvatarName");
        if (string.IsNullOrEmpty(nick)) return null;
        foreach (var o in TDU2GameData.Players.Values)
            if (string.Equals(o.Nick, nick, StringComparison.OrdinalIgnoreCase)) return o;
        return null;
    }

    /// <summary>Menü der Karte: IsFriend -> "Aus Freundesliste entfernen" statt "Zu Freunden hinzufügen"; gesperrt für sich selbst/ohne Server.</summary>
    void ApplyCardFriendState(PlayerInfo? p)
    {
        var ui = _ui;
        if (ui == null) return;
        string nick = p?.Nick ?? ui.GetVariable("/:listAvatarName") ?? "";
        bool self = string.Equals(nick, TDU2Session.Nick, StringComparison.OrdinalIgnoreCase);
        bool can = TDU2Session.LoggedIn && nick.Length > 0 && !self;
        bool friend = can && Friends.IsFriend(nick);
        ui.SetValue("/:IsFriend", friend);
        ui.SetValue("/:LockAddFriend", !can || friend || Friends.RequestSentTo(nick));
        ui.SetValue("/:LockAddToFriends", !can || friend || Friends.RequestSentTo(nick));
        ui.SetValue("/:LockDelFriend", !can || !friend);
        ui.SetValue("/:LockFriendRequest", !can);
    }

    bool HandleFriendCommand(string c)
    {
        bool add = c == "frre" || c == "pcrd:frre", del = c == "delf" || c == "pcrd:delf";
        if (!add && !del) return false;
        string? nick = _cardPlayer?.Nick;
        if (string.IsNullOrEmpty(nick)) nick = _ui?.GetVariable("/:listAvatarName");
        Log.Info($"Pausemenü: Freunde {(add ? "Anfrage an" : "entfernen")} '{nick}'");
        if (string.IsNullOrEmpty(nick)) return true;
        void Done(NetResponse? r)
        {
            if (_ui == null) return;
            ApplyCardFriendState(_cardPlayer);
            var ci = _ui.Find(CardPath)?.Sprite;
            if (ci != null && _ui.IsShown(ci)) _ui.CallClip(CardPath, "setMenu");   // Menü der Karte neu aufbauen
        }
        if (add) Friends.Request(nick!, Done); else Friends.Remove(nick!, Done);
        return true;
    }

    // Karte: "pcrd:pjoi" (Beitreten) bzw. join_player.joinPlayer() -> "join:wait" (Ladeanzeige wartet auf _root.CanJoinPlayer)
    // -> "pjoi"; Fehler: _root.JoinError + timeOutLoading() (Dialog player_join_fail). "pcrd:pinv" = Einladen.
    bool HandlePartyCommand(string c)
    {
        var ui = _ui!;
        var p = c == "join:wait" || c == "pjoi" || c == "pcrd:pjoi" || c == "pcrd:pinv" ? LiveCardPlayer() : _cardPlayer;
        switch (c)
        {
            case "join:wait":
                {
                    string err = "Spieler nicht gefunden.";
                    bool ok = p != null && p.Id != 0 && Party.CanJoin(p.Id, out err);
                    Log.Info($"Pausemenü: Beitreten {p?.Nick ?? "-"}: " + (ok ? "ok" : err));
                    if (ok) ui.SetValue("/:CanJoinPlayer", true);
                    else
                    {
                        ui.SetVariable("/:JoinError", err);
                        // Ladeanzeige der Beitreten-Funktion (/join_player); der letzte Besitzer von timeOutLoading war oft die
                        // Spielersuche -> deren Fehlerfenster ("none") statt player_join_fail mit JoinError
                        var owners = ui.FunctionOwners("timeOutLoading");
                        if (!ui.CallClip("/join_player", "timeOutLoading") && (owners.Count == 0 || !ui.CallClip(owners[owners.Count - 1], "timeOutLoading"))) ui.SetValue("/:Input", true);
                    }
                    return true;
                }
            case "join:canc":
                return true;
            case "pjoi":
            case "pcrd:pjoi":
                if (p != null && p.Id != 0)
                {
                    int id = p.Id;
                    Close();
                    Party.Join(id);
                }
                return true;
            case "pcrd:pinv":
                if (p == null || p.Id == 0) Log.Info("Pausemenü: " + (_cardPlayer?.Nick ?? "Spieler") + " ist nicht online – Einladung nicht möglich.");
                else if (p.Id == TDU2Session.PlayerId) Log.Info("Pausemenü: Du kannst dich nicht selbst einladen.");
                else Party.Invite(p.Id);
                return true;
        }
        return false;
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
                    _ui.ForceVisible("/options/controls_menu/content_menu/pc/binder_key/#2", false);
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
