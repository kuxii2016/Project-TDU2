using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Game.Screens;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using RaceTexts = Paradise.Core.Racing.RaceTexts;
using RaceMode = Paradise.Core.Racing.RaceMode;
using TDU2GameData = Paradise.Game.Online.TDU2GameData;
using TDU2Controls = Paradise.Game.Input.TDU2Controls;
using CultureInfo = System.Globalization.CultureInfo;
using FVec = Paradise.Core.Flash.Vector2;
using FlashScreen = Paradise.Core.Flash.Screen;
using RaceRoadPath = Paradise.Game.Racing.RaceRoadPath;
using Minimap = Paradise.Core.Vehicles.Minimap;
using GameFiles = Paradise.Core.Files.GameFiles;
using Ige2File = Paradise.Core.Formats.Ige2File;
using Ige2Writer = Paradise.Core.Formats.Ige2Writer;
using RaceRepository = Paradise.Core.Racing.RaceRepository;
using TDU2EditorSaves = Paradise.Core.Savegame.TDU2EditorSaves;
using TDU2Session = Paradise.Core.Online.TDU2Session;
using FrontendSounds = Paradise.Core.Audio.FrontendSounds;
using AudioEngine = Paradise.Core.Audio.AudioEngine;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Challenge-Editor (GME, GAME_MODE_EDITOR = 5, exe GM_GMEditor.cpp) – Film frontend/&lt;res&gt;/gmeditor.bnk über der Kartenansicht.
/// Einstieg wie im Spiel über das Pausemenü: main.vmf valid_menu "editor" -> FSCommand "goed" (exe 0x9ec1bb -> 0x9e97a0: nächster
/// Modus 5, Pause schließt). Der Entwickler-Editor aus GamePC.cpr (DISABLEGME/"[GME] Designer Edition") ist nicht dieser Modus.
///
/// Wie im Original liegt der ganze Editor über der Kartenansicht (Gamemodes/Utils/MapView, dieselbe wie die große Karte); die Steuerung
/// der Karte schaltet der Film mit "mcof"/"mcon" (exe +0x1be014 = 1/0) ab bzw. an – an nur beim Bearbeiten der Strecke.
///   Öffnen (exe 0xacd2c0): /:WhatZoom, /:LockUpload, /:BackFromTest = 0, /:WhatGM (Name der Spielart), /:LockUndo = 1,
///   /:LockRedo = 1, /:NbChallMax = 64, /:isChallLoaded = 0. Nach dem Laden der eigenen Challenges (exe 0xad0c40): /:WhatGM,
///   /:IsOverwriteGME = "1", /:IsMultiTab = "2", /:isChallLoaded = 1, /:BackFromTest, /:GMELoaded = 1, /:NbChallCreated = Anzahl.
///   Film: "init" -> Bild 2 wartet über uploader.initLoading auf _root.GMELoaded -> "open" -> openGME (Menü "Neue Challenge" /
///   "Challenges", B im Startmenü -> "close" -> Bild 51 FSCommand "gofr").
///   Neue Challenge: ValidMenu "new" -> setGMSelector -> Spielart -> FSCommand "nech" (exe 0xad75be -> 0xace5c0: Name Text 0x1c96 mit
///   PLAYER_NAME, Beschreibung Text 0x1394) -> "open_edit" -> "gmed:card:open" (exe 0xad50b9 -> ChallengeIDCard 0x976210 füllt die Karte).
/// Folgt (Docs/re/gme_crc_spec.md): Strecke bearbeiten (emin/emou, Cursor, setp/movp/insp/delp/chkp/chki/loop/sens, undo/redo),
/// Parameter, Testfahrt (test), Speichern (save, Ige/YYYYMMDDhhmmss.ig2), Hochladen (gmed:upld).
/// </summary>
public sealed class GAME_MODE_EDITOR : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_EDITOR;
    static readonly ScreenDef[] _screens = { ScreenDefs.Editor };
    public override IReadOnlyList<ScreenDef> Screens => _screens;

    /// <summary>exe 0xacd40e: höchstens 64 eigene Challenges (der Film-Testwert 20 gilt nur mit _root.fake).</summary>
    public const int MaxChallenges = 64;

    IGameModeContext? _ctx;
    VmfPlayer? _ui;
    bool _hooked, _leaving;
    /// <summary>exe GM_GMEditor +0x1c7604: Challenge darf hochgeladen werden (0 nach Neuanlegen/Laden, 0xad7829).</summary>
    bool _uploadable;
    bool _loadedPending;
    /// <summary>Kartenansicht unter dem Film (gemeinsam mit der großen Karte).</summary>
    readonly MapView _view = new();
    /// <summary>exe +0x1be014 (invertiert): Karte reagiert auf Verschieben/Zoom – "mcon" an, "mcof" aus.</summary>
    bool _mapControls;
    bool _dragging; Vector2 _lastMouse;
    /// <summary>Challenge in Bearbeitung (exe: Objekt hinter vtable +0x38, Daten ab +0x58); null = keine.</summary>
    EditorChallenge? _challenge;

    // ---- Strecke bearbeiten (emin .. emou)
    bool _editTrack;
    /// <summary>Typ unter dem Cursor wie zuletzt an EditionManager gegeben (exe +0x1c5610).</summary>
    string _cursorType = "";
    /// <summary>Punkt unter dem Cursor (-1 = keiner), verschobener Punkt (movp .. setp/cmov, -1 = keiner).</summary>
    int _hoverPoint = -1, _movingPoint = -1;
    /// <summary>Straßenpunkt unter dem Cursor (Spielkoordinaten), null = keine Straße.</summary>
    Vector3? _cursorRoad;
    /// <summary>Abschnitt der Route unter dem Cursor (Einfügen: insp), -1 = keiner.</summary>
    int _cursorSegment = -1;
    int _pointChar, _pointsShown;
    const int PointDepth0 = 1000;
    /// <summary>Verlauf für gmed:undo/redo (exe historicUpdate, /:LockUndo, /:LockRedo).</summary>
    readonly List<List<Vector3>> _undo = new(), _redo = new();
    Vector2 _downMouse;

    // ---- Speichern / Liste / Testfahrt
    /// <summary>Datei der Challenge im Spielstand ("Ige/….ig2"), null = noch nie gespeichert.</summary>
    string? _fileName;
    /// <summary>exe 0x17222b5: seit dem letzten Speichern geändert (/:IsModified, "qugm").</summary>
    bool _modified;
    /// <summary>Liste "gme_chall" (challenges_list) und die Dateien ihrer Zeilen.</summary>
    VmfPlayer.FlashList? _list;
    readonly List<string> _listFiles = new();
    readonly List<EditorChallenge?> _listItems = new();
    /// <summary>Rückkehr aus der Testfahrt (exe +0x1c5539 -> /:BackFromTest).</summary>
    bool _backFromTest;
    /// <summary>Testfahrt bis ins Ziel gefahren (Rückruf von BeginTest) – macht die Challenge hochladbar (exe +0x1c7604).</summary>
    bool _testCompleted;
    /// <summary>Nach dem Speichern: Bearbeitung im nächsten Bild neu öffnen (nicht mitten im FSCommand des Films).</summary>
    bool _reopenEdit;
    static string? Nick => TDU2Session.LoggedIn ? TDU2Session.Nick : null;

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        if (from == Gamemode.GAME_MODE_PAUSE && _ui != null) return;   // Weiter nach dem Pausemenü
        if (_backFromTest && _challenge != null) { ReturnFromTest(ctx); return; }
        _leaving = false;
        _challenge = null;
        _mapControls = false;
        _editTrack = false; _movingPoint = -1; _hoverPoint = -1; _cursorType = "";
        _undo.Clear(); _redo.Clear(); _pointsShown = 0; _pointChar = 0;
        _fileName = null; _modified = false; _list = null;
        _ui = ctx.Open(ScreenDefs.Editor);
        var ui = _ui;
        if (ui == null) { Log.Warn("Editor: gmeditor.bnk nicht geladen"); GameMode.Set(Gamemode.GAME_MODE_FREERIDE); return; }
        ui.Visible = true;
        if (!_hooked) { ui.FSCommand += OnCommand; ui.ListCreated += OnListCreated; ui.ListLineDrawn += OnListLineDrawn; _hooked = true; }
        ClearPoints(ui);   // Symbole einer früheren Sitzung (der Film bleibt im Zwischenspeicher)
        FlashLocalization.Apply(ui, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language));
        ui.MouseEnabled = true;
        ui.SetValue("/:fake", false);
        // Plattform wie die übrigen PC-Filme (Hilfeleiste/Tasten des Films)
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetVariable("/:MenuType", "pc");
        ui.SetValue("/:IsPC_X360", true);

        // Kartenansicht wie die große Karte, Mitte am Fahrzeug (exe 0xacd287 -> Basis-Karte; Zoomstufe der Karte)
        _view.Bind(ctx);
        var vm = ctx.Vehicles?.Manager; var s = ctx.Terrain?.Streamer; var cam = ctx.Terrain?.Camera;
        if (s != null && cam != null)
            _view.Begin(ctx, vm?.Current != null ? s.GameToRender(vm.PositionGame) : cam.Position, 2);
        AudioEngine.Paused = true;   // wie die große Karte: Weltgeräusche (Motor) aus, Menü-Klänge laufen weiter
        // Fahrzeug, Gruppe, Tuningstufe, Geld oben rechts (/infos: displayInfos liest _root.cur_upgrade / _root.player_group)
        PlayerCarInfo.Apply(ui, ctx);

        // exe 0xacd2c0 (Öffnen des Editors)
        ui.SetValue("/:WhatZoom", _view.Zoom);
        ui.SetValue("/:LockUpload", !_uploadable);   // exe: !(+0x1c7604); das Feld wird beim Laden/Neuanlegen auf 0 gesetzt (0xad7829)
        ui.SetValue("/:BackFromTest", false);
        ui.SetVariable("/:WhatGM", "timeAttack");   // exe 0x4145c9(Spielart der Challenge) – neue Challenge: Zeitfahren
        ui.SetValue("/:LockUndo", true);
        ui.SetValue("/:LockRedo", true);
        ui.SetValue("/:NbChallMax", MaxChallenges);
        ui.SetValue("/:isChallLoaded", false);

        ui.SetValue("/:GMELoaded", false);
        ui.SetValue("/:IsDesigner", false);   // "[GME] Designer Edition" nur im Entwickler-Editor
        ui.SetValue("/:FirstTime", false);    // [OFFEN] Hilfesystem beim ersten Öffnen (exe /:FirstTime, Profil-Flag)
        _loadedPending = true;               // exe setzt GMELoaded erst nach dem Laden der Challenges (0xad0c40)

        ui.GotoLabel("/", "init", true);
        Log.Info("Editor: geöffnet");
    }

    /// <summary>
    /// exe 0xad0c40: eigene Challenges geladen. Der Film wartet ab Bild 2 über uploader.initLoading("_root.GMELoaded") auf diesen
    /// Wechsel – darum erst nach dem Start von "init" setzen (wie die exe: erst 0, nach dem Laden 1).
    /// </summary>
    void ChallengesLoaded(VmfPlayer ui)
    {
        _loadedPending = false;
        int created = TDU2EditorSaves.Count(Nick);
        ui.SetVariable("/:WhatGM", "timeAttack");
        ui.SetVariable("/:IsOverwriteGME", "1");
        ui.SetVariable("/:IsMultiTab", "2");
        ui.SetValue("/:isChallLoaded", true);
        ui.SetValue("/:BackFromTest", false);
        ui.SetValue("/:GMELoaded", true);
        ui.SetValue("/:NbChallCreated", created);
        Log.Info("Editor: " + created + " eigene Challenges (höchstens " + MaxChallenges + ")");
    }

    /// <summary>
    /// FSCommand "nech" (exe 0xad75be): Spielart-Wahl bestätigt (gm_selector AfterValid, danach geht der Film selbst nach
    /// "open_edit"). /:IsOverwriteGME = "0", /:IsMultiTab = "2", /:GME_Help = " ", LockUndo/LockRedo = 1; neue Challenge aus
    /// _root.WhatGM / _root.WhatGMType (0xace5c0).
    /// </summary>
    void NewChallenge(VmfPlayer ui)
    {
        ui.SetVariable("/:IsOverwriteGME", "0");
        ui.SetVariable("/:IsMultiTab", "2");
        ui.SetVariable("/:GME_Help", " ");
        ClearPoints(ui); _undo.Clear(); _redo.Clear();
        _challenge = EditorChallenge.CreateNew(ui.GetVariable("/:WhatGM") ?? "timeAttack", ui.GetVariable("/:WhatGMType") ?? "solo");
        _fileName = null; _modified = false;
        ui.SetValue("/:IsTestPossible", false);
        // Startwerte der exe-Neuanlage (Reset 0xb376c0, danach 0xace5c0):
        //  Zeitlimit CtDw 160000 ms (2'40''), Verkehr trat 3 / ChTR 0 – bei Tempo und Radar ChTR = 1 und ChCD = 1 (0xace7c2/0xace7ea),
        //  Strafe offR 0, Pflichtweg forc 0, Rundkurs 0, Runden 1.
        _challenge.TimeOutMs = 160000;
        _challenge.Traffic = _challenge.Mode == RaceMode.Speed || _challenge.Mode == RaceMode.SpeedTrap ? 3 : 0;
        // [ANNAHME] Ziel-/Mindestgeschwindigkeit (tras): Startwert der exe nicht im Reset – Wert der Original-ig2 derselben Spielart
        var tmpl = Template(_challenge.Mode);
        if (tmpl != null) { var tf = Ige2File.Parse(tmpl); if (tf.Int("tras") > 0) _challenge.SpeedMms = tf.Int("tras"); }
        ui.SetValue("/:LockUndo", true);
        ui.SetValue("/:LockRedo", true);
        _uploadable = false;
        Log.Info("Editor: neue Challenge \"" + _challenge.Name + "\", Spielart " + _challenge.ModeName + " (" + _challenge.ModeType + ")");
    }

    /// <summary>
    /// "gmed:card:open" (exe 0xad50b9 -> ChallengeIDCard 0x9771d0 -> 0x976210): Karte der Challenge füllen.
    ///  /:ChallengeName, /:ChallengeDescription; je Spielart (exe 0x976300 ff.):
    ///   Zeitfahren: /:RefScore (ohne Referenzzeit "--"), /:NbCheckpoints = "%d %s" (Anzahl, Text 0x138c), /:Distance = Länge/1000
    ///   "%.1f %s" (Text 0x2e6, exe 0xae2940);
    ///   Tempo (10): /:RefScore "--", /:Distance "--", /:NbCheckpoints "--";
    ///   Radar (1): /:RefScore "--", /:NbCheckpoints = "%d %s" (Anzahl, Text 0x387 "Radar"), /:Distance "--".
    ///  /:IsLoopEnabled = Rundkurs, /:LockNbLaps = !Rundkurs (exe +0x668).
    /// [OFFEN] /:TrafficValue (+0x428/+0xbc0), /:PenaltyValue (+0x1b68/+0x1d48), Fahrzeug-Einschränkung (0x977810:
    ///  /:RestrictionCategory/Brand/Vehicle) – Startwerte der exe noch nicht verfolgt.
    /// </summary>
    void FillCard(VmfPlayer ui)
    {
        var c = _challenge;
        if (c == null) return;
        var inv = CultureInfo.InvariantCulture;
        ui.SetVariable("/:ChallengeName", c.Name);
        ui.SetVariable("/:ChallengeDescription", c.Description);
        int count = c.Checkpoints.Count;
        switch (c.Mode)
        {
            case RaceMode.Speed:
                ui.SetVariable("/:RefScore", "--");
                ui.SetVariable("/:Distance", "--");
                ui.SetVariable("/:NbCheckpoints", "--");
                break;
            case RaceMode.SpeedTrap:
                ui.SetVariable("/:RefScore", "--");
                ui.SetVariable("/:NbCheckpoints", count + " " + RaceTexts.Get(0x387, "Radar"));
                ui.SetVariable("/:Distance", "--");
                break;
            default:
                ui.SetVariable("/:RefScore", "--");
                ui.SetVariable("/:NbCheckpoints", count + " " + RaceTexts.Get(0x138c, "Checkpoint(s)"));
                ui.SetVariable("/:Distance", (c.LengthM / 1000f).ToString("0.0", inv) + " " + RaceTexts.Get(0x2e6, "Km"));
                break;
        }
        ui.SetValue("/:IsLoopEnabled", c.IsLoop);
        ui.SetValue("/:LockNbLaps", !c.IsLoop);
        ui.SetValue("/:TrafficValue", c.Traffic);
        ui.SetValue("/:PenaltyValue", c.Penalty);
        SetParamVars(ui, c.IsLoop ? c.Laps : 0, c.ForcedPath, c.TimeOutMs);
        SetSpeedVar(ui, c.Mode, c.SpeedMms);
        SetRestrictionVars(ui, c.Restriction, c);
    }

    /// <summary>
    /// exe 0x975140 / 0x977810: Fahrzeug-Einschränkung. Typ 0..2 (Tabelle 0x1669530): Text 0x1391 "Keine Beschränkung" / 0x214
    /// "Fahrzeug" / 0xeb5 "Kategorie" -> /:RestrictionTypeStr, Filter "empty"/"vehicle"/"category" -> /:RestrictionTypeFilter;
    /// /:RestrictionCategory = Gruppe ("all" ohne Kategorie-Einschränkung), /:RestrictionVehicle/Brand = Fahrzeug; dann
    /// /parameters.updateRestriction().
    /// </summary>
    void SetRestrictionVars(VmfPlayer ui, int type, EditorChallenge c)
    {
        ulong[] txt = { 0x1391, 0x214, 0xeb5 };
        string[] fallback = { "Keine Beschränkung", "Fahrzeug", "Kategorie" };
        string[] filter = { "empty", "vehicle", "category" };
        type = System.Math.Clamp(type, 0, 2);
        ui.SetVariable("/:RestrictionTypeStr", RaceTexts.Get(txt[type], fallback[type]));
        ui.SetVariable("/:RestrictionTypeFilter", filter[type]);
        ui.SetVariable("/:RestrictionCategory", type == 2 ? c.RestrictionCategory : "all");
        ui.SetVariable("/:RestrictionVehicle", type == 1 ? c.RestrictionVehicle : "");
        ui.SetVariable("/:RestrictionBrand", type == 1 ? c.RestrictionBrand : "");
        ui.CallClip("/parameters", "updateRestriction");
    }

    /// <summary>Fahrzeug bzw. Gruppe des aktuellen Autos für die Einschränkung übernehmen.</summary>
    void TakeCurrentCar(EditorChallenge c)
    {
        var car = _ctx?.Vehicles?.Manager?.Current?.Car;
        c.RestrictionCategory = _ctx != null ? PlayerCarInfo.CurrentGroup(_ctx) : "all";
        c.RestrictionVehicle = car != null ? car.ModelName ?? "" : "";
        c.RestrictionBrand = car?.Brand ?? "";
        c.RestrictionCarFile = car?.FileName ?? "";
        c.RestrictionCarId = car?.Id ?? 0;
        c.RestrictionBrandId = car?.Raw != null ? car.Raw.I("_car_brand") : 0;
        c.RestrictionCat = car?.Raw != null ? car.Raw.I("_vehiclecategory") : 0;
    }

    /// <summary>Geladene Challenge: Namen zur Fahrzeug-Einschränkung aus der Datenbank (VMod = _id_car).</summary>
    void ResolveRestriction(EditorChallenge c)
    {
        if (c.Restriction != 1 || c.RestrictionCarId == 0) return;
        var db = _ctx?.Vehicles?.Manager?.Database;
        if (db == null || !db.ById.TryGetValue(c.RestrictionCarId, out var car)) return;
        c.RestrictionVehicle = car.ModelName ?? ""; c.RestrictionBrand = car.Brand ?? ""; c.RestrictionCarFile = car.FileName ?? "";
    }

    /// <summary>exe 0x40f2e5 (test): Fahrzeug erfüllt die Einschränkung der Challenge (/:IsVehicleOK).</summary>
    bool VehicleOk(EditorChallenge c)
    {
        if (c.Restriction == 0 || _ctx == null) return true;
        var car = _ctx.Vehicles?.Manager?.Current?.Car;
        if (c.Restriction == 2)
        {
            int cat = car?.Raw != null ? car.Raw.I("_vehiclecategory") : 0;
            if (c.RestrictionCat > 0) return cat == c.RestrictionCat;                                   // exe Typ 4: genau die Kategorie
            if (c.RestrictionClass > 0) return cat >= 0 && cat < EditorChallenge.CatClass.Length && EditorChallenge.CatClass[cat] == c.RestrictionClass;
            return PlayerCarInfo.CurrentGroup(_ctx) == c.RestrictionCategory;
        }
        return car != null && string.Equals(car.FileName, c.RestrictionCarFile, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>exe 0x975b03: Tempo -> /:SpeedToReach, Radar -> /:SpeedTrapMin = "%d %s" (Wert +0x14 = ig2 "tras", mm/s).</summary>
    static void SetSpeedVar(VmfPlayer ui, RaceMode mode, int mms)
    {
        string v = ((int)MathF.Round(Paradise.Game.Racing.ChallengeModes.ToUnit(mms / 1000f))).ToString(CultureInfo.InvariantCulture) + " " + Paradise.Game.Racing.ChallengeModes.UnitName;
        if (mode == RaceMode.Speed) ui.SetVariable("/:SpeedToReach", v);
        else if (mode == RaceMode.SpeedTrap) ui.SetVariable("/:SpeedTrapMin", v);
    }

    /// <summary>
    /// exe 0x975930 (Werte der Parameter auf den Film): /:NbLaps = Rundkurs ? (Runden > 1 ? "%d %s" Text 0x44 : "1 %s" Text 0x10d2) : "--";
    /// /:IsForcedPath, /:IsForcedPathStr = Text 0x127 "Ein" / 0x126 "Aus"; /:TimeOut = "%d'%02d''" (Minuten, Sekunden).
    /// </summary>
    static void SetParamVars(VmfPlayer ui, int laps, bool forcedPath, int timeOutMs)
    {
        ui.SetVariable("/:NbLaps", laps > 1 ? laps + " " + RaceTexts.Get(0x44, "Runden") : laps == 1 ? "1 " + RaceTexts.Get(0x10d2, "Runde") : "--");
        ui.SetValue("/:IsForcedPath", forcedPath);
        ui.SetVariable("/:IsForcedPathStr", RaceTexts.Get(forcedPath ? 0x127UL : 0x126UL, forcedPath ? "Ein" : "Aus"));
        int sec = timeOutMs / 1000;
        ui.SetVariable("/:TimeOut", (sec / 60) + "'" + (sec % 60).ToString("00") + "''");
    }

    // ================================================================== Parameter (gmed:para:*, exe ChallengeIDCard 0x977a30)
    // Arbeitswerte des Parameter-Bildschirms (exe Karte +0x8 Runden, +0xc Pfad, +0x10 Zeitlimit) – übernommen erst mit "vali"
    int _pLaps, _pTimeOut, _pSpeed, _pRestr; bool _pPath;
    string _pFocus = "";

    /// <summary>"open" (exe 0x977b76 -> 0x975ba0): Werte der Challenge übernehmen und anzeigen.</summary>
    void ParaOpen(VmfPlayer ui)
    {
        var c = _challenge;
        if (c == null) return;
        _pLaps = c.IsLoop ? c.Laps : 0; _pPath = c.ForcedPath; _pTimeOut = c.TimeOutMs; _pSpeed = c.SpeedMms; _pFocus = "";
        SetParamVars(ui, _pLaps, _pPath, _pTimeOut);
        SetSpeedVar(ui, c.Mode, _pSpeed);
        _pRestr = c.Restriction;
        SetRestrictionVars(ui, _pRestr, c);
        ui.SetValue("/:TrafficValue", c.Traffic);
        ui.SetValue("/:PenaltyValue", c.Penalty);
    }

    /// <summary>
    /// "laps"/"path"/"tmou" + ":scne"/":scpr" (exe 0x977c9d / 0x977b41): Wert um die Schrittweite ändern, am Ende umlaufen. Bereiche
    /// nach exe 0x976da0 (gewählt über /:VarToFocus, "chpa"): Runden 1..20, Pfad 0..1, Zeitlimit 10 s .. 20 min in 5-s-Schritten.
    /// </summary>
    void ParaStep(VmfPlayer ui, string what, bool next)
    {
        // Die Auswahlbox ruft nach dem FSCommand updateText(): param_txt = eval(_root.VarToFocus). Steht dort noch die Variable einer
        // anderen Zeile (F11-Log: Restriction zeigte "_root.IsForcedPathStr"), zeigt die Zeile deren Wert bzw. Namen – darum die
        // Variable des geänderten Parameters setzen (exe: chpa liest /:VarToFocus, 0x976da0 wählt danach den Parameter).
        string? focusVar = what switch
        {
            "laps" => "_root.NbLaps", "path" => "_root.IsForcedPathStr", "tmou" => "_root.TimeOut", "rstr" => "_root.RestrictionTypeStr",
            "spee" => "_root.SpeedToReach", "spmn" => "_root.SpeedTrapMin", "brnd" => "_root.RestrictionBrand", "vhcl" => "_root.RestrictionVehicle",
            _ => null,
        };
        if (focusVar != null) ui.SetVariable("/:VarToFocus", focusVar);
        switch (what)
        {
            case "laps": if (_challenge?.IsLoop == true) _pLaps = Wrap(_pLaps, next ? 1 : -1, 1, 20); break;
            case "path": _pPath = Wrap(_pPath ? 1 : 0, next ? 1 : -1, 0, 1) == 1; break;
            case "tmou": _pTimeOut = Wrap(_pTimeOut, next ? 5000 : -5000, 10000, 1200000); break;
            case "rstr":   // exe 0x977bcb: Typ +1, über 2 zurück auf 0; scpr (0x42441a) rückwärts
                _pRestr = (_pRestr + (next ? 1 : 2)) % 3;
                if (_challenge != null) { if (_pRestr != 0) TakeCurrentCar(_challenge); SetRestrictionVars(ui, _pRestr, _challenge); }
                return;
            case "brnd": case "vhcl":
                // [OFFEN] Marke/Fahrzeug durchblättern (exe 0x4181f1/0x42c0de) – vorerst das aktuelle Auto
                if (_challenge != null) { TakeCurrentCar(_challenge); SetRestrictionVars(ui, _pRestr, _challenge); }
                return;
            case "spee": case "spmn":
                {
                    // exe 0x977128: 0 .. 111111 mm/s (400 km/h), Schritt 5 km/h (1388 mm/s; 5 mph = 2234 bei Meilen), ohne Umlauf,
                    // danach auf das Raster der Einheit (exe 0x41fee7)
                    const int step = 1388;
                    int v = System.Math.Clamp(_pSpeed + (next ? step : -step), 0, 111111);
                    _pSpeed = (int)MathF.Round(v / (float)step) * step;
                    if (_challenge != null) SetSpeedVar(ui, _challenge.Mode, _pSpeed);
                    return;
                }
            default: Log.NotImplemented("Editor Parameter nicht portiert: " + what); return;
        }
        SetParamVars(ui, _pLaps, _pPath, _pTimeOut);
    }

    static int Wrap(int v, int step, int min, int max)
    {
        v += step;
        if (v > max) v = min;
        if (v < min) v = max;
        return v;
    }

    /// <summary>"vali" (exe 0x977d1f): Werte in die Challenge übernehmen (Name/Beschreibung setzt der Film selbst in _root, Verkehr/
    /// Strafe die Regler); danach Karte neu.</summary>
    void ParaValidate(VmfPlayer ui)
    {
        var c = _challenge;
        if (c == null) return;
        if (c.IsLoop && _pLaps >= 1) c.Laps = _pLaps;
        c.ForcedPath = _pPath; c.TimeOutMs = _pTimeOut; c.SpeedMms = _pSpeed; c.Restriction = _pRestr;
        c.Name = Clip(ui.GetVariable("/:ChallengeName") ?? c.Name, 63);
        c.Description = Clip(ui.GetVariable("/:ChallengeDescription") ?? c.Description, 63);
        if (int.TryParse(ui.GetVariable("/:TrafficValue"), out int tv)) c.Traffic = tv;
        if (int.TryParse(ui.GetVariable("/:PenaltyValue"), out int pv)) c.Penalty = pv;
        _modified = true;
        if (_uploadable) { _uploadable = false; ui.SetValue("/:LockUpload", true); }
        FillCard(ui);
        Log.Info("Editor: Parameter übernommen – \"" + c.Name + "\", Runden " + c.Laps + ", Pfad " + c.ForcedPath + ", Verkehr " + c.Traffic + ", Strafe " + c.Penalty);
    }

    static string Clip(string s, int max) => s.Length > max ? s.Substring(0, max) : s;

    void OnPara(VmfPlayer ui, string sub)
    {
        // sub = "open" | "chpa" | "vali" | "canc" | "<param>:scne|scpr"
        int k = sub.IndexOf(':');
        if (k > 0) { ParaStep(ui, sub.Substring(0, k), sub.EndsWith(":scne")); return; }
        if (sub == "brnd" || sub == "vhcl") { ParaStep(ui, sub, true); return; }
        switch (sub)
        {
            case "open": ParaOpen(ui); return;
            case "chpa": _pFocus = ui.GetVariable("/:VarToFocus") ?? ""; return;
            case "vali": ParaValidate(ui); return;
            case "canc": return;   // Änderungen verwerfen (Arbeitswerte werden beim nächsten "open" neu geladen)
            default: Log.NotImplemented("Editor Parameter nicht portiert: gmed:para:" + sub); return;
        }
    }

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        var ui = _ui;
        if (ui == null || ctx.Paused || _leaving) return;
        if (_loadedPending) ChallengesLoaded(ui);
        // challenges_list: chall_list "loading" wartet über uploader.initLoading auf _root.Clip.isListLoaded (exe setzt es, wenn die
        // eigenen Challenges gelesen sind); erst danach öffnet der Film die Liste und legt ASFlashList "gme_chall" an
        var cl = ui.Find("/chall_list");
        if (cl?.Sprite != null && cl.Sprite.Timeline.Labels.TryGetValue("loading", out int lf) && cl.Sprite.Timeline.Labels.TryGetValue("open", out int of)
            && cl.Sprite.Frame >= lf && cl.Sprite.Frame < of)
            ui.SetValue("/chall_list:isListLoaded", true);
        if (_reopenEdit) { _reopenEdit = false; ui.SetValue("/:Input", true); ui.GotoLabel("/", "open_edit", true); }
        if (ctx.Pressed(0x7A)) DumpParameters(ui);   // F11: Zustand des Parameter-Bildschirms ins Log (Fehlersuche)
        _view.LastDt = MathF.Max(1e-4f, time.DeltaTime);
        if (FlashDialog.Active) { FlashDialog.Update(); _view.UpdateCamera(false); return; }
        if (_mapControls) HandleMap(ctx, ui, time.DeltaTime);
        _view.ClampTarget();
        _view.UpdateCamera(false);
        if (_editTrack) UpdateEditTrack(ui);
        UpdatePoints(ui);
        HandleKeys(ui);
    }

    /// <summary>Straßennetz über der Kartenansicht (wie die große Karte).</summary>
    public override void Render(IGameModeContext ctx, in GameTime time) => _view.DrawRoads();

    /// <summary>
    /// Kartensteuerung (nur nach "mcon"): Pfeile/WASD bzw. Ziehen verschieben, Mausrad zoomt über setZoom des Films (-> zoin/zoou),
    /// wie in der großen Karte.
    /// </summary>
    void HandleMap(IGameModeContext ctx, VmfPlayer ui, float dt)
    {
        Vector3 move = Vector3.Zero;
        if (TDU2Controls.Held("menu_up")) move += MapView.North;
        if (TDU2Controls.Held("menu_down")) move -= MapView.North;
        if (TDU2Controls.Held("menu_right")) move += MapView.East;
        if (TDU2Controls.Held("menu_left")) move -= MapView.East;
        _view.Pan(move, dt, TDU2Controls.KeyDown(0x10));
        var mp = MapView.MousePixel();
        if (FlashInput.GetMouseButtonDown(1) || FlashInput.GetMouseButtonDown(2)) { _dragging = true; _lastMouse = mp; }
        if (!FlashInput.GetMouseButton(1) && !FlashInput.GetMouseButton(2)) _dragging = false;
        if (_dragging) { _view.Drag(_lastMouse, mp); _lastMouse = mp; }
        float wheel = FlashInput.mouseScrollDelta;
        if (wheel > 0.1f && _view.Zoom > 1 && !ui.CallClip("/", "setZoom", "in")) SetZoom(ui, _view.Zoom - 1);
        if (wheel < -0.1f && _view.Zoom < 4 && !ui.CallClip("/", "setZoom", "out")) SetZoom(ui, _view.Zoom + 1);
    }

    void SetZoom(VmfPlayer ui, int z)
    {
        _view.Zoom = System.Math.Clamp(z, 1, 4);
        ui.SetValue("/:WhatZoom", _view.Zoom);
    }

    // ================================================================== Strecke bearbeiten
    /// <summary>
    /// Höchstzahl der Punkte je Spielart wie exe 0xad6168: Zeitfahren 50 (Index 0x31), Radar 9 (Start + 8 Radare, Index 8),
    /// Tempo 1 (nur der Start, Index 0). HUD: /:MaxCheckPoints 50 bzw. /:MaxTraps 8 (exe 0xad96d4/0xad975e).
    /// </summary>
    int MaxPoints => _challenge?.Mode switch { RaceMode.SpeedTrap => 9, RaceMode.Speed => 1, _ => 50 };
    bool IsFull => _challenge != null && _challenge.Checkpoints.Count >= MaxPoints;

    void BeginEditTrack(VmfPlayer ui)
    {
        _editTrack = true; _cursorType = ""; _movingPoint = -1;
        ui.MouseIgnore.Add("/curseur"); ui.MouseIgnore.Add("/curseur_ghost"); ui.MouseIgnore.Add("/pack");
        ui.CallClip("/curseur_ghost", "initGhost");
        UpdateHud(ui);
    }

    void EndEditTrack(VmfPlayer ui)
    {
        _editTrack = false; _movingPoint = -1; _cursorType = "";
        ui.SetVisible("/curseur_ghost", false);
        if (_ctx != null && _challenge != null) FillCard(ui);
    }

    /// <summary>
    /// Jedes Bild beim Bearbeiten (exe 0xad6100 ff.): Cursor unter der Maus, Straße/Punkt/Route darunter bestimmen; wechselt der
    /// Typ, /:IsSpotOK setzen (2 = voll, 1 = Straße, 0 = nicht möglich) und _root.EditionManager(Typ) aufrufen (exe 0xad6505).
    /// Typen: start/finish/checkpoint/radar (Punkt unter dem Cursor), track_road (Straße auf der Strecke: Einfügen), road (freie
    /// Straße: Setzen), track_invalid_road / offroad (keine Straße).
    /// </summary>
    void UpdateEditTrack(VmfPlayer ui)
    {
        var s = _ctx?.Terrain?.Streamer; var mm = _ctx?.Vehicles?.Manager?.Minimap; var c = _challenge;
        if (s == null || c == null) return;
        var mouse = MapView.MousePixel();
        var local = ui.GuiToLocal("/", new FVec(mouse.X, mouse.Y));
        if (local.HasValue) { ui.SetPosition("/curseur", local.Value); ui.SetPosition("/curseur_ghost", local.Value); }

        // Punkt unter dem Cursor (Symbolgröße ~ 22 px bei 720p)
        float hScale = FlashScreen.height / 720f, best = 22f * 22f * hScale * hScale;
        _hoverPoint = -1;
        if (_movingPoint < 0)
            for (int i = 0; i < c.Checkpoints.Count; i++)
            {
                var g = _view.ToGui(s.GameToRender(c.Checkpoints[i]));
                if (!g.HasValue) continue;
                float d2 = (g.Value - mouse).LengthSquared();
                if (d2 < best) { best = d2; _hoverPoint = i; }
            }

        // Straße unter dem Cursor (wie die Karte: bis ~2,5 % der Sichtbreite, mind. 25 m)
        _cursorRoad = null; _cursorSegment = -1;
        var gp = _view.GroundPoint(mouse);
        if (gp.HasValue && mm != null)
        {
            var game = s.RenderToGame(gp.Value);
            float tol = MathF.Max(25f, _view.CurWidth * 0.025f);
            if (mm.NearestRoad(game, tol, out var rp))
                _cursorRoad = new Vector3(rp.X, s.TryGetMapGroundHeight(rp.X, rp.Z, out float y) ? y : game.Y, rp.Z);
            if (c.RoadPath.Count > 1) _cursorSegment = NearestSegment(c, game, tol);
        }

        string type;
        if (_hoverPoint >= 0) type = PointLabel(c, _hoverPoint);
        else if (_cursorRoad.HasValue) type = _cursorSegment >= 0 && _movingPoint < 0 ? "track_road" : "road";
        else type = _cursorSegment >= 0 ? "track_invalid_road" : "offroad";

        // Vorschau-Symbol am Cursor: nächster Punkt, solange er gesetzt werden kann
        bool canPut = _hoverPoint < 0 && _cursorRoad.HasValue && (_movingPoint >= 0 || !IsFull);
        ui.SetVisible("/curseur_ghost", canPut);
        if (canPut) ui.CallClip("/curseur_ghost/ic", "setGMEIcon", _movingPoint >= 0 ? PointLabel(c, _movingPoint) : NextLabel(c));

        if (type == _cursorType) return;
        _cursorType = type;
        ui.SetValue("/:IsSpotOK", SpotState(type == "track_road"));
        ui.CallClip("/", "EditionManager", type);
    }

    /// <summary>/:IsSpotOK wie exe 0xad6f27 (chkp) bzw. 0xad6d35 (chki): 2 = keine weiteren Punkte, 1 = Straße, 0 = nicht möglich.</summary>
    int SpotState(bool insert)
    {
        if (_movingPoint < 0 && IsFull) return 2;
        if (!_cursorRoad.HasValue) return 0;
        return insert && _cursorSegment < 0 ? 0 : 1;
    }

    void CheckSpot(VmfPlayer ui, bool insert) => ui.SetValue("/:IsSpotOK", SpotState(insert));

    void PutPoint(VmfPlayer ui)
    {
        var c = _challenge;
        if (c == null || !_cursorRoad.HasValue) return;
        PushUndo(c);
        if (_movingPoint >= 0 && _movingPoint < c.Checkpoints.Count) c.Checkpoints[_movingPoint] = _cursorRoad.Value;
        else if (!IsFull) c.Checkpoints.Add(_cursorRoad.Value);
        _movingPoint = -1;
        TrackChanged(ui);
    }

    void InsertPoint(VmfPlayer ui)
    {
        var c = _challenge;
        if (c == null || !_cursorRoad.HasValue || _cursorSegment < 0 || IsFull) return;
        PushUndo(c);
        c.Checkpoints.Insert(System.Math.Min(_cursorSegment + 1, c.Checkpoints.Count), _cursorRoad.Value);
        TrackChanged(ui);
    }

    void DeletePoint(VmfPlayer ui)
    {
        var c = _challenge;
        if (c == null || _hoverPoint < 0 || _hoverPoint >= c.Checkpoints.Count) return;
        PushUndo(c);
        c.Checkpoints.RemoveAt(_hoverPoint);
        if (c.Checkpoints.Count < 3) c.IsLoop = false;
        _hoverPoint = -1;
        TrackChanged(ui);
    }

    void ToggleLoop(VmfPlayer ui)
    {
        var c = _challenge;
        if (c == null) return;
        PushUndo(c);
        c.IsLoop = !c.IsLoop && c.Checkpoints.Count >= 3;
        TrackChanged(ui);
    }

    /// <summary>Y am Start (Tempo/Radar, EditionManager: LockDirection frei): Startrichtung umdrehen (exe 0x97e720 dreht um π).</summary>
    void TurnStart(VmfPlayer ui)
    {
        var c = _challenge;
        if (c == null || c.Checkpoints.Count < 1) return;
        PushUndo(c);
        c.StartReversed = !c.StartReversed;
        TrackChanged(ui);
    }

    /// <summary>Fahrtrichtung am Start (Spielkoordinaten XZ): entlang der Straße zum nächsten Punkt bzw. der Straße, ggf. umgedreht.</summary>
    Vector3 StartDirection(EditorChallenge c)
    {
        var p = c.Checkpoints[0];
        Vector3 dir = Vector3.Zero;
        if (c.RoadPath.Count > 1) dir = RouteDir(c, p);
        else if (c.StartHeadingSaved.HasValue) dir = c.StartHeadingSaved.Value;   // geladen: gespeicherte Richtung (enthält das Umdrehen)
        else
        {
            var mm = _ctx?.Vehicles?.Manager?.Minimap;
            var probe = new Vector3(p.X + 12f, 0f, p.Z);
            if (mm != null && mm.NearestRoad(probe, 10f, out var n1)) dir = new Vector3(n1.X - p.X, 0f, n1.Z - p.Z);
        }
        dir.Y = 0f;
        if (dir.LengthSquared() < 1e-4f) dir = new Vector3(0f, 0f, -1f);
        dir = Vector3.Normalize(dir);
        return c.StartReversed ? -dir : dir;
    }

    void PushUndo(EditorChallenge c)
    {
        _undo.Add(new List<Vector3>(c.Checkpoints));
        _redo.Clear();
    }

    void UndoRedo(VmfPlayer ui, List<List<Vector3>> from, List<List<Vector3>> to)
    {
        var c = _challenge;
        if (c == null || from.Count == 0) return;
        to.Add(new List<Vector3>(c.Checkpoints));
        c.Checkpoints.Clear(); c.Checkpoints.AddRange(from[^1]); from.RemoveAt(from.Count - 1);
        if (c.Checkpoints.Count < 3) c.IsLoop = false;
        TrackChanged(ui);
    }

    /// <summary>Nach jeder Änderung: Straßenroute, Länge, Symbole, HUD-Zähler, Undo/Redo-Sperren (historicUpdate liest sie).</summary>
    void TrackChanged(VmfPlayer ui)
    {
        var c = _challenge!;
        RaceRoadPath.Lanes ??= _ctx?.Vehicles?.Traffic?.Lanes;
        var pts = new List<Vector3>(c.Checkpoints);
        if (c.IsLoop && pts.Count > 2) pts.Add(pts[0]);
        c.RoadPath = RaceRoadPath.Route(RaceRoadPath.FindLanes(), pts, out c.RoadSegments, out _);
        float len = 0f;
        for (int i = 1; i < c.RoadPath.Count; i++) len += Vector3.Distance(c.RoadPath[i - 1], c.RoadPath[i]);
        c.LengthM = len;
        Minimap.SetMapRacePath(c.RoadPath.Count > 1 ? c.RoadPath : null);
        _cursorType = "";   // Typ unter dem Cursor neu melden
        _modified = true;   // exe: jede Änderung setzt 0x17222b5
        if (_uploadable) { _uploadable = false; ui.SetValue("/:LockUpload", true); }
        ui.SetValue("/:LockUndo", _undo.Count == 0);
        ui.SetValue("/:LockRedo", _redo.Count == 0);
        UpdateHud(ui);
    }

    /// <summary>
    /// HUD der Streckenbearbeitung (exe 0xad966e): Zeitfahren /:NbCheckPointsInt = Punkte, /:MaxCheckPoints = 50; Radar
    /// /:NbTrapsInt = Punkte − 1 (ohne Start), /:MaxTraps = 8; /:Distance wie auf der Karte der Challenge.
    /// </summary>
    void UpdateHud(VmfPlayer ui)
    {
        var c = _challenge;
        if (c == null) return;
        int n = c.Checkpoints.Count;
        // exe +0x1c55d8 -> /:IsTestPossible: Testfahrt und Speichern erst mit gültiger Strecke (setSquareMenu sperrt sonst beide)
        ui.SetValue("/:IsTestPossible", TrackValid(c));
        if (c.Mode == RaceMode.SpeedTrap) { ui.SetValue("/:NbTrapsInt", System.Math.Max(0, n - 1)); ui.SetValue("/:MaxTraps", 8); }
        else if (c.Mode != RaceMode.Speed) { ui.SetValue("/:NbCheckPointsInt", n); ui.SetValue("/:MaxCheckPoints", 50); }
        FillCard(ui);
    }

    /// <summary>Symbol eines Punkts (exe 0xad6365 / 0x41247c): erster = start, letzter = finish (nicht beim Rundkurs), sonst checkpoint bzw. radar.</summary>
    static string PointLabel(EditorChallenge c, int i)
    {
        if (i == 0) return "start";
        if (i == c.Checkpoints.Count - 1 && i > 0 && !c.IsLoop && c.Mode != RaceMode.SpeedTrap) return "finish";
        return c.Mode == RaceMode.SpeedTrap ? "radar" : "checkpoint";
    }

    /// <summary>Symbol des nächsten zu setzenden Punkts (exe 0xad62bb: ohne Punkte "start").</summary>
    static string NextLabel(EditorChallenge c) => c.Checkpoints.Count < 1 ? "start" : c.Mode == RaceMode.SpeedTrap ? "radar" : "checkpoint";

    static int NearestSegment(EditorChallenge c, Vector3 game, float tol)
    {
        int bestSeg = -1; float best = tol * tol;
        for (int i = 1; i < c.RoadPath.Count; i++)
        {
            var a = c.RoadPath[i - 1]; var b = c.RoadPath[i];
            float dx = b.X - a.X, dz = b.Z - a.Z, l2 = dx * dx + dz * dz;
            float t = l2 > 1e-6f ? System.Math.Clamp(((game.X - a.X) * dx + (game.Z - a.Z) * dz) / l2, 0f, 1f) : 0f;
            float ex = a.X + dx * t - game.X, ez = a.Z + dz * t - game.Z, d2 = ex * ex + ez * ez;
            if (d2 < best) { best = d2; bestSeg = i < c.RoadSegments.Count ? c.RoadSegments[i] : -1; }
        }
        return bestSeg;
    }

    /// <summary>Alle Punkt-Kopien entfernen (Verlassen, neue Challenge) – sonst bleiben alte Symbole im Film stehen.</summary>
    void ClearPoints(VmfPlayer ui)
    {
        for (int i = 0; i < 64; i++) ui.Detach("/pack", PointDepth0 + i);
        _pointsShown = 0;
        Minimap.SetMapRacePath(null);
    }

    /// <summary>
    /// Punkte der Challenge auf der Karte: Kopien von /pack/point (exe 0xad9560: initGMEIcon, dann setGMEIcon je Punkt), an der
    /// Bildposition des Punkts.
    /// </summary>
    void UpdatePoints(VmfPlayer ui)
    {
        var c = _challenge; var s = _ctx?.Terrain?.Streamer;
        int n = c != null && s != null ? c.Checkpoints.Count : 0;
        if (_pointChar == 0)
        {
            var tmpl = ui.Find("/pack/point");
            if (tmpl == null) return;
            _pointChar = tmpl.CharId; tmpl.Visible = false;
        }
        for (int i = n; i < _pointsShown; i++) ui.Detach("/pack", PointDepth0 + i);
        for (int i = 0; i < n; i++)
        {
            string name = "point" + i.ToString("00"), path = "/pack/" + name;
            bool isNew = i >= _pointsShown;
            var d = ui.AttachSprite("/pack", _pointChar, name, PointDepth0 + i);
            if (d == null) continue;
            if (isNew) ui.CallClip(path, "initGMEIcon");
            ui.CallClip(path, "setGMEIcon", PointLabel(c!, i));   // setGMEIcon kehrt bei gleichem Symbol sofort zurück
            var g = _view.ToGui(s!.GameToRender(c!.Checkpoints[i]));
            var lp = g.HasValue ? ui.GuiToLocal("/pack", new FVec(g.Value.X, g.Value.Y)) : null;
            d.Visible = lp.HasValue && i != _movingPoint;
            if (lp.HasValue) d.Position = lp.Value;
            // Richtungspfeil (/pack/point/fleche): nur am Start von Tempo/Radar (dort dreht Y die Richtung), sonst aus (resetIconStatus)
            bool arrow = i == 0 && (c!.Mode == RaceMode.Speed || c.Mode == RaceMode.SpeedTrap) && g.HasValue;
            ui.SetVisible(path + "/fleche", arrow);
            if (arrow)
            {
                var dir = StartDirection(c!);
                var g2 = _view.ToGui(s!.GameToRender(c!.Checkpoints[0] + dir * 30f));
                if (g2.HasValue)
                {
                    var dv = g2.Value - g!.Value;
                    // Pfeil zeigt im Film nach unten (+y): Drehung = Bildschirmwinkel − 90°
                    ui.SetRotation(path + "/fleche", MathF.Atan2(dv.Y, dv.X) * 180f / MathF.PI - 90f);
                }
            }
        }
        _pointsShown = n;
    }

    // ================================================================== Speichern (save) und .ig2
    /// <summary>
    /// Vorlage für die Datei: eine Original-.ig2 derselben Spielart aus Bnk/ig2 (Reihenfolge/Flags/Längen aller MDSC-Tags wie die
    /// Serialisierung der exe, siehe Ige2Writer). Keine Prüfungen der Rennschule (TEST = 1).
    /// </summary>
    static readonly Dictionary<RaceMode, byte[]?> _templates = new();
    static byte[]? Template(RaceMode mode)
    {
        if (_templates.TryGetValue(mode, out var t)) return t;
        byte[]? found = null, fallback = null;
        try
        {
            string? bnk = GameFiles.BnkDir;
            if (!string.IsNullOrEmpty(bnk))
                foreach (var path in GameFiles.EnumerateFiles(System.IO.Path.Combine(bnk, "ig2"), "*.ig2"))
                {
                    var b = GameFiles.ReadAllBytes(path);
                    var f = Ige2File.Parse(b);
                    if (f.Checkpoints.Count == 0 || f.Int("TEST") != 0) continue;
                    fallback ??= b;
                    if (f.Mode == (uint)mode) { found = b; break; }
                }
        }
        catch (Exception ex) { Log.Warn("Editor: Vorlage .ig2 nicht lesbar: " + ex.Message); }
        _templates[mode] = found ?? fallback;
        if (found == null) Log.Warn("Editor: keine Original-.ig2 der Spielart " + mode + " als Vorlage" + (fallback != null ? " – nehme eine andere" : ""));
        return found ?? fallback;
    }

    /// <summary>Challenge als .ig2 (Werte der Challenge in den Tags, Tore als CHK2 in Fahrtrichtung).</summary>
    byte[]? BuildIge2(EditorChallenge c)
    {
        var tmpl = Template(c.Mode);
        if (tmpl == null) return null;
        string island = _ctx?.Terrain?.Streamer?.Index?.IslandName ?? "";
        var ints = new Dictionary<string, int>
        {
            ["gmth"] = (int)c.Mode,
            ["isla"] = island.IndexOf("ibiza", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1,   // 1 = Oahu (Original-ig2 "OAHU"), 0 = Ibiza (wie die Lizenzen, RaceCinematics)
            ["leme"] = (int)MathF.Round(c.LengthM),
            ["nblp"] = c.IsLoop ? c.Laps : 0,
            ["#"] = c.IsLoop ? 1 : 0,
            ["forc"] = c.ForcedPath ? 1 : 0,                       // +0x10e0
            ["ChTR"] = c.Traffic > 0 ? 1 : 0, ["trat"] = c.Traffic, // +0x428 / +0xbc0 (Karte: TrafficValue = ChTR ? trat : 0)
            ["offR"] = c.Penalty > 0 ? 1 : 0, ["PeCr"] = c.Penalty, // +0x1b68 / +0x1d48 (Karte: PenaltyValue)
            ["NbCp"] = c.Checkpoints.Count,                         // +0x3a00
            ["NbIA"] = 0,                                           // solo: keine Gegner
            ["TEST"] = 0,
            ["timG"] = 0,                                           // noch keine Referenzzeit
            ["dbna"] = 0, ["dbd1"] = 0,                             // eigener Name/Beschreibung statt Textdatenbank
        };
        // Zeitlimit: exe 0xace5c0 schaltet es (ChCD +0x2ba0) nur bei Tempo/Radar ein; CtDw (+0x2c38) trägt immer den Wert der Karte
        bool timed = c.Mode == RaceMode.Speed || c.Mode == RaceMode.SpeedTrap;
        ints["ChCD"] = timed ? 1 : 0; ints["CtDw"] = c.TimeOutMs;
        if (timed) ints["tras"] = c.SpeedMms;
        // Einschränkung (exe 0x975240 -> 0x975d10): VSpe (+0x1170) 0 keine, 2 Fahrzeug (Marke VBra +0x1208, Auto VMod +0x1368),
        // 3 Klasse (Vlt2 +0x1490), 4 genaue Kategorie (Vli2 +0x1528; Werte = DB _vehiclecategory, Tabelle exe 0x1488948)
        switch (c.Restriction)
        {
            case 1:
                ints["VSpe"] = 2; ints["VMod"] = c.RestrictionCarId;
                ints["VBra"] = c.RestrictionBrandId;   // [ANNAHME] gespeichert als DBBrands-Code; die exe hält die Marke als u64 bei VBra +0xa0
                break;
            case 2 when c.RestrictionCat > 0: ints["VSpe"] = 4; ints["Vli2"] = c.RestrictionCat; break;
            case 2 when c.RestrictionClass > 0: ints["VSpe"] = 3; ints["Vlt2"] = c.RestrictionClass; break;
            default: ints["VSpe"] = 0; break;
        }
        var texts = new Dictionary<string, string> { ["mnme"] = c.Name, ["DesC"] = c.Description };
        var gates = new List<Ige2Writer.Gate>();
        var net = _ctx?.Vehicles?.Manager?.Minimap?.Gps.Network;
        int onRoad = 0;
        for (int i = 0; i < c.Checkpoints.Count; i++)
        {
            var g = RoadGate(net, c.Checkpoints[i], GateDirection(c, i));
            if (g.OnRoad) onRoad++;
            gates.Add(g);
        }
        if (onRoad < gates.Count) Log.Warn("Editor: " + (gates.Count - onRoad) + " von " + gates.Count + " Toren ohne Straßenbezug (IA-Netz " + (net == null ? "nicht geladen" : "zu weit weg") + ") – frei gespeichert");
        // Zwischenzeiten der Vorlage (CpTs) gehören zu deren Strecke: leeren (sonst Zeitvergleich an jedem Tor)
        return Ige2Writer.Build(tmpl, ints, texts, gates, TemplateOnly);
    }

    /// <summary>
    /// Werte der Vorlage, die zu deren Rennen gehören und in einer eigenen Challenge leer sein müssen (sonst zeigt das Renn-HUD
    /// fremde Referenzen): Zwischenzeiten CpTs, Referenzpunkte Tempo SPPG, Zielzeit timG, Gegner NbIA/IAS*, Fahrzeuge der Prüfung
    /// VSpe/VBra/VBrM/VMod/CvBr/CvMo, Preise fee1/rewm, Kennungen der Textdatenbank dbna/dbd1.
    /// </summary>
    static readonly string[] TemplateOnly =
        { "CpTs", "SPPG", "timG", "NbIA", "IASA", "IASB", "IASC", "IASI", "IASJ", "VSpe", "VBra", "VBrM", "VMod", "CvBr", "CvMo", "fee1", "rewm", "dbna", "dbd1" };

    /// <summary>
    /// Tor wie die Original-ig2 (CHK2 Placement 0): nächster Punkt des IA-Straßennetzes (ROAD_DETAIL, kubische Bézier-Abschnitte je
    /// Stück). Seite 1 = Fahrt in Straßenrichtung (Lage t), 0 = dagegen (Lage 1 - t); Position = Kurvenpunkt + 0,10 m; Vorwärts =
    /// Tangente in Fahrtrichtung; Hochachse um die Querneigung gerollt (Seite 1: +Neigung, 0: -Neigung). An allen 30 Toren der
    /// Original-ig2 nachgerechnet (Abstand 0,10 m, Richtung und Rollwinkel exakt). Ohne Straße im Umkreis von 12 m: frei (Placement 1).
    /// </summary>
    static Ige2Writer.Gate RoadGate(Paradise.Core.Formats.IaNetwork? net, Vector3 p, Vector3 dir)
    {
        var free = new Ige2Writer.Gate(p, dir, 7f, 0);
        if (net == null) return free;
        const float MaxDist = 12f;
        float best = MaxDist * MaxDist; int bRoad = 0, bPiece = -1, bSeg = 0; float bT = 0f;
        foreach (var road in net.Roads.Values)
            for (int pi = 0; pi < road.Pieces.Count; pi++)
            {
                var P = road.Pieces[pi].Points;
                for (int s = 0; s + 3 < P.Length; s += 3)
                {
                    // grobe Abweisung über die Kontrollpunkte (die Kurve liegt in ihrer Hülle)
                    float minX = MathF.Min(MathF.Min(P[s].X, P[s + 1].X), MathF.Min(P[s + 2].X, P[s + 3].X)) - MaxDist;
                    float maxX = MathF.Max(MathF.Max(P[s].X, P[s + 1].X), MathF.Max(P[s + 2].X, P[s + 3].X)) + MaxDist;
                    float minZ = MathF.Min(MathF.Min(P[s].Z, P[s + 1].Z), MathF.Min(P[s + 2].Z, P[s + 3].Z)) - MaxDist;
                    float maxZ = MathF.Max(MathF.Max(P[s].Z, P[s + 1].Z), MathF.Max(P[s + 2].Z, P[s + 3].Z)) + MaxDist;
                    if (p.X < minX || p.X > maxX || p.Z < minZ || p.Z > maxZ) continue;
                    // abtasten, dann um das beste Stück verfeinern (Abstand in XZ: die Punkte des Editors liegen auf der Karte)
                    int bi = 0; float bd = float.MaxValue;
                    for (int k = 0; k <= 32; k++) { float d = DistXZ(Bez(P, s, k / 32f), p); if (d < bd) { bd = d; bi = k; } }
                    float lo = MathF.Max(0f, (bi - 1) / 32f), hi = MathF.Min(1f, (bi + 1) / 32f);
                    for (int it = 0; it < 24; it++)
                    {
                        float m1 = lo + (hi - lo) / 3f, m2 = hi - (hi - lo) / 3f;
                        if (DistXZ(Bez(P, s, m1), p) < DistXZ(Bez(P, s, m2), p)) hi = m2; else lo = m1;
                    }
                    float t = (lo + hi) * 0.5f, dd = DistXZ(Bez(P, s, t), p);
                    if (dd < best) { best = dd; bRoad = road.Id; bPiece = pi; bSeg = s / 3; bT = t; }
                }
            }
        if (bPiece < 0) return free;
        var piece = net.Roads[bRoad].Pieces[bPiece];
        var pts = piece.Points;
        var pos = Bez(pts, bSeg * 3, bT);
        var tan = BezD(pts, bSeg * 3, bT);
        if (tan.LengthSquared() < 1e-8f) return free;
        tan = Vector3.Normalize(tan);
        var d2 = new Vector3(dir.X, 0f, dir.Z);
        int side = d2.LengthSquared() < 1e-8f || tan.X * d2.X + tan.Z * d2.Z >= 0f ? 1 : 0;
        var fwd = side == 1 ? tan : -tan;
        float bank = 0f;
        if (piece.Bank.Length > bSeg + 1) bank = piece.Bank[bSeg] * (1f - bT) + piece.Bank[bSeg + 1] * bT;
        else if (piece.Bank.Length > bSeg) bank = piece.Bank[bSeg];
        float roll = side == 1 ? bank : -bank;
        var r0 = Vector3.Cross(Vector3.UnitY, fwd);
        if (r0.LengthSquared() < 1e-8f) return free;
        r0 = Vector3.Normalize(r0);
        var u0 = Vector3.Cross(fwd, r0);
        var up = MathF.Cos(roll) * u0 + MathF.Sin(roll) * r0;
        return new Ige2Writer.Gate(pos + new Vector3(0f, 0.10f, 0f), fwd, 7f, 0)
        {
            OnRoad = true, Road = bRoad, Piece = bPiece, Segment = bSeg, Side = side, Along = side == 1 ? bT : 1f - bT, Up = up,
        };
    }

    static float DistXZ(Vector3 a, Vector3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return dx * dx + dz * dz; }

    static Vector3 Bez(Vector3[] P, int s, float t)
    {
        float u = 1f - t;
        return u * u * u * P[s] + 3f * u * u * t * P[s + 1] + 3f * u * t * t * P[s + 2] + t * t * t * P[s + 3];
    }

    static Vector3 BezD(Vector3[] P, int s, float t)
    {
        float u = 1f - t;
        return 3f * u * u * (P[s + 1] - P[s]) + 6f * u * t * (P[s + 2] - P[s + 1]) + 3f * t * t * (P[s + 3] - P[s + 2]);
    }

    /// <summary>Fahrtrichtung am Tor i: entlang der Straßenroute (Start: wie der Richtungspfeil).</summary>
    Vector3 GateDirection(EditorChallenge c, int i)
    {
        if (i == 0) return StartDirection(c);
        var p = c.Checkpoints[i];
        Vector3 dir = c.RoadPath.Count > 1 ? RouteDir(c, p) : p - c.Checkpoints[i - 1];
        dir.Y = 0f;
        return dir.LengthSquared() > 1e-4f ? Vector3.Normalize(dir) : Vector3.UnitZ;
    }

    /// <summary>
    /// Richtung der Straßenroute an einem Punkt: vom nächsten Routenpunkt ~20 m voraus (die ersten Punkte eines Abschnitts liegen
    /// oft dicht beieinander oder auf der Gegenspur – ein Nachbarpunkt allein gibt keine verlässliche Richtung).
    /// </summary>
    static Vector3 RouteDir(EditorChallenge c, Vector3 p)
    {
        var path = c.RoadPath;
        int k = 0; float bd = float.MaxValue;
        for (int n = 0; n < path.Count; n++) { var d = path[n] - p; d.Y = 0f; float m = d.LengthSquared(); if (m < bd) { bd = m; k = n; } }
        for (int n = k + 1; n < path.Count; n++)
        {
            var d = path[n] - path[k]; d.Y = 0f;
            if (d.LengthSquared() >= 400f) return d;
        }
        for (int n = k - 1; n >= 0; n--)
        {
            var d = path[k] - path[n]; d.Y = 0f;
            if (d.LengthSquared() >= 400f) return d;
        }
        return path.Count > 1 ? path[^1] - path[0] : Vector3.Zero;
    }

    /// <summary>
    /// FSCommand "save" (exe 0xad79b5 -> 0xacdb40): Challenge in den Spielstand (Kontext IG2), dann /:SaveMessage = "SAVE SUCCESSFUL"
    /// bzw. "SAVE FAILED", /:IsSaveOver = "1"; geändert-Merker zurück. Neue Challenge: Name nach exe 0x78a170.
    /// </summary>
    void SaveChallenge(VmfPlayer ui)
    {
        var c = _challenge;
        bool ok = false;
        if (c != null)
        {
            if (_fileName == null && TDU2EditorSaves.Count(Nick) >= TDU2EditorSaves.Max) Log.Warn("Editor: schon " + TDU2EditorSaves.Max + " Challenges");
            else
            {
                byte[]? data = null;
                try { data = BuildIge2(c); } catch (Exception ex) { Log.Warn("Editor: .ig2 nicht gebaut: " + ex.Message); }
                if (data != null)
                {
                    string name = _fileName ?? TDU2EditorSaves.NewName(DateTime.Now);
                    ok = TDU2EditorSaves.Write(Nick, name, data);
                    if (ok) { _fileName = name; _modified = false; }
                }
            }
        }
        ui.SetVariable("/:SaveMessage", ok ? "SAVE SUCCESSFUL" : "SAVE FAILED");
        ui.SetVariable("/:IsSaveOver", "1");
        if (ok)
        {
            ui.SetVariable("/:IsOverwriteGME", "1");   // nächstes Speichern fragt "überschreiben?"
            ui.SetValue("/:NbChallCreated", TDU2EditorSaves.Count(Nick));
        }
        // ValidMenu "save" springt danach nach "edit_stop" (Input = true) – die Wurzel steht dort aber schon, ein Sprung auf das
        // eigene Bild führt keine Aktionen aus: Menü (menu_sq) und Eingabe bleiben zu. Wie nach der Testfahrt die Bearbeitung neu
        // öffnen ("open_edit": Karte, Menü, Eingabe).
        _reopenEdit = true;
    }

    // ================================================================== Liste der eigenen Challenges (challenges_list)
    /// <summary>ASFlashList "gme_chall" (chall_list): eine Zeile je gespeicherter Challenge – Symbol der Spielart, Name, Gebiet, Datum.</summary>
    void OnListCreated(VmfPlayer.FlashList l)
    {
        if (GameMode.Current != Gamemode.GAME_MODE_EDITOR || _ui == null || l.Name != "gme_chall") return;
        _list = l;
        l.ColumnVars = new[] { "name", "date" };
        l.Rows.Clear(); _listFiles.Clear(); _listItems.Clear();
        foreach (var name in TDU2EditorSaves.List(Nick))
        {
            var data = TDU2EditorSaves.Read(Nick, name);
            var c = data != null ? EditorChallenge.FromIge2(Ige2File.Parse(data)) : null;
            _listFiles.Add(name); _listItems.Add(c);
            l.Rows.Add(new[] { c?.Name ?? name, DateOf(name) });
        }
        _ui.ListRefresh(l);
        _ui.ListLoaded(l);
        Log.Info("Editor: Liste mit " + l.Rows.Count + " Challenges");
    }

    /// <summary>Zeile neu beschrieben: Symbol der Spielart (content/ic) und Gebiet (content/area: h = Oahu, i = Ibiza).</summary>
    void OnListLineDrawn(VmfPlayer.FlashList l, int line, int index)
    {
        if (_ui == null || l != _list) return;
        string lp = "/chall_list/line" + line + "/content";
        var c = index >= 0 && index < _listItems.Count ? _listItems[index] : null;
        if (c == null) return;
        _ui.GotoLabel(lp + "/ic", c.ModeName, false);
        _ui.GotoLabel(lp + "/area", c.Island == 0 ? "i" : "h", false);
    }

    /// <summary>"Ige/JJJJMMTThhmmss.ig2" -> "TT/MM/JJJJ".</summary>
    static string DateOf(string name)
    {
        string f = System.IO.Path.GetFileNameWithoutExtension(name);
        return f.Length >= 8 ? f.Substring(6, 2) + "/" + f.Substring(4, 2) + "/" + f.Substring(0, 4) : f;
    }

    int SelectedIndex() => _list != null ? _list.Selected : -1;

    /// <summary>"edch": gewählte Challenge laden (wie exe 0xad0c40: IsOverwriteGME "1", Undo/Redo leer), der Film geht nach open_edit.</summary>
    void LoadSelected(VmfPlayer ui)
    {
        int i = SelectedIndex();
        if (i < 0 || i >= _listItems.Count || _listItems[i] == null) { Log.Warn("Editor: keine Challenge gewählt"); return; }
        ClearPoints(ui); _undo.Clear(); _redo.Clear();
        _challenge = _listItems[i]; _fileName = _listFiles[i]; _modified = false; _uploadable = false;
        ResolveRestriction(_challenge!);
        ui.SetVariable("/:WhatGM", _challenge!.ModeName);
        ui.SetVariable("/:WhatGMType", _challenge.ModeType);
        ui.SetVariable("/:IsOverwriteGME", "1");
        ui.SetValue("/:LockUndo", true); ui.SetValue("/:LockRedo", true);
        TrackChanged(ui);
        _modified = false; _undo.Clear();
        Log.Info("Editor: Challenge geladen: " + _fileName + " \"" + _challenge.Name + "\"");
    }

    void DeleteSelected(VmfPlayer ui)
    {
        int i = SelectedIndex();
        if (i < 0 || i >= _listFiles.Count) return;
        if (TDU2EditorSaves.Delete(Nick, _listFiles[i]))
        {
            if (_fileName == _listFiles[i]) _fileName = null;
            Log.Info("Editor: Challenge gelöscht: " + _listFiles[i]);
            ui.SetValue("/:NbChallCreated", TDU2EditorSaves.Count(Nick));
            if (_list != null) OnListCreated(_list);
        }
    }

    // ================================================================== Testfahrt (test)
    /// <summary>Mindestens nötige Punkte: Zeitfahren Start + Ziel, Radar Start + 1 Radar, Tempo der Start.</summary>
    bool TrackValid(EditorChallenge c) => c.Checkpoints.Count >= (c.Mode == RaceMode.Speed ? 1 : 2);

    /// <summary>
    /// FSCommand "test" (exe 0xad7c68): /:IsVehicleOK (Fahrzeug erlaubt – ohne Einschränkung immer), dann die Challenge über den
    /// Renn-Ablauf fahren (exe 0xad27c0 -> Challenge-Verwaltung 0x44dad6). Die Challenge läuft aus ihrer .ig2 wie jedes Rennen
    /// (RaceRepository.FromIge2); danach zurück in den Editor mit /:BackFromTest.
    /// </summary>
    void StartTest(VmfPlayer ui)
    {
        var c = _challenge; var ctx = _ctx;
        bool vehOk = c == null || VehicleOk(c);
        ui.SetValue("/:IsVehicleOK", vehOk);
        if (!vehOk) { Log.Info("Editor: Testfahrt – Fahrzeug passt nicht zur Einschränkung"); return; }
        if (c == null || ctx == null || !TrackValid(c)) { FrontendSounds.Play("wrong"); Log.Info("Editor: Testfahrt nicht möglich (zu wenige Punkte)"); return; }
        byte[]? data = null;
        try { data = BuildIge2(c); } catch (Exception ex) { Log.Warn("Editor: .ig2 für die Testfahrt nicht gebaut: " + ex.Message); }
        if (data == null) { FrontendSounds.Play("wrong"); return; }
        var def = RaceRepository.FromIge2(Ige2File.Parse(data), "GME_" + (_fileName ?? "neu"), null);
        def.Laps = c.IsLoop ? System.Math.Max(1, c.Laps) : 1;
        def.RoadPath = new List<Vector3>(c.RoadPath.Count > 1 ? c.RoadPath : c.Checkpoints);
        def.RoadPathSeg = new List<int>(c.RoadSegments);
        var dir = StartDirection(c);
        def.StartHeading = MathF.Atan2(dir.X, dir.Z);
        Log.Info("Editor: Testfahrt " + c.ModeName + ", " + c.Checkpoints.Count + " Punkte");
        _view.End();
        AudioEngine.Paused = false;
        _editTrack = false; _mapControls = false;
        // eigene Challenge: keine Referenzen/Gegner/Prüfungsfahrzeuge (HUD zeigte sonst Werte der Vorlage)
        def.RefSplitsMs.Clear(); def.TargetTimeMs = 0; def.SilverTimeMs = 0; def.BronzeTimeMs = 0;
        def.SpeedPointsRef = 0f; def.BotCount = 0; def.Opponents.Clear(); def.CarId = 0; def.RewardMoney = 0;
        // Fahrlinie auf der Straße nur mit "Waymarked path" (Pflichtweg, ig2 "forc")
        GAME_MODE_CHAMPIONSHIP.BeginTest(ctx, def, c.ForcedPath, completed => { _backFromTest = true; _testCompleted = completed; GameMode.Set(Gamemode.GAME_MODE_EDITOR); });
    }

    /// <summary>Zurück aus der Testfahrt: Kartenansicht, Film nach "open" mit /:BackFromTest (Film: GME_Step "edit" -> open_edit).</summary>
    void ReturnFromTest(IGameModeContext ctx)
    {
        _backFromTest = false;
        _ui = ctx.Open(ScreenDefs.Editor);
        var ui = _ui;
        if (ui == null) { GameMode.Set(Gamemode.GAME_MODE_FREERIDE); return; }
        ui.Visible = true;
        _leaving = false; _editTrack = false; _mapControls = false; _cursorType = "";
        var vm = ctx.Vehicles?.Manager; var s = ctx.Terrain?.Streamer; var cam = ctx.Terrain?.Camera;
        // Auto nach der Testfahrt anhalten (Ende der Fahrt lässt es sonst weiterrollen; die Karte hält nur die Simulation an)
        if (vm?.Current != null) { vm.ExternalControl = false; vm.TeleportSafe(vm.PositionGame, vm.ForwardGame); }
        if (s != null && cam != null)
            _view.Begin(ctx, _challenge!.Checkpoints.Count > 0 ? s.GameToRender(_challenge.Checkpoints[0]) : vm?.Current != null ? s.GameToRender(vm.PositionGame) : cam.Position, _view.Zoom);
        PlayerCarInfo.Apply(ui, ctx);
        AudioEngine.Paused = true;
        ui.MouseEnabled = true;
        ui.SetValue("/:BackFromTest", true);
        TrackChanged(ui);
        // Hochladen erst nach einer bis ins Ziel gefahrenen Testfahrt (Film: LockUpload -> Dialog "upload_locked"); jede spätere
        // Änderung an Strecke oder Parametern sperrt es wieder (TrackChanged/ParaValidate)
        _uploadable = _testCompleted;
        _testCompleted = false;
        ui.SetValue("/:LockUpload", !_uploadable);
        Log.Info("Editor: Testfahrt " + (_uploadable ? "beendet – Hochladen freigegeben" : "abgebrochen – Hochladen bleibt gesperrt"));
        ui.GotoLabel("/", "open", true);
        Log.Info("Editor: zurück aus der Testfahrt");
    }

    /// <summary>
    /// Fehlersuche (F11): Variablen der Parameter-Zeilen und der Baum von /parameters ins Log – eine Taste beim offenen
    /// Einstellungsmenü liefert alles, um Anzeigefehler der Zeilen (falscher Text, überlagerte Regler) ohne Raten zu finden.
    /// </summary>
    static void DumpParameters(VmfPlayer ui)
    {
        var sb = new System.Text.StringBuilder("Editor F11 – Parameter:\n");
        foreach (var v in new[] { "VarToFocus", "NbLaps", "TrafficValue", "PenaltyValue", "IsForcedPath", "IsForcedPathStr", "RestrictionTypeStr",
                                  "RestrictionTypeFilter", "RestrictionCategory", "TimeOut", "SpeedToReach", "SpeedTrapMin", "Input", "Clip", "GME_Step", "WhatGM" })
            sb.Append("  /:").Append(v).Append(" = ").Append(ui.GetVariable("/:" + v) ?? "(undef)").Append('\n');
        for (int i = 0; i < 10; i++)
            foreach (var t in new[] { "", "/choicebox", "/gauge", "/choicebox_ic" })
            {
                string path = "/parameters/p" + i + t;
                if (ui.Find(path) == null) continue;
                sb.Append("  ").Append(path).Append(": id=").Append(ui.GetVariable(path + ":id") ?? "-")
                  .Append(" cmd=").Append(ui.GetVariable(path + ":cmd") ?? "-")
                  .Append(" varToFocus=").Append(ui.GetVariable(path + ":varToFocus") ?? "-")
                  .Append(" frame=").Append(ui.GetVariable(path + ":frame") ?? "-").Append('\n');
            }
        Log.Info(sb.ToString());
        Log.Info("Editor F11 – Baum:\n" + ui.DumpTree(7));
    }

    /// <summary>Clip steht still auf "default" (geschlossenes Menü).</summary>
    static bool AtDefault(VmfPlayer.Instance i) => !i.Playing && i.Timeline.Labels.TryGetValue("default", out int f) && i.Frame == f;

    /// <summary>
    /// Tasten an den Film. openGME setzt _root.Clip auf die Wurzel; deren A() ist das A der Streckenbearbeitung (insertPoint/putPoint/
    /// movePoint) – das Startmenü wählt über A() des Clips /menu (setBt -> BtValid -> _root.ValidMenu), die Spielart-Wahl über A()/B()/
    /// Move() von /gm_selector. Darum je nach offenem Teil gezielt an den Clip; B() der Wurzel verteilt nach _root.GME_Step.
    /// </summary>
    void HandleKeys(VmfPlayer ui)
    {
        string? clip = null;
        var sel = ui.Find("/gm_selector");
        if (sel?.Sprite != null && sel.Visible && ui.IsShown(sel.Sprite) && !AtDefault(sel.Sprite)) clip = "/gm_selector";
        else
        {
            string step = ui.GetVariable("/:GME_Step") ?? "";
            var menu = ui.Find("/menu");
            if ((step == "root" || step == "edit") && menu?.Sprite != null && ui.IsShown(menu.Sprite)) clip = "/menu";
        }
        void Send(string fn, params object[] a)
        {
            if (!(clip != null && ui.CallClip(clip, fn, a))) ui.Input(fn, a);
        }
        if (!_mapControls)
        {
            if (FlashNav.Pressed(FlashKey.Up)) Send("Move", "up");
            if (FlashNav.Pressed(FlashKey.Down)) Send("Move", "down");
            if (FlashNav.Pressed(FlashKey.Left)) Send("Move", "left");
            if (FlashNav.Pressed(FlashKey.Right)) Send("Move", "right");
        }
        if (FlashNav.Pressed(FlashKey.Confirm)) Send("A");
        // Strecke: Klick auf die Karte (ohne Ziehen, nicht über Knöpfen des Films) = A, wie die Eingabetaste
        if (_editTrack)
        {
            var mp = MapView.MousePixel();
            if (FlashInput.GetMouseButtonDown(0)) _downMouse = mp;
            if (FlashInput.GetMouseButtonUp(0) && (mp - _downMouse).LengthSquared() < 36f && !ui.MouseOverButton)
            {
                ui.ConsumeClick();
                ui.Input("A");
            }
        }
        bool typing = ui.InputFocus != null;   // virtuelle Tastatur (/vk): Buchstaben gehören dem Textfeld
        if (!typing && FlashNav.Pressed(FlashKey.X)) ui.Input("X");
        if (!typing && FlashNav.Pressed(FlashKey.Y)) ui.Input("Y");
        if (FlashNav.Pressed(FlashKey.Back))
        {
            if (!(clip == "/gm_selector" && ui.CallClip(clip, "B"))) ui.Input("B");
        }
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to == Gamemode.GAME_MODE_PAUSE) return;
        _view.End();
        AudioEngine.Paused = false;
        _mapControls = false;
        _editTrack = false;
        if (_ui != null) { ClearPoints(_ui); _ui.Visible = false; }   // wie die große Karte: Film aus, nimmt sonst Tasten (ESC) weiter an
        Minimap.SetMapRacePath(null);
        _ui = null;
    }

    void OnCommand(string cmd, string args)
    {
        if (GameMode.Current != Gamemode.GAME_MODE_EDITOR || _ui == null) return;
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (c.Length == 0 || c.StartsWith("sndp:")) return;   // Klänge (FrontendSounds); leere Befehle des Films
        switch (c)
        {
            case "dbxo": FlashDialog.Open(_ui); return;   // max_chall / no_chall_created / confirm_quit … (general.bnk)
            case "nech": NewChallenge(_ui); return;        // gm_selector AfterValid
            case "mcof": _mapControls = false; _dragging = false; return;   // exe 0xad7428: +0x1be014 = 1 (Karte aus)
            case "mcon": _mapControls = true; return;                       // exe 0xad74b0: +0x1be014 = 0 (Karte an)
            case "zoin": SetZoom(_ui, _view.Zoom - 1); return;               // setZoom("in") (exe 0xad7d99)
            case "zoou": SetZoom(_ui, _view.Zoom + 1); return;               // setZoom("out") (exe 0xad7d79)
            // exe 0xad6cc5: merkt die Challenge als bearbeitete (+0x1c5ca8 = Challenge, +0x1c5ca4/+0x1c5cac = 0, +0x1c5ca0 = 1) –
            // hier hält _challenge diesen Zustand
            case "cech": return;
            case "gmed:card:open": FillCard(_ui); return;
            case var p when p.StartsWith("gmed:para:"): OnPara(_ui, p.Substring(10)); return;
            // exe 0x977269: vor dem Upload-Fenster /:UploadForbidden (gesperrter Spielhallen-Spot) und /:SpotLockedtext
            case "gmed:upld:chck":
                _ui.SetValue("/:UploadForbidden", false);
                _ui.SetVariable("/:SpotLockedtext", "");
                return;
            case var u when u.StartsWith("gmed:upld"):
                Log.NotImplemented("Editor Hochladen: " + u + " (Server-Anbindung an den eigenen TDU2Server folgt)");
                return;
            case "emin": BeginEditTrack(_ui); return;        // StartEditTrack (exe 0xace1f0)
            case "emou": EndEditTrack(_ui); return;          // StopEditTrack (exe 0xaccd90)
            case "chkp": CheckSpot(_ui, false); return;      // putPoint: Platz prüfen (exe 0xad6f27)
            case "chki": CheckSpot(_ui, true); return;       // insertPoint: Platz prüfen (exe 0xad6d35)
            case "setp": PutPoint(_ui); return;              // Punkt setzen bzw. verschobenen Punkt absetzen (exe 0xad7acc)
            case "insp": InsertPoint(_ui); return;           // Punkt in die Strecke einfügen (exe 0xad734f)
            case "delp": DeletePoint(_ui); return;           // Punkt löschen (exe 0xad702f)
            case "movp": _movingPoint = _hoverPoint; _cursorType = ""; return;   // Punkt aufnehmen (exe 0xad756e)
            case "cmov": _movingPoint = -1; _cursorType = ""; return;            // Verschieben abbrechen (exe 0xad6fd3)
            case "loop": ToggleLoop(_ui); return;            // Rundkurs an/aus (exe 0xad7415)
            case "sens": TurnStart(_ui); return;             // Startrichtung um 180° drehen (exe 0xad7b1c -> 0x97e720, Drehung um π)
            case "gmed:undo": UndoRedo(_ui, _undo, _redo); return;
            case "gmed:redo": UndoRedo(_ui, _redo, _undo); return;
            // [OFFEN] exe-Zweige noch nicht verfolgt: inon (nach jeder Aktion), zoed (Zoom beim Bearbeiten), gmeh (Hilfe der Strecke)
            case "inon": case "zoed": case "gmeh": return;
            case "tfoc": return;
            case "save": SaveChallenge(_ui); return;                                   // exe 0xad79b5
            case "qugm": _ui.SetVariable("/:IsModified", _modified ? "1" : "0"); return; // exe 0xad795d: vor dem Verlassen fragen
            case "test": StartTest(_ui); return;                                       // exe 0xad7c68
            case "edch": LoadSelected(_ui); return;                                    // Challenge aus der Liste bearbeiten (exe 0xad700b)
            case "delc": case "gmed:delc": DeleteSelected(_ui); return;               // Challenge aus der Liste löschen   // Texteingabe: der Film ruft danach SetInputFocus("/:EditableText") selbst (VM: Tastatur ins Feld)
            case "gofr":                                   // Bild 51 nach "close": Editor verlassen
                _leaving = true;
                Log.Info("Editor: geschlossen");
                GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
                return;
            default:
                Log.NotImplemented("Editor FSCommand nicht portiert: " + cmd + (string.IsNullOrEmpty(args) ? "" : " " + args));
                return;
        }
    }
}

