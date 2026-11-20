using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Online;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using Paradise.Game.Utils;
using System.Numerics;
using AudioEngine = Paradise.Core.Audio.AudioEngine;
using CarDatabase = Paradise.Core.Vehicles.CarDatabase;
using CarPhysics = Paradise.Core.Vehicles.CarPhysics;
using FlashHost = Paradise.Core.Flash.FlashHost;
using FlashInput = Paradise.Core.Flash.FlashInput;
using FlashLocalization = Paradise.Core.Flash.FlashLocalization;
using FlashScreen = Paradise.Core.Flash.Screen;
using FlashTexture = Paradise.Core.Flash.Texture2D;
using FlashTextures = Paradise.Core.Flash.FlashTextures;
using FrontendSounds = Paradise.Core.Audio.FrontendSounds;
using FVec = Paradise.Core.Flash.Vector2;
using Gps = Paradise.Core.Vehicles.Gps;
using Minimap = Paradise.Core.Vehicles.Minimap;
using Spot = Paradise.Core.Formats.Spot;
using SpotsFile = Paradise.Core.Formats.SpotsFile;
using TDU2Bookmarks = Paradise.Core.Vehicles.TDU2Bookmarks;
using VmfPlayer = Paradise.Core.Flash.VmfPlayer;
using CultureInfo = System.Globalization.CultureInfo;
using RaceMode = Paradise.Core.Racing.RaceMode;
using RaceTexts = Paradise.Core.Racing.RaceTexts;
using ChallengeModes = Paradise.Game.Racing.ChallengeModes;
using RaceRoadPath = Paradise.Game.Racing.RaceRoadPath;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Spielmodus Große Karte (Taste M) – steuert alles der Karte. Port von Unity TDU2BigMap (Client/Assets/TDU2Map/Runtime/UI/TDU2BigMap.cs):
///   * keine Bildkarte – die Kamera schaut von oben auf die echte 3D-Insel. Zoomstufen _root.WhatZoom 4 (Insel) .. 1 (nah),
///     je Stufe Sichtbreite und Neigung wie im Unity-Projekt. In der Engine zeichnet die normale Weltkamera (TerrainModule) aus
///     dieser Lage direkt ins Bild (Unity: eigene Kartenkamera bzw. RenderTexture).
///   * darüber frontend/&lt;res&gt;/map.bnk (map.vmf): "map_open" beim Öffnen, Zoom über setZoom("in"/"out") -> FSCommand zoin/zoou,
///     "close" spielt die Schließanimation, "gofr" = zurück ins Fahren.
/// Bedienung wie Unity: WASD/Pfeile oder Maus ziehen = verschieben, Mausrad/Bild↑↓/+/-/E/Q = Zoom, Leertaste = zum Fahrzeug,
/// M = schließen, Esc/Rücktaste = zurück (Film) bzw. schließen.
/// Portiert: Kartengelände (MapStreamer), Straßennetz (TDU2Minimap.DrawMapRoads), Spots mit Filter, Spot unter dem Cursor + Tooltip-Titel.
/// Wettbewerbsmodus (Meisterschaften, Gamemodes/Utils/Championships): map.vmf IsContest, "co_direct_enter" vom Treffpunkt,
/// FSCommands coin (Wettbewerb betreten) / coou (verlassen) / giup (aufgeben) / mcen bzw. goin (Rennen starten) – nur die Rennen
/// der Meisterschaft, Haken/Medaille gefahrener Rennen, Wertung im Tooltip (setContests/setRankings).
/// </summary>
public class GAME_MODE_MAP : GameModeHandler
{
    IGameModeContext _ctx = null!;
    VmfPlayer? _ui;

    // ------------------------------------------------------------------ Spielmodus
    public override Gamemode Mode => Gamemode.GAME_MODE_MAP;
    static readonly ScreenDef[] _screens = { ScreenDefs.Map };
    public override IReadOnlyList<ScreenDef> Screens => _screens;

    /// <summary>Öffnen mit der Taste "map" (M) aus der Freifahrt; keine Welt/kein Film: zurück ins Fahren.</summary>
    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        _view.Bind(ctx);
        Attach(ctx.Open(ScreenDefs.Map));
        int contest = _reqContest; bool direct = _reqDirect;
        _reqContest = -1; _reqDirect = false;
        if (!IsOpen && !Open(contest, direct)) GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    // ------------------------------------------------------------------ Wettbewerbsmodus (Meisterschaft)
    static int _reqContest = -1;
    static bool _reqDirect;

    /// <summary>
    /// Karte im Wettbewerbsmodus einer Meisterschaft öffnen (Index wie <see cref="Championships.Cups"/>): zeigt nur deren Rennen,
    /// Auswahl startet das Rennen. direct = vom Treffpunkt aus (zurück schließt die Karte, ContestDirectAccess). Unity OpenContest.
    /// </summary>
    public static bool OpenContest(int cupIndex, bool direct)
    {
        if (cupIndex < 0 || cupIndex >= Championships.Cups.Count || GameMode.Current == Gamemode.GAME_MODE_MAP) return false;
        _reqContest = cupIndex; _reqDirect = direct;
        GameMode.Set(Gamemode.GAME_MODE_MAP);
        return GameMode.Current == Gamemode.GAME_MODE_MAP;
    }

    int _contestCup = -1;            // Meisterschaft, deren Rennen gezeigt werden (-1 = normale Karte)
    bool _contestDirect;             // vom Treffpunkt aus geöffnet: zurück = Karte schließen
    (int cup, int race) _pendingStart = (-1, -1);   // nach dem Schließen dieses Rennen starten

    /// <param name="setFlag">false beim direkten Öffnen vom Treffpunkt/nach dem Rennen: /:IsContest setzt dann der Film selbst
    /// (map.vmf Bild 42: /:ContestDirectAccess = 1 -> "co_direct_enter" -> "contest" setzt IsContest = 1).</param>
    void EnterContest(int cup, bool setFlag = true)
    {
        var mc = Championships.MapCupAt(cup);
        var ui = _ui; var s = _ctx.Terrain?.Streamer;
        if (mc == null || ui == null) return;
        _contestCup = cup;
        HideTooltip(); _extended = false;
        SetMapRacePath(null);
        SetContestVars(cup);
        ui.SetVariable("/:ContestName", mc.Name);
        ui.SetVariable("/:ContestType", mc.ContestType);
        // Meisterschafts-Seite: Bild der Meisterschaft in championship/pages/cont/p/picture/lure_c (exe 0xb28f20, Bild über 0xb28c30)
        ui.SetClipTexture("/championship/pages/cont/p/picture/lure_c", CupPicture(mc));
        if (setFlag) ui.SetValue("/:IsContest", true);
        BuildSpots();
        // Kamera auf die Rennen der Meisterschaft
        if (s != null)
        {
            var min = s.GameToRender(mc.Hub); var max = min;
            foreach (var r in Championships.MapRaces(cup)) { var p = s.GameToRender(r.Start); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            _view.Target = (min + max) * 0.5f; _view.Target.Y = SeaY();
            float ext = MathF.Max(max.X - min.X, max.Z - min.Z) * 1.35f + 400f;
            int z = 1; while (z < 4 && _view.WidthFor(z) < ext) z++;
            SetZoom(z);
        }
        Log.Info("Karte: Wettbewerb " + mc.Name + " (" + mc.RaceCount + " Rennen, " + mc.RacesDone + " gefahren)" + (_contestDirect ? " vom Treffpunkt" : ""));
    }

    List<Championships.MapRace>? _raceCache; int _raceCacheCup = -1, _raceCacheFrame = -1;
    /// <summary>Rennen eines Wettbewerbs-Spots (Status/Platz), sonst null.</summary>
    Championships.MapRace? RaceOf(MapSpot? ms)
    {
        if (ms == null || ms.cup < 0 || ms.race < 0) return null;
        if (_raceCacheCup != ms.cup || _raceCacheFrame != _openFrames) { _raceCache = Championships.MapRaces(ms.cup); _raceCacheCup = ms.cup; _raceCacheFrame = _openFrames; }
        return ms.race < _raceCache!.Count ? _raceCache[ms.race] : null;
    }

    void LeaveContest()
    {
        if (_contestCup < 0) return;
        _contestCup = -1;
        SetMapRacePath(null);
        HideTooltip(); _extended = false;
        _ui?.SetValue("/:IsContest", false);
        BuildSpots();
    }

    /// <summary>Variablen für Tooltip/Infos der Meisterschaft (infos.setContests, setRankings, Restriction).</summary>
    void SetContestVars(int cup)
    {
        var ui = _ui; var mc = Championships.MapCupAt(cup);
        if (ui == null || mc == null) return;
        var races = Championships.MapRaces(cup);
        // exe 0xb18f60: /:nb_chall_done (gefahrene Rennen, 0x435d2d) und /:nb_chall (Rennen, 0x40bc53) – der Film setzt daraus
        // "Challenges done n/m" zusammen (ohne nb_chall_done stand nur "/6" da); /:WhatCup = Medaille der Meisterschaft (0x42564e)
        ui.SetValue("/:nb_chall_done", mc.RacesDone);
        ui.SetValue("/:nb_chall", System.Math.Min(8, races.Count));
        ui.SetVariable("/:WhatCup", mc.Done && mc.Place == 1 ? "gold" : mc.Done && mc.Place == 2 ? "silver" : mc.Done && mc.Place == 3 ? "bronze" : "empty");
        for (int i = 0; i < 8; i++)
        {
            ui.SetVariable("/:chall" + i + "_type", i < races.Count ? races[i].Label : "empty");
            ui.SetValue("/:chall" + i + "_state", i < races.Count ? races[i].State : 0);
        }
        var st = Championships.MapStandings(cup);
        int me = 0;
        for (int i = 0; i < 8; i++)
        {
            bool has = i < st.Count;
            ui.SetVariable("/:rank" + i + "_pos", has ? (i + 1).ToString() : "");
            ui.SetVariable("/:rank" + i + "_name", has ? st[i].name : "");
            ui.SetVariable("/:rank" + i + "_pts", has ? st[i].points + " " + Txt("m_Pts_", "Pkt.") : "");
            if (has && st[i].player) me = i;
        }
        ui.SetValue("/:PlayerRank", me);
        ui.SetValue("/:IsResultsDone", mc.RacesDone > 0 ? 1 : 0);
        ui.SetValue("/:SelectedContestGroup", 1);
        ui.SetValue("/:num_contest", 1);
        ui.SetVariable("/:GoldReward", mc.ChampReward > 0 ? Money(mc.ChampReward) : "");
        ui.SetVariable("/:RestrictionType", "category");
        ui.SetVariable("/:GroupRestriction", mc.Group ?? "");
        ui.SetVariable("/:ChampRival", Championships.RivalName(cup) ?? "");
        ui.SetVariable("/:LevelCount", mc.RacesDone + "/" + mc.RaceCount);
    }

    /// <summary>
    /// Tooltip eines Rennens wie exe 0xb19600 (Infos einer Challenge auf der großen Karte):
    ///  /:IsSolo (solo = 1), /:IsDone "on"/"off", /:GMDescription = Beschreibung der Spielart (Text 0xc2/0xc7/0xc5/0x88e),
    ///  /:Distance = "%.1f %s" (ig2 "leme"/1000, Einheit Text 0x2e6, exe 0xae2940), /:WhatCup = Medaille ("gold"/"silver"/"bronze"/
    ///  "empty", exe 0x42564e), /:GoldReward = Preisgeld, /:NbGames = "--" (solo), dann je Spielart (Sprungtabelle 0xb19efc):
    ///  Zeitfahren/Rennen /:NbLaps ("%d %s", Text 0x10d2 "Runde" bzw. 0x44 "Runden", Punkt-zu-Punkt "--", exe 0x4c9b80),
    ///  Tempo /:SpeedMin = "%.2f %s" (tras in der Anzeigeeinheit), Radar /:NbTraps = "".
    /// Die Regel-Symbole rules_ic_0..7 (Verkehr/Polizei/Wetter) sind noch nicht portiert.
    /// </summary>
    void SetChallengeVars(Championships.MapRace r, Championships.MapCup? mc)
    {
        var ui = _ui!;
        var inv = CultureInfo.InvariantCulture;
        ui.SetValue("/:IsSolo", true);
        ui.SetVariable("/:IsDone", r.State == 2 ? "on" : "off");
        ui.SetVariable("/:GMDescription", RaceTexts.Get(ChallengeModes.DescriptionHc(r.Def), r.ModeName));
        ui.SetVariable("/:Distance", r.LengthM > 0f ? (r.LengthM / 1000f).ToString("0.0", inv) + " " + RaceTexts.Get(0x2e6, "Km") : "");
        ui.SetVariable("/:WhatCup", r.State == 2 && r.Place == 1 ? "gold" : r.State == 2 && r.Place == 2 ? "silver" : r.State == 2 && r.Place == 3 ? "bronze" : "empty");
        ui.SetVariable("/:GoldReward", mc != null && mc.RaceReward > 0 ? Money(mc.RaceReward) : "");
        ui.SetVariable("/:NbGames", "--");
        switch (r.Kind)
        {
            case RaceMode.SpeedTrap:
                ui.SetVariable("/:NbTraps", "");
                break;
            case RaceMode.Speed:
                ui.SetVariable("/:SpeedMin", ChallengeModes.ToUnit(r.SpeedMin).ToString("0.00", inv) + " " + ChallengeModes.UnitName);
                break;
            case RaceMode.TimeAttack:
            case RaceMode.Race:
                {
                    int laps = r.IsCircuit ? r.LapsRaw : 0;
                    ui.SetVariable("/:NbLaps", laps <= 0 ? "--" : laps + " " + RaceTexts.Get(laps == 1 ? 0x10d2UL : 0x44UL, laps == 1 ? "Runde" : "Runden"));
                    break;
                }
        }
    }

    /// <summary>Strecke des Rennens unter dem Cursor auf die Karte (Minimap.MapRacePath); andere Spots: keine Strecke.</summary>
    void ShowRacePath(MapSpot? ms)
    {
        var r = RaceOf(ms);
        if (r == null) { SetMapRacePath(null); return; }
        var def = r.Def;
        if (def.RoadPath == null || def.RoadPath.Count < 2)
        {
            RaceRoadPath.Lanes ??= _ctx.Vehicles?.Traffic?.Lanes;
            if (RaceRoadPath.FindLanes() != null)
            {
                try { RaceRoadPath.Compute(def); }
                catch (Exception ex) { Log.Warn("Karte: Straßenroute " + def.Name + ": " + ex.Message); }
            }
        }
        IReadOnlyList<Vector3> path;
        if (def.RoadPath != null && def.RoadPath.Count > 1) path = def.RoadPath;
        else { var l = new List<Vector3>(def.Checkpoints.Count); foreach (var c in def.Checkpoints) l.Add(c.Position); path = l; }
        SetMapRacePath(path);
    }

    static void SetMapRacePath(IReadOnlyList<Vector3>? path) => Minimap.SetMapRacePath(path);

    static string Money(long v) => v.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $";
    string Txt(string key, string fallback) => _texts != null && _texts.TryGetValue(key, out var t) && !string.IsNullOrEmpty(t) ? t.Trim() : fallback;

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        if (!Update(time.DeltaTime)) GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    /// <summary>Unity TDU2BigMap.DrawRoads: Straßennetz (und GPS-Route) über dem Kartengelände (MapMetersPerPixel 0 = ShowRoads aus).</summary>
    public override void Render(IGameModeContext ctx, in GameTime time) => _view.DrawRoads();

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        Finish();
        // Teleport zu Händler/Haus/Club …: wie im Spiel gleich hinein (Unity EnterWhenReady)
        var pe = _pendingEnter; _pendingEnter = null;
        if (pe != null) { TeleportOutside(pe); InteriorUtils.EnterWhenReady(ctx, pe); }
    }

