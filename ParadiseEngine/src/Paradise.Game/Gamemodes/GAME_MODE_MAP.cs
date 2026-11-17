using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Online;
using Paradise.Game.Input;
using Paradise.Game.Screens;
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
/// Noch nicht portiert (folgt): Tooltip-Inhalte (Händler/Häuser, Bilder, Logos), GPS-Ziel/Lesezeichen/Lieblings-Spots, Teleport,
/// Mini-Menü, Mitspieler, Wettbewerbsmodus (Meisterschaften).
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
        Attach(ctx.Open(ScreenDefs.Map));
        if (!IsOpen && !Open()) GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        if (!Update(time.DeltaTime)) GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    /// <summary>Unity TDU2BigMap.DrawRoads: Straßennetz (und GPS-Route) über dem Kartengelände (MapMetersPerPixel 0 = ShowRoads aus).</summary>
    public override void Render(IGameModeContext ctx, in GameTime time)
    {
        var t = ctx.Terrain; var s = t?.Streamer; var vm = ctx.Vehicles?.Manager; var r = ctx.Vehicles?.Renderer;
        if (t == null || s == null || vm?.Minimap == null || r == null || !t.MapView || t.MapMetersPerPixel <= 0f) return;
        vm.Minimap.DrawMapRoads(r, s.RootMatrix, t.MapMetersPerPixel, (x, z) => s.TryGetMapGroundHeight(x, z, out float y) ? y + 1f : 0f);
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        Finish();
        // Teleport zu Händler/Haus/Club …: wie im Spiel gleich hinein (Unity EnterWhenReady)
        var pe = _pendingEnter; _pendingEnter = null;
        if (pe != null) { TeleportOutside(pe); InteriorUtils.EnterWhenReady(ctx, pe); }
    }

    /// <summary>Spot mit Interieur, das nach dem Schließen der Karte betreten wird (Unity _pendingEnter).</summary>
    Spot? _pendingEnter;

    // Kamera je Zoomstufe (WhatZoom 1 = nah … 4 = ganze Insel) – Unity-Inspektorwerte
    public float[] ViewWidth = { 1536f, 6144f, 0f, 0f };
    public float[] Inclination = { 45f, 25f, 0f, 0f };
    public float FarFactor = 0.5f;
    public float FieldOfView = 40f;
    public float Smooth = 4f;
    public float PanSpeed = 0.25f;

    public bool IsOpen => _open;
    bool _open, _closing;
    float _closeAt, _clock;
    int _zoom = 3, _openFrames;
    Vector3 _target;               // Kartenmitte (Render-Raum, Höhe = Meeresspiegel)
    float _curWidth, _curIncl;
    /// <summary>Straßennetz über der Karte zeichnen (Unity TDU2BigMap.ShowRoads).</summary>
    public bool ShowRoads = true;
    bool _savedMinimap = true, _savedHud = true;
    float _savedFov, _savedNear, _savedFar;
    bool _dragging; Vector2 _lastMouse;
    static int _filter;            // Reiter der Filterleiste (_root.numIcFilter), bleibt bis zum nächsten Öffnen

    // Spots (Unity BuildSpots/UpdateOverlay): Kopien von /pack/point je Platz, nächste zum Kartenmittelpunkt zuerst
    public float SpotScale = 0.75f;
    public int MaxSpots = 160;
    sealed class MapSpot { public Spot spot = null!; public string label = ""; public int bookmark = -1; }
    readonly List<MapSpot> _spots = new();
    readonly Dictionary<int, string> _slotLabel = new();
    int _pointChar, _pointDepth0 = 1000, _hover = -1;
    bool _ttipOpen, _cursorOverSpot, _lod;
    Dictionary<string, string>? _texts;
    // Tooltip ausgeklappt / Mini-Menü offen (Eingabe gehört dann dem Film), gewählter Spot, Füllen der Liste nach dem Umschalten
    bool _extended, _miniMenu;
    int _selected = -1;
    float _fillAt = -1f;
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
    public bool Open()
    {
        var ui = _ui; var terrain = _ctx.Terrain; var cam = terrain?.Camera; var s = terrain?.Streamer;
        if (ui == null || terrain == null || cam == null || s == null || _open) return false;
        _open = true; _closing = false; _openFrames = 0; _clock = 0f;

        // Spiel anhalten wie das Pausemenü, HUD aus
        var vehicles = _ctx.Vehicles; var vm = vehicles?.Manager;
        if (vehicles != null) vehicles.Frozen = true;
        AudioEngine.Paused = true;   // Unity: AudioListener.pause = true (Menü-Sounds laufen weiter)
        // Kartenmusik wie im Spiel (MngMusic "MAP" → sound/musics/map.wav, 0,5); Spot-Musik im Interieur solange aus
        Paradise.Core.Audio.SpotMusic.Duck(true);
        Paradise.Core.Audio.FrontendMusic.Play("MAP");
        if (vm?.Minimap != null) { _savedMinimap = vm.Minimap.ShowMinimap; vm.Minimap.ShowMinimap = false; }
        if (vm != null) { _savedHud = vm.ShowHud; vm.ShowHud = false; vm.ApplyHudVisibility(); }

        // Kamera
        _savedFov = terrain.FieldOfView; _savedNear = terrain.NearClip; _savedFar = terrain.FarClip;
        terrain.FlyCameraEnabled = false;
        terrain.StreamFocus = cam.Position;   // Unity: eigene Kartenkamera – Streaming/Ursprung bleiben an der Spielkamera
        terrain.MapView = true;
        s.RequestMapTerrain();   // falls noch nicht im Hintergrund geladen
        terrain.FieldOfView = FieldOfView;
        var veh = vm?.Current;
        _target = veh != null ? s.GameToRender(vm!.PositionGame) : cam.Position;
        _target.Y = SeaY();
        _zoom = 2;
        UpdateCamera(true);

        ui.SetValue("/:WhatPlatform", 1);
        ui.SetVariable("/:MenuType", "pc");
        ui.SetValue("/:IsPC_X360", true);
        ui.SetValue("/:fake", false);
        ui.SetValue("/:MapMode", 0);
        ui.SetValue("/:WhatZoom", _zoom);
        ui.SetValue("/:Input", true);
        ui.SetValue("/:IsContest", false);
        ui.SetValue("/:ContestDirectAccess", false);
        ui.SetValue("/:IsFirstTime", 0);
        ui.SetValue("/:LockGiveUp", 0);
        ui.SetValue("/:IsResultsDone", 1);
        ui.SetVariable("/:WhatSpot", "noSpot");
        ui.SetVariable("/:WhatSpotType", "none");
        ui.SetValue("/:IsTutorial", 0);
        ui.SetValue("/:IsTargetOK", true);
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
        ui.SetVariable("/:money", TDU2Session.LoggedIn
            ? TDU2Session.Save.money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $" : "999 999 999 $");
        var car = veh?.Car;
        ui.SetVariable("/:cur_upgrade", Paradise.Core.Vehicles.CarUpgrades.LevelLabel(Paradise.Core.Vehicles.CarUpgrades.Get(car?.FileName).Global));   // Tuningstufe (Tuner)
        ui.SetVariable("/:player_group", GroupLabel(car?.Raw != null ? car.Raw.I("_vehiclecategory") : 0));
        if (car != null)
        {
            string name = ((car.Brand ?? "") + " " + car.ModelName).Trim();
            ui.SetVariable("/:player_car", name);
            ui.SetVariable("/:m_MapPlayerVehicle", name);
        }
        if (_texts == null)
        {
            try { _texts = FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language); }
            catch (Exception ex) { Log.Warn("Karte: Texte nicht geladen: " + ex.Message); _texts = new(); }
        }
        BuildSpots();
        _gpsMarkerShown = false; ui.SetVisible("/target", false);   // Unity: GPS-Ziel beim Öffnen neu einblenden ("open")
        ui.Visible = true;
        ui.GotoLabel("/", "map_open", true);
        // Laufschrift des Autonamens neu messen (Bild 0 von /infos/car liest _root.player_car per getTextLength)
        ui.GotoLabel("/infos/car", "empty", false);
        ui.GotoLabel("/infos/car", "set", false);
        Log.Info("Große Karte geöffnet");
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
        var terrain = _ctx.Terrain;
        if (terrain != null)
        {
            terrain.MapView = false;
            terrain.StreamFocus = null;
            terrain.FieldOfView = _savedFov; terrain.NearClip = _savedNear; terrain.FarClip = _savedFar;
        }
        var vehicles = _ctx.Vehicles; var vm = vehicles?.Manager;
        if (vehicles != null) vehicles.Frozen = false;
        AudioEngine.Paused = false;
        Paradise.Core.Audio.FrontendMusic.Stop();
        Paradise.Core.Audio.SpotMusic.Duck(false);
        if (vm?.Minimap != null) vm.Minimap.ShowMinimap = _savedMinimap;
        if (vm != null) vm.ShowHud = _savedHud;
        Log.Info("Große Karte geschlossen");
    }

    // ------------------------------------------------------------------ Jedes Bild
    /// <summary>Unity Update (Karte offen). Rückgabe false = Karte zu (Spielmodus zurück ins Fahren).</summary>
    public bool Update(float dt)
    {
        _clock += dt;
        _lastDt = MathF.Max(1e-4f, dt);
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

    void OnCommand(string cmd, string args)
    {
        if (!_open) return;
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("sndp:")) return;   // Menü-Sounds (FrontendSounds)
        Log.Info("Karte FSCommand: " + c + (string.IsNullOrEmpty(args) ? "" : " " + args) + " (Tooltip " + _extended + ", Mini-Menü " + _miniMenu + ")");
        if (c.StartsWith("bmap:fltr:icf"))
        {
            // Reiter der Filterleiste gewechselt (presets.sendCmdIc)
            if (int.TryParse(c.Substring(13), out int f)) { _filter = System.Math.Clamp(f, 0, 5); _hover = -1; HideTooltip(); }
            return;
        }
        switch (c)
        {
            case "zoin": SetZoom(_zoom - 1); break;   // setZoom("in")  -> Spiel verkleinert WhatZoom
            case "zoou": SetZoom(_zoom + 1); break;   // setZoom("out")
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
            case "goin":                              // zum Spot teleportieren (Menü des Tooltips)
                TeleportToSpot(_selected >= 0 && _selected < _spots.Count ? _spots[_selected].spot : null);
                BeginClosing(); break;
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
            default: break;
        }
    }

    void SetZoom(int z)
    {
        _zoom = System.Math.Clamp(z, 1, 4);
        _ui?.SetValue("/:WhatZoom", _zoom);
        ApplyLod(false);
    }

    // ------------------------------------------------------------------ Spots (Unity TDU2BigMap)
    void BuildSpots()
    {
        var ui = _ui!;
        _spots.Clear();
        var spots = _ctx.Terrain?.Streamer?.Spots;
        if (spots != null)
            foreach (var s in spots.Spots)
            {
                string? label = Minimap.SpotLabel(s.Code);
                if (label == null) continue;
                _spots.Add(new MapSpot { spot = s, label = label });
            }
        // Lesezeichen (nur auf der großen Karte, gespeichert je Spieler und Insel)
        string isl = IslandName();
        foreach (var b in TDU2Bookmarks.All)
            if (string.Equals(b.Island ?? "", isl, StringComparison.OrdinalIgnoreCase))
                _spots.Add(new MapSpot { spot = new Spot { Code = "#BOOKMARK_" + b.Id, Position = b.Pos, Position2 = b.Pos, Index = -1 }, label = "bookmark", bookmark = b.Id });
        Log.Info("Karte: " + _spots.Count + " Spots mit Symbol");
        var tmpl = ui.Find("/pack/point");
        if (tmpl != null) { _pointChar = tmpl.CharId; tmpl.Visible = false; }
        foreach (var kv in _slotLabel) ui.Detach("/pack", _pointDepth0 + kv.Key);
        _slotLabel.Clear();
        _hover = -1; _cursorOverSpot = false; _ttipOpen = false;
        _selected = -1; _extended = false; _miniMenu = false; _fillAt = -1f;
        ApplyLod(true);
    }

    /// <summary>Spot im gewählten Reiter der Filterleiste? 0 Herausforderungen, 2 Orte für den Avatar, 3 Orte für Fahrzeuge,
    /// 5 eigene Orte (Lesezeichen + Favoriten).</summary>
    static bool FilterPass(MapSpot ms)
    {
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
        bool lod = _zoom >= 3;   // weit weg: kleine Punkte (point/spot "lod")
        if (_ui == null || (!force && lod == _lod)) return;
        _lod = lod;
        foreach (var kv in _slotLabel) _ui.CallClip("/pack/point" + kv.Key.ToString("000"), "LOD", lod ? 1 : 0);
    }

    Vector2? ToGui(Vector3 render) => _ctx.Terrain != null && _ctx.Terrain.WorldToScreen(render, out var p) ? p : null;
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
        if (hover != _hover && !_extended && !_miniMenu)
        {
            _hover = hover;
            // Cursor-Ring wie im Spiel: über einem Spot zusammenziehen (spotOver), sonst zurück (spotOut)
            bool over = hover >= 0;
            if (over != _cursorOverSpot) { _cursorOverSpot = over; ui.CallClip("/curseur", over ? "spotOver" : "spotOut"); }
            ui.SetVariable("/:WhatSpot", hover >= 0 ? _spots[hover].label : "noSpot");
            ui.SetVariable("/:WhatSpotType", hover >= 0 ? "spot" : "none");
            if (hover >= 0) ShowTooltip(_spots[hover]); else HideTooltip();
        }
    }

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
        string name = LocalizedSpotName(label, s.Code);
        if (ms.bookmark >= 0) name = (_texts != null && _texts.TryGetValue("m_Bookmark", out var bt) && !string.IsNullOrEmpty(bt) ? bt : "Lesezeichen") + " " + ms.bookmark;
        ui.SetValue("/:IsSpotFavorite", TDU2Bookmarks.IsFavorite(s.Code));
        // wie im Spiel: Name aus der Sprachdatenbank (Händler/Shops DBCarShop._libelle_hc, Häuser DBHouse._housename_hc)
        var dbName = ms.bookmark < 0 ? SpotDbName(s) : null;
        if (!string.IsNullOrEmpty(dbName)) name = dbName;
        else if (label == "carDealer" || label == "bikeDealer") { var b = DealerBrands(s.Code); if (!string.IsNullOrEmpty(b)) name = b; }
        ui.SetVariable("/:SpotName", name);
        ui.SetVariable("/:WhatSpot", label);
        ui.GotoLabel("/tool_tip/ic", label, false);
        if (label == "carDealer" || label == "bikeDealer") ApplyDealerLogo("/tool_tip/ic", s.Code);
        else ui.SetClipTexture("/tool_tip/ic/logo/lure", null);
        ui.GotoFrameNumber("/tool_tip/ic/done", 0);          // Meisterschaften (Haken/Medaille) folgen mit TDU2Championships
        ui.GotoLabel("/tool_tip/ic/rank", "default", false);
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
        if (_ui == null || _selected < 0 || _selected >= _spots.Count) return;
        var ui = _ui;
        var ms = _spots[_selected];
        var s = ms.spot; var label = ms.label;
        ui.SetClipTexture("/tool_tip/infos/picture/lure_map", SpotPicture(s));
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
        float speed = PanSpeed * _curWidth * (IsDown(0x10) ? 2.5f : 1f);   // Umschalt = schneller
        _target += move * speed * dt;

        // Ziehen mit der Maus (Weltpunkt unter der Maus bleibt unter der Maus)
        var mp = MousePixel();
        if (FlashInput.GetMouseButtonDown(0) && !((_extended || _miniMenu) && ui.MouseOverButton)) { _dragging = true; _lastMouse = mp; }
        if (FlashInput.GetMouseButtonDown(1) || FlashInput.GetMouseButtonDown(2)) { _dragging = true; _lastMouse = mp; }
        if (!FlashInput.GetMouseButton(0) && !FlashInput.GetMouseButton(1) && !FlashInput.GetMouseButton(2)) _dragging = false;
        if (_dragging)
        {
            var a = GroundPoint(_lastMouse); var b = GroundPoint(mp);
            if (a.HasValue && b.HasValue) _target += a.Value - b.Value;
            _lastMouse = mp;
        }

        // Zoom über den Film (Animationen von Cursor/Spots wie im Spiel): Mausrad, Bild↑/↓, +/-, E/Q
        float wheel = FlashInput.mouseScrollDelta;
        bool zin = wheel > 0.1f || _ctx.Pressed(0x21) || _ctx.Pressed(0x6B) || _ctx.Pressed(0xBB) || _ctx.Pressed(0x45);
        bool zout = wheel < -0.1f || _ctx.Pressed(0x22) || _ctx.Pressed(0x6D) || _ctx.Pressed(0xBD) || _ctx.Pressed(0x51);
        // an den Grenzen nicht an den Film geben (weiter rauszoomen startet dort den Abspann)
        if (zin && _zoom > 1) { if (!ui.CallClip("/navigator", "setZoom", "in")) SetZoom(_zoom - 1); }
        if (zout && _zoom < 4) { if (!ui.CallClip("/navigator", "setZoom", "out")) SetZoom(_zoom + 1); }

        // Leertaste: zum Fahrzeug
        var vm = _ctx.Vehicles?.Manager; var s = _ctx.Terrain?.Streamer;
        if (_ctx.Pressed(0x20) && vm?.Current != null && s != null) { _target = s.GameToRender(vm.PositionGame); _target.Y = SeaY(); }

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
            else if (_hover >= 0)
            {
                // Spot: Tooltip mit Infos (bmap:ttip:extd)
                _selected = _hover;
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
                _selected = -1;
                _menuWorld = _cursorWorld;
                ui.SetVariable("/:WhatSpot", "noSpot");
                ui.SetVariable("/:WhatSpotType", "none");
                ui.SetValue("/:IsTeleportOK", OnRoad(out _));
                _menuTargetPresent = GpsTargetUnderMouse(mp);
                ui.SetValue("/:IsTargetPresent", _menuTargetPresent);
                ui.SetValue("/:IsTargetOK", true);
                ui.Input("A");
            }
        }

        // Filterleiste: Tab / Umschalt+Tab (wie die Pfeile im Film)
        if (_ctx.Pressed(0x09) && !_extended && !_miniMenu) ui.CallClip("/presets", "Move", IsDown(0x10) ? "left" : "right");
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
        if (!mm.NearestRoad(g, MathF.Max(25f, _curWidth * 0.025f), out var rp)) return false;
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
        _target = s.GameToRender(new Vector3(game.X, 0f, game.Z)); _target.Y = SeaY();
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

    /// <summary>Maus in Pixeln, Ursprung oben links.</summary>
    static Vector2 MousePixel()
    {
        var m = FlashInput.mousePosition;   // Unity: y von unten
        return new Vector2(m.x, FlashScreen.height - m.y);
    }

    // Spiel -Z = oben auf der Karte (Unity North: MapRoot.TransformVector(Vector3.back)); Render-Raum = Spiel + Verschiebung
    static Vector3 North() => new(0f, 0f, -1f);
    static Vector3 East() => new(1f, 0f, 0f);

    /// <summary>Meeresspiegel (Spiel-y 0) im Render-Raum.</summary>
    float SeaY() => _ctx.Terrain?.Streamer?.GameToRender(Vector3.Zero).Y ?? 0f;

    /// <summary>Schnittpunkt des Strahls durch den Bildpunkt mit der Meeresebene (Unity GroundPoint).</summary>
    Vector3? GroundPoint(Vector2 pixel)
    {
        var t = _ctx.Terrain;
        if (t == null || !t.ScreenRay(pixel.X, pixel.Y, out var o, out var d)) return null;
        if (MathF.Abs(d.Y) < 1e-4f) return null;
        float k = (SeaY() - o.Y) / d.Y;
        return k > 0f ? o + d * k : null;
    }

    void ClampTarget()
    {
        var s = _ctx.Terrain?.Streamer; var idx = s?.Index;
        if (s == null || idx == null) return;
        var g = s.RenderToGame(_target);
        g.X = System.Math.Clamp(g.X, 0f, System.Math.Max(1, idx.AreasX) * 6144f);
        g.Z = System.Math.Clamp(g.Z, 0f, System.Math.Max(1, idx.AreasZ) * 6144f);
        var r = s.GameToRender(g); _target.X = r.X; _target.Z = r.Z;
    }

    // ------------------------------------------------------------------ Kamera
    float IslandWidth()
    {
        var idx = _ctx.Terrain?.Streamer?.Index;
        float a = idx != null ? System.Math.Max(idx.AreasX, idx.AreasZ) : 8;
        return a * 6144f;
    }

    float WidthFor(int z)
    {
        int i = System.Math.Clamp(z, 1, 4) - 1;
        float w = i < ViewWidth.Length ? ViewWidth[i] : 0f;
        if (w > 0f) return w;
        return z >= 4 ? IslandWidth() : IslandWidth() * FarFactor;
    }

    float InclFor(int z)
    {
        int i = System.Math.Clamp(z, 1, 4) - 1;
        return i < Inclination.Length ? Inclination[i] : 0f;
    }

    /// <summary>Unity UpdateCamera: Abstand so, dass die Sichtbreite genau ins Bild passt; Neigung je Zoomstufe, Norden oben.</summary>
    void UpdateCamera(bool snap)
    {
        var terrain = _ctx.Terrain; var cam = terrain?.Camera;
        if (terrain == null || cam == null) return;
        float tw = WidthFor(_zoom), ti = InclFor(_zoom);
        if (snap || _curWidth <= 0f) { _curWidth = tw; _curIncl = ti; }
        else
        {
            float k = 1f - MathF.Exp(-Smooth * _lastDt);
            _curWidth = MathF.Exp(MathF.Log(_curWidth) + (MathF.Log(tw) - MathF.Log(_curWidth)) * k);
            _curIncl += (ti - _curIncl) * k;
        }
        float aspect = FlashScreen.height > 0 ? (float)FlashScreen.width / FlashScreen.height : 16f / 9f;
        float vfov = FieldOfView * MathF.PI / 180f;
        float hfov = 2f * MathF.Atan(MathF.Tan(vfov * 0.5f) * MathF.Max(0.1f, aspect));
        float dist = _curWidth * 0.5f / MathF.Tan(hfov * 0.5f);
        float incl = _curIncl * MathF.PI / 180f;
        var north = North();
        var pos = _target + Vector3.UnitY * (dist * MathF.Cos(incl)) - north * (dist * MathF.Sin(incl));
        var fwd = Vector3.Normalize(_target - pos);
        // Oben = Norden, senkrecht zur Blickrichtung (Unity LookRotation(fwd, north))
        var up = Vector3.Normalize(north - fwd * Vector3.Dot(north, fwd));
        cam.Position = pos;
        cam.Override = (fwd, up);
        terrain.MapMetersPerPixel = ShowRoads ? _curWidth / MathF.Max(1f, FlashScreen.width) : 0f;
        terrain.FieldOfView = FieldOfView;
        terrain.NearClip = MathF.Max(1f, dist * 0.02f);
        terrain.FarClip = dist * 3f + 20000f;
    }

    float _lastDt = 1f / 60f;   // Bildzeit für die weiche Kamerafahrt

    /// <summary>Fahrzeuggruppe (Unity GroupLabel): A1–A7, B1–B5, C1–C4, mA1/2, mB1–3, mC1/2.</summary>
    static string GroupLabel(int cat)
    {
        if (cat >= 1 && cat <= 7) return "A" + cat;
        if (cat >= 9 && cat <= 13) return "B" + (cat - 8);
        if (cat >= 14 && cat <= 17) return "C" + (cat - 13);
        if (cat >= 18 && cat <= 19) return "mA" + (cat - 17);
        if (cat >= 20 && cat <= 22) return "mB" + (cat - 19);
        if (cat >= 23 && cat <= 24) return "mC" + (cat - 22);
        return "empty";
    }
}