/// <summary>
/// Challenge des Editors (exe: Challenge-Daten ab Objekt +0x58; Name +0x830 und Beschreibung +0x900 je höchstens 0x40 Zeichen,
/// Spielart +0x98, Typ +0x120, Rundkurs +0x668).
/// </summary>
public sealed class EditorChallenge
{
    public string Name = "", Description = "";
    /// <summary>_root.WhatGM ("timeAttack", "speed", "speedtrap" …) und _root.WhatGMType ("solo", "multi", "club").</summary>
    public string ModeName = "timeAttack", ModeType = "solo";
    public RaceMode Mode = RaceMode.TimeAttack;
    public bool IsLoop;
    /// <summary>Startrichtung umgedreht (Y/"sens" bei Tempo/Radar).</summary>
    public bool StartReversed;
    public float LengthM;
    /// <summary>Runden beim Rundkurs (exe +0xb18, 1..20), Pflichtweg (+0x10e0), Zeitlimit ms (+0x2c38, Tempo/Radar).</summary>
    public int Laps = 1;
    public bool ForcedPath;
    public int TimeOutMs = 160000;   // exe Reset 0xb376c0: CtDw 0x27100
    /// <summary>Tempo: Zielgeschwindigkeit, Radar: Mindestgeschwindigkeit (ig2 "tras", mm/s; exe Karte +0x14).</summary>
    public int SpeedMms;
    /// <summary>Fahrzeug-Einschränkung 0 keine, 1 Fahrzeug, 2 Kategorie (exe Karte +0x5c) mit Gruppe/Fahrzeug.</summary>
    public int Restriction;
    public string RestrictionCategory = "all", RestrictionVehicle = "", RestrictionBrand = "", RestrictionCarFile = "";
    /// <summary>Fahrzeug (DB _id_car, Marke _car_brand), genaue Kategorie (DB _vehiclecategory, ig2 Vli2) bzw. Klasse (ig2 Vlt2).</summary>
    public int RestrictionCarId, RestrictionBrandId, RestrictionCat, RestrictionClass;