    /// <summary>Spot mit Interieur, das nach dem Schließen der Karte betreten wird (Unity _pendingEnter).</summary>
    Spot? _pendingEnter;

    // Kamera/Zoom/Straßennetz der Kartenansicht (gemeinsam mit dem Challenge-Editor): Gamemodes/Utils/MapView.cs
    readonly MapView _view = new();

    public bool IsOpen => _open;
    bool _open, _closing;
    float _closeAt, _clock;
    int _openFrames;
    bool _dragging; Vector2 _lastMouse;
    static int _filter;            // Reiter der Filterleiste (_root.numIcFilter), bleibt bis zum nächsten Öffnen

    // Spots (Unity BuildSpots/UpdateOverlay): Kopien von /pack/point je Platz, nächste zum Kartenmittelpunkt zuerst
    public float SpotScale = 0.65f;
    public int MaxSpots = 160;
    /// <summary>Spot auf der Karte: normaler Spot, Meisterschaft (cup = Index) oder – im Wettbewerbsmodus – deren Rennen (race).</summary>
    sealed class MapSpot { public Spot spot = null!; public string label = ""; public int bookmark = -1; public int cup = -1, race = -1; }
    readonly List<MapSpot> _spots = new();
    readonly Dictionary<int, string> _slotLabel = new();
    int _pointChar, _pointDepth0 = 1000, _hover = -1;
    bool _ttipOpen, _cursorOverSpot, _lod;
    Dictionary<string, string>? _texts;
    // Tooltip ausgeklappt / Mini-Menü offen (Eingabe gehört dann dem Film), gewählter Spot, Füllen der Liste nach dem Umschalten
    bool _extended, _miniMenu;
    int _selected = -1;
    float _fillAt = -1f;
    // Mitspieler (Unity UpdatePlayerBlips): Spieler-Id -> Slot (Tiefe hinter den Spots), Label je Beziehung
    readonly Dictionary<int, int> _playerSlots = new();
    readonly Dictionary<int, string> _playerLabel = new();
    int _hoverPlayer = -1, _selectedPlayer = -1;
    Vector2 _downMouse;
    Vector3? _cursorWorld, _menuWorld;
    bool _menuTargetPresent;             // Ziel liegt am Punkt des Menüs (Eintrag "Ziel entfernen")
    Vector2 _downMouseR;
    bool _gpsMarkerShown; Vector3 _gpsMarkerFor;   // Render-Raum (Meereshöhe): Punkt unter dem Cursor bzw. des Mini-Menüs/gewählten Spots
    readonly Dictionary<string, FlashTexture?> _pics = new();
    string[]? _spotPicFiles;

    /// <summary>Film übernehmen (einmal nach dem Laden von map.bnk).</summary>
    public void Attach(VmfPlayer? ui)
    {
        if (ui == null || ReferenceEquals(ui, _ui)) return;
        _ui = ui;
        ui.Visible = false;
        ui.FSCommand += OnCommand;
    }

    // ------------------------------------------------------------------ Öffnen / Schließen
    /// <summary>Unity OpenMap: Spiel anhalten, HUD aus, Kamera über die Insel, Film-Zustand wie vom Spiel vor "map_open".</summary>
    public bool Open() => Open(-1, false);

