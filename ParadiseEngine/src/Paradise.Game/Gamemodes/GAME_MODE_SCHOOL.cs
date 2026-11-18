using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Racing;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using Paradise.Game.Input;
using Paradise.Game.Online;
using Paradise.Game.Racing;
using Paradise.Game.Screens;
using System.Globalization;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Rennschule (Port Unity RacingSchoolMenu): Interieur-Auslöser Typ 20 = Instruktor (Spots ERS_A Asphalt, ERS_B Dirt, ERS_C Classic).
/// Oberfläche ist der Original-Film frontend/&lt;res&gt;/r_school.bnk.
///
/// Ablauf und Schnittstelle wie in der EXE (Menü-Klasse bei 0xb8fe60..0xb92600, FSCommand-Handler 0xb91ae0):
///   Öffnen        GotoLabel("/", "open_school"); danach öffnet die Root-Funktion Open() das Lizenzrad (im Spiel per
///                 nativem _root.rschoolOpen() nach Auswahl des Instruktors).
///   rsch:stli     Lizenzliste: /:li%d_state ("done"/"current"/"locked"), /:li%d_gp1, /:li%d_gp2 ("A7","A6" / "empty"),
///                 /:nb_licences, /:num_licence (erste offene Lizenz)
///   rsch:dili     Lizenzkarte: /:LicenceName, /:LicenceTxt, /:LicenceLevel, /:LicencePlayerName, /:LicenceDate ("25SEP1986"),
///                 /:LicenceNumber, /:nb_tests_done, /:num_test = 0, /:nb_tests_total, /:licence_group1/2
///   rsch:dits     Prüfungsliste: /tests/line%d/:done, :t_name, :meteo ("sun"/"rain"/"night", ig2 rswh),
///                 :place ("r_school"/"road", rsl1), :type ("turn".."jump", rsl2)
///   rsch:dtup     /:TestDesc der markierten Prüfung (/:num_test)
///   rsch:tspr     Prüfung /:num_test der Lizenz /:num_licence starten
///   rsch:exit     Schule verlassen;  dbxo = Ja/Nein-Dialog (hier sofort "ja")
/// Welche Lizenzen eine Schule zeigt: Schultyp aus dem Spot-Code und die Insel. Freischaltung: <see cref="RaceDatabase.RequiredCategory"/>.
/// Fortschritt: <see cref="RaceProgress"/>.
///
/// Prüfung starten: Auto der Prüfung (ig2 "VMod") laden, auf den Startpunkt (spotworld.bin) setzen, nachdem die Strecke
/// gestreamt ist; <see cref="RaceManager"/> übernimmt Countdown, Zeit, Tore und Props, <see cref="RaceHud"/> das HUD,
/// danach <see cref="RaceResults"/> (results.bnk). "Zurück zur Akademie" setzt das eigene Auto zurück in die Schule und öffnet
/// das Menü wieder. Während der Prüfung ist dies der Spielmodus (Fahren, HUD, ESC-Menü im Rennmodus).
/// </summary>
public class GAME_MODE_SCHOOL : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_SCHOOL;
    static readonly ScreenDef[] _screens = { ScreenDefs.Hud, ScreenDefs.School };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.School;

    /// <summary>Die (einzige) Instanz aus <see cref="GameModes"/>.</summary>
    public static GAME_MODE_SCHOOL Instance => (GAME_MODE_SCHOOL)GameModes.Get(Gamemode.GAME_MODE_SCHOOL);

    VmfPlayer? _film;
    RaceRepository? _repo;
    string? _bnkDir;
    int _island = 1;
    char _school = '\0';
    readonly RaceManager _race = new();
    readonly RaceHud _hud = new();
    readonly RaceResults _results = new();
    readonly RaceAutopilot _autopilot = new();
    bool _wired;
    bool _open, _fromInterior;
    readonly List<LicenseInfo> _licences = new();
    List<RaceDefinition> _tests = new();
    int _licIndex;
    int _testIndex;
    bool _backWin;

    // Spot (für die Rückkehr): Ausfahrt in Inselkoordinaten
    string? _spotCode;
    Vector3 _spotExit; float _spotHeading; bool _haveSpot;
    Spot? _spotObj;
    CarPhysics? _playerCar;

    // Startvorgang
    enum Phase { None, StreamToStart, StreamToSchool, EnterSchool }
    Phase _phase;
    float _phaseT;
    RaceDefinition? _pending;
    CarPhysics? _pendingCar;
    float _openDelay = -1f;
    float _resultDelay = -1f;
    float _clock;
    bool _loggedPose;

    public bool IsOpen => _open;
    public RaceManager Race => _race;
    public RaceHud Hud => _hud;
    public RaceRepository? Repository => _repo;
    public string? LastFilmCommand { get; private set; }
    public bool InTest => _race.State != RaceState.Idle && _race.State != RaceState.Finished;
    /// <summary>Prüfung, Startvorgang oder Ergebnis laufen (der Modus bleibt aktiv).</summary>
    bool Busy => InTest || _phase != Phase.None || _results.IsOpen || _resultDelay >= 0f || _race.State == RaceState.Finished;

    /// <summary>Rennschul-Menü oder Ergebnis offen: ESC gehört diesen Filmen (Unity BlocksPauseMenu).</summary>
    public static bool BlocksPauseMenu => (GameModes.Get(Gamemode.GAME_MODE_SCHOOL) is GAME_MODE_SCHOOL s && s._open) || RaceResults.AnyOpen;

    // ------------------------------------------------------------------ Einstieg (Hotspot Typ 20)
    /// <summary>Instruktor (Auswahlpunkt Typ 20) in einer Rennschule angesprochen: Menü öffnen. false = keine Rennschule.</summary>
    internal static bool OpenFromInstructor()
    {
        if (!(_spot?.Code ?? "").StartsWith("ERS", StringComparison.OrdinalIgnoreCase)) return false;
        if (_t == null || _scene == null) return true;
        var s = Instance;
        s._openRequested = true;
        GameMode.Set(Gamemode.GAME_MODE_SCHOOL);
        return true;
    }
    bool _openRequested;

    void Wire(IGameModeContext ctx)
    {
        _ctx = ctx;
        _race.Streamer = ctx.Terrain?.Streamer;
        _race.Vehicles = ctx.Vehicles?.Manager;
        _hud.Race = _race;
        _hud.Minimap = ctx.Vehicles?.Manager?.Minimap;
        _results.Manager = ctx.Vehicles?.Manager;
        RaceRoadPath.Lanes = ctx.Vehicles?.Traffic?.Lanes;
        if (_wired) return;
        _wired = true;
        _hud.Bind();
        _results.Retry += () => { if (_pending != null) BeginTest(_pending); };
        _results.Next += NextTest;
        _results.Quit += ReturnToSchool;
        _race.Completed += OnRaceEnded;
        _race.Failed += OnRaceEnded;
        _race.Aborted += _ => ReturnToSchool();
        // ESC während einer Prüfung: Renn-Menü des Films (WhatGameMode 2) mit "Neu starten" und "Beenden"
        PauseMenu.RegisterRace(this,
            () => InTest,
            () => { if (_pending != null) BeginTest(_pending); },
            () => { if (InTest) _race.Abort(); else ReturnToSchool(); });
    }

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        if (Init(ctx)) Wire(ctx);
        _film ??= ctx.Open(ScreenDefs.School);
        if (_film != null && !_filmHooked) { _film.FSCommand += OnFilmCommand; _filmHooked = true; }
        if (_film != null) _film.Visible = _open;   // ScreenModule blendet alle Oberflächen des Modus ein; das Menü nur, wenn offen
        if (from == Gamemode.GAME_MODE_PAUSE) return;
        if (_openRequested) { _openRequested = false; Open(); }
    }
    bool _filmHooked;

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to == Gamemode.GAME_MODE_PAUSE) return;   // Pausemenü: Prüfung bleibt stehen
        if (_open) Close();
        if (Busy)
        {
            // Modus von außen gewechselt (Entwicklertaste, Inselwechsel …): Prüfung beenden wie Unity OnDisable
            Log.Info("Rennschule: Modus " + to + " – laufende Prüfung beendet");
            _race.Stop(); _race.Cleanup(); _autopilot.End(); _results.Close(); _hud.Hide();
            _phase = Phase.None; _resultDelay = -1f;
            PrivateSession(false);
        }
        if (_film != null) _film.Visible = false;
        base.Exit(ctx, to);
    }

    public override void Render(IGameModeContext ctx, in GameTime time) { if (_t != null && Inside) RenderCars(); }

    // ------------------------------------------------------------------ jedes Bild
    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        float dt = time.DeltaTime;
        _clock += dt;
        if (!Init(ctx)) return;
        Wire(ctx);
        _hud.Minimap = ctx.Vehicles?.Manager?.Minimap;

        TickPhase(dt);
        if (_resultDelay >= 0f) { _resultDelay -= dt; if (_resultDelay < 0f) ShowResultsAfterCinematic(); }

        if (_open)
        {
            if (!Watchdog(ctx)) UpdateMenu(ctx, dt);
            return;
        }
        if (_phase == Phase.EnterSchool)
        {
            // zurück in der Akademie: Interieur laden (wie der Raum-Modus), dann Menü öffnen
            if (!UpdateInside(dt, ctx.Paused)) { _phase = Phase.None; GameMode.Set(Gamemode.GAME_MODE_FREERIDE); }
            return;
        }

        if (_results.IsOpen) { _results.Update(ctx, dt); _autopilot.Update(dt); return; }

        if (InTest || _race.State == RaceState.Finished || _phase != Phase.None)
        {
            // Fahren wie in der Freifahrt (Unity: Rennschule läuft über der normalen Fahrzeugsteuerung)
            HandlePauseKey(ctx);
            if (ctx.Paused) return;
            var veh = ctx.Vehicles; var vm = veh?.Manager;
            if (TDU2Controls.Pressed("minimap") && vm?.Minimap != null && vm.Current != null && !veh!.Suspended && !veh.Frozen)
                vm.Minimap.ShowMinimap = !vm.Minimap.ShowMinimap;
            // Unity AbortKey (Backspace) während Countdown/Lauf
            if ((_race.State == RaceState.Running || _race.State == RaceState.Countdown) && ctx.Pressed(0x08)) { _race.Abort(); return; }
            _race.Update(dt);
            _hud.Update();
            _autopilot.Update(dt);
            return;
        }

        // nichts mehr zu tun: zurück in den Raum bzw. in die Freifahrt
        GameMode.Set(Inside ? RoomMode : Gamemode.GAME_MODE_FREERIDE);
    }

    // ------------------------------------------------------------------ Menü-Eingabe
    void UpdateMenu(IGameModeContext ctx, float dt)
    {
        if (_film == null || ctx.Paused) return;
        if (_openDelay >= 0f)
        {
            _openDelay -= dt;
            if (_openDelay < 0f && !_backWin) _film.CallClip("/", "Open");   // wie _root.rschoolOpen()
        }
        if (FlashDialog.Active) return;
        FlashNav.Directions(_film);
        if (FlashNav.Pressed(FlashKey.Confirm))
        {
            if (!FlashNav.Send(_film, FlashKey.Confirm)) _film.CallClip("/", "Open");   // auf der Schulübersicht: Lizenzrad wieder öffnen
        }
        if (FlashNav.Pressed(FlashKey.Back) || _escNow)
        {
            if (!FlashNav.Send(_film, FlashKey.Back)) Close();
        }
        if (ctx.Pressed(0x79)) Close();   // F10: Notausgang
    }

    // ------------------------------------------------------------------ Absicherung gegen hängendes Menü
    float _exitSeen = -1f, _lastEsc = -10f;
    bool _escNow;   // ESC in diesem Bild (Watchdog liest die Taste, das Menü wertet sie als Zurück)

    /// <summary>
    /// Bleibt das Menü offen, obwohl man nicht mehr in der Schule ist oder der Film den Ausgang schon gespielt hat
    /// (Label school_out / SelItemType = exit) ohne "rsch:exit" zu senden, steht das Spiel. Dann hier schließen.
    /// Doppeltes ESC (innerhalb 1,5 s) schließt immer.
    /// </summary>
    bool Watchdog(IGameModeContext ctx)
    {
        if (_fromInterior && !Inside && !InTest)
        {
            Log.Info("Rennschule Watchdog: Interieur verlassen, Menü noch offen -> schließen");
            Close(); return true;
        }
        if (_film != null)
        {
            string lab = _film.Label("/") ?? "";
            string sel = _film.GetVariable("/:SelItemType") ?? "";
            bool exiting = string.Equals(lab, "school_out", StringComparison.OrdinalIgnoreCase) || string.Equals(sel, "exit", StringComparison.OrdinalIgnoreCase);
            if (exiting) { if (_exitSeen < 0f) _exitSeen = _clock; }
            else _exitSeen = -1f;
            if (_exitSeen >= 0f && _clock - _exitSeen > 1.5f)
            {
                Log.Info("Rennschule Watchdog: Film im Ausgang (" + lab + "/" + sel + ") ohne rsch:exit -> schließen");
                _exitSeen = -1f; _film.SetVariable("/:SelItemType", "");
                Close(); return true;
            }
        }
        _escNow = ctx.Pressed(0x1B);
        if (_escNow)
        {
            if (_clock - _lastEsc < 1.5f) { Log.Info("Rennschule Watchdog: doppeltes ESC -> schließen"); _lastEsc = -10f; Close(); return true; }
            _lastEsc = _clock;
        }
        return false;
    }

    // ------------------------------------------------------------------ Öffnen / Schließen
    public void Open()
    {
        if (_open || _ctx == null) return;
        _fromInterior = Inside;
        if (_fromInterior) CaptureSpot();
        EnsureData();
        _film ??= _ctx.Open(ScreenDefs.School);
        _open = true;
        if (_ctx.Vehicles != null) _ctx.Vehicles.Frozen = true;   // Unity: Time.timeScale = 0
        if (_t != null) _t.Input.CursorLocked = false;            // Maus frei für den Film
        FillLicences(true);
        if (_film != null)
        {
            PublishBase();
            // back_win = 1 spielt im Film die Lizenz-Animation (licences "win" + win_message). Deren Bestätigung (A) wird im
            // Nachbau nicht sauber zugestellt -> Menü bleibt hängen. Die Lizenz zeigt schon das Ergebnis-Fenster, hier normal öffnen.
            _film.SetValue("/:back_win", 0);
            _film.Visible = true;
            _film.MouseEnabled = true;
            _film.GotoLabel("/", "open_school", true);
            _openDelay = 0.35f;
        }
        Log.Info("Rennschule geöffnet: Spot " + (_spotCode ?? "?") + ", Schule " + (_school == '\0' ? "alle" : _school.ToString()) +
                 ", Insel " + _island + ", Lizenzen " + _licences.Count + ", Film " + (_film != null ? "ja" : "nein") + (_backWin ? ", Lizenz gewonnen" : ""));
        _backWin = false;
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        _openDelay = -1f;
        if (_ctx?.Vehicles != null) _ctx.Vehicles.Frozen = false;
        if (_film != null) { _film.Visible = false; _film.GotoLabel("/", "default", false); }
        Log.Info("Rennschul-Menü geschlossen");
    }

    // ------------------------------------------------------------------ Daten
    void EnsureData()
    {
        string? bnkDir = Paradise.Core.Config.GameData.BnkDir;
        if (string.IsNullOrEmpty(bnkDir)) return;
        if (_repo == null || _bnkDir != bnkDir)
        {
            _bnkDir = bnkDir;
            _repo = RaceRepository.Load(bnkDir);
        }
        _island = CurrentIsland();
        _school = SchoolFromSpot(_spotCode);
        if (_school == '\0' && !string.IsNullOrEmpty(RaceConfig.FallbackSchool)) _school = char.ToUpperInvariant(RaceConfig.FallbackSchool[0]);
        _licences.Clear();
        _licences.AddRange(_repo.Database.RacingSchools(_island, _school));
        if (_licences.Count == 0 && _school != '\0') _licences.AddRange(_repo.Database.RacingSchools(_island));
    }

    /// <summary>Renn-Daten (db_data.cpr, spotworld.bin, ig2) laden – auch für das Entwicklermenü.</summary>
    public RaceRepository? EnsureRepository()
    {
        string? bnkDir = Paradise.Core.Config.GameData.BnkDir;
        if (string.IsNullOrEmpty(bnkDir)) return null;
        if (_repo == null || _bnkDir != bnkDir) { _bnkDir = bnkDir; _repo = RaceRepository.Load(bnkDir); }
        return _repo;
    }

    /// <summary>ERS_A = Asphalt, ERS_B = Dirt, ERS_C = Classic.</summary>
    static char SchoolFromSpot(string? code)
    {
        if (string.IsNullOrEmpty(code)) return '\0';
        var c = code.ToUpperInvariant();
        int i = c.IndexOf("ERS_", StringComparison.Ordinal);
        if (i >= 0 && i + 4 < c.Length && "ABC".IndexOf(c[i + 4]) >= 0) return c[i + 4];
        return '\0';
    }

    /// <summary>Aktuellen Spot des Interieurs merken (Unity: privates Feld _spot von TDU2Interiors).</summary>
    void CaptureSpot()
    {
        var s = _spot;
        if (s == null) return;
        _spotObj = s;
        _spotCode = s.Code;
        _spotExit = s.Position2.LengthSquared() > 1f ? s.Position2 : s.Position;
        _spotHeading = s.Heading2;
        _haveSpot = true;
    }

    /// <summary>Aktuelle Insel als DBLicense._island (1 = Hawaii, 0 = Ibiza).</summary>
    int CurrentIsland()
    {
        string n = _t?.Streamer?.Index?.IslandName ?? "";
        return n.StartsWith("i", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    // ------------------------------------------------------------------ Lizenzen / Prüfungen
    bool IsLicenceWon(LicenseInfo? l)
    {
        if (l == null) return false;
        if (RaceProgress.IsLicenceWon(l.Hash)) return true;
        // alter Fortschritt: alle Prüfungen bestanden
        if (l.ChallengeFiles.Count == 0) return false;
        foreach (var t in l.ChallengeFiles) if (!RaceProgress.IsTestDone(t)) return false;
        return true;
    }

    bool IsUnlocked(LicenseInfo l)
    {
        if (RaceConfig.UnlockAllLicences) return true;
        int req = RaceDatabase.RequiredCategory(l.VehicleCategory1);
        if (req == 0) return true;
        if (req < 0) return false;
        var prev = _repo?.Database.SchoolLicenseWithCategory(req);
        return prev != null && IsLicenceWon(prev);
    }

    string LicenceState(LicenseInfo l) => !IsUnlocked(l) ? "locked" : IsLicenceWon(l) ? "done" : "current";

    /// <summary>rsch:stli – Lizenzliste in den Film schreiben, aktuelle Lizenz bestimmen.</summary>
    void FillLicences(bool chooseCurrent)
    {
        if (chooseCurrent)
        {
            int cur = -1;
            for (int i = 0; i < _licences.Count && cur < 0; i++) if (LicenceState(_licences[i]) == "current") cur = i;
            if (cur < 0) cur = Mathf.Clamp(_licIndex, 0, Mathf.Max(0, _licences.Count - 1));
            SelectLicence(cur);
        }
        if (_film == null) return;
        for (int i = 0; i < _licences.Count; i++)
        {
            var l = _licences[i];
            _film.SetVariable("/:li" + i + "_state", LicenceState(l));
            _film.SetVariable("/:li" + i + "_gp1", l.Group1);
            _film.SetVariable("/:li" + i + "_gp2", l.Group2);
        }
        _film.SetValue("/:nb_licences", _licences.Count);
        _film.SetValue("/:num_licence", _licIndex);
    }

    void SelectLicence(int index)
    {
        if (_licences.Count == 0 || _repo == null) { _tests = new List<RaceDefinition>(); _licIndex = 0; _testIndex = -1; return; }
        _licIndex = Mathf.Clamp(index, 0, _licences.Count - 1);
        _tests = _repo.BuildForLicense(_licences[_licIndex]);
        _testIndex = FirstUndone();
    }

    int FirstUndone()
    {
        for (int i = 0; i < _tests.Count; i++) if (!RaceProgress.IsTestDone(_tests[i].Name)) return i;
        return 0;
    }

    int DoneCount() { int n = 0; foreach (var t in _tests) if (RaceProgress.IsTestDone(t.Name)) n++; return n; }

    static readonly string[] Months = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

    /// <summary>rsch:dili – Lizenzkarte der Lizenz /:num_licence.</summary>
    void FillLicenceCard()
    {
        if (_film == null || _licences.Count == 0) return;
        var l = _licences[_licIndex];
        string player = TDU2GameData.PlayerName.Length > 0 ? TDU2GameData.PlayerName : "Player";
        string name = _school == 'B' ? RaceTexts.Get(1744, "Dirt") : _school == 'C' ? RaceTexts.Get(1745, "Classic") : RaceTexts.Get(1743, "Asphalt");
        _film.SetVariable("/:LicenceName", l.DisplayName);
        _film.SetVariable("/:LicenceTxt", RaceTexts.Get(l.DescHc, name));
        _film.SetVariable("/:LicenceLevel", "");
        _film.SetVariable("/:LicencePlayerName", player);
        var date = RaceProgress.LicenceDate(l.Hash) ?? DateTime.Now;
        _film.SetVariable("/:LicenceDate", date.Day.ToString("00", CultureInfo.InvariantCulture) + Months[date.Month - 1] + date.Year.ToString(CultureInfo.InvariantCulture));
        _film.SetVariable("/:LicenceNumber", "411 14 38 42 6116");
        _film.SetVariable("/:licence_group1", l.Group1);
        _film.SetVariable("/:licence_group2", l.Group2);
        _film.SetVariable("/:Licence_group1", l.Group1);
        _film.SetVariable("/:Licence_group2", l.Group2);
        _film.SetVariable("/:nb_tests_done", DoneCount().ToString(CultureInfo.InvariantCulture));
        _film.SetVariable("/:nb_tests_total", _tests.Count.ToString(CultureInfo.InvariantCulture));
        _film.SetValue("/:nb_tests_total", _tests.Count);
        _film.SetValue("/:num_test", 0);
        _film.SetValue("/:IsLicensed", IsLicenceWon(l) ? 1 : 0);
    }

    static readonly string[] TypeLabels = { "turn", "turn90", "turn180", "slalom", "overtaking", "drift", "acceleration", "braking", "mixed", "circuit", "manual", "obstable", "jump" };
    static readonly string[] MeteoLabels = { "sun", "rain", "night" };

    /// <summary>rsch:dits – Prüfungsliste der Lizenz /:num_licence.</summary>
    void FillTests()
    {
        if (_film == null) return;
        for (int i = 0; i < _tests.Count && i < 8; i++)
        {
            var t = _tests[i];
            string p = "/tests/line" + i;
            _film.SetVariable(p + ":done", RaceProgress.IsTestDone(t.Name) ? "1" : "0");
            _film.SetVariable(p + ":t_name", RaceTexts.Get(t.NameHc, t.Title));
            _film.SetVariable(p + ":meteo", MeteoLabels[Mathf.Clamp((int)t.Weather, 0, 2)]);
            _film.SetVariable(p + ":place", t.Place == SchoolTestPlace.Road ? "road" : "r_school");
            _film.SetVariable(p + ":type", TypeLabels[Mathf.Clamp((int)t.TestType, 0, TypeLabels.Length - 1)]);
        }
        _film.SetValue("/:nb_tests_total", _tests.Count);
        _film.SetValue("/:num_test", Mathf.Clamp(_testIndex, 0, Mathf.Max(0, _tests.Count - 1)));
    }

    /// <summary>rsch:dtup – Beschreibung der markierten Prüfung.</summary>
    void FillTestDesc(int i)
    {
        if (_film == null || i < 0 || i >= _tests.Count) return;
        var t = _tests[i];
        string desc = RaceTexts.Get(t.DescHc, "");
        string limit = t.HasTimeLimit ? RaceHud.FormatRaceTimeMs(t.TimeLimitMs) : t.TargetTimeMs > 0 ? RaceHud.FormatRaceTimeMs(t.TargetTimeMs) : "";
        int best = RaceProgress.BestTimeMs(t.Name);
        if (limit.Length > 0) desc += "\n\n" + (t.HasTimeLimit ? "Zeitlimit: " : "Zielzeit: ") + limit;
        if (best > 0) desc += "   Bestzeit: " + RaceHud.FormatRaceTimeMs(best);
        _film.SetVariable("/:TestDesc", desc);
    }

    void PublishBase()
    {
        if (_film == null) return;
        if (!string.IsNullOrEmpty(_bnkDir)) FlashLocalization.Apply(_film, FlashLocalization.Load(_bnkDir, FlashHost.Language));
        _film.SetValue("/:WhatPlatform", 1);
        _film.SetValue("/:fake", 0);
        _film.SetValue("/:FirstTime", false);
        _film.SetValue("/:Input", true);
        _film.SetValue("/:IsRacingSchool", true);
        _film.SetVariable("/:WhatPage", "racing_school");
    }

    // ------------------------------------------------------------------ Original-Film (r_school.bnk)
    int FilmInt(string name, int def)
    {
        if (_film == null) return def;
        var v = _film.GetVariable(name);
        if (string.IsNullOrEmpty(v)) return def;
        return double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)System.Math.Round(d) : def;
    }

    void OnFilmCommand(string cmd, string args)
    {
        if (!_open) return;
        LastFilmCommand = cmd + (string.IsNullOrEmpty(args) ? "" : " " + args);
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("sndp:")) return;   // Menü-Sounds: ScreenModule hat FrontendSounds angehängt
        Log.Info("Rennschule FSCommand: " + LastFilmCommand);
        switch (c)
        {
            case "rsin": PublishBase(); FillLicences(false); return;
            case "rsch:stli": FillLicences(true); return;
            case "rsch:dili":
                {
                    int li = FilmInt("/:num_licence", _licIndex);
                    if (li != _licIndex || _tests.Count == 0) SelectLicence(li);
                    FillLicenceCard();
                    return;
                }
            case "rsch:dits":
                {
                    int li = FilmInt("/:num_licence", _licIndex);
                    if (li != _licIndex) SelectLicence(li);
                    FillTests();
                    return;
                }
            case "rsch:dtup": FillTestDesc(FilmInt("/:num_test", _testIndex)); return;
            case "rsch:tspr":
                {
                    int li = FilmInt("/:num_licence", _licIndex);
                    if (li != _licIndex) SelectLicence(li);
                    StartTest(FilmInt("/:num_test", _testIndex));
                    return;
                }
            case "rsch:exit": Close(); return;
            // "Zurück" vom Lizenzrad: das Spiel wechselt in die Schulansicht (Kamera/Instruktor wählen, natives 3D-Menü).
            // Die gibt es im Nachbau nicht -> Menü schließen, man steht wieder im Interieur (Instruktor erneut ansprechen).
            case "wmon": Log.Info("Rennschule: wmon (Schulansicht) -> Menü schließen"); Close(); return;
            case "dbxo":
                _film?.SetVariable("/:PlayerAnswer", "a");
                _film?.CallClip("/", "DialogBox");
                return;
        }
    }

    // ------------------------------------------------------------------ Prüfung starten
    void StartTest(int i)
    {
        if (i < 0 || i >= _tests.Count) return;
        _testIndex = i;
        Close();
        if (Inside)
        {
            RememberPlayerCar(_lastCar);   // eigenes Auto merken (wird nicht gespawnt)
            LeaveForTravel("Prüfung");
        }
        else if (_ctx?.Vehicles?.Manager?.CurrentCar is { } cc) RememberPlayerCar(cc);
        BeginTest(_tests[i]);
    }

    /// <summary>
    /// Prüfung direkt starten (Entwicklermenü F1 > Race Debug): ohne Menü, eigenes Auto wird gemerkt.
    /// Gehört die Prüfung zur aktuell gewählten Lizenz, laufen "Nächster Test"/Fortschritt normal weiter.
    /// </summary>
    public bool StartTestDirect(RaceDefinition? def)
    {
        if (def == null || _ctx == null || _t == null || _v == null || _v.Manager == null) return false;
        Wire(_ctx);
        EnsureData();
        int i = _tests.FindIndex(t => string.Equals(t.Name, def.Name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) { _tests = new List<RaceDefinition> { def }; i = 0; }
        if (_open) Close();
        _testIndex = i;
        if (Inside)
        {
            CaptureSpot();
            RememberPlayerCar(_lastCar);
            LeaveForTravel("Prüfung");
        }
        else if (_v.Manager.CurrentCar is { } cc) RememberPlayerCar(cc);
        GameMode.Set(Gamemode.GAME_MODE_SCHOOL);
        BeginTest(_tests[i]);
        return true;
    }

    // ------------------------------------------------------------------ Prüfung = private Sitzung
    bool _private;

    /// <summary>
    /// Während einer Prüfung: kein Verkehr (Rennschul-Prüfungen haben keinen), Mitspieler ausgeblendet und man selbst für
    /// die anderen unsichtbar (Multiplayer.Hidden). Danach alles wie vorher (Verkehr entsteht nur in der Freifahrt neu).
    /// </summary>
    void PrivateSession(bool on)
    {
        if (on == _private) return;
        _private = on;
        Log.Info("Private Sitzung " + (on ? "an" : "aus"));
        if (on) _ctx?.Vehicles?.Traffic?.ClearCars();
        Multiplayer.Hidden = on && RaceConfig.HidePlayersInRacingSchool;
    }

    void BeginTest(RaceDefinition def)
    {
        var vm = _v?.Manager;
        if (vm == null || _t == null) return;
        _loggedPose = false;
        PrivateSession(true);
        if (_ctx != null) Wire(_ctx);
        _autopilot.End();
        _race.Stop();      // laufende Prüfung still beenden (Neu starten aus dem ESC-Menü / Ergebnis)
        _race.Cleanup();
        _results.Close();
        _hud.Minimap = vm.Minimap;
        _pending = def;
        if (def.RoadPath == null) { try { RaceRoadPath.Compute(def); } catch (Exception ex) { Log.Warn("Straßenroute: " + ex.Message); } }
        RaceRoute.RoadPath = def.RoadPath; RaceRoute.RoadPathSeg = def.RoadPathSeg;
        _pendingCar = TestCar(def);
        if (_pendingCar == null) { Log.Warn("Rennschule: kein Fahrzeug verfügbar"); return; }
        if (def.Weather != SchoolTestWeather.Sun) Log.Info("Prüfung " + def.Name + ": Wetter im Original " + def.Weather + " (DBChallWeather) – hier nicht nachgebildet");
        RaceProgress.AddTry(def.Name);
        ApplyEase(def);
        // Kamera zum Start, damit der Streamer die Strecke lädt; Auto erst spawnen, wenn der Boden da ist
        vm.Despawn();
        StartPose(def, out var pos, out var fwd);
        var st = _t.Streamer;
        if (_t.Camera != null && st != null) _t.Camera.Position = st.GameToRender(pos - fwd * 8f + Vector3.UnitY * 3f);
        _phase = Phase.StreamToStart;
        _phaseT = 0f;
        if (GameMode.Current != Gamemode.GAME_MODE_SCHOOL) GameMode.Set(Gamemode.GAME_MODE_SCHOOL);
        Log.Info("Prüfung " + def.Name + " (" + RaceTexts.Get(def.NameHc, def.Title) + "): Auto " + _pendingCar.ModelName +
                 ", " + def.Checkpoints.Count + " Checkpoints, " + def.Decors.Count + " Deko-Objekte, " + def.Opponents.Count + " Fahrzeuge, " +
                 (def.HasTimeLimit ? "Zeitlimit " + RaceHud.FormatRaceTimeMs(def.TimeLimitMs) : "Zielzeit " + RaceHud.FormatRaceTimeMs(def.TargetTimeMs)));
    }

    /// <summary>
    /// Prüfungsauto = ig2 "VMod" = DBCarPhysicsData._id_car. Das sind eigene Sondermodelle (z.B. #626 "CCXR Edition Racing School
    /// Asphalt", _duplicate = 383, _sticker = "rsa_CCXR") mit eigener Physik und Beklebung. Sie teilen sich die Fahrzeugbank mit
    /// dem Serienauto und stehen deshalb nicht in Manager.Available – geladen wird genau dieser Datensatz (SpawnCarExact).
    /// </summary>
    CarPhysics? TestCar(RaceDefinition def)
    {
        var vm = _v!.Manager!;
        CarPhysics? car = null;
        if (RaceConfig.UseTestCar && def.CarId > 0 && vm.Database != null && _bnkDir != null)
        {
            if (vm.Database.ById.TryGetValue(def.CarId, out var c))
            {
                if (CarDatabase.CarBank(_bnkDir, c) != null) car = c;
                else Log.Warn("Prüfungsauto " + c.FileName + " (#" + def.CarId + ") hat keine Fahrzeugbank (vehicules/high) – nehme eigenes Auto");
            }
            else Log.Warn("Prüfungsauto #" + def.CarId + " steht nicht in DBCarPhysicsData – nehme eigenes Auto");
        }
        return car ?? _playerCar ?? vm.CurrentCar ?? vm.FindCar(vm.StartCar);
    }

    // ------------------------------------------------------------------ mehr Zeit nach Fehlversuchen
    readonly Dictionary<string, (int target, int limit)> _origTimes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> _failStreak = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Zeitbonus der Prüfung (Anteil, 0 = Original). Ab EaseAfterFails Fehlversuchen hintereinander je Versuch +EaseStep.</summary>
    public float EaseFor(string test)
    {
        if (RaceConfig.EaseAfterFails <= 0 || string.IsNullOrEmpty(test) || !_failStreak.TryGetValue(test, out int f) || f < RaceConfig.EaseAfterFails) return 0f;
        return Mathf.Min(RaceConfig.EaseMax, RaceConfig.EaseStep * (f - RaceConfig.EaseAfterFails + 1));
    }

    void ApplyEase(RaceDefinition def)
    {
        if (!_origTimes.TryGetValue(def.Name, out var o)) { o = (def.TargetTimeMs, def.TimeLimitMs); _origTimes[def.Name] = o; }
        float k = 1f + EaseFor(def.Name);
        def.TargetTimeMs = o.target > 0 ? Mathf.RoundToInt(o.target * k) : o.target;
        def.TimeLimitMs = o.limit > 0 ? Mathf.RoundToInt(o.limit * k) : o.limit;
        if (k > 1.0001f)
        {
            int fails = _failStreak.TryGetValue(def.Name, out int f) ? f : 0;
            Log.Info("Prüfung " + def.Name + ": " + fails + " Fehlversuche hintereinander – " + Mathf.RoundToInt((k - 1f) * 100f) + " % mehr Zeit (" +
                     RaceHud.FormatRaceTimeMs(def.HasTimeLimit ? def.TimeLimitMs : def.TargetTimeMs) + " statt " + RaceHud.FormatRaceTimeMs(def.HasTimeLimit ? o.limit : o.target) + ")");
        }
    }

    void NoteAttempt(RaceResult? res)
    {
        if (res?.Race == null || res.Aborted) return;
        string n = res.Race.Name;
        if (res.Passed) _failStreak.Remove(n);
        else _failStreak[n] = (_failStreak.TryGetValue(n, out int f) ? f : 0) + 1;
    }

    /// <summary>Prüfungsautos (Rennschul-Sondermodelle) nie als "eigenes Auto" merken (auch Spielstand/Garage nutzen das).</summary>
    public static bool IsSchoolCar(CarPhysics? c)
    {
        if (c == null || c.Raw == null) return false;
        var st = c.Raw.S("_sticker", "") ?? "";
        var ver = c.Raw.S("_version_name", "") ?? "";
        return st.StartsWith("rs", StringComparison.OrdinalIgnoreCase) && st.Length > 3 && st[3] == '_' || ver.IndexOf("Racing School", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void RememberPlayerCar(CarPhysics? c)
    {
        if (c != null && !IsSchoolCar(c)) _playerCar = c;
    }

    CarPhysics? PlayerCar()
    {
        if (_playerCar != null && !IsSchoolCar(_playerCar)) return _playerCar;
        var last = _lastCar;
        if (last != null && !IsSchoolCar(last)) return last;
        var vm = _v?.Manager;
        return vm?.FindCar(vm.StartCar);
    }

    /// <summary>Startposition/-richtung (Spielraum): spotworld-Start, Richtung der Startlinie zur ersten Zielmarke hin.</summary>
    void StartPose(RaceDefinition def, out Vector3 pos, out Vector3 forward)
    {
        Vector3 start = def.StartPosition;
        Vector3 f = def.Start != null ? def.Start.Forward : new Vector3(MathF.Sin(def.StartHeading), 0f, MathF.Cos(def.StartHeading));
        var seq = def.TargetSequence();
        // Startrichtung = Startlinie (CHK2 #0, gleich der spotworld-Richtung). Nur wenn sie klar vom ersten Ziel wegzeigt
        // (> 100°, Ziel mindestens 15 m entfernt), umdrehen. Die Straßenroute wird hier bewusst nicht benutzt (kann am Start
        // auf der Gegenspur beginnen).
        Vector3 aim = Vector3.Zero;
        foreach (int k in seq) { var d = def.Checkpoints[k].Position - start; d.Y = 0f; if (d.LengthSquared() > 225f) { aim = d; break; } }
        float ang = 0f;
        var ff = new Vector3(f.X, 0f, f.Z);
        if (aim != Vector3.Zero && ff.LengthSquared() > 1e-6f)
            ang = MathF.Acos(System.Math.Clamp(Vector3.Dot(Vector3.Normalize(ff), Vector3.Normalize(aim)), -1f, 1f)) * Mathf.Rad2Deg;
        if (ang > 100f) f = -f;
        if (!_loggedPose)
        {
            _loggedPose = true;
            Log.Info($"Startpose {def.Name}: Start {start} Startlinie {(def.Start != null ? def.Start.Forward.ToString() : "-")} spotHeading {def.StartHeading:0.000} ersteZiel {aim} Winkel {ang:0} -> Richtung {f}");
        }
        f.Y = 0f; if (f.LengthSquared() < 1e-6f) f = Vector3.UnitZ;
        pos = start;
        forward = Vector3.Normalize(f);
    }

    /// <summary>Startpose der Prüfung (Entwicklermenü).</summary>
    public bool TryGetStartPose(RaceDefinition? def, out Vector3 pos, out Vector3 forward)
    {
        pos = Vector3.Zero; forward = Vector3.UnitZ;
        if (def == null || (def.Checkpoints.Count == 0 && def.StartPosition.LengthSquared() < 1f)) return false;
        bool was = _loggedPose; _loggedPose = true;
        StartPose(def, out pos, out forward);
        _loggedPose = was;
        return true;
    }

    bool GroundReady(Vector3 game) => _t?.Streamer != null && _t.Streamer.RaycastDown(game, 50f, 200f, out _);

    void TickPhase(float dt)
    {
        var vm = _v?.Manager;
        if (_phase == Phase.None || vm == null) return;
        _phaseT += dt;
        if (_phase == Phase.StreamToStart && _pending != null && _pendingCar != null)
        {
            StartPose(_pending, out var pos, out var fwd);
            if (_t?.Camera != null && _t.Streamer != null) _t.Camera.Position = _t.Streamer.GameToRender(pos - fwd * 8f + Vector3.UnitY * 3f);
            if (!GroundReady(pos) && _phaseT < 15f) return;
            if (!vm.SpawnCarExact(_pendingCar, pos, fwd) && _phaseT < 20f) return;   // anderes Auto lädt noch -> nächstes Bild
            _phase = Phase.None;
            Log.Info("Prüfungsauto: #" + _pendingCar.Id + " " + _pendingCar.FileName + " " + _pendingCar.ModelName +
                     (_pendingCar.Raw != null ? " (" + _pendingCar.Raw.S("_version_name", "") + ", Beklebung " + _pendingCar.Raw.S("_sticker", "-") + ")" : ""));
            bool ok = _race.StartRace(_pending);
            if (!ok) Log.Warn("Prüfung " + _pending.Name + " konnte nicht gestartet werden (" + _pending.Checkpoints.Count + " Checkpoints)");
        }
        else if (_phase == Phase.EnterSchool)
        {
            if (!Inside) { _phase = Phase.None; return; }
            if (!SceneReady && _phaseT < 20f) return;
            if (_phaseT < 0.5f) return;
            _phase = Phase.None;
            Log.Info("Rennschule: zurück in der Akademie (" + _spotCode + "), eigenes Auto beim Verlassen: " + (PlayerCar()?.FileName ?? "-"));
            Open();
        }
        else if (_phase == Phase.StreamToSchool)
        {
            var p = _spotExit;
            if (_t?.Camera != null && _t.Streamer != null) _t.Camera.Position = _t.Streamer.GameToRender(p + Vector3.UnitY * 6f);
            if (!GroundReady(p) && _phaseT < 15f) return;
            var fwd = new Vector3(MathF.Sin(_spotHeading), 0f, MathF.Cos(_spotHeading));
            if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.UnitZ;
            var car = PlayerCar();
            if (car != null && !vm.SpawnCarExact(car, p, Vector3.Normalize(fwd)) && _phaseT < 20f) return;
            _phase = Phase.None;
            Log.Info("Rennschule: eigenes Auto zurück: " + (car != null ? car.FileName + " #" + car.Id : "-"));
            _openRequested = false;
            Open();
        }
    }

    // ------------------------------------------------------------------ Ergebnis
    RaceResult? _lastResult;
    int _lastLicensed;

    LicenseInfo? CurrentLicence => _licences.Count > 0 && _licIndex < _licences.Count ? _licences[_licIndex] : null;

    void OnRaceEnded(RaceManager r)
    {
        var res = r.Result;
        _lastResult = res;
        NoteAttempt(res);
        _lastLicensed = 0;
        var lic = CurrentLicence;
        if (res != null && res.Passed && res.Race != null)
        {
            RaceProgress.MarkTestDone(res.Race.Name);
            RaceProgress.ReportTime(res.Race.Name, res.FinalTimeMs);
        }
        if (lic != null)
        {
            bool wasWon = RaceProgress.IsLicenceWon(lic.Hash);
            if (!wasWon && IsLicenceWon(lic)) { RaceProgress.MarkLicenceWon(lic.Hash); _lastLicensed = 2; _backWin = true; }
            else if (wasWon) _lastLicensed = 1;
        }
        Log.Info("Prüfung " + (res?.Race?.Name ?? "") + (res != null && res.Passed ? " bestanden" : " nicht bestanden") +
                 ": " + RaceHud.FormatRaceTimeMs(res?.FinalTimeMs ?? 0) + (res != null && res.PenaltyMs > 0 ? " (inkl. " + RaceHud.FormatRaceTimeMs(res.PenaltyMs) + " Strafe)" : "") +
                 (res != null && res.TimedOut ? " – Zeit abgelaufen" : "") + (_lastLicensed == 2 && lic != null ? " – Lizenz " + lic.DisplayName + " erworben!" : ""));
        // wie im Spiel: nach dem Ziel übernimmt der Autopilot, bremst ab und folgt der Straße
        var vm = _v?.Manager;
        if (vm?.Current != null) _autopilot.Begin(vm);
        _resultDelay = 1.2f;   // im Original läuft hier die Sieg-/Niederlage-Cinematic (pack_racingschool)
    }

    /// <summary>Wie im Original: erst das Instruktor-Kino (Sieg/Niederlage, pack_racingschool), dann das Ergebnis.</summary>
    void ShowResultsAfterCinematic()
    {
        var res = _lastResult;
        var lic = CurrentLicence;
        char school = res?.Race != null && res.Race.School != '\0' ? res.Race.School : lic != null ? lic.School : 'A';
        var cin = res != null ? RaceCinematics.RacingSchoolResult(_bnkDir, school, res.Passed) : null;
        if (cin == null) { ShowResults(); return; }
        _autopilot.End();
        RaceCinematics.Play(cin, ShowResults);
    }

    void ShowResults()
    {
        var res = _lastResult;
        if (res == null) { ReturnToSchool(); return; }
        var lic = CurrentLicence;
        string name = res.Race != null ? RaceTexts.Get(res.Race.NameHc, res.Race.Title) : "";
        // ohne results.bnk: direkt zurück zur Schule
        if (!_results.Show(res, lic, DoneCount(), _tests.Count, _lastLicensed, name)) ReturnToSchool();
    }

    void NextTest()
    {
        int next = _testIndex + 1;
        if (next >= _tests.Count) { ReturnToSchool(); return; }
        _testIndex = next;
        BeginTest(_tests[next]);
    }

    /// <summary>"Zurück zur Renn-Akademie": eigenes Auto in die Schule bzw. vor die Schule setzen, Menü wieder öffnen.</summary>
    void ReturnToSchool()
    {
        if (_results.IsOpen) _results.Close();
        _race.Cleanup();
        _race.Abort();
        _autopilot.End();
        _resultDelay = -1f;
        var vm = _v?.Manager;
        if (vm == null) return;
        Log.Info("ReturnToSchool: spotObj=" + (_spotObj?.Code ?? "-") + " haveSpot=" + _haveSpot + " playerCar=" + (PlayerCar()?.FileName ?? "-") + " current=" + (vm.CurrentCar?.FileName ?? "-"));
        PrivateSession(false);
        // wie im Spiel: zurück ins Interieur der Akademie, dort öffnet sich das Menü wieder
        if (RaceConfig.ReturnIntoInterior && _spotObj != null)
        {
            var car = PlayerCar();
            vm.Despawn();
            if (EnterSpot(_spotObj))
            {
                if (car != null) _lastCar = car;
                _phase = Phase.EnterSchool;
                _phaseT = 0f;
                return;
            }
        }
        if (!_haveSpot)
        {
            // Spot unbekannt (Entwicklermenü): am aktuellen Ort mit dem eigenen Auto weiterfahren
            var car = PlayerCar();
            if (car != null && vm.Current != null && vm.CurrentCar != car)
            {
                var p = vm.PositionGame; var f = vm.ForwardGame; f.Y = 0f;
                vm.SpawnCarExact(car, p + Vector3.UnitY * 0.3f, f.LengthSquared() > 1e-4f ? Vector3.Normalize(f) : Vector3.UnitZ);
            }
            _phase = Phase.None;
            GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
            return;
        }
        vm.Despawn();
        if (_t?.Camera != null && _t.Streamer != null) _t.Camera.Position = _t.Streamer.GameToRender(_spotExit + Vector3.UnitY * 6f);
        _phase = Phase.StreamToSchool;
        _phaseT = 0f;
    }
}