    /// <summary>exe 0x4c4540: Klasse einer Kategorie (1 A, 2 B, 3 C, 4 M, 5 R, 6 sonst) für Kategorie 0..28.</summary>
    public static readonly int[] CatClass = { 0, 1, 1, 1, 1, 1, 1, 1, 6, 2, 2, 2, 2, 6, 3, 3, 3, 3, 4, 4, 6, 6, 3, 5, 5, 5, 5, 5, 5 };
    static readonly string[] ClassLabel = { "all", "A", "B", "C", "M", "R", "all" };
    /// <summary>Verkehr/Strafe 0..3 (Regler des Films, /:TrafficValue, /:PenaltyValue). [OFFEN] Startwerte der exe (+0xbc0/+0x1d48).</summary>
    public int Traffic, Penalty;
    /// <summary>Gesetzte Punkte (Spielkoordinaten, auf der Straße).</summary>
    public readonly List<Vector3> Checkpoints = new();
    /// <summary>Straßenroute durch die Punkte (RaceRoadPath.Route) und Abschnitt je Routenpunkt.</summary>
    public List<Vector3> RoadPath = new();
    public List<int> RoadSegments = new();

    /// <summary>
    /// exe 0xace5c0: neue Challenge – Spielart aus WhatGM/WhatGMType, Name = Text 0x1c96 ("Herausforderung von §PLAYER_NAME§") mit dem
    /// Spielernamen, Beschreibung = Text 0x1394; beide auf 0x40 Zeichen (63 + Ende) begrenzt.
    /// </summary>
    public static EditorChallenge CreateNew(string whatGm, string whatGmType)
    {
        var c = new EditorChallenge { ModeName = whatGm, ModeType = whatGmType, Mode = ModeOf(whatGm) };
        string player = TDU2GameData.PlayerName;
        c.Name = Clip(RaceTexts.Get(0x1c96, "§PLAYER_NAME§").Replace("§PLAYER_NAME§", player), 63);
        c.Description = Clip(RaceTexts.Get(0x1394, ""), 63);
        return c;
    }