    bool Open(int contestCup, bool direct)
    {
        var ui = _ui; var terrain = _ctx.Terrain; var cam = terrain?.Camera; var s = terrain?.Streamer;
        if (ui == null || terrain == null || cam == null || s == null || _open) return false;
        _open = true; _closing = false; _openFrames = 0; _clock = 0f;
        _contestCup = -1; _contestDirect = direct && contestCup >= 0; _pendingStart = (-1, -1);

        // Spiel anhalten wie das Pausemenü, HUD aus
        var vm = _ctx.Vehicles?.Manager;
        AudioEngine.Paused = true;   // Unity: AudioListener.pause = true (Menü-Sounds laufen weiter)
        // Kartenmusik wie im Spiel (MngMusic "MAP" → sound/musics/map.wav, 0,5); Spot-Musik im Interieur solange aus
        Paradise.Core.Audio.SpotMusic.Duck(true);
        Paradise.Core.Audio.FrontendMusic.Play("MAP");

        // Kamera (Fahrzeuge anhalten, HUD/Minimap aus): MapView
        var veh = vm?.Current;
        _view.Begin(_ctx, veh != null ? s.GameToRender(vm!.PositionGame) : cam.Position, 2);
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetVariable("/:MenuType", "pc");
        ui.SetValue("/:IsPC_X360", true);
        ui.SetValue("/:fake", false);
        ui.SetValue("/:MapMode", 0);
        ui.SetValue("/:WhatZoom", _view.Zoom);
        ui.SetValue("/:Input", true);
        ui.SetValue("/:IsContest", false);
        ui.SetValue("/:ContestDirectAccess", _contestDirect);
        ui.SetValue("/:IsFirstTime", 0);
        ui.SetValue("/:LockGiveUp", 0);
        ui.SetValue("/:IsResultsDone", 1);
        ui.SetVariable("/:WhatSpot", "noSpot");
        ui.SetVariable("/:WhatSpotType", "none");
        ui.SetValue("/:IsTutorial", 0);
        ui.SetValue("/:IsTargetOK", contestCup < 0);   // A auf freier Fläche öffnet im Film das Mini-Menü (Teleport/GPS/Lesezeichen)
        ui.SetValue("/:IsTeleportOK", true);
        ui.SetValue("/:IsTargetPresent", false);
        ui.SetValue("/:IsSpotFavorite", false);
        ui.SetValue("/:numIcFilter", _filter);
        foreach (var lk in new[] { "LockPresetSolo", "LockPresetMulti", "LockPresetAvSpots", "LockPresetVeSpots", "LockPresetPlayers", "LockPresetMySpots" })
            ui.SetValue("/:" + lk, false);
        ui.SetVisible("/target", false);
        // Kartencursor/GPS-Ziel liegen unter der Maus: nicht als "Maus über Knopf" werten (sonst Flackern/doppelte Klicks)
        ui.MouseIgnore.Add("/curseur"); ui.MouseIgnore.Add("/curseur_ghost"); ui.MouseIgnore.Add("/target");
        ui.SetValue("/:CanAddMySpot", TDU2Bookmarks.CanAdd);
        ui.SetVariable("/:lockGotoStatus", "none");
        // Geld, Tuningstufe, Gruppe, Name (gemeinsam mit dem Challenge-Editor): Gamemodes/Utils/PlayerCarInfo.cs
        string? carName = PlayerCarInfo.Apply(ui, _ctx);
        if (carName != null) ui.SetVariable("/:m_MapPlayerVehicle", carName);
        if (_texts == null)
        {
            try { _texts = FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language); }
            catch (Exception ex) { Log.Warn("Karte: Texte nicht geladen: " + ex.Message); _texts = new(); }
        }
        BuildSpots();
        // Treffpunkt bzw. "Weiter" nach einem Meisterschaftsrennen: Daten des Wettbewerbs VOR map_open setzen. Den Sprung nach
        // "co_direct_enter" macht der Film selbst (map.vmf Bild 42 prüft /:ContestDirectAccess == 1; das Testdaten-Init in Bild 0
        // läuft nur mit /:fake == 1). Vorher sprang die Engine nach 1,3 s ein zweites Mal hinein – der Film lief co_direct_enter
        // doppelt (sbdd/stof, Vorschau) und die Ansicht stand erst nach Schließen/Öffnen der Karte richtig.
        if (contestCup >= 0) EnterContest(contestCup, false);
        _gpsMarkerShown = false; ui.SetVisible("/target", false);   // Unity: GPS-Ziel beim Öffnen neu einblenden ("open")
        ui.Visible = true;
        ui.GotoLabel("/", "map_open", true);
        // Laufschrift des Autonamens neu messen (Bild 0 von /infos/car liest _root.player_car per getTextLength)
        ui.GotoLabel("/infos/car", "empty", false);
        ui.GotoLabel("/infos/car", "set", false);
        Log.Info("Große Karte geöffnet" + (contestCup >= 0 ? " (Wettbewerb " + contestCup + ")" : ""));
        ui.PlayLabel("/curseur/cible", "on");
        ui.PlayLabel("/curseur", "lod2");
        return true;
    }

    /// <summary>Schließanimation abspielen, danach <see cref="Finish"/>.</summary>
    public void Close()
    {
        if (!_open || _closing) return;
        _closing = true;
        HideTooltip();
        _ui?.GotoLabel("/", "close", true);
        _closeAt = _clock + 0.45f;
    }

    /// <summary>Schließen, wenn der Film die Schließanimation selbst spielt (gofr).</summary>
    void BeginClosing()
    {
        if (_closing) return;
        _closing = true;
        HideTooltip();
        _closeAt = _clock + 0.45f;
    }

    /// <summary>Unity Finish: Zustand wiederherstellen; der Spielmodus wechselt danach zurück ins Fahren.</summary>
    public void Finish()
    {
        if (!_open) return;
        _open = false; _closing = false;
        if (_ui != null) _ui.Visible = false;   // bleibt am Ende der Schließanimation; nächstes Öffnen springt nach "map_open"
        _view.End();
        AudioEngine.Paused = false;
        Paradise.Core.Audio.FrontendMusic.Stop();
        Paradise.Core.Audio.SpotMusic.Duck(false);
        _contestCup = -1;
        SetMapRacePath(null);
        _ui?.SetValue("/:IsContest", false);
        // Meisterschaftsrennen gewählt: Start im nächsten Bild der Freifahrt (Unity StartFromMap nach dem Schließen)
        var ps = _pendingStart; _pendingStart = (-1, -1);
        if (ps.cup >= 0) Championships.StartFromMap(ps.cup, ps.race);
        Log.Info("Große Karte geschlossen");
    }

    // ------------------------------------------------------------------ Jedes Bild
    /// <summary>Unity Update (Karte offen). Rückgabe false = Karte zu (Spielmodus zurück ins Fahren).</summary>
    public bool Update(float dt)
    {
        _clock += dt;
        _view.LastDt = MathF.Max(1e-4f, dt);
        if (!_open || _ui == null) return false;
        _openFrames++;
        if (_closing)
        {
            if (_clock >= _closeAt) { Finish(); return false; }
            UpdateCamera(false);
            return true;
        }
        if (TDU2Controls.Pressed("map")) { Close(); return true; }
        // Zurück wie im Spiel an den Film (Tooltip/Mini-Menü schließen bzw. Karte: "gofr"); reagiert er nicht, direkt schließen
        if (_ctx.Pressed(0x1B) || TDU2Controls.BackPressed())
        {
            bool handled = _ui.Input("B");
            Log.Info("Karte: Zurück -> Film " + (handled ? "reagiert" : "reagiert nicht, Karte zu") + " (Tooltip " + _extended + ", Mini-Menü " + _miniMenu + ")");
            if (!handled) Close();
            return true;
        }
        HandleInput(dt);
        UpdateCamera(false);
        UpdateOverlay();
        if (_fillAt > 0f && _clock >= _fillAt) { _fillAt = -1f; FillExtendedInfos(); }
        return true;
    }

    void SetZoom(int z)
    {
        _view.Zoom = System.Math.Clamp(z, 1, 4);
        _ui?.SetValue("/:WhatZoom", _view.Zoom);
        ApplyLod(false);
    }

    // ------------------------------------------------------------------ Spots (Unity TDU2BigMap)
    void BuildSpots()
    {
        var ui = _ui!;
        _spots.Clear();
        if (_contestCup >= 0)
        {
            // Wettbewerbsmodus: nur die Rennen der Meisterschaft
            foreach (var r in Championships.MapRaces(_contestCup))
                _spots.Add(new MapSpot
                {
                    spot = new Spot { Code = "#CUPRACE_" + r.Name, Position = r.Start, Position2 = r.Start - r.Forward * 14f, Index = -1 },
                    label = r.Label,
                    cup = _contestCup,
                    race = r.Index,
                });
        }
        else
        {
            var cupByCode = new Dictionary<string, int>();
            foreach (var c in Championships.MapCups()) cupByCode[c.Code] = c.Index;
            var spots = _ctx.Terrain?.Streamer?.Spots;
            if (spots != null)
                foreach (var s in spots.Spots)
                {
                    string? label = Minimap.SpotLabel(s.Code);
                    if (label == null) continue;
                    _spots.Add(new MapSpot { spot = s, label = label, cup = cupByCode.TryGetValue(s.Code, out int ci) ? ci : -1 });
                }
            // Lesezeichen (nur auf der großen Karte, gespeichert je Spieler und Insel)
            string isl = IslandName();
            foreach (var b in TDU2Bookmarks.All)
                if (string.Equals(b.Island ?? "", isl, StringComparison.OrdinalIgnoreCase))
                    _spots.Add(new MapSpot { spot = new Spot { Code = "#BOOKMARK_" + b.Id, Position = b.Pos, Position2 = b.Pos, Index = -1 }, label = "bookmark", bookmark = b.Id });
        }
        int extra = 0; foreach (var e in _spots) if (e.cup >= 0) extra++;
        Log.Info("Karte: " + _spots.Count + " Spots mit Symbol" + (_contestCup >= 0 ? " (Wettbewerb)" : ", davon " + extra + " Meisterschaften"));
        var tmpl = ui.Find("/pack/point");
        if (tmpl != null) { _pointChar = tmpl.CharId; tmpl.Visible = false; }
        foreach (var kv in _slotLabel) ui.Detach("/pack", _pointDepth0 + kv.Key);
        _slotLabel.Clear();
        foreach (var kv in _playerSlots) ui.Detach("/pack", _pointDepth0 + MaxSpots + 1 + kv.Value);
        _playerSlots.Clear(); _playerLabel.Clear();
        _hoverPlayer = -1; _selectedPlayer = -1;
        _hover = -1; _cursorOverSpot = false; _ttipOpen = false;
        _selected = -1; _extended = false; _miniMenu = false; _fillAt = -1f;
        ApplyLod(true);
    }

    /// <summary>Spot im gewählten Reiter der Filterleiste? 0 Herausforderungen, 2 Orte für den Avatar, 3 Orte für Fahrzeuge,
    /// 5 eigene Orte (Lesezeichen + Favoriten).</summary>
    bool FilterPass(MapSpot ms)
    {
        if (_contestCup >= 0) return true;
        if (ms.bookmark >= 0) return _filter == 5;
        int cat = SpotCategory(ms);
        return _filter switch { 0 => cat == 0, 2 => cat == 2, 3 => cat == 3, 5 => TDU2Bookmarks.IsFavorite(ms.spot.Code), _ => false };   // 1 Multi, 4 Spieler: folgen
    }

    static int SpotCategory(MapSpot ms) => ms.label switch
    {
        "home" or "club" or "estates" or "clothes" or "hair" or "surgery" or "casino" or "gamblingHall" or "airport" or "watches" or "forsale" => 2,
        "carDealer" or "bikeDealer" or "tuning" or "carWash" or "carPaint" or "carRental" or "auctionHall" => 3,
        _ => 0,
    };

    void ApplyLod(bool force)
    {
        bool lod = _view.Zoom >= 3;   // weit weg: kleine Punkte (point/spot "lod")
        if (_ui == null || (!force && lod == _lod)) return;
        _lod = lod;
        foreach (var kv in _slotLabel) _ui.CallClip("/pack/point" + kv.Key.ToString("000"), "LOD", lod ? 1 : 0);
    }

    Vector2? ToGui(Vector3 render) => _view.ToGui(render);
    static FVec F(Vector2 v) => new(v.X, v.Y);

    /// <summary>Unity UpdateOverlay: Spielersymbol, Kartencursor, Spots (Filter, nächste zuerst), Spot unter dem Cursor + Tooltip.</summary>
    void UpdateOverlay()
    {
        var ui = _ui; var s = _ctx.Terrain?.Streamer;
        if (ui == null || s == null || _closing) return;
        var vm = _ctx.Vehicles?.Manager;
        if (vm?.Current != null)
        {
            var g = ToGui(s.GameToRender(vm.PositionGame));
            var lp = g.HasValue ? ui.GuiToLocal("/", F(g.Value)) : null;
            if (lp.HasValue) { ui.SetVisible("/player", true); ui.SetPosition("/player", lp.Value); }
            else ui.SetVisible("/player", false);
        }
        var mouse = MousePixel();
        var cl = ui.GuiToLocal("/", F(mouse));
        if (cl.HasValue) ui.SetPosition("/curseur", cl.Value);
        UpdateGpsMarker();

        if (_pointChar == 0) return;
        float w = FlashScreen.width, h = FlashScreen.height;
        var list = new List<(float d, int idx, Vector2 gui)>();
        var center = new Vector2(w * 0.5f, h * 0.5f);
        for (int i = 0; i < _spots.Count; i++)
        {
            if (!FilterPass(_spots[i])) continue;
            var g = ToGui(s.GameToRender(_spots[i].spot.Position));
            if (!g.HasValue || g.Value.X < -40 || g.Value.Y < -40 || g.Value.X > w + 40 || g.Value.Y > h + 40) continue;
            list.Add(((g.Value - center).LengthSquared(), i, g.Value));
        }
        list.Sort((a, b) => a.d.CompareTo(b.d));
        int n = System.Math.Min(list.Count, MaxSpots);
        int hover = -1; float best = 22f * 22f * (h / 720f) * (h / 720f);
        for (int slot = 0; slot < MaxSpots; slot++)
        {
            string name = "point" + slot.ToString("000");
            if (slot >= n)
            {
                if (_slotLabel.Remove(slot)) ui.Detach("/pack", _pointDepth0 + slot);
                continue;
            }
            var (_, idx, gui) = list[slot];
            var d = ui.AttachSprite("/pack", _pointChar, name, _pointDepth0 + slot);
            if (d == null) continue;
            string label = _spots[idx].label;
            var gpsI = Gps.Instance;
            bool tgt = gpsI != null && gpsI.HasTarget && gpsI.IsTargetNear(_spots[idx].spot.Position, 40f);
            string key = label + "|" + idx + (tgt ? "|T" : "");
            if (!_slotLabel.TryGetValue(slot, out var cur) || cur != key)
            {
                ui.GotoLabel("/pack/" + name + "/spot", label, false);
                ui.CallClip("/pack/" + name, "LOD", _lod ? 1 : 0);
                ui.CallClip("/pack/" + name, "resetIconStatus");   // sonst zeigt das Symbol alle Medaillen-Fähnchen
                if (Minimap.ExtraSpotNumbers.TryGetValue(_spots[idx].spot.Code, out var cnum)) ui.SetVariable("/pack/" + name + ":num_contest", cnum);
                // gefahrene Rennen im Wettbewerb: Haken + Medaille wie im Spiel
                var rs = RaceOf(_spots[idx]);
                if (rs != null && rs.State == 2)
                {
                    ui.CallClip("/pack/" + name, "setDoneStatus", true);
                    ui.CallClip("/pack/" + name, "setRankStatus", rs.Place >= 1 && rs.Place <= 3 ? rs.Place : 0);
                }
                if (label == "carDealer" || label == "bikeDealer") ApplyDealerLogo("/pack/" + name + "/spot", _spots[idx].spot.Code);
                else ui.SetClipTexture("/pack/" + name + "/spot/ic/logo/lure", null);
                // GPS-Ziel: Fähnchen (spot/target "open") und Pfeil wie im Spiel, sonst aus
                if (tgt) ui.CallClip("/pack/" + name, "setTargetStatus", true);
                ui.SetVisible("/pack/" + name + "/fleche", tgt);
                _slotLabel[slot] = key;
            }
            var lp = ui.GuiToLocal("/pack", F(gui));
            if (lp.HasValue) d.Position = lp.Value;
            d.Matrix.A = SpotScale; d.Matrix.D = SpotScale; d.Matrix.B = 0f; d.Matrix.C = 0f; d.ScriptMatrix = true;
            d.Visible = true;
            float dm = (gui - mouse).LengthSquared();
            if (dm < best) { best = dm; hover = idx; }
        }
        int hoverPlayer = UpdatePlayerBlips(mouse, ref best);
        if (hoverPlayer >= 0) hover = -1;
        if ((hover != _hover || hoverPlayer != _hoverPlayer) && !_extended && !_miniMenu)
        {
            _hover = hover; _hoverPlayer = hoverPlayer;
            // Cursor-Ring wie im Spiel: über einem Spot zusammenziehen (spotOver), sonst zurück (spotOut)
            bool over = hover >= 0 || hoverPlayer >= 0;
            if (over != _cursorOverSpot) { _cursorOverSpot = over; ui.CallClip("/curseur", over ? "spotOver" : "spotOut"); }
            if (hoverPlayer >= 0 && Paradise.Game.Online.TDU2GameData.Players.TryGetValue(hoverPlayer, out var pl)) { ShowPlayerTooltip(pl); return; }
            // WhatSpotType "contest" (Meisterschaft/deren Rennen): A darauf öffnet im Film den Wettbewerb bzw. das Auswahlmenü
            bool contest = hover >= 0 && _spots[hover].cup >= 0;
            ui.SetVariable("/:WhatSpot", hover >= 0 ? _spots[hover].label : "noSpot");
            ui.SetVariable("/:WhatSpotType", hover >= 0 ? (contest ? "contest" : "spot") : "none");
            if (hover >= 0) ShowTooltip(_spots[hover]); else HideTooltip();
            ShowRacePath(hover >= 0 ? _spots[hover] : null);   // Wettbewerb: Strecke des Rennens unter dem Cursor
        }
    }

    /// <summary>Blips der Mitspieler (Label im Spot-Symbol je Beziehung); liefert den Spieler unter dem Mauszeiger (Unity UpdatePlayerBlips).</summary>
    int UpdatePlayerBlips(Vector2 mouse, ref float best)
    {
        var ui = _ui!; var s = _ctx.Terrain?.Streamer;
        int hover = -1;
        var seen = new HashSet<int>();
        if (s != null && _pointChar != 0)
            foreach (var p in Paradise.Game.Online.TDU2GameData.Players.Values)
            {
                if (!p.Online || !p.HasPos) continue;
                var g = ToGui(s.GameToRender(p.GamePos));
                if (!g.HasValue) continue;
                if (!_playerSlots.TryGetValue(p.Id, out int slot))
                {
                    slot = 0; while (_playerSlots.ContainsValue(slot)) slot++;
                    if (slot >= 64) continue;
                    _playerSlots[p.Id] = slot;
                }
                seen.Add(p.Id);
                string name = "plyr" + slot.ToString("00");
                bool fresh = ui.Find("/pack/" + name) == null;
                var d = ui.AttachSprite("/pack", _pointChar, name, _pointDepth0 + MaxSpots + 1 + slot);
                if (d == null) continue;
                // Original-Labels im Spot-Symbol je Beziehung: player (weiß), friend (grün), clubMember (orange)
                var rel = Paradise.Game.Online.Friends.RelationOf(p.Nick);
                string label = rel == Paradise.Game.Online.PlayerRelation.Friend ? "friend" : rel == Paradise.Game.Online.PlayerRelation.Club ? "clubMember" : "player";
                if (fresh || !_playerLabel.TryGetValue(p.Id, out var oldLabel) || oldLabel != label)
                {
                    ui.GotoLabel("/pack/" + name + "/spot", label, false);
                    _playerLabel[p.Id] = label;
                }
                if (fresh)
                {
                    ui.CallClip("/pack/" + name, "LOD", _lod ? 1 : 0);
                    ui.SetVisible("/pack/" + name + "/fleche", false);
                }
                var lp = ui.GuiToLocal("/pack", F(g.Value));
                if (lp.HasValue) d.Position = lp.Value;
                d.Matrix.A = SpotScale; d.Matrix.D = SpotScale; d.Matrix.B = 0f; d.Matrix.C = 0f; d.ScriptMatrix = true;
                d.Visible = true;
                float dm = (g.Value - mouse).LengthSquared();
                if (dm < best) { best = dm; hover = p.Id; }
            }
        var gone = new List<int>();
        foreach (var kv in _playerSlots) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone) { ui.Detach("/pack", _pointDepth0 + MaxSpots + 1 + _playerSlots[id]); _playerSlots.Remove(id); _playerLabel.Remove(id); }
        return hover;
    }

    void ShowPlayerTooltip(Paradise.Game.Online.PlayerInfo p)
    {
        var ui = _ui!;
        ui.SetVariable("/:WhatSpot", "player");
        ui.SetVariable("/:WhatSpotType", "spot");
        ui.SetVariable("/:SpotName", p.Nick);
        ui.GotoLabel("/tool_tip/ic", "player", false);
        // Menü des Spieler-Tooltips (SetMenu "player": join, invite) – gesperrt dargestellt, solange _root.LockJoin/LockInvite
        // nicht gesetzt sind (eigener _root von map.bnk, die Sperren des Pausemenüs gelten hier nicht)
        ui.SetValue("/:LockJoin", !Paradise.Game.Online.Party.CanJoin(p.Id, out _));
        ui.SetValue("/:LockInvite", !Paradise.Core.Online.TDU2Session.LoggedIn || p.Id == 0);
        if (!_ttipOpen) { ui.CallClip("/tool_tip", "Open"); _ttipOpen = true; }
    }

    /// <summary>Ausgeklappter Tooltip eines Spielers (infos.setPlayerInfos wartet auf _root.MapPlayerInfosLoaded).</summary>
    void FillPlayerInfos()
    {
        var ui = _ui!;
        if (!Paradise.Game.Online.TDU2GameData.Players.TryGetValue(_selectedPlayer, out var p)) return;
        var c = p.Car;
        ui.SetVariable("/:m_MapPlayerVehicle", p.CarName);
        ui.SetVariable("/:MapPlayerVehicleGroup", GroupLabel(c?.Raw != null ? c.Raw.I("_vehiclecategory") : 0));
        ui.SetValue("/:MapPlayerVehicleUpg", 0);
        ui.SetValue("/:MapPlayerNationality", p.Nationality >= 1 && p.Nationality <= 113 ? p.Nationality : 135);   // Flagge wie exe 0xc09030 (unbekannt = 135)
        foreach (var n in new[] { "MapPlayerCurGlobalLevel", "MapPlayerGlobalLevel", "MapPlayerRacingCurLevel", "MapPlayerRacingLevel", "MapPlayerSocialCurlevel",
                                  "MapPlayerSocialLevel", "MapPlayerCruisingCurLevel", "MapPlayerCruisingLevel", "MapPlayerCollectionLevel", "MapPlayerCollectionCurLevel" })
            ui.SetValue("/:" + n, 0);
        ui.SetValue("/:MapPlayerNbCars", p.NbCars);
        ui.SetValue("/:MapPlayerNbHouses", 0);
        ui.SetVariable("/:MapPlayerMoney", p.Money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $");
        ui.SetValue("/:MapPlayerInfosLoaded", true);
    }

    /// <summary>Zum Mitspieler: Beitreten wie über die Spielerliste (verlässt auch ein Interieur; Fehler im Chat) – Unity TeleportToPlayer.</summary>
    static void TeleportToPlayer(int id) => Paradise.Game.Online.Party.Join(id);

    static readonly Dictionary<string, string> SpotLocKeys = new()
    {
        { "carDealer", "m_CarDealer" }, { "bikeDealer", "m_BikeDealer" }, { "carPaint", "m_CarPaint" }, { "carRental", "m_CarRental" },
        { "carWash", "m_CarWash_" }, { "estates", "m_Estates" }, { "home", "m_Home" }, { "hair", "m_HairDresser" },
        { "clothes", "m_Clothes_" }, { "rschool", "m_RacingSchool" }, { "tuning", "m_AfterMarket" }, { "casino", "m_Casino_title" },
        { "gamblingHall", "m_gamblin_hall_short" }, { "cup", "m_Cup_" },
    };

    string LocalizedSpotName(string label, string code)
    {
        if (_texts != null && SpotLocKeys.TryGetValue(label, out var key) && _texts.TryGetValue(key, out var txt) && !string.IsNullOrEmpty(txt))
            return txt.Trim();
        return SpotsFile.CategoryFromCode(code).Replace("?", "").Trim();
    }

    /// <summary>Unity ShowTooltip: Titel /:SpotName (Datenbankname, Händler: Marken), Symbol mit Logo, Zahlen, Bild.</summary>
    void ShowTooltip(MapSpot ms)
    {
        var ui = _ui!;
        var s = ms.spot; var label = ms.label;
        string name = Minimap.ExtraSpotNames.TryGetValue(s.Code, out var extraName) ? extraName : LocalizedSpotName(label, s.Code);
        if (ms.bookmark >= 0) name = (_texts != null && _texts.TryGetValue("m_Bookmark", out var bt) && !string.IsNullOrEmpty(bt) ? bt : "Lesezeichen") + " " + ms.bookmark;
        ui.SetValue("/:IsSpotFavorite", TDU2Bookmarks.IsFavorite(s.Code));
        // wie im Spiel: Name aus der Sprachdatenbank (Händler/Shops DBCarShop._libelle_hc, Häuser DBHouse._housename_hc)
        var dbName = ms.bookmark < 0 && ms.cup < 0 ? SpotDbName(s) : null;

        Log.Info("Karte: Tooltip " + s.Code + " (" + label + ") -> Name '" + name + "'" + (string.IsNullOrEmpty(dbName) ? "" : ", DB-Name '" + dbName + "'"));

        if (label == "clothes")
        {
            var clothes = AvatarUtils.GetClothesCountFromSpot(s, PlayerUtils.GetAvatarSex());
            var clothesparts = AvatarUtils.GetExtraPartsCountFromSpot(s);
            Log.Info($"Parts Hats: {clothesparts.Hats} Pullover: {clothesparts.Pullovers} Pants: {clothesparts.Pants} Shoes: {clothesparts.Shoes}");
            Log.Info("Karte: Kleidung " + s.Code + " -> " + clothes.Owned + "/" + clothes.Available + " (Besitz/Angebot)");
        }

        if (!string.IsNullOrEmpty(dbName)) 
            name = dbName;
        else if (label == "carDealer" || label == "bikeDealer") 
        { 
            var b = DealerBrands(s.Code); 
            if (!string.IsNullOrEmpty(b)) 
                name = b; 
        }
        if (ms.cup >= 0)
        {
            var mc = Championships.MapCupAt(ms.cup);
            if (ms.race >= 0)
            {
                // Rennen im Wettbewerbsmodus
                var r = RaceOf(ms);
                if (r != null)
                {
                    name = (ms.race + 1) + ". " + r.Title;
                    SetChallengeVars(r, mc);
                }
            }
            else if (mc != null)
            {
                name = mc.Name;
                SetContestVars(ms.cup);
                ui.SetVariable("/:ContestName", mc.Name);
                ui.SetVariable("/:ContestType", mc.ContestType);
            }
        }
        ui.SetVariable("/:SpotName", name);
        ui.SetVariable("/:WhatSpot", label);
        ui.GotoLabel("/tool_tip/ic", label, false);
        if (label == "carDealer" || label == "bikeDealer") ApplyDealerLogo("/tool_tip/ic", s.Code);
        else ui.SetClipTexture("/tool_tip/ic/logo/lure", null);
        if (Minimap.ExtraSpotNumbers.TryGetValue(s.Code, out var tnum)) ui.SetVariable("/tool_tip:num_contest", tnum);
        // Haken/Medaille gefahrener Meisterschaftsrennen auch im Tooltip-Symbol
        var rsT = RaceOf(ms);
        if (rsT != null && rsT.State == 2) ui.GotoLabel("/tool_tip/ic/done", "on", false); else ui.GotoFrameNumber("/tool_tip/ic/done", 0);
        int medal = rsT != null && rsT.State == 2 ? rsT.Place : 0;
        ui.GotoLabel("/tool_tip/ic/rank", medal == 1 ? "gold" : medal == 2 ? "silver" : medal == 3 ? "bronze" : "default", false);
        SetSpotVars(ms);
        ui.SetClipTexture("/tool_tip/infos/picture/lure_map", SpotPicture(s));   // auch null (kein altes Bild stehen lassen)
        if (!_ttipOpen) { ui.CallClip("/tool_tip", "Open"); _ttipOpen = true; }
    }

    CarDatabase? Db => _ctx.Vehicles?.Manager?.Database;
    string IslandName() => _ctx.Terrain?.Streamer?.Index?.IslandName ?? "";
    static string Lang => FlashHost.Language;

    /// <summary>Zahlen im Tooltip normaler Spots (Händler: Fahrzeuge, Häuser: Garagen …); nicht gesetzte bleiben leer.</summary>
    void SetSpotVars(MapSpot ms)
    {
        var ui = _ui!; var db = Db;
        // Tuner nur mit einem Auto seiner Marken: gesperrt -> SetMenu "spot_locked", Eintreten gesperrt, Text m_SpotLockedTuner_
        var cur = _ctx.Vehicles?.Manager?.Current?.Car;
        ui.SetValue("/:IsSpotLocked", ms.label == "tuning" && cur != null && !GAME_MODE_TUNER.Accepts(ms.spot, cur, db, IslandName()));
        if (ms.label == "tuning")
        {
            // Tooltip des Tuners (infos.setTuner): Anzahl Marken (Seiten zu 8) und beste freie Tuningstufe (txt.upg level0..5)
            ui.SetValue("/:NbBrands", TunerLogos(ms.spot).Count);
            ui.SetVariable("/:MaxUpgLevel", "level" + System.Math.Clamp(GAME_MODE_TUNER.AreaLevel(), 0, 5));
        }
        if (ms.label == "carDealer" || ms.label == "bikeDealer" || ms.label == "carRental")
        {
            var shop = db?.ShopForSpot(ms.spot, IslandName());
            int n = 0; if (shop != null) foreach (int id in shop.CarIds) if (id != 0) n++;
            ui.SetVariable("/:nb_cars", n > 0 ? n.ToString() : "");
            ui.SetVariable("/:NbCarsForRent", n > 0 ? n.ToString() : "");
            int owned = 0;
            var save = TDU2Session.Save;
            if (shop != null && save?.ownedCars != null && db != null)
                foreach (int id in shop.CarIds)
                    if (id != 0 && db.ById.TryGetValue(id, out var c))
                        foreach (var oc in save.ownedCars) if (string.Equals(oc, c.FileName, StringComparison.OrdinalIgnoreCase)) { owned++; break; }
            ui.SetVariable("/:nb_cars_bought", owned.ToString());
        }
        else if (ms.label == "home")
        {
            // Haus: Garagenplätze, Stufe und die dort abgestellten Autos (infos.CarsList liest /:nb_cars, /:carN_car_name, carN_gp, carN_state)
            var house = db?.HouseForSpot(ms.spot, IslandName());
            var cars = HouseCars(ms.spot.Code);
            int places = house != null && house.CarSlots > 0 ? house.CarSlots : cars.Count;
            Log.Info($"Karte: Haus {ms.spot.Code} -> DB {(house != null ? house.SpotName + ", Stellplätze " + house.CarSlots + ", Stufe " + house.Level : "nicht gefunden")}, Autos {cars.Count}");

            ui.SetVariable("/:nb_cars", cars.Count.ToString());
            ui.SetVariable("/:nb_garage_places", places > 0 ? places.ToString() : "");
            if (house != null && house.Level > 0) ui.SetValue("/:HouseLevel", house.Level);
            for (int i = 0; i < 12; i++)
            {
                var c = i < cars.Count ? cars[i] : null;
                ui.SetVariable("/:car" + i + "_car_name", c != null ? ((c.Brand ?? "") + " " + c.ModelName).Trim() : "");
                ui.SetVariable("/:car" + i + "_gp", c != null ? GroupLabel(c.Raw != null ? c.Raw.I("_vehiclecategory") : 0) : "");
                ui.SetValue("/:car" + i + "_state", false);
            }
        }
    }

    /// <summary>Autos in der Garage eines Hauses: das gefahrene Auto zuerst, dann die eigenen Autos, höchstens so viele wie Stellplätze.</summary>
    List<CarPhysics> HouseCars(string code)
    {
        var r = new List<CarPhysics>();
        var vm = _ctx.Vehicles?.Manager;
        if (vm == null) return r;
        // Autos auf den Stellplätzen dieses Hauses (SaveData.garage – dieselbe Belegung wie Garage/Platz wechseln)
        var sv = TDU2Session.Save; var h = vm.Database?.HouseFor(code);
        if (sv != null && h != null && vm.Database != null)
        {
            Paradise.Core.Savegame.TDU2Garage.Normalize(sv, vm.Database, null);
            for (int k = 0; k < h.CarSlots; k++)
            {
                string? file = Paradise.Core.Savegame.TDU2Garage.CarAt(sv, h.SpotName, k);
                var c = file != null ? vm.FindCar(file) : null;
                if (c != null && !r.Contains(c)) r.Add(c);
            }
            return r;
        }
        var cur = vm.Current?.Car;
        var owned = TDU2Session.OwnedCars;
        if (cur != null) foreach (var n in owned) if (string.Equals(n, cur.FileName, StringComparison.OrdinalIgnoreCase)) { r.Add(cur); break; }
        foreach (var n in owned) { var c = vm.FindCar(n); if (c != null && !r.Contains(c)) r.Add(c); }
        var house = vm.Database?.HouseFor(code);
        if (house != null && house.CarSlots > 0 && r.Count > house.CarSlots) r.RemoveRange(house.CarSlots, r.Count - house.CarSlots);
        return r;
    }

    /// <summary>Händler-Icon wie im Spiel: "ic" auf "brands" + Logo in ic/logo/lure (Unity TDU2BigMap: Logo von der Minimap).</summary>
    void ApplyDealerLogo(string spotPath, string code)
    {
        var mm = _ctx.Vehicles?.Manager?.Minimap;
        var tex = mm != null ? mm.DealerLogoTexture(code) : null;
        if (tex != null)
        {
            _ui!.GotoLabel(spotPath + "/ic", "brands", false);
            _ui.SetClipTexture(spotPath + "/ic/logo/lure", tex);
        }
    }

    /// <summary>Name eines Händlers/Shops/Hauses aus der Sprachdatenbank (z.B. "Audi (Norden)", "Salines-Villa"); null = keiner.</summary>
    string? SpotDbName(Spot s)
    {
        var db = Db;
        if (db == null || s == null || string.IsNullOrEmpty(s.Code)) return null;
        int hash = 0;
        if (s.Code.StartsWith("EPH", StringComparison.OrdinalIgnoreCase)) { var h = db.HouseForSpot(s, IslandName()); if (h != null) hash = h.NameHash; }
        else { var sh = db.ShopForSpot(s, IslandName()); if (sh != null) hash = sh.NameHash; }
        return FlashLocalization.Text(FlashHost.BnkDir, Lang, hash);
    }

    /// <summary>Marken im Bestand eines Händlers (häufigste zuerst), z.B. "Ferrari" oder "Audi / Volkswagen".</summary>
    string? DealerBrands(string code)
    {
        var db = Db; var shop = db?.ShopFor(code);
        if (db == null || shop == null) return null;
        var count = new Dictionary<string, int>();
        foreach (int id in shop.CarIds)
            if (id != 0 && db.ById.TryGetValue(id, out var c) && !string.IsNullOrEmpty(c.Brand)) { count.TryGetValue(c.Brand, out int n); count[c.Brand] = n + 1; }
        var list = new List<KeyValuePair<string, int>>(count);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        var names = new List<string>();
        for (int i = 0; i < list.Count && i < 3; i++) names.Add(list[i].Key);
        return string.Join(" / ", names);
    }

    // ------------------------------------------------------------------ Bilder (FrontEnd/AllRes/MapScreens/Spots, RealtorScr)
    // Name wie im Spiel: <code>_<AreaX><AreaZ><SectorX><SectorZ>[i] (i = Ibiza), z.B. ecd_ital_3210.bnk; Häuser (EPH) in RealtorScr.
    string? SpotPictureFile(Spot s, string allres, string name)
    {
        // Meisterschaft/deren Rennen: Bild der Meisterschaft (frontend/AllRes/MapScreens/contest/…)
        string? extra = null;
        if (s.Code.StartsWith("#CUPRACE_") && _contestCup >= 0) extra = Championships.MapCupAt(_contestCup)?.Picture;
        else if (Minimap.ExtraSpotPictures.TryGetValue(s.Code, out var ep)) extra = ep;
        if (!string.IsNullOrEmpty(extra))
            return CarDatabase.FindFile(Path.Combine(allres, "MapScreens", Path.GetDirectoryName(extra) ?? ""), Path.GetFileName(extra) + ".bnk");
        if (s.Code.StartsWith('#')) return null;
        string dir = Path.Combine(allres, "MapScreens", "Spots");
        string? f = CarDatabase.FindFile(dir, name + ".bnk");
        if (f != null) return f;
        string other = name.EndsWith("i") ? name.Substring(0, name.Length - 1) : name + "i";
        f = CarDatabase.FindFile(dir, other + ".bnk");
        if (f != null) return f;
        try
        {
            _spotPicFiles ??= GameFiles.GetFiles(dir, "*.bnk");
            string prefix = s.Code.ToLowerInvariant() + "_";
            string want = s.AreaX.ToString() + s.AreaZ + s.SectorX + s.SectorZ;
            string? best = null; int bestScore = -1;
            foreach (var file in _spotPicFiles)
            {
                string fn = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (!fn.StartsWith(prefix)) continue;
                string digits = fn.Substring(prefix.Length).TrimEnd('i');
                int score = digits == want ? 100 : 0;
                for (int k = 0; k < System.Math.Min(digits.Length, want.Length); k++) if (digits[k] == want[k]) score += 10 - k;
                if (score > bestScore) { bestScore = score; best = file; }
            }
            return best;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Bild einer Meisterschaft (frontend/AllRes/MapScreens/&lt;Picture&gt;.bnk, wie die Rennen der Meisterschaft).</summary>
    FlashTexture? CupPicture(Championships.MapCup mc)
    {
        string bnkDir = FlashHost.BnkDir;
        if (string.IsNullOrEmpty(mc.Picture) || string.IsNullOrEmpty(bnkDir)) return null;
        string key = "#CUP|" + mc.Picture;
        if (_pics.TryGetValue(key, out var t)) return t;
        t = null;
        try
        {
            string dir = Path.Combine(bnkDir, "frontend", "AllRes", "MapScreens", Path.GetDirectoryName(mc.Picture) ?? "");
            string? f = CarDatabase.FindFile(dir, Path.GetFileName(mc.Picture) + ".bnk");
            if (f != null) t = FlashTextures.FirstTexture(f);
        }
        catch (Exception ex) { Log.Warn("Karte: Bild der Meisterschaft " + mc.Picture + ": " + ex.Message); }
        _pics[key] = t;
        if (t == null) Log.Debug("Karte: kein Bild für Meisterschaft " + mc.Code + " (" + mc.Picture + ")");
        return t;
    }

    FlashTexture? SpotPicture(Spot s)
    {
        string bnkDir = FlashHost.BnkDir;
        if (s == null || string.IsNullOrEmpty(bnkDir)) return null;
        bool ibiza = IslandName().StartsWith("i", StringComparison.OrdinalIgnoreCase);
        string name = s.Code.ToLowerInvariant() + "_" + s.AreaX + s.AreaZ + s.SectorX + s.SectorZ + (ibiza ? "i" : "");
        if (_pics.TryGetValue(s.Code + "|" + name, out var t)) return t;
        t = null;
        try
        {
            string allres = Path.Combine(bnkDir, "frontend", "AllRes");
            bool house = s.Code.StartsWith("EPH", StringComparison.OrdinalIgnoreCase);
            string? f = house ? CarDatabase.FindFile(Path.Combine(allres, "RealtorScr"), name + ".bnk") : null;
            f ??= SpotPictureFile(s, allres, name);
            if (f != null) t = FlashTextures.FirstTexture(f);
        }
        catch (Exception ex) { Log.Warn("Karte: Bild " + name + ": " + ex.Message); }
        _pics[s.Code + "|" + name] = t;
        if (t == null) Log.Debug("Karte: kein Bild für Spot " + s.Code + " (" + name + ")");
        return t;
    }

    /// <summary>Ausgeklappter Tooltip (Unity FillExtendedInfos): Bild, Fahrzeugliste des Händlers bzw. die Autos im Haus.</summary>
    void FillExtendedInfos()
    {
        if (_ui != null && _selectedPlayer >= 0) { FillPlayerInfos(); return; }
        if (_ui == null || _selected < 0 || _selected >= _spots.Count) return;
        var ui = _ui;
        var ms = _spots[_selected];
        var s = ms.spot; var label = ms.label;
        ui.SetClipTexture("/tool_tip/infos/picture/lure_map", SpotPicture(s));
        if (ms.cup >= 0) { SetContestVars(ms.cup); return; }
        if (label == "clothes") { FillClothesInfos(s); return; }
        if (label == "tuning")
        {
            // Marken des Tuners: Logos in txt/list/br0..15/l (logotexturepage.bnk wie die Händler-Symbole)
            var logos = TunerLogos(s);
            for (int i = 0; i < 16; i++)
                ui.SetClipTexture("/tool_tip/infos/txt/list/br" + i + "/l", i < logos.Count ? FlashTextures.BrandLogo(FlashHost.BnkDir, logos[i]) : null);
            return;
        }
        if (label != "carDealer" && label != "bikeDealer" && label != "home") return;
        var db = Db; var shop = label == "home" ? null : db?.ShopForSpot(s, IslandName());
        var cars = label == "home" ? HouseCars(s.Code) : new List<CarPhysics>();
        if (shop != null && db != null) foreach (int id in shop.CarIds) if (id != 0 && db.ById.TryGetValue(id, out var c)) cars.Add(c);
        const string txt = "/tool_tip/infos/txt";
        ui.SetVariable(txt + ":list_title", _texts != null && _texts.TryGetValue("m_Vehicles_", out var v) ? v : "Fahrzeuge");
        ui.SetVariable("/:nb_cars", cars.Count.ToString());
        // Blättern macht der Film selbst (txt.Move: nb_pages, num_page 0.., last_page, num_page_folio 1..) – 4 Zeilen je Seite
        int pages = System.Math.Clamp((System.Math.Min(cars.Count, 12) + 3) / 4, 1, 3);
        ui.SetVariable(txt + ":nb_pages", pages.ToString());
        ui.SetVariable(txt + ":last_page", (pages - 1).ToString());
        ui.SetVariable(txt + ":num_page", "0");
        ui.SetVariable(txt + ":prev_page", "0");
        ui.SetVariable(txt + ":num_page_folio", "1");
        ui.SetVariable(txt + "/folio:nb_pages", pages.ToString());
        ui.SetVariable(txt + "/folio:num_page_folio", "1");
        for (int i = 0; i < 12; i++)
        {
            string li = txt + "/pages/li" + i;
            if (i >= cars.Count) { ui.GotoLabel(li, "default", false); continue; }
            var c = cars[i];
            ui.GotoLabel(li, "cars", false);
            ui.GotoLabel(li + "/disp", "default", false);
            ui.SetVariable(li + "/disp:txt", ((c.Brand ?? "") + " " + c.ModelName).Trim());
            ui.GotoLabel(li + "/disp/gp", GroupLabel(c.Raw != null ? c.Raw.I("_vehiclecategory") : 0), false);
            ui.GotoLabel(li + "/disp/chck", "off", false);
            ui.GotoLabel(li + "/disp/car", "set", false);
        }
    }

    /// <summary>Logos (DBBrands._logo_global_index) der Marken eines Tuners: je Marke der Autos in DBCarShop._slot_1..9, in dieser Reihenfolge.</summary>
    List<int> TunerLogos(Spot s)
    {
        var r = new List<int>();
        var db = Db; var shop = db?.ShopForSpot(s, IslandName());
        if (shop == null || db == null) return r;
        foreach (int id in shop.CarIds)
            if (id != 0 && db.ById.TryGetValue(id, out var c) && c.LogoIndex >= 0 && !r.Contains(c.LogoIndex)) r.Add(c.LogoIndex);
        return r;
    }

    /// <summary>Besitz-/Angebotszahlen eines Kleidungsladens in der ausgeklappten Infokarte setzen (chbr / FillExtendedInfos).</summary>
    void FillClothesInfos(Spot s)
    {
        if (_ui == null || s == null) return;
        int sex = PlayerUtils.GetAvatarSex();
        var clothes = AvatarUtils.GetClothesCountFromSpot(s, sex);
        var parts = AvatarUtils.GetExtraPartsCountFromSpot(s, sex);
        Log.Info("Karte: Kleidung " + s.Code + " Besitz/Angebot " + clothes.Owned + "/" + clothes.Available
            + " (Hüte " + parts.Hats.Owned + "/" + parts.Hats.Available
            + ", Oberteile " + parts.Pullovers.Owned + "/" + parts.Pullovers.Available
            + ", Hosen " + parts.Pants.Owned + "/" + parts.Pants.Available
            + ", Sonnenbrillen " + parts.Sunglasses.Owned + "/" + parts.Sunglasses.Available
            + ", Handschuhe " + parts.Gloves.Owned + "/" + parts.Gloves.Available
            + ", Schuhe " + parts.Shoes.Owned + "/" + parts.Shoes.Available + ")");

        _ui.SetObjectText("/tool_tip/infos/#122", clothes.Owned.ToString());
        _ui.SetObjectText("/tool_tip/infos/#123", clothes.Available.ToString());
        void SetItem(string path, AvatarUtils.ShopStockCount c)
        {
            _ui.SetObjectText(path + "/#4", c.Owned.ToString());
            _ui.SetObjectText(path + "/#6", c.Available.ToString());
        }
        SetItem("/tool_tip/infos/txt/pages/item0", parts.Hats);
        SetItem("/tool_tip/infos/txt/pages/item1", parts.Pullovers);
        SetItem("/tool_tip/infos/txt/pages/item2", parts.Pants);
        SetItem("/tool_tip/infos/txt/pages/item3", parts.Sunglasses);
        SetItem("/tool_tip/infos/txt/pages/item4", parts.Gloves);
        SetItem("/tool_tip/infos/txt/pages/item5", parts.Shoes);
    }

    void HideTooltip()
    {
        if (_ttipOpen && _ui != null) _ui.CallClip("/tool_tip", "Close");
        _ttipOpen = false;
    }

    // ------------------------------------------------------------------ Eingabe
    void HandleInput(float dt)
    {
        if (_openFrames < 3) return;
        var ui = _ui!;
        Vector3 north = North(), east = East();
        Vector3 move = Vector3.Zero;
        if (_extended || _miniMenu)
        {
            // ausgeklappter Tooltip / Mini-Menü: Pfeile gehören dem Film (Blättern der Liste, Menüauswahl) statt der Kamera
            if (TDU2Controls.Pressed("menu_left")) ui.Input("Move", "left");
            if (TDU2Controls.Pressed("menu_right")) ui.Input("Move", "right");
            if (TDU2Controls.Pressed("menu_up")) { if (!MenuCall("Move", "up")) ui.Input("Move", "up"); }
            if (TDU2Controls.Pressed("menu_down")) { if (!MenuCall("Move", "down")) ui.Input("Move", "down"); }
        }
        else
        {
            if (TDU2Controls.Held("menu_up")) move += north;
            if (TDU2Controls.Held("menu_down")) move -= north;
            if (TDU2Controls.Held("menu_right")) move += east;
            if (TDU2Controls.Held("menu_left")) move -= east;
        }
        _view.Pan(move, dt, IsDown(0x10));   // Umschalt = schneller

        // Ziehen mit der Maus (Weltpunkt unter der Maus bleibt unter der Maus)
        var mp = MousePixel();
        if (FlashInput.GetMouseButtonDown(0) && !((_extended || _miniMenu) && ui.MouseOverButton)) { _dragging = true; _lastMouse = mp; }
        if (FlashInput.GetMouseButtonDown(1) || FlashInput.GetMouseButtonDown(2)) { _dragging = true; _lastMouse = mp; }
        if (!FlashInput.GetMouseButton(0) && !FlashInput.GetMouseButton(1) && !FlashInput.GetMouseButton(2)) _dragging = false;
        if (_dragging)
        {
            _view.Drag(_lastMouse, mp);
            _lastMouse = mp;
        }

        // Zoom über den Film (Animationen von Cursor/Spots wie im Spiel): Mausrad, Bild↑/↓, +/-, E/Q
        float wheel = FlashInput.mouseScrollDelta;
        bool zin = wheel > 0.1f || _ctx.Pressed(0x21) || _ctx.Pressed(0x6B) || _ctx.Pressed(0xBB) || _ctx.Pressed(0x45);
        bool zout = wheel < -0.1f || _ctx.Pressed(0x22) || _ctx.Pressed(0x6D) || _ctx.Pressed(0xBD) || _ctx.Pressed(0x51);
        // an den Grenzen nicht an den Film geben (weiter rauszoomen startet dort den Abspann)
        if (zin && _view.Zoom > 1) { if (!ui.CallClip("/navigator", "setZoom", "in")) SetZoom(_view.Zoom - 1); }
        if (zout && _view.Zoom < 4) { if (!ui.CallClip("/navigator", "setZoom", "out")) SetZoom(_view.Zoom + 1); }

        // Leertaste: zum Fahrzeug
        var vm = _ctx.Vehicles?.Manager; var s = _ctx.Terrain?.Streamer;
        if (_ctx.Pressed(0x20) && vm?.Current != null && s != null) { _view.Target = s.GameToRender(vm.PositionGame); _view.Target.Y = SeaY(); }

        // A wie im Spiel: auf einem Spot -> Tooltip mit Infos; auf freier Fläche -> Mini-Menü. Klick ohne Ziehen; über Knöpfen
        // des ausgeklappten Tooltips und über den Leisten des Films gehört der Klick dem Film.
        bool click = FlashInput.GetMouseButtonUp(0) && (mp - _downMouse).LengthSquared() < 36f && !((_extended || _miniMenu) && ui.MouseOverButton) && !OverFilmBar(mp);
        if (FlashInput.GetMouseButtonDown(0)) _downMouse = mp;
        if (FlashInput.GetMouseButtonDown(1)) _downMouseR = mp;
        // Rechtsklick ohne Ziehen: GPS-Ziel direkt setzen/entfernen (auf einem Spot: am Spot)
        if (FlashInput.GetMouseButtonUp(1) && (mp - _downMouseR).LengthSquared() < 36f && !_extended && !_miniMenu)
        {
            var gw = GroundPoint(mp); var sm = _ctx.Terrain?.Streamer;
            if (_hover >= 0 && _hover < _spots.Count) ToggleGpsTarget(_spots[_hover].spot.Position);
            else if (gw.HasValue && sm != null) ToggleGpsTarget(sm.RenderToGame(gw.Value));
        }
        if (_miniMenu && click) click = false;   // Mini-Menü offen: Klicks gehören dem Film; schließen mit rechts/Esc
        if (_miniMenu && FlashInput.GetMouseButtonUp(1) && (mp - _downMouseR).LengthSquared() < 36f) ui.Input("B");
        if (click || TDU2Controls.ConfirmPressed())
        {
            Log.Info("Karte: " + (click ? "Klick" : "Enter") + " – Spot " + (_hover >= 0 && _hover < _spots.Count ? _spots[_hover].label + " " + _spots[_hover].spot.Code : "-") + ", Tooltip " + _extended + ", Mini-Menü " + _miniMenu);
            if (click) ui.ConsumeClick();   // der Film soll das Loslassen nicht mehr sehen
            if (!_miniMenu) _cursorWorld = GroundPoint(mp);
            if (_extended || _miniMenu) { if (click || !MenuCall("A")) ui.Input("A"); }
            else if (_hoverPlayer >= 0) { _selectedPlayer = _hoverPlayer; _selected = -1; ui.Input("A"); }   // Mitspieler: Infos
            else if (_hover >= 0)
            {
                // Spot: Tooltip mit Infos (bmap:ttip:extd)
                _selected = _hover; _selectedPlayer = -1;
                var sm = _ctx.Terrain?.Streamer;
                _menuWorld = sm != null ? sm.GameToRender(_spots[_hover].spot.Position) : null;
                _menuTargetPresent = Gps.Instance != null && Gps.Instance.IsTargetNear(_spots[_hover].spot.Position, 40f);
                ui.SetValue("/:IsTargetPresent", _menuTargetPresent);
                ui.SetValue("/:IsSpotFavorite", TDU2Bookmarks.IsFavorite(_spots[_hover].spot.Code));
                ui.SetValue("/:IsTeleportOK", true);
                ui.Input("A");
            }
            else
            {
                // freie Fläche: Mini-Menü des Films (Teleport nur auf Straßen; GPS-Ziel/Lesezeichen folgen mit ihrem Port – gesperrt)
                _selected = -1; _selectedPlayer = -1;
                _menuWorld = _cursorWorld;
                ui.SetVariable("/:WhatSpot", "noSpot");
                ui.SetVariable("/:WhatSpotType", "none");
                ui.SetValue("/:IsTeleportOK", OnRoad(out _));
                _menuTargetPresent = GpsTargetUnderMouse(mp);
                ui.SetValue("/:IsTargetPresent", _menuTargetPresent);
                ui.SetValue("/:IsTargetOK", _contestCup < 0);
                // Wettbewerb: freie Fläche = Teleport (nur auf Straßen), sonst Mini-Menü des Films
                if (_contestCup >= 0) { if (OnRoad(out _)) { TeleportToCursor(); Close(); } else FrontendSounds.Play("wrong"); }
                else ui.Input("A");
            }
        }

        // Filterleiste: Tab / Umschalt+Tab (wie die Pfeile im Film)
        if (_ctx.Pressed(0x09) && _contestCup < 0 && !_extended && !_miniMenu) ui.CallClip("/presets", "Move", IsDown(0x10) ? "left" : "right");
        ClampTarget();
    }

    static bool IsDown(int vk) => TDU2Controls.KeyDown(vk);

    // ------------------------------------------------------------------ GPS-Ziel (Unity TDU2BigMap)
    /// <summary>
    /// GPS-Ziel auf der Karte: Clip /target des Films (map.vmf, Labels default/open/loop – blendet sich bei "open" ein und
    /// läuft dann in "loop").
    /// </summary>
    void UpdateGpsMarker()
    {
        var gps = Gps.Instance; var s = _ctx.Terrain?.Streamer; var ui = _ui!;
        bool show = gps != null && gps.HasTarget && s != null;
        Vector2? gui = show ? ToGui(s!.GameToRender(gps!.TargetGame)) : null;
        var lp = gui.HasValue ? ui.GuiToLocal("/", F(gui.Value)) : null;
        show &= lp.HasValue;
        if (show)
        {
            if (!_gpsMarkerShown || _gpsMarkerFor != gps!.TargetGame)
            {
                bool ok = ui.GotoLabel("/target", "open", true);
                _gpsMarkerFor = gps!.TargetGame;
                Log.Info($"Karte: GPS-Ziel /target {(ok ? "eingeblendet" : "nicht gefunden (Clip/Label fehlt)")} bei {gui!.Value}");
            }
            ui.SetPosition("/target", lp!.Value);
        }
        if (show != _gpsMarkerShown || !show) ui.SetVisible("/target", show);
        _gpsMarkerShown = show;
    }

    /// <summary>Punkt des Mini-Menüs bzw. gewählten Spots in Spielkoordinaten.</summary>
    Vector3? MenuPoint()
    {
        var s = _ctx.Terrain?.Streamer;
        if (s == null) return null;
        if (_selected >= 0 && _selected < _spots.Count) return _spots[_selected].spot.Position;
        var w = _menuWorld ?? _cursorWorld;
        return w.HasValue ? s.RenderToGame(w.Value) : null;
    }

    /// <summary>GPS-Ziel unter dem Mauszeiger (Bildschirmabstand wie die Spot-Auswahl)?</summary>
    bool GpsTargetUnderMouse(Vector2 mouse)
    {
        var gps = Gps.Instance; var s = _ctx.Terrain?.Streamer;
        if (gps == null || !gps.HasTarget || s == null) return false;
        var g = ToGui(s.GameToRender(gps.TargetGame));
        if (!g.HasValue) return false;
        float r = 22f * (FlashScreen.height / 720f);
        return (g.Value - mouse).LengthSquared() < r * r;
    }

    /// <summary>GPS-Ziel an diesem Punkt (Spielkoordinaten) setzen; liegt dort schon das Ziel: entfernen.</summary>
    void ToggleGpsTarget(Vector3? game, bool? present = null, bool sound = true)
    {
        var gps = Gps.Instance;
        if (gps == null || !game.HasValue) { if (sound) FrontendSounds.Play("wrong"); return; }
        bool under = _selected < 0 && GpsTargetUnderMouse(MousePixel());
        bool remove = present ?? (gps.HasTarget && (gps.IsTargetNear(game.Value, 40f) || under));
        if (remove && gps.HasTarget) gps.ClearTarget();
        else gps.SetTarget(game.Value);
        _ui!.SetValue("/:IsTargetPresent", gps.HasTarget);
        if (sound) FrontendSounds.Play(gps.HasTarget ? "dropflag" : "valid");
        _slotLabel.Clear();   // Unity BuildSpots: Symbole (Zielfähnchen) neu aufbauen
    }

    // ------------------------------------------------------------------ Teleport (gotp / goin) – Unity TDU2BigMap
    /// <summary>Straße unter dem Cursor (bis ~2,5 % der Sichtbreite, mind. 25 m) – Spielkoordinaten mit Bodenhöhe.</summary>
    bool OnRoad(out Vector3 game)
    {
        game = default;
        var mm = _ctx.Vehicles?.Manager?.Minimap; var s = _ctx.Terrain?.Streamer;
        if (mm == null || s == null || !_cursorWorld.HasValue) return false;
        var g = s.RenderToGame(_cursorWorld.Value);
        if (!mm.NearestRoad(g, MathF.Max(25f, _view.CurWidth * 0.025f), out var rp)) return false;
        game = new Vector3(rp.X, s.TryGetMapGroundHeight(rp.X, rp.Z, out float y) ? y : float.NaN, rp.Z);
        return true;
    }

    void TeleportToCursor()
    {
        if (!OnRoad(out var w)) return;
        // Fahrtrichtung entlang der Straße: Nachbarpunkt suchen
        Vector3? dir = null;
        var mm = _ctx.Vehicles!.Manager!.Minimap!;
        var g = new Vector3(w.X, 0f, w.Z);
        if (mm.NearestRoad(g + new Vector3(12f, 0f, 0f), 10f, out var n1) && (n1 - g).LengthSquared() > 1f) dir = n1 - g;
        TeleportTo(w, dir);
    }

    void TeleportToSpot(Spot? s)
    {
        if (s == null) { TeleportToCursor(); return; }
        // Spot mit Interieur (Händler, Haus, Club, Laden …): nach dem Schließen der Karte direkt hinein
        if (s.Index >= 0 && !string.IsNullOrEmpty(s.Code) && !s.Code.StartsWith("#") && !InteriorUtils.Inside)
        { _pendingEnter = s; return; }
        TeleportOutside(s);
    }

    void TeleportOutside(Spot s)
    {
        // Einfahrt (Position2), sonst Spot-Position; Blick zum Spot
        bool p2 = s.Position2.LengthSquared() > 1f && !float.IsNaN(s.Position2.X);
        var at = p2 ? s.Position2 : s.Position;
        var look = s.Position - at; look.Y = 0f;
        TeleportTo(at, look.LengthSquared() > 1f ? look : null);
    }

    void TeleportTo(Vector3 game, Vector3? dir)
    {
        var vm = _ctx.Vehicles?.Manager; var s = _ctx.Terrain?.Streamer;
        if (vm?.Current == null || s == null) return;
        // Zielhöhe aus den Spieldaten: das Auto wartet dort, bis der Boden geladen ist
        vm.TeleportSafe(game, dir ?? vm.ForwardGame);
        _view.Target = s.GameToRender(new Vector3(game.X, 0f, game.Z)); _view.Target.Y = SeaY();
        Log.Info($"Karte: Teleport nach {game}");
    }

    /// <summary>Menü des ausgeklappten Tooltips bzw. Mini-Menüs direkt ansprechen (Unity MenuCall).</summary>
    bool MenuCall(string fn, params object[] args)
    {
        if (_ui == null) return false;
        string? path = _miniMenu ? "/mini_menu/menu" : _extended ? "/tool_tip/infos/menu" : null;
        var d = path != null ? _ui.Find(path) : null;
        if (d?.Sprite == null || !_ui.IsShown(d.Sprite)) return false;
        return _ui.CallClip(path!, fn, args);
    }

    /// <summary>Maus über der Filterleiste bzw. Infoleiste des Films (GUI-Pixel, y nach unten)?</summary>
    static bool OverFilmBar(Vector2 g)
    {
        float w = FlashScreen.width, h = FlashScreen.height;
        return (g.X < w * 0.45f && g.Y < h * 0.2f) || (g.X > w * 0.62f && g.Y < h * 0.18f) || g.Y > h * 0.87f;
    }

    // Kartenansicht (Kamera, Meeresebene, Begrenzung): Gamemodes/Utils/MapView.cs
    static Vector2 MousePixel() => MapView.MousePixel();
    static Vector3 North() => MapView.North;
    static Vector3 East() => MapView.East;
    float SeaY() => _view.SeaY();
    Vector3? GroundPoint(Vector2 pixel) => _view.GroundPoint(pixel);
    void ClampTarget() => _view.ClampTarget();
    void UpdateCamera(bool snap) => _view.UpdateCamera(snap);

    static string GroupLabel(int cat) => PlayerCarInfo.GroupLabel(cat);


    /// <summary>
    /// Flash Map Commands
    /// </summary>
    /// <param name="cmd"></param>
    /// <param name="args"></param>
    void OnCommand(string cmd, string args)
    {
        if (!_open)  return;
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("sndp:")) return;
        if (c.StartsWith("bmap:fltr:icf"))
        {
            if (int.TryParse(c.Substring(13), out int f)) { _filter = System.Math.Clamp(f, 0, 5); _hover = -1; HideTooltip(); }
            return;
        }
        switch (c)
        {
            case "zoin": SetZoom(_view.Zoom - 1); break;   // setZoom("in")  -> Spiel verkleinert WhatZoom
            case "zoou": SetZoom(_view.Zoom + 1); break;   // setZoom("out")
            case "gofr": BeginClosing(); break;       // B im Navigator: zurück ins freie Fahren (Schließanimation läuft)
            case "bmap:ttip:extd":                    // A auf einem Spot: Tooltip mit Infos/Menü ausklappen
                _ui!.CallClip("/tool_tip", "Extend", 1);
                _ui.SetValue("/:Input", true);
                _extended = true;
                _fillAt = _clock + 0.15f;             // Liste nach dem Umschalten der Seiten füllen
                break;
            case "bmap:ttip:redu": _extended = false; break;   // Tooltip wieder eingeklappt
            case "bmap:ttip:mecl": break;                       // Menü des Tooltips zu
            case "bmap:ttip:meop": _miniMenu = true; break;     // Mini-Menü offen (Eingabe gehört dem Film)
            case "inon": _miniMenu = false; break;              // Menü zu, Eingabe wieder an die Karte
            case "gotp":                              // Teleport an die Cursorposition (bzw. die des Mini-Menüs)
                if (_menuWorld.HasValue) _cursorWorld = _menuWorld;
                TeleportToCursor(); BeginClosing(); break;
            case "coin":                              // Wettbewerb der Meisterschaft betreten (A auf dem Meisterschafts-Spot)
                {
                    int cup = _contestCup >= 0 ? _contestCup : (_selected >= 0 && _selected < _spots.Count ? _spots[_selected].cup : -1);
                    if (cup < 0 && _hover >= 0 && _hover < _spots.Count) cup = _spots[_hover].cup;
                    if (cup >= 0 && cup != _contestCup) EnterContest(cup);
                    break;
                }
            case "coou":                              // Wettbewerb verlassen: vom Treffpunkt -> Karte zu, sonst normale Karte
                if (_contestDirect) { _contestCup = -1; BeginClosing(); }
                else LeaveContest();
                break;
            case "giup":                              // Meisterschaft aufgeben
                if (_contestCup >= 0) { Championships.GiveUp(_contestCup); SetContestVars(_contestCup); BuildSpots(); }
                break;
            case "mcen":                              // Menü des Tooltips bestätigt ("Teilnehmen" eines Meisterschaftsrennens)
                if (_contestCup >= 0 && _selected >= 0 && _selected < _spots.Count && _spots[_selected].race >= 0)
                {
                    _pendingStart = (_spots[_selected].cup, _spots[_selected].race);
                    Log.Info("Karte: Meisterschaftsrennen " + (_spots[_selected].race + 1) + " gewählt");
                    HideTooltip(); _extended = false;
                    Close();
                }
                break;
            case "goin":                              // zum Spot teleportieren (Menü des Tooltips) bzw. zum Mitspieler
                if (_contestCup >= 0 && _selected >= 0 && _selected < _spots.Count && _spots[_selected].race >= 0)
                {
                    // Rennen der Meisterschaft: nach dem Schließen an den Start und los
                    _pendingStart = (_spots[_selected].cup, _spots[_selected].race);
                    BeginClosing(); break;
                }
                if (_selectedPlayer >= 0) TeleportToPlayer(_selectedPlayer);
                else TeleportToSpot(_selected >= 0 && _selected < _spots.Count ? _spots[_selected].spot : null);
                BeginClosing(); break;
            // Mitspieler (Tooltip-Menü): Beitreten über die Ladeanzeige join_player (cmon_joinPlayer wie im Pausemenü):
            // joinPlayer() -> "join:wait" (Lader wartet auf _root.CanJoinPlayer) -> endLoading -> "pjoi"; Fehler: JoinError + timeOutLoading
            case "join:wait":
                {
                    string err = "Spieler nicht gefunden.";
                    bool ok = _selectedPlayer >= 0 && Paradise.Game.Online.Party.CanJoin(_selectedPlayer, out err);
                    Log.Info($"Karte: Beitreten {_selectedPlayer}: " + (ok ? "ok" : err));
                    if (ok) _ui!.SetValue("/:CanJoinPlayer", true);
                    else
                    {
                        _ui!.SetVariable("/:JoinError", err);
                        if (!_ui.CallClip("/join_player", "timeOutLoading")) _ui.SetValue("/:Input", true);
                    }
                    break;
                }
            case "join:canc": break;
            case "pjoi":
                if (_selectedPlayer >= 0) TeleportToPlayer(_selectedPlayer);
                BeginClosing(); break;
            case "pinv":                              // Mitspieler einladen (Tooltip-Menü)
                if (_selectedPlayer >= 0) Paradise.Game.Online.Party.Invite(_selectedPlayer);
                break;
            case "targ":                              // GPS-Ziel setzen bzw. entfernen (Sound spielt der Film: sndp:valid/dropflag)
                ToggleGpsTarget(MenuPoint(), _menuTargetPresent, false); break;
            case "tpok":                              // Teleport möglich? (nur auf Straßen, wie im Spiel)
                _ui!.SetValue("/:IsTeleportOK", OnRoad(out _)); break;
            case "fiou": case "fiin": case "fity": case "stof": case "ston": case "mnok": break;   // Animationsmeldungen der Filterleiste
            case "bmrk:enbl":                         // Lesezeichen an der Cursorposition (Unity TDU2BigMap)
                {
                    var g = MenuPoint(); var sm = _ctx.Terrain?.Streamer;
                    if (g.HasValue && TDU2Bookmarks.CanAdd)
                    {
                        // Höhe vom Gelände (Cursor liegt auf Meereshöhe), damit das Symbol beim Kippen der Karte an der Stelle bleibt
                        var p = g.Value;
                        if (sm != null && sm.RaycastDown(new Vector3(p.X, p.Y + 3000f, p.Z), 0f, 6000f, out float gy)) p.Y = gy;
                        TDU2Bookmarks.Add(IslandName(), p); BuildSpots();
                        _ui!.CallClip("/presets/ttip", "Open");   // Meldung "Lesezeichen hinzugefügt" (schließt sich selbst)
                    }
                    _ui!.SetValue("/:CanAddMySpot", TDU2Bookmarks.CanAdd);
                    break;
                }
            case "bmrk:dsbl":                         // Lesezeichen entfernen
                {
                    var ms = _selected >= 0 && _selected < _spots.Count ? _spots[_selected] : (_hover >= 0 && _hover < _spots.Count ? _spots[_hover] : null);
                    if (ms != null && ms.bookmark >= 0) { TDU2Bookmarks.Remove(ms.bookmark); _selected = -1; HideTooltip(); BuildSpots(); }
                    _ui!.SetValue("/:CanAddMySpot", TDU2Bookmarks.CanAdd);
                    break;
                }
            case "mysp:enbl":                         // Spot als Favorit
            case "mysp:dsbl":
                {
                    var ms = _selected >= 0 && _selected < _spots.Count ? _spots[_selected] : null;
                    if (ms != null && ms.bookmark < 0) TDU2Bookmarks.SetFavorite(ms.spot.Code, c == "mysp:enbl");
                    _ui!.SetValue("/:IsSpotFavorite", ms != null && TDU2Bookmarks.IsFavorite(ms.spot.Code));
                    break;
                }
            case "bmap:ttip:inf":
            case "chbr": // Besitz-/Angebotszahlen der Infokarte (Shops)
                {
                    var ms = _selected >= 0 && _selected < _spots.Count ? _spots[_selected] : (_hover >= 0 && _hover < _spots.Count ? _spots[_hover] : null);
                    if (ms == null) break;
                    Log.Info("Karte: chbr " + ms.spot.Code + " (" + ms.label + ")");
                    if (ms.label == "clothes")
                    {

                        int sex = PlayerUtils.GetAvatarSex();
                        var clothes = AvatarUtils.GetClothesCountFromSpot(ms.spot, sex);
                        var parts = AvatarUtils.GetExtraPartsCountFromSpot(ms.spot, sex);
                        _ui.SetText("/:NbClothes", clothes.Owned.ToString());
                        _ui.SetText("/:NbClothesMax", clothes.Available.ToString());
                        FillClothesInfos(ms.spot);
                    }
                }
                break;
            default: { Log.NotImplemented($"Missing Map Command: {c} isExtended: {_extended} or isMiniMenue: {_miniMenu}"); } break;
        }
    }

}
