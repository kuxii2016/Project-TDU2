using Paradise.Core.Cinematics;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Savegame;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Config;
using Paradise.Game.Online;
using Paradise.Game.Screens;
using System.Globalization;
using System.Text.RegularExpressions;
using Vector3 = System.Numerics.Vector3;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Anmeldung wie im Spiel, mit der Original-Oberfläche (Port von Unity TDU2Login):
///   1. log_in2.bnk: "Press Start" (Label next nach Taste/Klick).
///   2. main.bnk /menus/profile: Profilliste (FSCommands popn/psel/ngam/quit, X = neues Profil, Entf = Profil vom PC entfernen).
///      /menus/profile/new_profile: Popup wie im Original in zwei Arten (is_connect):
///        gamespy_on  = Online-Profil: Spitzname, E-Mail, Passwort -> Konto auf dem TDU2Server (register/login).
///        gamespy_off = Offline-Profil: nur Spitzname, kein Server, Spielstand nur in der lokalen DATA.
///      Umschalten im Popup: X-Symbol anklicken oder Einfg. Häkchen in der Liste (connected_profileN) = Online-Profil.
///   3. Spielstand: online vom Server (Abgleich mit der lokalen DATA wie Unity SyncSavegame), offline aus der DATA
///      (TDU2SaveGame). Auto + Position laden, neues Profil nach GamePC.cpr. Danach Freifahrt + Autosave (SessionAutosave).
/// Tasten im Profilmenü: Enter wählen, Pfeile hoch/runter, X/Einfg neues Profil, Y neues Spiel, Entf entfernen, Esc/Rücktaste zurück.
/// Im Popup: Tippen, Tab/Pfeile Feld wechseln, Enter bestätigen, Esc abbrechen.
/// </summary>
public class GAME_MODE_LOGIN : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_LOGIN;
    static readonly ScreenDef[] _screens = { ScreenDefs.Login, ScreenDefs.Pause };
    public override IReadOnlyList<ScreenDef> Screens => _screens;

    const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SPACE = 0x20, VK_UP = 0x26, VK_DOWN = 0x28,
              VK_INSERT = 0x2D, VK_DELETE = 0x2E, VK_N = 0x4E, VK_R = 0x52, VK_S = 0x53, VK_W = 0x57, VK_X = 0x58, VK_Y = 0x59;

    const string ProfilePath = "/menus/profile";
    const string NewProfilePath = "/menus/profile/new_profile";

    enum St { Init, PressStart, PressStartOut, Profiles, NewProfile, Busy, Closing, Spawning, Done }
    St _st = St.Init;

    readonly LoginConfig _cfg = new();
    IGameModeContext _ctx = null!;
    VmfPlayer? _intro, _ui;
    bool _subscribed;
    float _clock, _t;

    List<TDU2Session.LocalProfile> _profiles = new();
    string _last = "";
    Task<NetResponse>? _task;
    Action<NetResponse>? _then;
    St _afterBusy;
    float _sentAt;

    bool German => FlashHost.Language == "ge";
    /// <summary>Ländernummer (Flagge): Config, sonst aus der Windows-Region (Unity TDU2GameData.NationalityId).</summary>
    int Nationality => _cfg.NationalityId > 0 ? _cfg.NationalityId : TDU2CountryHelper.GetSystemCountryId();
    VehicleManager? Vm => _ctx.Vehicles?.Manager;
    MapStreamer? Streamer => _ctx.Terrain?.Streamer;

    string ServerHost => _ctx.Options.ServerHost ?? "";
    int ServerPort => _ctx.Options.ServerPort > 0 ? _ctx.Options.ServerPort : _cfg.DefaultTcpPort;
    bool HasServer => _ctx.Options.HasServer;

    // ------------------------------------------------------------------ Ablauf

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        _st = St.Init; _clock = 0f;
        _intro = ctx.Open(ScreenDefs.Login);
        _ui = ctx.Open(ScreenDefs.Pause);
        if (_ui != null) _ui.Visible = false;   // Profilmenü erst nach Press Start (Unity OpenProfiles)
        if (_intro != null)
        {
            _intro.Visible = true;
            //WTF exestiert nicht
            //if (from != Gamemode.GAME_MODE_BOOTMENU) _intro.GotoFrameNumber("/", 0, true);   // erneut geöffnet (F5): Press Start von vorn
        }
        if (HasServer) TDU2Session.Connect(ServerHost, ServerPort);
        if (ctx.Vehicles != null) ctx.Vehicles.Suspended = true;
        TDU2SaveGame.Database = Vm?.Database;
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        EndTitleScene();
        Unsubscribe();
        if (ctx.Vehicles != null) ctx.Vehicles.Suspended = false;
        if (_ui != null && to != Gamemode.GAME_MODE_LOGIN) { _ui.SetValue("/:Input", true); _ui.GotoLabel("/", "default", false); _ui.Visible = false; }
        if (_intro != null) _intro.Visible = false;
    }

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        _clock += time.DeltaTime;
        if (_st != St.Done && ctx.Vehicles != null) ctx.Vehicles.Suspended = true;
        if (TDU2SaveGame.Database == null) TDU2SaveGame.Database = Vm?.Database;
        PollTask();
        UpdateTitleScene(time.DeltaTime);
        switch (_st)
        {
            case St.Init:
                if (_intro != null) _st = St.PressStart;
                else if (Ready) OpenProfiles();
                break;
            case St.PressStart:
                if (ctx.Pressed(VK_R)) TitleScene.Next();   // OpenTDU2: nächstes Kino
                if (Ready && (ctx.Pressed(VK_RETURN) | ctx.Pressed(VK_SPACE) | FlashInput.GetMouseButtonDown(0)))
                {
                    _intro!.GotoLabel("/", "next", true);
                    _st = St.PressStartOut; _t = _clock;
                }
                break;
            case St.PressStartOut:
                if (_clock - _t > 2.4f || (_intro?.Root != null && _intro.Root.Frame >= 120)) { if (_intro != null) _intro.Visible = false; OpenProfiles(); }
                break;
            case St.Profiles: UpdateProfiles(); break;
            case St.NewProfile: UpdateNewProfile(); break;
            case St.Closing: UpdateClosing(); break;
            case St.Spawning: UpdateSpawning(); break;
        }
    }

    // ------------------------------------------------------------------ 3D-Hintergrund (Press-Start-Kino der Insel, Unity UpdateTitleScene)
    bool _titleTried;
    float _fovBefore = -1f;

    /// <summary>Wie im Original (GMLogin): hinter Press Start und den Profilmenüs läuft eines der Press-Start-Kinos.</summary>
    void UpdateTitleScene(float dt)
    {
        bool front = _st is St.Init or St.PressStart or St.PressStartOut or St.Profiles or St.NewProfile or St.Busy or St.Closing;
        if (!front) return;
        var vm = Vm; var s = Streamer; var terrain = _ctx.Terrain;
        if (!_titleTried && vm?.Database != null && s?.Index != null && FlashHost.Renderer != null)
        {
            _titleTried = true;
            if (terrain != null) _fovBefore = terrain.FieldOfView;
            TitleScene.Begin(vm, s, FlashHost.Renderer);
        }
        var ts = TitleScene.Current;
        var cam = terrain?.Camera;
        if (ts == null || s == null || cam == null || !ts.Update(dt)) return;
        cam.Position = s.GameToRender(ts.CameraGame);
        cam.Override = (ts.CameraForward, ts.CameraUp);
        terrain!.FieldOfView = ts.CameraFov;
    }

    void EndTitleScene()
    {
        TitleScene.End();
        var terrain = _ctx?.Terrain;
        if (terrain != null)
        {
            if (terrain.Camera != null) terrain.Camera.Override = null;
            if (_fovBefore > 0f) terrain.FieldOfView = _fovBefore;
        }
        _fovBefore = -1f;
        _titleTried = false;
    }

    /// <summary>Auto des Titel-Kinos zeichnen.</summary>
    public override void Render(IGameModeContext ctx, in GameTime time)
    {
        var s = Streamer;
        if (s != null) TitleScene.Current?.Render(s.RootMatrix);
    }

    /// <summary>Welt, Fahrzeugdatenbank, Spots und main.bnk geladen (Unity Ready).</summary>
    bool Ready => _ui != null && Vm != null && Vm.Ready && Streamer?.Spots != null;

    void PollTask()
    {
        if (_task == null || !_task.IsCompleted) return;
        var t = _task; var then = _then; _task = null; _then = null;
        NetResponse r = t.Status == TaskStatus.RanToCompletion && t.Result != null ? t.Result : NetResponse.Error("net:" + t.Exception?.GetBaseException().Message);
        Log.Info($"Login: Server-Antwort nach {_clock - _sentAt:0.0} s: ok={r.ok} Fehler={(string.IsNullOrEmpty(r.error) ? "-" : r.error)} nick={(string.IsNullOrEmpty(r.nick) ? "-" : r.nick)}");
        if (_st == St.Busy) _st = _afterBusy;
        try { then?.Invoke(r); }
        catch (Exception ex) { Log.Error("Login: " + ex.Message, ex); }
    }

    void Request(NetRequest req, Action<NetResponse> then)
    {
        _afterBusy = _st;
        _st = St.Busy;
        _then = then;
        _sentAt = _clock;
        if (!HasServer || TDU2Session.Client == null)
        {
            _task = Task.FromResult(NetResponse.Error("no_server"));
            return;
        }
        TDU2Session.Connect(ServerHost, ServerPort);
        Log.Info($"Login: Anfrage {req.op} für {req.nick} an {ServerHost}:{ServerPort}");
        _task = TDU2Session.Client.Send(req);
    }

    // ------------------------------------------------------------------ Profilliste (main.bnk /menus/profile)
    string? _avertText;

    void OpenProfiles()
    {
        var ui = _ui;
        if (ui == null) return;
        if (!_subscribed) { ui.FSCommand += OnCommand; ui.HintClicked += OnHintClicked; ui.TextFieldClicked += OnFieldClicked; _subscribed = true; }
        _profiles = TDU2Session.LoadProfiles(out _last);
        _avertText ??= ui.GetVariable("/:m_AvertAtariAccount_") ?? "";
        // Zustand wie beim Spielstart (Frontend: WhatGameMode 0, PC)
        ui.SetValue("/:WhatGameMode", 0);
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetVariable("/:MenuType", "pc");
        ui.SetValue("/:IsPC_X360", true);
        ui.SetValue("/:Input", true);
        ui.SetValue("/:PauseBack", false);
        ui.SetValue("/:EditProfile", false);
        ui.SetValue("/:EditPassOnly", false);
        ui.SetValue("/:ForceSelection", 1);   // ohne Profil kein Spiel: B/Esc = Beenden
        ui.SetValue("/:WhatMouseButton", 0);  // Profilzeile anklicken = wählen
        ui.SetValue("/:NoProfile", _profiles.Count == 0 ? 1 : 0);
        ui.SetVariable("/:CurrentProfile", _last ?? "");
        // Art des Popups "Neues Profil" (X im Film): online nur mit Server
        SetPopupOnline(HasServer && _cfg.NewProfileOnlineWithServer);

        ui.Visible = true;
        ui.GotoLabel("/", "root_stop", false);
        ui.SetVisible("/menus", true);
        ui.SetVisible("/help", true);
        ui.GotoLabel("/menus", "profiles_in", true);
        _st = St.Profiles;
    }

    void Unsubscribe()
    {
        var ui = _ui;
        if (_subscribed && ui != null)
        {
            ui.FSCommand -= OnCommand; ui.HintClicked -= OnHintClicked; ui.TextFieldClicked -= OnFieldClicked;
            ui.ReleaseForced(ProfilePath);
        }
        _subscribed = false;
    }

    /// <summary>Mausklick auf die Tasten-Symbole (OK = A, Abbrechen/Beenden = B, Neues Profil/Online = X).</summary>
    void OnHintClicked(string key)
    {
        if (_st == St.NewProfile)
        {
            if (key == "A") SubmitNewProfile();
            else if (key == "B") CancelNewProfile();
            else if (key == "X" && !_editPassOnly) TogglePopupOnline();
            return;
        }
        if (_st == St.Profiles) _ui!.Input(key);
    }

    /// <summary>Mausklick auf ein Eingabefeld des Popups: Fokus dorthin.</summary>
    void OnFieldClicked(string variable)
    {
        if (_st != St.NewProfile) return;
        int i = _fields.FindIndex(f => string.Equals(f, variable, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) { _focus = i; RenderFields(); }
    }

    void OnCommand(string cmd, string args)
    {
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (_cfg.LogCommands) Log.Info($"Login FSCommand {cmd} {args} (Zustand {_st}, Profile {_profiles.Count})");
        if (c.StartsWith("sndp:")) return;   // Menü-Sounds (FrontendSounds)
        switch (c)
        {
            case "popn": FillProfiles(); break;
            case "psel": OnProfileSelected(); break;
            case "tfoc": FocusField(_ui!.GetVariable("/:TextToFocus")); break;
            case "quit": QuitGame(); break;
            case "ngam":
                if (_st == St.Profiles && _profiles.Count > 0) { _newGame = true; OnProfileSelected(); }
                break;
        }
    }

    void FillProfiles()
    {
        var ui = _ui!;
        int n = Math.Min(8, _profiles.Count);
        int cur = _profiles.FindIndex(p => string.Equals(p.nick, _last, StringComparison.OrdinalIgnoreCase)) + 1;
        if (cur < 1 || cur > n) cur = 1;
        ui.SetValue(ProfilePath + ":nb_profiles", n);
        ui.SetValue(ProfilePath + ":cur_profile", cur);
        for (int i = 1; i <= 8; i++)
        {
            string clip = $"{ProfilePath}/profile{i}";
            string name = i <= n ? _profiles[i - 1].nick : "";
            ui.SetVariable(clip + ":dummy", name);
            ui.SetVariable(clip + ":name", name);
            // Häkchen = Online-Profil (Konto auf dem Server), leer = Offline-Profil. Der Film liest connected_profileN und spielt
            // checkbox "on"/"off" selbst – deshalb nur den Wert setzen.
            ui.SetNumber($"{ProfilePath}:connected_profile{i}", i <= n && _profiles[i - 1].online ? 1 : 0);
            // leere Zeilen ausblenden (der Film setzt _visible bei jedem Öffnen wieder auf true -> festhalten)
            ui.ForceVisible(clip, i <= n ? null : false);
            ui.ForceVisible($"{ProfilePath}/barre/sur{i}", i <= n ? null : false);
        }
        ui.SetValue("/:NoProfile", n == 0 ? 1 : 0);
        ui.SetVariable("/:CurrentProfile", n > 0 ? _profiles[cur - 1].nick : "");
        if (n == 0) _openNewNext = true;   // noch kein Profil: gleich "Neues Profil" (wie im Spiel beim ersten Start)
    }

    bool _openNewNext;

    int CurLine()
    {
        var s = _ui!.GetVariable(ProfilePath + ":cur_line");
        return int.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out int v) ? v
             : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (int)d : 1;
    }

    void UpdateProfiles()
    {
        var ui = _ui!;
        if (_openNewNext) { _openNewNext = false; OpenNewProfile(false, "", HasServer && _cfg.NewProfileOnlineWithServer); return; }
        if (_ctx.Pressed(VK_UP) | _ctx.Pressed(VK_W)) MoveProfile(-1);
        if (_ctx.Pressed(VK_DOWN) | _ctx.Pressed(VK_S)) MoveProfile(1);
        if (_ctx.Pressed(VK_RETURN)) ui.Input("A");
        if (_ctx.Pressed(VK_X) | _ctx.Pressed(VK_INSERT) | _ctx.Pressed(VK_N)) ui.Input("X");
        if (_ctx.Pressed(VK_Y)) ui.Input("Y");   // Original: Y = Neues Spiel (FSCommand ngam) für das gewählte Profil
        if (_ctx.Pressed(VK_ESCAPE) | _ctx.Pressed(VK_BACK)) ui.Input("B");
        if (_ctx.Pressed(VK_DELETE)) RemoveProfile(CurLine() - 1);
    }

    void MoveProfile(int delta)
    {
        var ui = _ui!;
        int n = Math.Min(8, _profiles.Count);
        if (n == 0 || !IsTrue(ui.GetVariable("/:Input"))) return;
        int cur = Math.Clamp(CurLine(), 1, n), nw = Math.Clamp(cur + delta, 1, n);
        if (nw == cur) return;
        ui.GotoLabel($"{ProfilePath}/barre/sur{cur}", "out", true);
        ui.GotoLabel($"{ProfilePath}/barre/sur{nw}", "change", true);
        ui.GotoLabel($"{ProfilePath}/profile{cur}", "normal", false);
        ui.GotoLabel($"{ProfilePath}/profile{nw}", "selected", false);
        ui.SetValue(ProfilePath + ":cur_line", nw);
        ui.SetVariable("/:CurrentProfile", _profiles[nw - 1].nick);
    }

    static bool IsTrue(string? v) => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    void RemoveProfile(int index)
    {
        if (index < 0 || index >= _profiles.Count) return;
        var p = _profiles[index];
        _profiles.RemoveAt(index);
        TDU2Session.StoreProfiles(_profiles, _last);
        // Online: Merk-Token auf dem Server vergessen. Die lokale DATA bleibt (wie im Original: Profil nur aus der Liste).
        var client = TDU2Session.Client;
        if (p.online && client != null) Task.Run(() => client.SendBlocking(new NetRequest { op = "forget", nick = p.nick, token = p.token }));
        Log.Info($"Login: Profil {p.nick} ({(p.online ? "online" : "offline")}) von diesem PC entfernt");
        _ui!.GotoLabel(ProfilePath, "open", true);   // Liste neu aufbauen (popn)
    }

    void OnProfileSelected()
    {
        int i = CurLine() - 1;
        if (i < 0 || i >= _profiles.Count) { if (_profiles.Count > 0) OpenProfiles(); return; }
        var p = _profiles[i];
        if (!p.online) { LoginOffline(p.nick, false); return; }
        _ui!.SetVariable("/:CurrentProfile", (German ? "Anmelden: " : "Logging in: ") + p.nick);
        Request(new NetRequest { op = "login", nick = p.nick, token = p.token, nationality = Nationality }, r =>
        {
            if (r.ok) { RememberProfile(r.nick, r.token, true); CloseMenus(false, r, false); return; }
            if (r.error == "bad_token") { OpenNewProfile(true, p.nick, true); return; }
            // Fehler (Server nicht erreichbar …): der Film hat die Liste beim Bestätigen schon geschlossen -> wieder öffnen
            Log.Warn("Login " + p.nick + ": " + r.error);
            _newGame = false;
            OpenProfiles();
            _ui!.SetVariable("/:CurrentProfile", p.nick + " – " + TDU2Session.ErrorText(r.error, German));
        });
    }

    /// <summary>Offline-Profil: Spielstand aus der lokalen DATA (fehlt sie, ein neuer Spielstand).</summary>
    void LoginOffline(string nick, bool fromPopup)
    {
        SaveData save;
        if (TDU2SaveGame.Exists(nick))
        {
            if (TDU2SaveGame.TryRead(nick, Vm?.Database, out var local, out string why)) { save = local; Log.Info($"Login offline {nick}: DATA gelesen – {why}"); }
            else { Log.Warn($"Login offline {nick}: DATA nicht lesbar ({why}) – neuer Spielstand"); save = new SaveData(); }
        }
        else { Log.Info($"Login offline {nick}: noch keine DATA ({TDU2SaveGame.PathFor(nick)}) – neuer Spielstand"); save = new SaveData(); }
        RememberProfile(nick, "", false);
        var r = new NetResponse { ok = true, nick = nick, save = save };
        if (fromPopup) FinishPopupLogin(r, true);
        else CloseMenus(false, r, true);
    }

    void RememberProfile(string nick, string token, bool online)
    {
        int i = _profiles.FindIndex(p => string.Equals(p.nick, nick, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _profiles.RemoveAt(i);
        _profiles.Insert(0, new TDU2Session.LocalProfile { nick = nick, token = token ?? "", online = online });
        if (_profiles.Count > 8) _profiles.RemoveRange(8, _profiles.Count - 8);
        _last = nick;
        TDU2Session.StoreProfiles(_profiles, _last);
    }

    // ------------------------------------------------------------------ Popup "Neues Profil" / Passwort (new_profile/popup)
    bool _editPassOnly, _popupOnline;
    readonly List<string> _fields = new();
    readonly Dictionary<string, string> _text = new(StringComparer.OrdinalIgnoreCase);
    int _focus;

    /// <summary>Art des Popups setzen: is_connect wählt im Film gamespy_on (online) bzw. gamespy_off (offline).</summary>
    void SetPopupOnline(bool online)
    {
        _popupOnline = online;
        _ui?.SetValue(NewProfilePath + ":is_connect", online);
    }

    void OpenNewProfile(bool passwordOnly, string nick, bool online)
    {
        var ui = _ui!;
        _editPassOnly = passwordOnly;
        SetPopupOnline(online || passwordOnly);
        ui.SetValue("/:EditProfile", false);
        ui.SetValue("/:EditPassOnly", passwordOnly);
        ui.SetValue(ProfilePath + ":isNewProfile", true);
        _text["/:nickname"] = nick ?? "";
        _text["/:password"] = "";
        ui.GotoLabel(NewProfilePath, "open", true);
        // Liste offen (Passwort-Abfrage nach psel): wie X im Film die Liste schließen, Popup bleibt (isNewProfile)
        var list = ui.Find(ProfilePath)?.Sprite;
        if (list != null && list.Frame >= 1 && list.Frame < 9) ui.GotoLabel(ProfilePath, "close", true);
    }

    /// <summary>Online/Offline im offenen Popup wechseln (x_online im Film): gamespy_on &lt;-&gt; gamespy_off.</summary>
    void TogglePopupOnline()
    {
        if (!_popupOnline && !HasServer) { ShowPopupMessage(TDU2Session.ErrorText("no_server", German)); return; }
        SetPopupOnline(!_popupOnline);
        _ui!.GotoLabel(NewProfilePath + "/popup", _popupOnline ? "gamespy_on" : "gamespy_off", false);
        string nick = Value("/:nickname");
        InitFields();
        _text["/:nickname"] = nick;   // Name beim Umschalten behalten
        ShowPopupMessage(null);
        RenderFields();
    }

    /// <summary>Art des Popups laut Film: Label des Clips popup (gamespy_on = online, gamespy_off = offline); null = unbekannt.</summary>
    bool? FilmPopupOnline()
    {
        var inst = _ui?.Find(NewProfilePath + "/popup")?.Sprite;
        if (inst == null) return null;
        var labels = inst.Timeline.Labels;
        if (!labels.TryGetValue("gamespy_on", out int on) || !labels.TryGetValue("gamespy_off", out int off)) return null;
        int f = inst.Frame;
        bool atOn = on <= f && (off > f || on > off);
        bool atOff = off <= f && (on > f || off > on);
        return atOn ? true : atOff ? false : null;
    }

    /// <summary>
    /// Popup-Art an den Film anpassen: der Film wählt gamespy_on/off selbst (is_connect, X-Knopf). Ohne Server geht nur offline –
    /// dann auf gamespy_off umschalten. true = Felder neu aufgebaut.
    /// </summary>
    bool SyncPopupMode()
    {
        if (_editPassOnly) return false;
        var film = FilmPopupOnline();
        if (!film.HasValue) return false;
        bool online = film.Value;
        if (online && !HasServer)
        {
            SetPopupOnline(false);
            _ui!.GotoLabel(NewProfilePath + "/popup", "gamespy_off", false);
            online = false;
        }
        if (online == _popupOnline && _fields.Count > 0) return false;
        string nick = Value("/:nickname");
        _popupOnline = online;
        InitFields();
        _text["/:nickname"] = nick;
        Log.Info("Login: Popup " + (online ? "Online-Profil (gamespy_on)" : "Offline-Profil (gamespy_off)"));
        return true;
    }

    void InitFields()
    {
        _fields.Clear();
        // gamespy_on: Spitzname (/:nickname), Benutzername/E-Mail (/:email), Passwort (/:password); gamespy_off: nur Spitzname
        if (_editPassOnly) _fields.Add("/:password");
        else if (_popupOnline) { _fields.Add("/:nickname"); _fields.Add("/:email"); _fields.Add("/:password"); }
        else _fields.Add("/:nickname");
        if (!_editPassOnly) { _text["/:nickname"] = ""; _text["/:email"] = ""; }   // Passwort-Abfrage: Name bleibt (OpenNewProfile)
        _text["/:password"] = "";
        _focus = 0;
    }

    /// <summary>"tfoc": Film setzt /:TextToFocus auf das Eingabefeld (/:nickname, /:email, /:password).</summary>
    void FocusField(string? var)
    {
        if (string.IsNullOrEmpty(var)) return;
        if (_st != St.NewProfile && _st != St.Busy)
        {
            // Popup gerade geöffnet (X im Film oder OpenNewProfile)
            _editPassOnly = IsTrue(_ui!.GetVariable("/:EditPassOnly"));
            var ic = _ui.GetVariable(NewProfilePath + ":is_connect");
            if (ic != null) _popupOnline = IsTrue(ic) || _editPassOnly;
            InitFields();
            SyncPopupMode();
            ShowPopupMessage(null);
            _st = St.NewProfile;
        }
        int i = _fields.FindIndex(f => string.Equals(f, var, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _focus = i;
        RenderFields();
    }

    void UpdateNewProfile()
    {
        if (_ctx.Pressed(VK_ESCAPE)) { CancelNewProfile(); return; }
        if (_ctx.Pressed(VK_INSERT) && !_editPassOnly) { TogglePopupOnline(); return; }
        if (SyncPopupMode()) RenderFields();
        if (_fields.Count == 0) return;
        if (_ctx.Pressed(VK_TAB) | _ctx.Pressed(VK_DOWN)) _focus = (_focus + 1) % _fields.Count;
        if (_ctx.Pressed(VK_UP)) _focus = (_focus + _fields.Count - 1) % _fields.Count;
        if (_ctx.Pressed(VK_RETURN))
        {
            if (_focus < _fields.Count - 1 && string.IsNullOrEmpty(Value(_fields[_focus + 1]))) _focus++;   // erst alle Felder ausfüllen
            else { SubmitNewProfile(); return; }
        }
        string f = _fields[_focus];
        string v = Value(f);
        int max = f.EndsWith("password") ? 32 : f.EndsWith("email") ? 64 : 16;
        foreach (char ch in FlashInput.inputString)
        {
            if (ch == '\b') { if (v.Length > 0) v = v.Substring(0, v.Length - 1); }
            else if (ch == '\n' || ch == '\r' || ch == '\t') continue;
            else if (!char.IsControl(ch) && v.Length < max) v += ch;
        }
        _text[f] = v;
        RenderFields();
    }

    string Value(string field) => _text.TryGetValue(field, out var v) ? v ?? "" : "";

    void RenderFields()
    {
        var ui = _ui!;
        bool blink = (_clock % 1f) < 0.55f;
        foreach (var f in new[] { "/:nickname", "/:email", "/:password" })
        {
            string v = Value(f);
            string shown = f.EndsWith("password") ? new string('*', v.Length) : v;
            bool focused = _st == St.NewProfile && _fields.Count > 0 && string.Equals(_fields[_focus], f, StringComparison.OrdinalIgnoreCase);
            ui.SetVariable(f, shown + (focused && blink ? "_" : ""));
        }
    }

    void ShowPopupMessage(string? msg) => _ui!.SetVariable("/:m_AvertAtariAccount_", string.IsNullOrEmpty(msg) ? (_avertText ?? "") : msg);

    static readonly Regex NickRx = new("^[A-Za-z0-9_.\\-]{3,16}$");

    void SubmitNewProfile()
    {
        if (_st != St.NewProfile) return;
        string nick = Value("/:nickname").Trim(), pw = Value("/:password"), email = Value("/:email").Trim();
        if (!NickRx.IsMatch(nick)) { ShowPopupMessage(TDU2Session.ErrorText("invalid_nick", German)); _focus = 0; RenderFields(); return; }
        if (!_popupOnline)
        {
            // Offline-Profil: kein Server, kein Passwort – Spielstand nur lokal (DATA)
            Log.Info($"Login: Offline-Profil {nick}");
            LoginOffline(nick, true);
            return;
        }
        if (!_editPassOnly && email.Length > 0 && (email.IndexOf('@') < 1 || email.IndexOf('.') < 0)) { ShowPopupMessage(German ? "E-Mail-Adresse ungültig" : "Invalid e-mail address"); _focus = Math.Max(0, _fields.IndexOf("/:email")); RenderFields(); return; }
        if (pw.Length < 4) { ShowPopupMessage(TDU2Session.ErrorText("weak_password", German)); _focus = _fields.Count - 1; RenderFields(); return; }
        if (!HasServer) { ShowPopupMessage(TDU2Session.ErrorText("no_server", German)); return; }
        ShowPopupMessage((German ? "Verbinde mit " : "Connecting to ") + ServerHost + ":" + ServerPort + " ...");
        var req = _editPassOnly
            ? new NetRequest { op = "login", nick = nick, password = pw, nationality = Nationality }
            : new NetRequest { op = "register", nick = nick, password = pw, email = email, nationality = Nationality };
        Request(req, r =>
        {
            if (!r.ok && !_editPassOnly && r.error == "nick_taken")
            {
                // Konto gibt es schon (z.B. Profil auf diesem PC verloren): mit Name + Passwort anmelden
                ShowPopupMessage(German ? "Konto \"" + nick + "\" gefunden – melde an ..." : "Account \"" + nick + "\" found – logging in ...");
                Request(new NetRequest { op = "login", nick = nick, password = pw, nationality = Nationality }, r2 =>
                {
                    if (!r2.ok)
                    {
                        ShowPopupMessage(r2.error == "bad_password"
                            ? (German ? "Den Namen gibt es schon – Passwort stimmt nicht" : "Name already exists – wrong password")
                            : TDU2Session.ErrorText(r2.error, German));
                        _focus = _fields.Count - 1; _text["/:password"] = ""; RenderFields();
                        return;
                    }
                    Log.Info("Login: bestehendes Konto " + r2.nick + " wiederhergestellt (lokales Profil neu angelegt)");
                    RememberProfile(r2.nick, r2.token, true);
                    FinishPopupLogin(r2, false);
                });
                return;
            }
            if (!r.ok) { ShowPopupMessage(TDU2Session.ErrorText(r.error, German)); return; }
            RememberProfile(r.nick, r.token, true);
            FinishPopupLogin(r, false);
        });
    }

    void FinishPopupLogin(NetResponse r, bool offline)
    {
        _text["/:password"] = "";
        ShowPopupMessage(null);
        _ui!.SetValue(NewProfilePath + ":IsValidNewProfile", true);
        _ui.GotoLabel(NewProfilePath, "close", true);
        CloseMenus(true, r, offline);
    }

    float _cancelAt = -10f;

    void CancelNewProfile()
    {
        var ui = _ui!;
        if (_profiles.Count == 0 && !_editPassOnly)
        {
            // ohne Profil gibt es nichts zurück – erst ein zweites Abbrechen beendet das Spiel (wie "Beenden" im Film)
            if (_clock - _cancelAt < 2f) { QuitGame(); return; }
            _cancelAt = _clock;
            ShowPopupMessage(German ? "Profil anlegen (online mit Konto oder offline). Nochmal Abbrechen = Beenden."
                                    : "Create a profile (online account or offline). Cancel again = quit.");
            return;
        }
        _text["/:password"] = "";
        ShowPopupMessage(null);
        ui.SetValue("/:EditPassOnly", false);
        ui.SetValue(NewProfilePath + ":IsValidNewProfile", false);
        ui.GotoLabel(NewProfilePath, "close", true);   // Film öffnet danach die Profilliste wieder (popn)
        _st = St.Profiles;
        RenderFields();
    }

    // ------------------------------------------------------------------ Menüs schließen, Spielstand abgleichen
    NetResponse? _login;
    bool _fromPopup, _menusClosed, _offline, _newGame, _pushImported;

    void CloseMenus(bool fromPopup, NetResponse r, bool offline)
    {
        _login = r; _fromPopup = fromPopup; _menusClosed = false; _offline = offline;
        if (!offline) SyncSavegame(r);
        _st = St.Closing; _t = _clock;
        var ui = _ui!;
        ui.SetValue("/:EditPassOnly", false);
        ui.SetVariable("/:CurrentProfile", r.nick);
        if (!fromPopup) { ui.GotoLabel(ProfilePath, "close", true); _menusClosed = true; }   // → pcls + profiles_close
    }

    /// <summary>
    /// Unity SyncSavegame: der Server ist die Quelle. Hat er Fortschritt, wird die lokale DATA daraus (neu) gebaut.
    /// Ist der Server-Spielstand leer, die lokale DATA hat aber Fortschritt, wird sie übernommen und an den Server geschickt.
    /// </summary>
    void SyncSavegame(NetResponse r)
    {
        _pushImported = false;
        if (r == null || string.IsNullOrEmpty(r.nick)) return;
        try
        {
            var db = Vm?.Database;
            TDU2SaveGame.Database = db;
            r.save ??= new SaveData();
            var sv = r.save;
            TDU2Session.Normalize(sv);
            bool server = sv.starterChosen || sv.ownedCars.Count > 0 || sv.hasPosition || sv.story > 0;
            bool exists = TDU2SaveGame.Exists(r.nick);
            Log.Info($"Savegame {r.nick}: Server hat Fortschritt={server} (Autos {sv.ownedCars.Count}, Story {sv.story}), lokale DATA={exists} ({TDU2SaveGame.PathFor(r.nick)})");
            if (server)
            {
                // Der Server speichert keine Wreck-Funde. Ohne Merge überschreibt der Login die
                // lokale DATA mit einem Spielstand ohne foundWrecks – jeder bereits gefundene
                // Wrack wäre beim naechsten Start wieder "neu" und zeigte den 1/28-Funddialog.
                if (exists && TDU2SaveGame.TryRead(r.nick, db, out var localWrecks, out _))
                {
                    if ((sv.foundWrecks == null || sv.foundWrecks.Count == 0) && localWrecks.foundWrecks != null && localWrecks.foundWrecks.Count > 0)
                    {
                        sv.foundWrecks = localWrecks.foundWrecks;
                        Log.Info("Savegame: Wreck-Funde aus lokaler DATA uebernommen (" + string.Join("; ", sv.foundWrecks.ConvertAll(e => e.island + "[" + string.Join(",", e.indices) + "]")) + ")");
                    }
                    if ((sv.wreckEvents == null || sv.wreckEvents.Count == 0) && localWrecks.wreckEvents != null && localWrecks.wreckEvents.Count > 0)
                        sv.wreckEvents = localWrecks.wreckEvents;
                    // Der Server speichert die Straßen-Freischaltung nicht zuverlässig: lokale DATA gewinnt,
                    // sonst wären alle Straßen beim nächsten Start wieder grau und die Dealer verschwunden.
                    if ((sv.exploredRoads == null || sv.exploredRoads.Count == 0) && localWrecks.exploredRoads != null && localWrecks.exploredRoads.Count > 0)
                        sv.exploredRoads = localWrecks.exploredRoads;
                    if ((sv.unlockedDealers == null || sv.unlockedDealers.Count == 0) && localWrecks.unlockedDealers != null && localWrecks.unlockedDealers.Count > 0)
                        sv.unlockedDealers = localWrecks.unlockedDealers;
                    if (sv.cruisingRoadPoints == 0 && localWrecks.cruisingRoadPoints > 0)
                        sv.cruisingRoadPoints = localWrecks.cruisingRoadPoints;
                    if ((sv.cruisingRoadEvents == null || sv.cruisingRoadEvents.Count == 0) && localWrecks.cruisingRoadEvents != null && localWrecks.cruisingRoadEvents.Count > 0)
                        sv.cruisingRoadEvents = localWrecks.cruisingRoadEvents;
                    if (sv.roadDiscoveryVersion <= 0) sv.roadDiscoveryVersion = localWrecks.roadDiscoveryVersion > 0 ? localWrecks.roadDiscoveryVersion : 1;
                    // CRC: fehlen die hochgeladenen Challenge-Ids auf dem Server, aus der lokalen DATA uebernehmen
                    if ((sv.uploadedChallenges == null || sv.uploadedChallenges.Count == 0) && localWrecks.uploadedChallenges != null && localWrecks.uploadedChallenges.Count > 0)
                        sv.uploadedChallenges = localWrecks.uploadedChallenges;
                }
                TDU2SaveGame.WriteAsync(r.nick, sv, db, exists ? "lokale DATA mit dem Server-Spielstand aktualisiert" : "lokale DATA fehlte – aus dem Server-Spielstand gebaut"); return;
            }
            if (!exists) { TDU2SaveGame.WriteAsync(r.nick, sv, db, "neues Profil – lokale DATA angelegt"); return; }
            if (!TDU2SaveGame.TryRead(r.nick, db, out var local, out string why)) { Log.Warn("Savegame: lokale DATA nicht lesbar (" + why + ") – Server-Spielstand bleibt"); return; }
            bool localProgress = local.starterChosen || local.ownedCars.Count > 0 || local.hasPosition || local.story > 0;
            if (!localProgress) { Log.Debug("Savegame: lokale DATA ohne Fortschritt – nichts zu übernehmen"); return; }
            TDU2SaveGame.KeepServerOnly(local, sv);
            r.save = local;
            _pushImported = true;
            Log.Info("Savegame: Server-Spielstand leer – lokale DATA übernommen, wird an den Server geschickt (" + why + ")");
        }
        catch (Exception e) { Log.Error("Savegame: Abgleich fehlgeschlagen: " + e.Message); }
    }

    void UpdateClosing()
    {
        var ui = _ui!;
        float dt = _clock - _t;
        if (_fromPopup && !_menusClosed && dt > 0.5f) { _menusClosed = true; ui.GotoLabel("/menus", "profiles_close", true); }
        if (dt < 1.6f) return;
        bool progress = HasProgress(out string why);
        bool newGame = _newGame || !progress;
        if (_newGame) Log.Info($"Profil {_login?.nick}: Neues Spiel (Y)");
        else Log.Info($"Profil {_login?.nick}: {(newGame ? "kein" : "")} Spielfortschritt ({why}) – {(newGame ? "neues Spiel" : "weiterspielen")}");
        FinishLogin(_newGame);
    }

    bool HasProgress(out string why)
    {
        var save = _login?.save;
        int cars = save?.ownedCars?.Count ?? 0;
        bool starter = save != null && save.starterChosen, pos = save != null && save.hasPosition;
        int step = save?.story ?? -1;
        why = $"Startauto: {starter}, Autos: {cars}, Position: {pos}, Story: {step}";
        return starter || cars > 0 || pos || step > 0;
    }

    /// <summary>Anmeldung abschließen und spielen; newGame = Spielstand auf Anfang (Original "Neues Spiel").</summary>
    void FinishLogin(bool newGame)
    {
        if (newGame) ResetForNewGame(_login);
        _newGame = false;
        EndTitleScene();
        var ui = _ui!;
        ui.SetValue("/:Input", true);
        ui.GotoLabel("/", "default", false);
        ui.Visible = false;
        Unsubscribe();
        StartGame(_login!);
    }

    /// <summary>Neues Spiel: frischer Spielstand; behalten werden MyTDULife, Lesezeichen, Lieblings-Spots, eigene Farben (Unity).</summary>
    void ResetForNewGame(NetResponse? r)
    {
        if (r == null) return;
        var old = r.save ?? new SaveData();
        r.save = new SaveData
        {
            myLife = old.myLife ?? new MyLifeData(),
            bookmarks = old.bookmarks ?? new List<BookmarkEntry>(),
            favoriteSpots = old.favoriteSpots ?? new List<string>(),
            favoriteColors = old.favoriteColors ?? new List<int>(),
            story = 0,
        };
        _storyDone = false;
        TDU2SaveGame.WriteAsync(r.nick, r.save, Vm?.Database, "Neues Spiel – lokale DATA zurückgesetzt");
        Log.Info($"Neues Spiel für {r.nick}: Spielstand zurückgesetzt");
    }

    // ------------------------------------------------------------------ Spielstart (Unity StartGame)

    void StartGame(NetResponse r)
    {
        if (_offline) TDU2Session.BeginOffline(r.nick, r.save);
        else TDU2Session.Begin(r);
        var save = TDU2Session.Save;
        // Ladebildschirm (Unity TDU2LoadingScreen) bis Auto gesetzt, Welt um die Kamera gebaut und ggf. Inselwechsel/Interieur fertig –
        // sonst sieht man den Aufbau der Welt. Interieur und Inselwechsel hängen ihre Bedingungen an denselben Bildschirm an.
        LoadingScreen.Show("Spielstart", () => _st == St.Done && !IslandTravel.Pending && InteriorUtils._restore == null
            && (Vm?.Current != null || InteriorUtils.Inside || Story.Active) && LoadingScreen.WorldReady(), 1.5f, false);
        // Kilometerstände des Spielstands in den Tacho (Unity ApplyOdometers)
        foreach (var o in save.odometers) if (o != null && o.meters > 0) VehicleHud.SetOdometer(o.carId, o.meters);
        if (_pushImported) { _pushImported = false; TDU2Session.SaveAsync(); Log.Info("Savegame: übernommener lokaler Spielstand an den Server geschickt"); }
        var vm = Vm!; var s = Streamer!;
        string island = s.Index?.IslandName ?? "";
        if (string.IsNullOrEmpty(save.island)) save.island = island;
        Log.Info($"Angemeldet als {r.nick} ({(_offline ? "offline" : r.online + " online")}) – {save.ownedCars.Count} Autos, Startauto gewählt: {save.starterChosen}, Insel {save.island}, Position={save.hasPosition}, Auto {save.currentCar}");

        // Geschichte (GamePC.cpr SCENARIOTUTORIAL = ON) noch nicht gespielt bzw. mittendrin: Intro und Missionen statt des direkten
        // Starts – wie im Original auf Ibiza (Party-Haus EPH_L3DI_5067I). Unity TDU2Login.StartGame -> BeginStory.
        if (!_storyDone && Story.ShouldRun(GamePC.Load(vm.BnkDir), r.nick, save.starterChosen))
        {
            if (!island.StartsWith("ibiza", StringComparison.OrdinalIgnoreCase))
            {
                if (!IslandTravel.Pending && IslandTravel.CanTravelTo("ibiza", vm.BnkDir))
                {
                    save.hasPosition = false;
                    _st = St.Done;
                    IslandTravel.Go("ibiza", false);
                    Story.BeginAfterTravel(_ctx, r.nick, r);   // beginnt, sobald Ibiza steht
                    return;
                }
            }
            else
            {
                _st = St.Done;
                Story.Begin(_ctx, r.nick, r);
                return;
            }
        }

        // Spielstand auf einer anderen Insel: dorthin wechseln (IslandTravel, mit gespeicherter Position bzw. am Flughafen-Spot)
        bool travel = false;
        if (!string.IsNullOrEmpty(save.island) && !string.Equals(save.island, island, StringComparison.OrdinalIgnoreCase))
        {
            if (IslandTravel.CanTravelTo(save.island, vm.BnkDir)) travel = true;
            else
            {
                Log.Warn($"Spielstand liegt auf {save.island}, Insel nicht vorhanden – Start auf {island} am Spot");
                save.hasPosition = false; save.interior = ""; save.island = island;
            }
        }
        // zuletzt im Interieur: nach dem Spawnen (und ggf. dem Inselwechsel) wieder hinein
        InteriorUtils.RestoreOnStart(save.interior);
        if (!travel && save.hasPosition && !PositionOnIsland(new Vector3(save.px, save.py, save.pz)))
        {
            Log.Warn($"Spielstand: Position ({save.px:F0}, {save.pz:F0}) liegt nicht auf {island} – Start am Spot");
            save.hasPosition = false;
        }

        CarPhysics? car = null;
        if (save.starterChosen)
        {
            if (save.ownedCars.Count > 0 && !save.ownedCars.Exists(c => string.Equals(c, save.currentCar, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warn("Spielstand: aktuelles Auto " + save.currentCar + " gehört nicht zu den eigenen Autos – nehme " + save.ownedCars[0]);
                save.currentCar = save.ownedCars[0];
            }
            car = vm.FindCar(save.currentCar);
            if (car == null) foreach (var n in save.ownedCars) { car = vm.FindCar(n); if (car != null) break; }
        }
        var spots = s.Spots?.Spots;
        var gp = _cfg.GamePcStart ? GamePC.Load(vm.BnkDir) : null;
        var home = spots != null ? gp?.FindHouse(spots) : null;
        if (gp != null && home == null && gp.Houses.Count > 0) Log.Warn("GamePC.cpr: Standard-Haus " + string.Join("/", gp.Houses) + " liegt nicht auf " + island);
        var dealer = FindSpot(string.IsNullOrEmpty(r.startSpot) ? "ESTOL_B" : r.startSpot);
        var spot = home ?? dealer;

        if (car != null && travel)
        {
            Log.Info($"Spielstand liegt auf {save.island} – Inselwechsel beim Start");
            _st = St.Done;
            IslandTravel.Go(save.island, false, car.FileName);
            return;
        }
        if (car != null)
        {
            if (save.hasPosition)
            {
                float h = save.heading * (MathF.PI / 180f);
                BeginSpawn(new Vector3(save.px, save.py, save.pz), new Vector3(MathF.Sin(h), 0f, MathF.Cos(h)), car);
            }
            else if (spot != null) BeginSpawn(SpotPos(spot), SpotDir(spot), car);
            else BeginSpawn(s.ViewerGame, Vector3.UnitZ, car);
            return;
        }

        // neues Profil wie im Spiel (GamePC.cpr): Auto + Garagenautos, Startgeld, Beginn am Standard-Haus
        if (gp != null && StartFromGamePc(gp, home, dealer)) return;

        // sonst: Startautos des Servers (Händler-Auswahl im Interieur folgt mit TDU2Interiors) – das erste nehmen
        var starters = new List<CarPhysics>();
        foreach (var n in r.starterCars ?? new List<string>()) { var c = vm.FindCar(n); if (c != null && !starters.Contains(c)) starters.Add(c); }
        if (starters.Count == 0) starters.Add(vm.FindCar(vm.StartCar) ?? vm.Available[0]);
        OnStarterChosen(starters[0]);
        if (dealer != null) BeginSpawn(SpotPos(dealer), SpotDir(dealer), starters[0]);
        else BeginSpawn(s.ViewerGame, Vector3.UnitZ, starters[0]);
    }

    /// <summary>Neues Profil nach GamePC.cpr: DefaultCarConfig = aktuelles Auto, DefaultGarageConfig = weitere Autos, DefaultMoney.</summary>
    bool StartFromGamePc(GamePC gp, Spot? home, Spot? fallback)
    {
        var save = TDU2Session.Save;
        var vm = Vm!;
        if (save == null) return false;
        var cars = new List<CarPhysics>();
        var main = FindCarConfig(gp.DefaultCar);
        if (main != null) cars.Add(main);
        foreach (int id in gp.GarageCars)
        {
            var c = vm.Available.Find(x => x.Id == id);
            if (c != null && !cars.Contains(c)) cars.Add(c);
        }
        if (cars.Count == 0)
        {
            Log.Warn("GamePC.cpr: weder DefaultCarConfig (" + string.Join(" ", gp.DefaultCar) + ") noch DefaultGarageConfig gefunden");
            return false;
        }
        if (gp.DefaultMoney > 0) save.money = gp.DefaultMoney;
        for (int i = 1; i < cars.Count; i++) TDU2Session.AddOwned(cars[i].FileName);
        var car = cars[0];
        OnStarterChosen(car);
        Log.Info($"Neues Profil (GamePC.cpr): {car.FileName}" + (cars.Count > 1 ? " + Garage " + string.Join(", ", cars.GetRange(1, cars.Count - 1).ConvertAll(c => c.FileName)) : "") +
                 $", {save.money} Geld, Start " + (home != null ? "am Haus " + string.Join("/", gp.Houses) : gp.HasStartPosition ? "an " + gp.StartPosition : "am Spot"));
        // Wohnbereich des Hauses (TDU2Interiors.EnterSpot) folgt mit den Interieurs: Auto vor dem Haus
        if (home != null) BeginSpawn(SpotPos(home), SpotDir(home), car);
        else if (gp.HasStartPosition) BeginSpawn(gp.StartPosition, Vector3.UnitZ, car);
        else if (fallback != null) BeginSpawn(SpotPos(fallback), SpotDir(fallback), car);
        else BeginSpawn(Streamer!.ViewerGame, Vector3.UnitZ, car);
        return true;
    }

    /// <summary>DefaultCarConfig "Ferrari" "F430" "Scuderia" -> Auto (Dateiname Modell_Version, sonst Modell-/Versionsname).</summary>
    CarPhysics? FindCarConfig(string[] cfg)
    {
        var vm = Vm!;
        if (cfg == null || cfg.Length < 2) return null;
        string model = cfg[1].Trim(), version = cfg.Length > 2 ? cfg[2].Trim() : "";
        string file = (model + (version.Length > 0 ? "_" + version : "")).Replace(' ', '_');
        var c = vm.FindCar(file);
        if (c != null) return c;
        if (version.Length > 0)
            foreach (var x in vm.Available)
            {
                string mn = x.ModelName ?? "";
                if (mn.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0 && mn.IndexOf(version, StringComparison.OrdinalIgnoreCase) >= 0) return x;
            }
        return vm.FindCar(model);
    }

    /// <summary>Liegt eine Spielposition auf der geladenen Insel? (nächster Spot höchstens 15 km entfernt)</summary>
    bool PositionOnIsland(Vector3 game)
    {
        var spots = Streamer?.Spots?.Spots;
        if (spots == null || spots.Count == 0) return true;
        if (!float.IsFinite(game.X) || !float.IsFinite(game.Z)) return false;
        const float Max = 15000f;
        foreach (var s in spots)
        {
            float dx = s.Position.X - game.X, dz = s.Position.Z - game.Z;
            if (dx * dx + dz * dz < Max * Max) return true;
        }
        return false;
    }

    Spot? FindSpot(string code)
    {
        var spots = Streamer?.Spots?.Spots;
        if (spots == null || string.IsNullOrEmpty(code)) return null;
        foreach (var s in spots) if (string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    static Vector3 SpotPos(Spot s) => s.Position2.LengthSquared() > 1f ? s.Position2 : s.Position;
    static Vector3 SpotDir(Spot s) => new(MathF.Sin(s.Heading2), 0f, MathF.Cos(s.Heading2));

    /// <summary>Startauto gewählt (Händler, GamePC, Geschichte): eigenes Auto, aktuelles Auto, Spielstand speichern (Unity OnStarterChosen).</summary>
    internal static void OnStarterChosen(CarPhysics car)
    {
        if (car == null || TDU2Session.Save == null) return;
        TDU2Session.AddOwned(car.FileName);
        TDU2Session.SetCurrentCar(car.FileName);
        TDU2Session.Save.starterChosen = true;
        Log.Info("Startauto: " + car.FileName);
        SessionAutosave.Schedule(3f);   // kurz nach dem Losfahren speichern (dann mit Position)
        TDU2Session.SaveAsync();
    }

    // ------------------------------------------------------------------ Spawnen (Unity BeginSpawn/UpdateSpawning)
    Vector3 _spawnGame, _spawnDir;
    CarPhysics? _spawnCar;
    float _spawnUntil, _spawnFrom;

    void BeginSpawn(Vector3 game, Vector3 dirGame, CarPhysics car)
    {
        _spawnGame = game;
        dirGame.Y = 0f;
        _spawnDir = dirGame.LengthSquared() > 1e-4f ? Vector3.Normalize(dirGame) : Vector3.UnitZ;
        _spawnCar = car;
        _spawnFrom = _clock;
        _spawnUntil = _clock + _cfg.SpawnTimeout;
        _st = St.Spawning;
        PlaceCamera();
    }

    /// <summary>Freiflug-Kamera hinter die Startstelle – das Streaming lädt um die Kamera herum.</summary>
    void PlaceCamera()
    {
        var cam = _ctx.Terrain?.Camera; var s = Streamer;
        if (cam == null || s == null) return;
        cam.Override = null;
        cam.Position = s.GameToRender(_spawnGame - _spawnDir * 7f + Vector3.UnitY * 3f);
        cam.Yaw = MathF.Atan2(_spawnDir.X, _spawnDir.Z);
        cam.Pitch = -0.15f;
    }

    void UpdateSpawning()
    {
        PlaceCamera();
        var s = Streamer!; var vm = Vm!;
        bool ground = s.RaycastDown(_spawnGame + Vector3.UnitY * 60f, 0f, 200f, out _);
        if ((!ground || _clock - _spawnFrom < 0.5f) && _clock < _spawnUntil) return;
        if (!ground) Log.Warn($"Login: Boden am Startpunkt nach {_cfg.SpawnTimeout:0} s nicht geladen – setze trotzdem");
        var save = TDU2Session.Save;
        int color = -1;
        if (save?.carConfigs != null && _spawnCar != null)
            foreach (var e in save.carConfigs) if (e != null && string.Equals(e.car, _spawnCar.FileName, StringComparison.OrdinalIgnoreCase)) color = Math.Max(0, e.color);
        if (_spawnCar == null || !vm.SpawnCar(_spawnCar.FileName, _spawnGame, _spawnDir, color))
            Log.Warn("Login: Auto " + _spawnCar?.FileName + " nicht spawnbar");
        else TDU2Session.SetCurrentCar(_spawnCar.FileName);
        _st = St.Done;
        if (_ctx.Vehicles != null) _ctx.Vehicles.Suspended = false;
        SessionAutosave.Schedule(Math.Max(5f, _cfg.AutosaveSeconds));
        GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    /// <summary>Unity TDU2Login._storyDone: Geschichte in dieser Anmeldung schon beendet (Neues Spiel setzt zurück).</summary>
    bool _storyDone;

    void QuitGame()
    {
        Log.Info("Login: Spiel beenden");
        _ctx.Quit();
    }
}