    /// <summary>Spielart-Name des Films -> RaceMode (gm-Werte der ig2 "gmth": 0 Zeitfahren, 1 Radar, 3 Rennen, 10 Tempo …).</summary>
    static RaceMode ModeOf(string gm) => gm switch
    {
        "speed" => RaceMode.Speed,
        "speedtrap" => RaceMode.SpeedTrap,
        "race" => RaceMode.Race,
        _ => RaceMode.TimeAttack,
    };

    static string Clip(string s, int max) => s.Length > max ? s.Substring(0, max) : s;

    /// <summary>Insel (ig2 "isla").</summary>
    public int Island = 1;

    /// <summary>Gespeicherte Challenge (.ig2 im Spielstand) zurück in den Editor (Punkte = Tore, Werte aus den Tags).</summary>
    public static EditorChallenge FromIge2(Paradise.Core.Formats.Ige2File f)
    {
        var mode = (RaceMode)f.Mode;
        var c = new EditorChallenge
        {
            Mode = mode,
            ModeName = mode switch { RaceMode.Speed => "speed", RaceMode.SpeedTrap => "speedtrap", RaceMode.Race => "race", _ => "timeAttack" },
            ModeType = "solo",
            Name = f.Setting("mnme")?.AsText ?? "",
            Description = f.Setting("DesC")?.AsText ?? "",
            IsLoop = f.IsCircuit,
            Laps = System.Math.Max(1, f.Laps),
            ForcedPath = f.Int("forc") != 0,
            Traffic = f.Int("ChTR") != 0 ? f.Int("trat") : 0,
            Penalty = f.Int("offR") != 0 ? f.Int("PeCr") : 0,
            TimeOutMs = f.Int("CtDw") > 0 ? f.Int("CtDw") : 160000,
            SpeedMms = f.Int("tras"),
            LengthM = f.Length,
            Island = f.Island,
        };
        // Einschränkung (VSpe, siehe BuildIge2)
        switch (f.Int("VSpe"))
        {
            case 2: c.Restriction = 1; c.RestrictionCarId = f.Int("VMod"); c.RestrictionBrandId = f.Int("VBra"); break;
            case 3:
                c.Restriction = 2; c.RestrictionClass = f.Int("Vlt2");
                c.RestrictionCategory = c.RestrictionClass >= 0 && c.RestrictionClass < ClassLabel.Length ? ClassLabel[c.RestrictionClass] : "all";
                break;
            case 4: c.Restriction = 2; c.RestrictionCat = f.Int("Vli2"); c.RestrictionCategory = PlayerCarInfo.GroupLabel(c.RestrictionCat); break;
        }
        foreach (var cp in f.Checkpoints) c.Checkpoints.Add(cp.Position);
        if (c.Checkpoints.Count > 0 && (mode == RaceMode.Speed || mode == RaceMode.SpeedTrap))
        {
            // Startrichtung: gespeichert als Vorwärtsachse des Starttors – umgedreht, wenn sie gegen die Straße zum nächsten Punkt zeigt
            var fwd = f.Checkpoints[0].Forward;
            if (f.Checkpoints.Count > 1) { var d = f.Checkpoints[1].Position - f.Checkpoints[0].Position; c.StartReversed = d.X * fwd.X + d.Z * fwd.Z < 0f; }
            else c.StartHeadingSaved = fwd;
        }
        return c;
    }

    /// <summary>Gespeicherte Startrichtung bei nur einem Punkt (Tempo).</summary>
    public Vector3? StartHeadingSaved;
}
