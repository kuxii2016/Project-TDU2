using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Physics;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using Paradise.Game.World;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Gemeinsamer Interieur-Ablauf aller Interieur-Spielmodi (GAME_MODE_LIVING / GARAGE / SHOP / STICKERS) – Port von Unity
/// TDU2Interiors (Kern) + InteriorScene/InteriorFx: Einfahrt erkennen (Aufruf aus GAME_MODE_FREERIDE), Betreten von der Karte
/// (GAME_MODE_MAP "goin"), Szene laden/aufbauen, Ich-Perspektive, Ausstellungs-/Garagenautos, Bodenspiegelung, Außenwelt
/// ausblenden, Hotspots, Original-Oberfläche, Verlassen. Der Zustand ist statisch (ein Interieur zur Zeit; der Spielmodus wechselt
/// mit dem Raum: Wohnbereich <-> Garage). Modusspezifisches steht in der Klasse des Modus:
///   GAME_MODE_SHOP (Kauffenster, Vorschau), GAME_MODE_GARAGE (Losfahren), GAME_MODE_LIVING (Tagebuch, MyTDULife, My House,
///   TDU Media), GAME_MODE_TESTCAR (Testfahrt). Fahrzeugmenü (Händler und Garage gleich): InteriorUtils.CarMenu.cs,
///   Zurück/ESC aller Menüs: InteriorUtils.MenuFlow.cs, Laufen: InteriorUtils.Walker.cs.
/// </summary>
public abstract partial class InteriorUtils : GameModeHandler
{
    /// <summary>Original-Film des Raums (home.bnk; Händler/Sticker: shopscar.bnk).</summary>
    protected abstract ScreenDef Screen { get; }

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        if (_t != null) AttachUi(ctx.Open(Screen));
    }

    /// <summary>Unity TDU2Interiors.UpdateInside; ESC = Pausemenü, in Menüs gehören ESC/Rücktaste dem Film (Unity TDU2MenuFlow).</summary>
    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        bool ready = _t != null;
        if (ready && MenuOpen && !ctx.Paused)
        {
            if (ctx.Pressed(VK_ESCAPE)) MenuBack(true);
            else if (TDU2Controls.BackPressed()) MenuBack(false);
        }
        else HandlePauseKey(ctx);
        if (!ready || !UpdateInside(time.DeltaTime, ctx.Paused))
        {
            // Interieur verlassen: Testfahrt läuft draußen im eigenen Modus, sonst Freifahrt
            GameMode.Set(GAME_MODE_TESTCAR.TestDriveActive ? Gamemode.GAME_MODE_TESTCAR : Gamemode.GAME_MODE_FREERIDE);
            return;
        }
        if (RoomMode != Mode && !ctx.Paused) GameMode.Set(RoomMode);   // Raum gewechselt (Wohnbereich <-> Garage)
    }

    /// <summary>Autos auf den Stellplätzen zeichnen (Szene/Möbel zeichnet MapStreamer).</summary>
    public override void Render(IGameModeContext ctx, in GameTime time) { if (_t != null) RenderCars(); }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (ctx.Terrain != null) ctx.Terrain.Input.CursorLocked = false;   // Pausemenü/Freifahrt: Maus frei
    }

    // ------------------------------------------------------------------ Aufrufe aus Freifahrt / Karte
    internal static IGameModeContext? _ctx;

    /// <summary>Welt und Fahrzeuge übernehmen (einmal); false ohne Welt.</summary>
    internal static bool Init(IGameModeContext ctx)
    {
        _ctx = ctx;
        if (_t == null && ctx.Terrain != null && ctx.Vehicles != null) { _t = ctx.Terrain; _v = ctx.Vehicles; }
        return _t != null;
    }

    /// <summary>Freifahrt (Unity TDU2Interiors.UpdateOutside): Einfahrt anzeigen, mit Bestätigen betreten → Modus des Raums.</summary>
    public static void UpdateOutside(IGameModeContext ctx, float dt)
    {
        if (Init(ctx) && UpdateOutside(dt)) GameMode.Set(RoomMode);
    }

    /// <summary>Karte "goin" (Unity TDU2BigMap.EnterWhenReady): Auto steht an der Einfahrt, warten bis die Welt steht, dann hinein.</summary>
    public static void EnterWhenReady(IGameModeContext ctx, Spot s) { if (Init(ctx)) EnterWhenReady(s); }

    internal static float EnterMaxSpeedKmh = 40f;
    internal static float InteractDistance = 10f;

    internal enum Kind { Dealer, Living, Garage, StickerShop, Other }
    internal sealed class SpotBanks { public bool Valid, IsClub, IsCasino, CarWash; public Kind Kind; public string? Living, Garage, Name, TravelTo; }

    internal static TerrainModule _t = null!;
    internal static VehicleModule _v = null!;
    internal static readonly Dictionary<string, SpotBanks> _banks = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<Spot, Matrix4x4?> _enterBox = new();
    internal static readonly HashSet<string> _loggedInvalid = new();

    internal static Spot? _spot; internal static SpotBanks? _spotBanks; internal static Kind _kind;
    internal static Matrix4x4 _holder = Matrix4x4.Identity, _holderInv = Matrix4x4.Identity;
    internal static MapStreamer.SceneHandle? _scene;
    internal static InteriorScene? _data;
    internal static Walker? _fp;
    internal static Task<(BankModel model, InteriorScene data)>? _sceneTask;
    internal static string? _pendingBank, _pendingStartCam; internal static Kind _pendingKind;
    internal static CarPhysics? _lastCar;
    internal static Spot? _nearSpot; internal static SpotBanks? _nearBanks; internal static float _nextScan, _time;
    internal static VmfPlayer? _ui;
    internal static object? _uiAim, _aimTarget;

    internal static bool Inside => _spot != null;
    internal static Kind CurrentKind => _kind;
    internal static bool SceneReady => Inside && _scene != null && _sceneTask == null;
    internal static Spot? CurrentSpot => _spot;
    /// <summary>Spielmodus für die Oberfläche des aktuellen Interieurs.</summary>
    internal static Gamemode RoomMode => (_sceneTask != null ? _pendingKind : _kind) switch { Kind.Dealer => Gamemode.GAME_MODE_SHOP, Kind.Garage => Gamemode.GAME_MODE_GARAGE, Kind.StickerShop => Gamemode.GAME_MODE_STICKERS, _ => Gamemode.GAME_MODE_LIVING };

    internal static string? BnkDir => Paradise.Core.Config.GameData.BnkDir;
    internal static string InteriorDir => Path.Combine(BnkDir ?? "", "interior");
    internal static VehicleManager? Vm => _v.Manager;

    // ------------------------------------------------------------------ Draußen: Einfahrt erkennen (Unity UpdateOutside)
    /// <summary>Freifahrt: nahe Einfahrt anzeigen (HUD /HUD/enter), mit Bestätigen betreten. true = betreten.</summary>
    internal static bool UpdateOutside(float dt)
    {
        _time += dt;
        GAME_MODE_TESTCAR.UpdateTestDrive();   // Händler-Testfahrt: Restzeit im HUD, danach zurück zum Händler
        if (_waitSpot != null) return PollWaitEnter();
        var st = _t.Streamer; var vm = Vm;
        if (st?.Spots == null || vm == null || _v.Suspended || BnkDir == null) { _nearSpot = null; Vm?.Minimap?.ShowEnter(null); return false; }
        if (_time >= _nextScan)
        {
            _nextScan = _time + 0.25f;
            _nearSpot = null; _nearBanks = null;
            if (vm.Current != null && vm.Current.SpeedKmh > EnterMaxSpeedKmh) { Vm?.Minimap?.ShowEnter(null); return false; }
            var g = vm.Current != null ? vm.PositionGame : st.ViewerGame;
            float best = float.MaxValue;
            foreach (var s in st.Spots.Spots)
            {
                // Abstand zum Spot ODER zu seiner Einfahrt (Casino: Einfahrt ~160 m vom Anker)
                float dx = g.X - s.Position.X, dz = g.Z - s.Position.Z;
                float d2 = dx * dx + dz * dz;
                if (s.Position2.LengthSquared() > 1f)
                {
                    float ex = g.X - s.Position2.X, ez = g.Z - s.Position2.Z;
                    d2 = MathF.Min(d2, ex * ex + ez * ez);
                }
                if (d2 > 150f * 150f) continue;
                bool atGate = InEnterBox(s, g) || HorizDist(g, s.Position2) < 12f;
                if (!atGate) continue;
                var b = Resolve(s);
                if (!b.Valid)
                {
                    if (_loggedInvalid.Add(s.Code ?? "")) Log.Warn($"Spot ohne Interieur: '{s.Code}' Kategorie {s.TypeA & 0xFF} ({s.TypeA}/{s.TypeB}) bei {s.Position}");
                    continue;
                }
                // noch nicht portiert: Waschanlage, Landeplatz (Flug)
                if (b.CarWash || b.TravelTo != null) continue;
                if (d2 < best) { best = d2; _nearSpot = s; _nearBanks = b; }
            }
        }
        Vm?.Minimap?.ShowEnter(_nearSpot != null && vm.Current != null ? _nearSpot.Code : null);
        if (_nearSpot != null && _nearBanks != null && TDU2Controls.ConfirmPressed())
        {
            Vm?.Minimap?.ShowEnter(null);
            EnterInterior(_nearSpot, _nearBanks);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ Von der Karte hinein (Unity TDU2BigMap.EnterWhenReady/EnterSpot)
    internal static Spot? _waitSpot; internal static float _waitUntil; internal static bool _waitDone;

    /// <summary>Auto steht schon an der Einfahrt (Teleport der Karte): Ladebildschirm, warten bis die Welt dort steht (400 m), dann hinein.</summary>
    internal static void EnterWhenReady(Spot s)
    {
        if (Inside || _t.Streamer == null || Vm == null) return;
        _waitSpot = s; _waitUntil = _time + 20f; _waitDone = false;
        TDU2LoadingScreen.Show("Teleport " + s.Code, () => _waitDone && (!Inside || SceneReady), 0.8f, false);
    }

    /// <summary>Wartet auf die Welt an der Einfahrt; true = Interieur betreten.</summary>
    internal static bool PollWaitEnter()
    {
        var s = _waitSpot;
        if (s == null) return false;
        var st = _t.Streamer;
        if (st != null && _time < _waitUntil && !st.ReadyAround(st.GameToRender(s.Position), 400f)) return false;
        _waitSpot = null; _waitDone = true;
        return EnterSpot(s);
    }

    /// <summary>Unity TDU2Interiors.EnterSpot: Spot direkt betreten (ohne Einfahrt/Bestätigen).</summary>
    internal static bool EnterSpot(Spot s)
    {
        if (Vm == null || BnkDir == null) return false;
        var b = Resolve(s);
        if (!b.Valid || b.CarWash) { Log.Info($"Karte: Spot {s.Code} hat kein Interieur"); return false; }
        if (b.TravelTo != null) { Log.Info($"Karte: {s.Code} ({b.Name}) noch nicht portiert"); return false; }
        if (Inside) ExitInterior();
        Vm?.Minimap?.ShowEnter(null);
        EnterInterior(s, b);
        Log.Info("Karte: ins Interieur " + s.Code);
        return true;
    }

    internal static float HorizDist(Vector3 a, Vector3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return MathF.Sqrt(dx * dx + dz * dz); }

    internal static bool InEnterBox(Spot s, Vector3 g)
    {
        if (!_enterBox.TryGetValue(s, out var inv))
        {
            inv = MathF.Abs(s.Box2.GetDeterminant()) > 1e-3f && Matrix4x4.Invert(s.Box2, out var i) ? i : null;
            _enterBox[s] = inv;
        }
        if (!inv.HasValue) return false;
        var l = Vector3.Transform(g, inv.Value);
        return MathF.Abs(l.X) <= 0.6f && MathF.Abs(l.Z) <= 0.6f && MathF.Abs(l.Y) <= 1.5f;
    }

    internal static SpotBanks Resolve(Spot s)
    {
        string code = (s.Code ?? "").ToLowerInvariant();
        if (_banks.TryGetValue(code, out var b)) return b;
        b = new SpotBanks();
        try
        {
            if (code.StartsWith("eland"))
            {
                b.TravelTo = "casino"; b.Kind = Kind.Other; b.Name = "Landeplatz";   // Flug: TDU2IslandTravel (noch nicht portiert)
            }
            else if (code.Length > 4 && code.StartsWith("eph_"))
            {
                string rest = code[4..];
                b.Living = Bank("lph_" + rest); b.Garage = Bank("gph_" + rest); b.Kind = Kind.Living; b.Name = "Haus";
            }
            else if (code.Length > 4 && code.StartsWith("ech_"))
            {
                string rest = code[4..];
                // Clubhaus (ECH_*): das Spiel lädt für ECH_IA "Interior/ich_ib.bnk" (TestDrive2Dev.exe)
                b.Living = (rest == "ia" ? Bank("ich_ib") : null) ?? Bank("ich_" + rest);
                b.Garage = Bank("gch_" + rest) ?? BankPrefix("gch_" + rest[..1]); b.Kind = Kind.Living; b.Name = "Club";
                b.IsClub = true;
            }
            else if (code.StartsWith("ecas") || (s.TypeA & 0xFF) == 21)
            {
                b.Living = Bank("i" + code[1..]) ?? Bank("icashal"); b.Kind = Kind.Other; b.Name = "Casino";
                b.IsCasino = true;
            }
            else if (code.StartsWith("estol"))
            {
                b.Living = Bank("i" + code[1..]); b.Kind = Kind.Dealer; b.Name = "Gebrauchtwagenhändler";
            }
            else if (code.StartsWith("estick"))
            {
                b.Living = Bank("i" + code[1..]); b.Kind = Kind.StickerShop; b.Name = "Sticker-Shop";
            }
            else if (code.StartsWith("ecd") || code.StartsWith("ebd"))
            {
                b.Living = Bank("i" + code[1..]); b.Kind = Kind.Dealer; b.Name = code.StartsWith("ebd") ? "Motorradhändler" : "Autohändler";
            }
            else if (code.StartsWith("ecw"))
            {
                b.CarWash = true; b.Kind = Kind.Other; b.Name = "Waschanlage";   // TDU2CarWash (noch nicht portiert)
            }
            else if (code.Length > 1 && code[0] == 'e')
            {
                b.Living = Bank("i" + code[1..]); b.Kind = Kind.Other; b.Name = SpotsFile.CategoryFromCode(s.Code ?? "");
            }
            b.Valid = b.Living != null || b.Garage != null || b.CarWash || b.TravelTo != null;
        }
        catch (Exception) { b.Valid = false; }
        _banks[code] = b;
        return b;
    }

    internal static string? Bank(string name) => CarDatabase.FindFile(InteriorDir, name + ".bnk");

    internal static string? BankPrefix(string prefix)
    {
        try
        {
            foreach (var f in GameFiles.GetFiles(InteriorDir, prefix + "*.bnk"))
                if (!Path.GetFileName(f).Contains("__")) return f;
        }
        catch (Exception) { }
        return null;
    }

    // ------------------------------------------------------------------ Betreten / Verlassen
    internal static void EnterInterior(Spot s, SpotBanks b)
    {
        var vm = Vm!;
        // während der Testfahrt ein Interieur betreten: Testfahrt endet, beim Verlassen kommt das eigene Auto (Unity AbortTestDriveOnEnter)
        if (GAME_MODE_TESTCAR.TestDriveActive) { var prev = GAME_MODE_TESTCAR._testPrev; GAME_MODE_TESTCAR.StopTestDrive(); _lastCar = prev; }
        else if (vm.Current != null) _lastCar = vm.Current.Car;
        _lastCar ??= vm.FindCar(vm.StartCar) ?? (vm.Available.Count > 0 ? vm.Available[0] : null);
        vm.Despawn();
        _v.Suspended = true;
        _spot = s; _spotBanks = b;
        Paradise.Core.World.WorldWeather.Inside = true;   // Unity SetInteriorAmbient(true) + TDU2Weather.InsideInterior
        _fp = new Walker();
        _t.Input.CursorLocked = true;
        Log.Info($"Interieur betreten: {s.Code} ({b.Name}), Auto beim Verlassen: {_lastCar?.FileName ?? "-"}");
        // Wie im Spiel: Haus und Club betritt man im Wohnbereich bzw. im Clubraum; die Garage erreicht man von dort.
        if (b.Kind == Kind.Living && b.Living != null) LoadScene(b.Living, Kind.Living, null);
        else if (b.Kind == Kind.Living && b.Garage != null) LoadScene(b.Garage, Kind.Garage, null);
        else LoadScene((b.Living ?? b.Garage)!, b.Kind, null);
    }

    internal static void LoadScene(string bank, Kind kind, string? startCam)
    {
        var st = _t.Streamer!;
        // Ladebildschirm (Tipp-Bild) bis die Szene steht
        // Ladebildschirm (Tipp-Bild, Speichern) bis Szene und Möbel stehen – der Aufbau soll nicht zu sehen sein
        TDU2LoadingScreen.Show("Interieur " + Path.GetFileNameWithoutExtension(bank), () => !Inside || InteriorLoaded, 1.0f, true);
        ClearScene();
        _pendingBank = bank; _pendingKind = kind; _pendingStartCam = startCam;
        _sceneTask = Task.Run(() =>
        {
            var model = st.LoadSceneModel(bank);
            var data = InteriorScene.Parse(model.Bank);
            return (model, data);
        });
    }

    internal static void PollScene()
    {
        if (_sceneTask == null || !_sceneTask.IsCompleted) return;
        var t = _sceneTask; _sceneTask = null;
        if (t.IsFaulted)
        {
            Log.Error("Interieur: " + t.Exception?.GetBaseException().Message);
            if (_scene == null) ExitInterior();
            return;
        }
        if (!Inside) { t.Result.model?.Release(); return; }
        try { BuildInterior(t.Result.model, t.Result.data); }
        catch (Exception ex) { Log.Error("Interieur aufbauen: " + ex.Message); }
    }

    // Unity TDU2Interiors (Inspektor-Grundwerte)
    internal static float InteriorAltitude = 0f;
    internal static Vector3 InteriorAmbientColor = new(0.42f, 0.4f, 0.37f);
    internal static Vector3 GarageAmbient = new(0.44f, 0.44f, 0.46f);
    internal static float ClipInset = 0.3f;
    internal static float GarageFloorReflection = 0.45f;   // Unity TDU2Interiors.GarageFloorReflection
    internal static bool HideWholeExterior = false;
    internal static bool HideOutsideWorld = true, HideOutsideInLiving = false, HideOutsideEverywhere = false;

    internal static readonly List<StaticCollider> _colliders = new();    // Interieur + Möbel + Sicherheitsboden (Unity PrepareStatic/AddSafetyFloor)
    internal static readonly HashSet<StaticCollider> _own = new();
    internal static readonly List<RenderObject> _hiddenExterior = new();  // Unity _hiddenExteriors (SetActive(false): Bild + Kollision)
    internal static readonly List<(Vector3 min, Vector3 max)> _hiddenBoxes = new();
    internal static readonly List<MapStreamer.SceneHandle> _furniture = new();
    internal static Task<List<(InteriorScene.Ent e, BankModel m)>>? _furnTask;
    internal static Vector3 _floorPoint;
    internal static Vector3 _clipCenter, _clipExt; internal static bool _clipValid;
    internal static List<string>? _extNames; internal static bool _extHaveBounds, _extActive; internal static Vector3 _extMin, _extMax; internal static float _extUntil, _extNext; internal static int _extNodeCount;

    internal static PhysicsWorld? Phys => _t.Physics;

    /// <summary>Unity InteriorLoaded: Szene und Möbel stehen (Ende des Ladebildschirms). Autos auf den Stellplätzen folgen.</summary>
    internal static bool InteriorLoaded => SceneReady && _carTask == null && _carQueue.Count == 0 && _furnTask == null;

    internal static void BuildInterior(BankModel model, InteriorScene data)
    {
        var st = _t.Streamer!;
        _kind = _pendingKind;
        _data = data;
        // wie im Spiel an der Stelle des Spots (gleiche Platzierung wie das Exterior: Position, Drehung um Y = Winkel)
        _holder = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, _spot!.Value1))
                * Matrix4x4.CreateTranslation(_spot.Position + Vector3.UnitY * InteriorAltitude);
        Matrix4x4.Invert(_holder, out _holderInv);
        // Innenräume sind immer beleuchtet: leuchtende Materialien mit aufbauen (MapStreamer.BuildScene, MaterialFactory.Interior)
        MaterialFactory.MinFloorReflection = _pendingKind == Kind.Garage ? GarageFloorReflection : 0f;   // Unity InteriorFx.MinFloorReflection
        _scene = st.BuildScene(model, _holder, true);
        MaterialFactory.MinFloorReflection = 0f;
        SetupMirror();
        st.Scenes.Add(_scene);
        GAME_MODE_LIVING.ApplyHouseMaterials();   // My House: gespeicherte Böden/Tapeten
        // Garage wie im Spiel heller
        Paradise.Core.World.WorldWeather.InteriorAmbient = _kind == Kind.Garage ? GarageAmbient : InteriorAmbientColor;
        SetExteriorVisible(false, model);
        PrepareStatic(_scene, false);
        ComputeClipBox();
        UpdateRayFilter();     // Unity DisableWorldColliders (vor dem Sicherheitsboden: dessen Strahlen treffen nur das Interieur)
        HideWorld();
        AddSafetyFloor(data);

        // Möbel (Haus): interior/myhouse/<Name ohne _NN>.bnk
        if (data.HouseObjects.Count > 0)
        {
            var list = new List<(InteriorScene.Ent, string)>();
            foreach (var e in data.HouseObjects)
            {
                string def = System.Text.RegularExpressions.Regex.Replace(e.Name ?? "", @"_\d+$", "").ToLowerInvariant();
                string n = GAME_MODE_LIVING.HouseObjectBank(e, def);   // gespeicherte Einrichtung (My House), sonst Standard der Szene
                var f = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), n + ".bnk");
                if (f == null && n != def) { n = def; f = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), n + ".bnk"); }
                GAME_MODE_LIVING._houseBankOf[e] = n;
                if (f != null) list.Add((e, f));
            }
            if (list.Count > 0)
                _furnTask = Task.Run(() =>
                {
                    var r = new List<(InteriorScene.Ent, BankModel)>();
                    foreach (var (e, f) in list)
                    {
                        try { r.Add((e, st.LoadSceneModel(f))); } catch (Exception) { }
                    }
                    return r;
                });
        }

        QueueCars(data);
        LoadUi();
        // Ich-Perspektive an der Startkamera
        var cam = (_pendingStartCam != null ? data.Camera(_pendingStartCam) : null) ?? data.StartCam;
        Vector3 eye, look;
        if (cam != null) { eye = Vector3.Transform(cam.Position, _holder); look = Vector3.TransformNormal(cam.Forward, _holder); }
        else { eye = _holder.Translation + Vector3.UnitY * 1.7f; look = Vector3.TransformNormal(Vector3.UnitZ, _holder); }
        _fp!.Place(eye, look);
        int tris = 0; foreach (var c in _colliders) tris += c.Triangles;
        Log.Info($"Interieur aufgebaut: {Path.GetFileNameWithoutExtension(_pendingBank)} ({_kind}), {_scene.Objects.Count} Objekte, {tris} Kollisionsdreiecke, " +
                 $"{data.Selectables.Count} Hotspots, {data.CarSlots.Count} Stellplätze, {data.HouseObjects.Count} Möbel, Exterior {_hiddenExterior.Count} Teile aus, " +
                 $"Außenwelt {(st.HideWorld ? "aus" : "an")}, Welt-Kollision im Raum {(_clipValid && _kind != Kind.Living ? "aus" : "an")}");
        foreach (var s in data.Selectables)
        {
            // Mitte und Größe des Volumens (Kontrolle der Auswahl: zu große Volumen würden überall gewählt)
            var c = SelectableCenter(s);
            Vector3 ext = Vector3.Zero;
            if (Matrix4x4.Invert(s.Volume, out var w))
                ext = new Vector3(new Vector3(w.M11, w.M12, w.M13).Length(), new Vector3(w.M21, w.M22, w.M23).Length(), new Vector3(w.M31, w.M32, w.M33).Length());
            Log.Info($"  Hotspot {s.Name} Typ {s.Type} '{s.Str}' -> {Label(s)} – Mitte {c.X:0.00}/{c.Y:0.00}/{c.Z:0.00}, Größe {ext.X:0.00}x{ext.Y:0.00}x{ext.Z:0.00} m");
        }
        // Diagnose (wie Unity InteriorFx.LogInventory): Materialien mit Art, Haupttextur, Lightmap/Alpha
        var seen = new HashSet<IGpuMaterial>(ReferenceEqualityComparer.Instance);
        foreach (var o in _scene.Objects)
            foreach (var lod in o.Lods)
                foreach (var pt in lod.Parts)
                    if (seen.Add(pt.Material) && MaterialFactory.TryGetInfo(pt.Material, out var mi))
                        Log.Info($"  Material {mi.Def.Name} [{pt.Material.Kind}] Blend {mi.Blend} Haupt {mi.MainTexture ?? "-"} Detail {mi.DetailTexture ?? "-"} " +
                                 $"NachtLM {(mi.Desc.NightLm != null ? "ja" : "nein")} Interieur-LM {mi.Desc.InteriorLm} Unlit {mi.Desc.Unlit} Alpha {mi.Desc.Color.W:0.00} Cutoff {mi.Desc.Cutoff:0.00} Spiegel {mi.Desc.ReflMix:0.00} DetailAlpha {mi.Desc.DetailAlpha}");
    }

    // ------------------------------------------------------------------ Ausstellungs-/Garagenautos (Unity TDU2Interiors)
    internal sealed class DisplayCar
    {
        public CarPhysics Car = null!; public CarModel Model = null!; public CarAssets Assets = null!; public Matrix4x4 Pose; public StaticCollider? Col;
        /// <summary>Hülle der Kollisionsbox (Spielraum) – Unity Col.bounds zum Anvisieren.</summary>
        public Vector3 BoxMin, BoxMax;
        /// <summary>Stellplatz und Ausstattung (Händler: Vorschau mit anderer Ausstattung baut das Auto dort neu auf).</summary>
        public InteriorScene.Ent Slot = null!; public CarChoice Choice;
        public IGpuMesh? ShadowMesh; public IGpuMaterial? ShadowMat; public IGpuTexture? ShadowBlob; public Matrix4x4 ShadowPose;
    }
    internal static readonly List<DisplayCar> _cars = new();
    internal static readonly Queue<(CarPhysics car, InteriorScene.Ent slot)> _carQueue = new();
    internal static Task<CarAssets>? _carTask; internal static (CarPhysics car, InteriorScene.Ent slot) _carTaskInfo; internal static CarChoice _carTaskChoice;
    internal static readonly HashSet<StaticCollider> _carCols = new();
    internal static bool _floorQuery;

    /// <summary>Unity BuildInterior: Autos auf die Stellplätze (Händler: Shop-Liste, Garage: eigene Autos, Sticker-Shop: eigenes Auto).</summary>
    internal static void QueueCars(InteriorScene data, bool arriving = true)
    {
        var vm = Vm; var db = vm?.Database;
        if (vm == null || db == null) return;
        string island = _t.Streamer?.Index?.IslandName ?? "";
        var cars = new List<CarPhysics>();
        if (_kind == Kind.Dealer)
        {
            var shop = db.ShopForSpot(_spot!, island);
            if (shop != null)
                foreach (int id in shop.CarIds)
                    if (id != 0 && db.ById.TryGetValue(id, out var c)) { var av = vm.FindCar(c.FileName); if (av != null && !cars.Contains(av)) cars.Add(av); }
        }
        else if (_kind == Kind.StickerShop) { if (_lastCar != null) cars.Add(_lastCar); }
        else if (_kind == Kind.Garage && _spotBanks != null && _spotBanks.IsClub) { }   // Club-Garage: Autos der Clubmitglieder (nicht portiert)
        else if (_kind == Kind.Garage)
        {
            // Stellplätze dieser Garage aus dem Spielstand (Original: Garage[] mit Haus + IndexInGarage; Platz wechseln tauscht dort).
            // Das Auto, mit dem man kommt, steht – wenn es noch woanders steht und hier ein Platz frei ist – jetzt hier.
            var house = db.HouseForSpot(_spot!, island);
            var sv = Paradise.Core.Online.TDU2Session.Save;
            if (house != null && sv != null)
            {
                Paradise.Core.Savegame.TDU2Garage.Normalize(sv, db, house.SpotName);
                var here = _lastCar != null ? Paradise.Core.Savegame.TDU2Garage.SlotOf(sv, _lastCar.FileName) : null;
                if (arriving && _lastCar != null && (here == null || !string.Equals(here.house, house.SpotName, StringComparison.OrdinalIgnoreCase)))
                    Paradise.Core.Savegame.TDU2Garage.MoveToFreeSlot(sv, db, _lastCar.FileName, house.SpotName);
                for (int k = 0; k < house.CarSlots && k < data.CarSlots.Count; k++)
                {
                    string? file = Paradise.Core.Savegame.TDU2Garage.CarAt(sv, house.SpotName, k);
                    var c = file != null ? vm.FindCar(file) : null;
                    if (c != null) _carQueue.Enqueue((c, data.CarSlots[k]));
                }
            }
            else
            {
                if (_lastCar != null) cars.Add(_lastCar);
                foreach (var n in Paradise.Core.Online.TDU2Session.OwnedCars)
                {
                    var c = vm.FindCar(n);
                    if (c != null && !cars.Contains(c)) cars.Add(c);
                }
                int max = house != null && house.CarSlots > 0 ? house.CarSlots : data.CarSlots.Count;
                if (cars.Count > max) cars.RemoveRange(max, cars.Count - max);
            }
        }
        for (int i = 0; i < cars.Count && i < data.CarSlots.Count; i++) _carQueue.Enqueue((cars[i], data.CarSlots[i]));
        // Ausstellungsautos auf Drehtellern (VirtualVehiculeActor, VehicleId aus der Datenbank)
        foreach (var sc in data.ShowCars)
        {
            if (!db.ById.TryGetValue(sc.VehicleId, out var cp) || cp == null) { Log.Warn("Ausstellungsauto: VehicleId " + sc.VehicleId + " nicht in der Datenbank"); continue; }
            _carQueue.Enqueue((vm.FindCar(cp.FileName) ?? cp, sc));
        }
    }

    /// <summary>Unity PollCars: ein Auto nach dem anderen im Hintergrund laden, dann aufstellen.</summary>
    internal static void PollCars()
    {
        var vm = Vm;
        if (_carTask != null && _carTask.IsCompleted)
        {
            var t = _carTask; var info = _carTaskInfo; _carTask = null;
            if (t.IsFaulted) Log.Error("Ausstellungsauto " + info.car?.FileName + ": " + t.Exception?.GetBaseException().Message);
            else if (_scene == null) t.Result?.DestroyTextures();
            else
            {
                try { BuildDisplayCar(t.Result, info.car, info.slot); _cars[^1].Choice = _carTaskChoice; }
                catch (Exception ex) { Log.Error("Ausstellungsauto " + info.car?.FileName + ": " + ex.Message); }
            }
        }
        if (_carTask == null && _carQueue.Count > 0 && _scene != null && _sceneTask == null && vm?.Database != null)
        {
            var (car, slot) = _carQueue.Dequeue();
            string bnk = vm.BnkDir; var db = vm.Database;
            _carTaskInfo = (car, slot);
            // eigene Autos mit der gekauften Ausstattung (Unity PollCars: TDU2CarChoices), Händlerautos im Werkszustand
            int col = 0, rim = 0, intr = 0;
            if (_kind != Kind.Dealer && CarChoices.TryGet(car.FileName, out var ch)) { col = ch.Color; rim = ch.Rim; intr = ch.Interior; }
            _carTaskChoice = new CarChoice(col, rim, intr);
            _carTask = Task.Run(() => CarAssets.Load(bnk, db, car, col, false, rim, intr));
        }
    }

    internal static bool IsTirePart(CarNode? n, CarNode root)
    {
        for (; n != null && n != root; n = n.Parent)
        {
            string s = n.Name ?? "";
            if (s == "Tire" || s.StartsWith("D_TIRE", StringComparison.OrdinalIgnoreCase) || s.StartsWith("D_WHEEL", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("D_RIM", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Unity BuildDisplayCar: Auto am Stellplatz, auf den Hallenboden gestellt, Kollisionsbox der Karosserie.</summary>
    internal static void BuildDisplayCar(CarAssets a, CarPhysics car, InteriorScene.Ent slot)
    {
        var r = _v.Renderer;
        var pos = Vector3.Transform(slot.Position, _holder);
        var fwd = Vector3.TransformNormal(slot.Forward, _holder); fwd.Y = 0f;
        if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.UnitZ;
        var model = CarModel.Build(a, r);
        // Teile mit unbrauchbarer Geometrie ausblenden (Unity: Renderer größer 7 x 4 x 9 m)
        bool any = false, tires = false; Vector3 bmin = default, tmin = default;
        foreach (var n in model.Visual.SelfAndDescendants())
        {
            if (!n.HasMesh || !n.RendererEnabled || !n.ActiveInHierarchy) continue;
            var m = model.LocalToVisualParent(n);
            Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) != 0 ? n.MeshMax.X : n.MeshMin.X, (i & 2) != 0 ? n.MeshMax.Y : n.MeshMin.Y, (i & 4) != 0 ? n.MeshMax.Z : n.MeshMin.Z);
                var q = Vector3.Transform(c, m); mn = Vector3.Min(mn, q); mx = Vector3.Max(mx, q);
            }
            var s = mx - mn;
            if (s.X > 7f || s.Y > 4f || s.Z > 9f) { n.RendererEnabled = false; Log.Warn($"Auto {car.ModelName}: Teil '{n.Name}' ausgeblendet (Größe {s})"); continue; }
            if (!any) { bmin = mn; any = true; } else bmin = Vector3.Min(bmin, mn);
            if (IsTirePart(n, model.Visual)) { if (!tires) { tmin = mn; tires = true; } else tmin = Vector3.Min(tmin, mn); }
        }
        // auf den Boden stellen: Aufstandspunkt = Unterkante der Reifen
        float floor = FloorBelow(pos);
        float bottom = tires ? tmin.Y : any ? bmin.Y : 0f;
        pos.Y = floor - bottom;
        // Lage im Spielraum (Unity LookRotation im gespiegelten Fahrzeugraum, wie VehicleManager.Render)
        var q0 = U.Mirror(U.LookRotation(Vector3.Normalize(U.Mirror(fwd)), Paradise.Core.Math.Vec.Up));
        var pose = Matrix4x4.CreateFromQuaternion(q0) * Matrix4x4.CreateTranslation(pos);
        // Kollision: Karosserie, Unterkante etwas angehoben (Unity BoxCollider)
        var (cmin, cmax) = model.RecomputeBodyBounds();
        var size = cmax - cmin; var center = (cmin + cmax) * 0.5f;
        float lift = MathF.Min(0.15f, size.Y * 0.15f);
        center.Y += lift * 0.5f;
        var half = new Vector3(size.X * 0.96f, MathF.Max(0.3f, size.Y - lift), size.Z * 0.98f) * 0.5f;
        StaticCollider? col = null;
        var phys = Phys;
        var vtx = new Vector3[8];
        Vector3 boxMin = new(float.MaxValue), boxMax = new(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            vtx[i] = Vector3.Transform(center + new Vector3((i & 1) != 0 ? half.X : -half.X, (i & 2) != 0 ? half.Y : -half.Y, (i & 4) != 0 ? half.Z : -half.Z), pose);
            boxMin = Vector3.Min(boxMin, vtx[i]); boxMax = Vector3.Max(boxMax, vtx[i]);
        }
        if (phys != null)
        {
            int[] f = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
            var tri = new Vector3[f.Length];
            for (int i = 0; i < f.Length; i++) tri[i] = vtx[f[i]];
            col = phys.AddStaticMesh(tri, "Ausstellungsauto " + car.ModelName);
            if (col != null) { _own.Add(col); _carCols.Add(col); }
        }
        var dc = new DisplayCar { Car = car, Model = model, Assets = a, Pose = pose, Col = col, BoxMin = boxMin, BoxMax = boxMax, Slot = slot };
        // Unity TDU2VehicleEffects.AddStaticShadow: Schattenfläche 1,5 cm über dem Boden, Karosseriegröße x 1,25 / 1,12
        var (sm, smat, sblob) = VehicleEffects.CreateStaticShadow(r, a);
        dc.ShadowMesh = sm; dc.ShadowMat = smat; dc.ShadowBlob = sblob;
        var sc = Vector3.Transform(new Vector3(center.X, 0f, center.Z), pose);
        var srot = Matrix4x4.CreateFromQuaternion(U.LookRotation(Vector3.Normalize(fwd), Paradise.Core.Math.Vec.Up));
        dc.ShadowPose = Matrix4x4.CreateScale(size.X * 1.25f, 1f, size.Z * 1.12f) * srot * Matrix4x4.CreateTranslation(sc.X, floor + 0.015f, sc.Z);
        _cars.Add(dc);
        Log.Info($"Interieur: Auto {car.ModelName} am Stellplatz {slot.Name}");
    }

    /// <summary>Unity FloorBelow: nächster Boden unter einem Stellplatz, Ausstellungsautos zählen nicht.</summary>
    internal static float FloorBelow(Vector3 pos)
    {
        var phys = Phys;
        if (phys == null) return pos.Y;
        _floorQuery = true;
        try { return phys.Raycast(pos + Vector3.UnitY * 1.5f, -Vector3.UnitY, 4f, out var h) ? h.Point.Y : pos.Y; }
        finally { _floorQuery = false; }
    }

    /// <summary>Jedes Bild (GAME_MODE_LIVING.Render): Autos zeichnen.</summary>
    internal static void RenderCars()
    {
        var st = _t.Streamer;
        if (st == null) return;
        // Bodenspiegelung (Unity InteriorMirror): Ebene in den Render-Raum (Gleitkomma-Ursprung -> jedes Bild)
        RenderFx.PlanarOn = _mirrorValid && _scene != null && _sceneTask == null;
        if (RenderFx.PlanarOn) RenderFx.PlanarY = st.GameToRender(new Vector3(_holder.Translation.X, _mirrorY, _holder.Translation.Z)).Y;
        if (_cars.Count == 0) return;
        // Unity: Reflexionssonde des Interieurs in der Mitte des Raums (statt der Würfelkarte der Bank: Echtzeit)
        RenderFx.ProbeOn = true;
        RenderFx.ProbeStatic = true;   // Raum steht still, Autos sind nicht in der Sonde: einmal rendern statt jedes Bild eine Seite
        RenderFx.ProbePos = st.GameToRender(_sceneCenter);
        var r = _v.Renderer;
        foreach (var c in _cars)
        {
            c.Model.UpdateTransforms(c.Pose * st.RootMatrix);
            c.Model.Draw(r);
            if (c.ShadowMesh != null && c.ShadowMat != null) r.DrawMesh(c.ShadowMesh, c.ShadowMat, c.ShadowPose * st.RootMatrix);
        }
    }

    internal static void ClearCars()
    {
        var phys = Phys;
        foreach (var c in _cars)
        {
            CarAccessories.Forget(c.Model);
            c.Model.Destroy();
            c.Assets.DestroyTextures();
            c.ShadowMesh?.Dispose(); c.ShadowMat?.Dispose(); c.ShadowBlob?.Dispose();
            if (c.Col != null) phys?.Remove(c.Col);
        }
        _cars.Clear(); _carQueue.Clear(); _carCols.Clear();
        if (_carTask != null) { var t = _carTask; _carTask = null; t.ContinueWith(x => { if (!x.IsFaulted) x.Result?.DestroyTextures(); }); }
    }

    // ------------------------------------------------------------------ Bodenspiegelung (Unity InteriorFx.Apply + FindFloorPlane)
    internal static bool _mirrorValid; internal static float _mirrorY; internal static Vector3 _sceneCenter; internal static bool _f11Was;

    internal static float ReflMixOf(IGpuMaterial m) => MaterialFactory.TryGetInfo(m, out var i) ? i.Desc.ReflMix : 0f;

    /// <summary>
    /// Spiegelnde Flächen (ReflMix > 0, Garage: dazu alle großen waagerechten Flächen) -> Bodenebene = häufigste Höhe nach oben
    /// zeigender Dreiecke (Raster 5 cm, nach Fläche gewichtet). Garage: alles auf Bodenhöhe spiegelt mindestens GarageFloorReflection.
    /// </summary>
    internal static void SetupMirror()
    {
        _mirrorValid = false;
        if (_scene == null) return;
        bool any = false; Vector3 mn = default, mx = default;
        foreach (var o in _scene.Objects) { if (!any) { mn = o.BoundsMin; mx = o.BoundsMax; any = true; } else { mn = Vector3.Min(mn, o.BoundsMin); mx = Vector3.Max(mx, o.BoundsMax); } }
        _sceneCenter = any ? (mn + mx) * 0.5f : _holder.Translation;
        if (!MaterialFactory.PlanarReflections) return;
        float minRefl = _kind == Kind.Garage ? GarageFloorReflection : 0f;
        var bins = new Dictionary<int, float>();
        var binY = new Dictionary<int, float>();   // Summe Höhe x Fläche je Klasse (genaue Höhe)
        foreach (var o in _scene.Objects)
        {
            bool cand = false;
            foreach (var lod in o.Lods) foreach (var pt in lod.Parts) if (ReflMixOf(pt.Material) > 0.001f) cand = true;
            if (!cand && minRefl > 0.001f)
            {
                var sz = o.BoundsMax - o.BoundsMin;
                if (sz.X * sz.Z >= 40f) foreach (var lod in o.Lods) foreach (var pt in lod.Parts) if (pt.Material.Kind == MaterialKind.Object) cand = true;
            }
            if (!cand || o.Lods.Length == 0) continue;
            foreach (var pt in o.Lods[0].Parts)
            {
                var c = pt.Collision;
                if (c == null) continue;
                for (int i = 0; i + 2 < c.Length; i += 3)
                {
                    var a = Vector3.Transform(c[i], o.Model); var b = Vector3.Transform(c[i + 1], o.Model); var cc = Vector3.Transform(c[i + 2], o.Model);
                    var n = Vector3.Cross(b - a, cc - a);
                    float area2 = n.Length();
                    if (area2 < 1e-6f || MathF.Abs(n.Y) / area2 < 0.97f) continue;   // nur waagerecht
                    int k = (int)MathF.Round((a.Y + b.Y + cc.Y) / 3f / 0.05f);
                    bins.TryGetValue(k, out float w); bins[k] = w + area2 * 0.5f;
                    binY.TryGetValue(k, out float wy); binY[k] = wy + (a.Y + b.Y + cc.Y) / 3f * area2 * 0.5f;
                }
            }
        }
        if (bins.Count == 0) { Log.Info("Bodenspiegelung: keine spiegelnden Flächen"); return; }
        int best = 0; float bw = 0f;
        foreach (var kv in bins) if (kv.Value > bw) { bw = kv.Value; best = kv.Key; }
        if (bw < 4f) { Log.Info("Bodenspiegelung: keine waagerechte Fläche >= 4 m²"); return; }   // Unity: kein Boden
        // Unity FindFloorPlane: Boden = die UNTERSTE große waagerechte Ebene (Decken, Galerien, Dächer sind gleich groß oder größer)
        int lowest = best;
        foreach (var kv in bins) if (kv.Value >= bw * 0.3f && kv.Key < lowest) lowest = kv.Key;
        best = lowest;
        // genaue Höhe: flächengewichtetes Mittel der echten Dreieckshöhen in den Nachbarklassen (Unity: Mittel der Klassen;
        // hier die echten Höhen, damit Einmischen/Clip im Shader auf ±6 cm sicher greifen)
        float sw = 0f, sy = 0f;
        for (int d = -1; d <= 1; d++) if (bins.TryGetValue(best + d, out float x) && binY.TryGetValue(best + d, out float xy)) { sw += x; sy += xy; }
        _mirrorY = sw > 0f ? sy / sw : best * 0.05f;
        _mirrorValid = true;
        int nRefl = 0;
        if (minRefl > 0.001f)
            foreach (var o in _scene.Objects)
            {
                if (o.BoundsMin.Y > _mirrorY + 0.1f || o.BoundsMax.Y < _mirrorY - 0.1f) continue;
                foreach (var lod in o.Lods)
                    foreach (var pt in lod.Parts)
                        if (pt.Material.Kind == MaterialKind.Object && MaterialFactory.TryGetInfo(pt.Material, out var mi) && mi.Desc.ReflMix < minRefl)
                        {
                            mi.Desc.ReflMix = minRefl;
                            _v.Renderer.UpdateMaterial(pt.Material, mi.Desc);
                            nRefl++;
                        }
            }
        Log.Info($"Bodenspiegelung: Ebene y {_mirrorY:0.00} ({bw:0} m² waagerecht), {nRefl} Materialien auf Garagen-Mindestwert {minRefl:0.00}");
    }

    /// <summary>Unity PollFurniture: geladene Möbel unter ihren Ankern aufbauen (InteriorFx.Apply(null): nur Glas), Kollision.</summary>
    internal static void PollFurniture()
    {
        if (_furnTask == null || !_furnTask.IsCompleted) return;
        var t = _furnTask; _furnTask = null;
        if (t.IsFaulted) return;
        var st = _t.Streamer;
        if (_scene == null || st == null) { foreach (var (_, m) in t.Result) m?.Release(); return; }
        foreach (var (e, m) in t.Result)
        {
            var h = st.BuildScene(m, e.M * _holder, false, glassOnly: true);
            st.Scenes.Add(h);
            _furniture.Add(h);
            var col = PrepareStatic(h, false);
            GAME_MODE_LIVING.RegisterHouseObject(e, h, col);   // My House: Möbel tauschbar
        }
        RenderFx.ProbeVersion++;   // Sonde mit den Möbeln neu
        Log.Info($"Interieur: {t.Result.Count} Möbel aufgebaut");
    }

    /// <summary>
    /// Unity PrepareStatic: Kollision für jedes Netz (alle LOD-Stufen), außer Glas/transparente (Warteschlange ≥ 2900) – flache
    /// Böden schon – und ausgeblendete Hilfskörper (haben hier kein Material). Netz braucht zwei Ausdehnungen (UnityUtil.IsCollidable).
    /// </summary>
    internal static StaticCollider? PrepareStatic(MapStreamer.SceneHandle h, bool collideTransparent)
    {
        var phys = Phys;
        if (phys == null) return null;
        var tris = new List<Vector3>();
        foreach (var ro in h.Objects)
        {
            var sz = ro.BoundsMax - ro.BoundsMin;
            bool flatFloor = MathF.Min(sz.X, sz.Z) is float w && w > 1.5f && sz.Y < 0.35f && sz.Y < w * 0.1f;   // Unity IsFlatFloor
            bool flip = ro.Model.GetDeterminant() < 0f;
            foreach (var lod in ro.Lods)
            {
                if (lod.Parts.Length == 0) continue;
                if (lod.Parts[0].Material.Kind == MaterialKind.Transparent && !collideTransparent && !flatFloor) continue;   // sharedMaterial.renderQueue >= 2900
                foreach (var part in lod.Parts)
                {
                    var c = part.Collision;
                    if (c == null) continue;
                    for (int i = 0; i + 2 < c.Length; i += 3)
                    {
                        tris.Add(Vector3.Transform(c[i], ro.Model));
                        tris.Add(Vector3.Transform(c[flip ? i + 2 : i + 1], ro.Model));
                        tris.Add(Vector3.Transform(c[flip ? i + 1 : i + 2], ro.Model));
                    }
                }
            }
        }
        if (tris.Count == 0) return null;
        var sc = phys.AddStaticMesh(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tris), "Interieur");
        if (sc != null) { _colliders.Add(sc); _own.Add(sc); }
        return sc;
    }

    /// <summary>
    /// Unity AddSafetyFloor: unsichtbarer Boden (120 x 120 m, Oberkante 5 cm unter dem tiefsten Boden an Startkamera/Stellplätzen).
    /// </summary>
    internal static void AddSafetyFloor(InteriorScene data)
    {
        var phys = Phys;
        var cam = data.StartCam;
        Vector3 eye = cam != null ? Vector3.Transform(cam.Position, _holder) : _holder.Translation + Vector3.UnitY * 1.7f;
        float floor = eye.Y - 1.65f;
        if (phys != null && phys.Raycast(eye, -Vector3.UnitY, 4f, out var hit)) floor = hit.Point.Y;
        foreach (var slot in data.CarSlots)
        {
            var sp = Vector3.Transform(slot.Position, _holder);
            float sf = sp.Y;
            if (phys != null && phys.Raycast(sp + Vector3.UnitY, -Vector3.UnitY, 3f, out var sh)) sf = sh.Point.Y;
            floor = MathF.Min(floor, sf);
        }
        _floorPoint = new Vector3(eye.X, floor, eye.Z);
        if (phys == null) return;
        // Box 120 x 0,5 x 120 bei floor - 0,3, gedreht wie das Interieur -> Oberseite (AddStaticMesh trägt beide Windungen ein)
        float y = floor - 0.05f;
        var rot = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, _spot!.Value1));
        Vector3 P(float x, float z) => new Vector3(eye.X, y, eye.Z) + Vector3.TransformNormal(new Vector3(x, 0f, z), rot);
        var a = P(-60, -60); var b = P(60, -60); var c = P(60, 60); var d = P(-60, 60);
        var q = new[] { a, b, c, a, c, d };
        var sc = phys.AddStaticMesh(q, "Interieur Sicherheitsboden");
        if (sc != null) { _colliders.Add(sc); _own.Add(sc); }
    }

    /// <summary>Unity ComputeClipBox: Quader des Interieurs im Raum des Holders (aus den Bounds der Szene).</summary>
    internal static void ComputeClipBox()
    {
        _clipValid = false;
        if (_scene == null || _scene.Objects.Count == 0) return;
        bool any = false; Vector3 mn = default, mx = default;
        foreach (var o in _scene.Objects)
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) != 0 ? o.BoundsMax.X : o.BoundsMin.X, (i & 2) != 0 ? o.BoundsMax.Y : o.BoundsMin.Y, (i & 4) != 0 ? o.BoundsMax.Z : o.BoundsMin.Z);
                var l = Vector3.Transform(c, _holderInv);
                if (!any) { mn = mx = l; any = true; } else { mn = Vector3.Min(mn, l); mx = Vector3.Max(mx, l); }
            }
        if (!any) return;
        _clipCenter = (mn + mx) * 0.5f;
        // Rand nur seitlich (Fenster); nach oben/unten großzügig
        _clipExt = (mx - mn) * 0.5f - new Vector3(ClipInset, 0f, ClipInset) + new Vector3(0f, 1.0f, 0f);
        _clipValid = _clipExt.X > 0.5f && _clipExt.Z > 0.5f;
    }

    /// <summary>
    /// Unity DisableWorldColliders (nicht im Wohnbereich) und SetExteriorVisible (ausgeblendete Exterior-Teile ohne Kollision): Welt-
    /// Kollider sind dort aus. Die Welt-Kollision liegt hier als ein Netz je Sektor vor -> Treffer im Interieur-Quader bzw. in
    /// ausgeblendeten Teilen werden verworfen.
    /// </summary>
    internal static void UpdateRayFilter()
    {
        var phys = Phys;
        if (phys == null) return;
        phys.RayRejectStatic = (s, p) =>
        {
            if (_carCols.Contains(s)) return _floorQuery || _aimQuery;   // Boden unter dem Stellplatz bzw. Wand vor einem Auto (Unity !IsCarCollider)
            if (_own.Contains(s)) return false;
            if (_clipValid && _kind != Kind.Living)
            {
                var l = Vector3.Transform(p, _holderInv) - _clipCenter;
                if (MathF.Abs(l.X) <= _clipExt.X + ClipInset && MathF.Abs(l.Y) <= _clipExt.Y && MathF.Abs(l.Z) <= _clipExt.Z + ClipInset) return true;
            }
            foreach (var (mn, mx) in _hiddenBoxes)
                if (p.X >= mn.X && p.X <= mx.X && p.Y >= mn.Y && p.Y <= mx.Y && p.Z >= mn.Z && p.Z <= mx.Z) return true;
            return false;
        };
    }

    /// <summary>Unity WorldHiddenWanted: Außenwelt nur in Garagen ausblenden (durch Fenster sieht man im Spiel die Insel).</summary>
    internal static bool WorldHiddenWanted => HideOutsideWorld && _scene != null
        && (_kind == Kind.Garage || HideOutsideEverywhere || (_kind == Kind.Living && HideOutsideInLiving));

    internal static void HideWorld()
    {
        var st = _t.Streamer;
        if (st != null) st.HideWorld = WorldHiddenWanted;
    }

    /// <summary>Unity SetExteriorVisible: Interieur-Attrappen des Spot-Exteriors aus, LOD1 im Raum sperren.</summary>
    internal static void SetExteriorVisible(bool visible, BankModel? interiorModel = null)
    {
        var st = _t.Streamer;
        if (visible)
        {
            _extActive = false; _extNames = null;
            foreach (var o in _hiddenExterior) o.Active = true;
            _hiddenExterior.Clear(); _hiddenBoxes.Clear();
            _clipValid = false;
            st?.BlockLod1(null, null);   // grobe LOD1-Gebäude wieder nach Entfernung steuern
            return;
        }
        if (_spot == null || st == null) return;
        // Szenennamen des Interieurs (gleiche Szene steckt oft als Platzhalter im Exterior)
        var names = new List<string>();
        if (interiorModel != null)
            foreach (var set in interiorModel.AllObjectSets())
                if (!string.IsNullOrEmpty(set.Name)) names.Add(Path.GetFileNameWithoutExtension(set.Name));
        // Ausdehnung des Interieurs (Welt)
        bool haveBounds = false; Vector3 mn = default, mx = default;
        if (!HideWholeExterior && _scene != null)
            foreach (var o in _scene.Objects)
            {
                if (!haveBounds) { mn = o.BoundsMin; mx = o.BoundsMax; haveBounds = true; }
                else { mn = Vector3.Min(mn, o.BoundsMin); mx = Vector3.Max(mx, o.BoundsMax); }
            }
        if (haveBounds) { mn -= Vector3.One; mx += Vector3.One; st.BlockLod1(mn, mx); }
        else { var c = _holder.Translation; st.BlockLod1(c - new Vector3(20f, 10f, 20f), c + new Vector3(20f, 10f, 20f)); }
        _extNames = names; _extHaveBounds = haveBounds; _extMin = mn; _extMax = mx; _extActive = true;
        _extUntil = _time + 30f; _extNext = _time + 1f; _extNodeCount = -1;
        HideSpotExterior();
    }

    /// <summary>Unity UpdateSpotExterior: nachgeladene Exterior-Platzhalter erneut ausblenden (erst jede Sekunde, später alle 3 s bei neuen Kacheln).</summary>
    internal static void UpdateSpotExterior()
    {
        if (!_extActive || _time < _extNext) return;
        bool early = _time < _extUntil;
        _extNext = _time + (early ? 1f : 3f);
        HideSpotExterior(!early);
    }

    internal static void HideSpotExterior(bool onlyIfChanged = false)
    {
        var st = _t.Streamer;
        if (_spot == null || st == null || _extNames == null) return;
        string n1 = "Spot_" + _spot.Code, n2 = n1 + "_low";
        var nodes = new List<MapStreamer.Node>();
        foreach (var n in st.NodesNamed(n1)) nodes.Add(n);
        foreach (var n in st.NodesNamed(n2)) nodes.Add(n);
        if (onlyIfChanged && nodes.Count == _extNodeCount) return;
        _extNodeCount = nodes.Count;
        var center = _spot.Position;
        foreach (var node in nodes)
        {
            if (!node.HasBounds) continue;
            var nc = (node.BoundsMin + node.BoundsMax) * 0.5f;
            // Unity: Wurzel des Spots (Spot-Position) im Umkreis 50 m
            if (HorizDist(nc, center) > 50f && HorizDist(node.BoundsMin, center) > 50f && HorizDist(node.BoundsMax, center) > 50f) continue;
            foreach (var o in node.Objects)
            {
                if (!o.Active) continue;
                bool hide = HideWholeExterior || (!_extHaveBounds && _extNames.Count == 0)
                    || string.Equals(o.Name, "INTERIOR", StringComparison.OrdinalIgnoreCase);
                if (!hide)
                    foreach (var nm in _extNames)
                        if (o.Name.StartsWith(nm, StringComparison.OrdinalIgnoreCase)) { hide = true; break; }
                // Teil liegt komplett im Interieur (z.B. vereinfachte Innenräume des Exteriors)?
                if (!hide && _extHaveBounds)
                    hide = o.BoundsMin.X >= _extMin.X && o.BoundsMin.Y >= _extMin.Y && o.BoundsMin.Z >= _extMin.Z
                        && o.BoundsMax.X <= _extMax.X && o.BoundsMax.Y <= _extMax.Y && o.BoundsMax.Z <= _extMax.Z;
                if (!hide) continue;
                o.Active = false;
                _hiddenExterior.Add(o);
                _hiddenBoxes.Add((o.BoundsMin - new Vector3(0.05f), o.BoundsMax + new Vector3(0.05f)));
            }
        }
    }

    internal static void ClearScene()
    {
        var st = _t.Streamer;
        SetExteriorVisible(true);
        if (st != null) st.HideWorld = false;
        if (_scene != null && st != null) st.DestroyScene(_scene);
        _scene = null; _data = null;
        foreach (var f in _furniture) st?.DestroyScene(f);
        EndDealerModes();
        GAME_MODE_LIVING.EndHomeModes();
        ClearCars();
        _furniture.Clear();
        if (_furnTask != null) { var ft = _furnTask; _furnTask = null; ft.ContinueWith(x => { if (!x.IsFaulted) foreach (var (_, m) in x.Result) m?.Release(); }); }
        RenderFx.PlanarOn = false; _mirrorValid = false;
        var phys = Phys;
        foreach (var c in _colliders) phys?.Remove(c);
        _colliders.Clear(); _own.Clear();
        if (phys != null) phys.RayRejectStatic = null;
        _uiAim = null;
    }

    /// <summary>Unity ExitInterior: Szene weg, Auto an Position2/Richtung2 des Spots.</summary>
    internal static void ExitInterior(CarPhysics? car = null)
    {
        var s = _spot;
        ClearScene();
        _fp = null;
        _t.Input.CursorLocked = false;
        _t.NearClip = 1f;
        _spot = null; _spotBanks = null; _sceneTask = null;
        Paradise.Core.World.WorldWeather.Inside = false;   // Unity SetInteriorAmbient(false)
        _v.Suspended = false;
        if (s == null) return;
        var vm = Vm;
        var gp = s.Position2.LengthSquared() > 1f ? s.Position2 : s.Position;
        // Spots.bin Richtung2: vorne = (sin h, 0, cos h) im Spielraum
        var fwd = new Vector3(MathF.Sin(s.Heading2), 0f, MathF.Cos(s.Heading2));
        if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.UnitZ;
        fwd = Vector3.Normalize(fwd);
        car ??= _lastCar ?? (vm != null ? vm.FindCar(vm.StartCar) ?? (vm.Available.Count > 0 ? vm.Available[0] : null) : null);
        Log.Info($"Interieur verlassen {s.Code}, Auto: {car?.FileName ?? "-"}");
        TDU2LoadingScreen.Show("Welt (" + s.Code + ")", () => Inside || (vm?.Current != null), 1.0f, true);
        if (car != null && vm != null)
        {
            _lastCar = car;
            vm.SpawnCar(car.FileName, gp, fwd);   // Boden + 0,4 m wie Unity
        }
        _nextScan = _time + 2f;
    }

    // ------------------------------------------------------------------ Drinnen (Unity UpdateInside)
    /// <summary>Jedes Bild im Interieur. false = verlassen (zurück in die Freifahrt).</summary>
    internal static bool UpdateInside(float dt, bool paused)
    {
        _time += dt;
        PollScene();
        if (!Inside) return false;
        if (_fp == null || _scene == null || _data == null) return true;
        // Fahrzeugmenü (Unity TDU2CarDealer): Maus frei für den Film, kein Laufen/Umsehen; im Auto Maus gefangen (Umsehen)
        // Fahrzeugliste der Garage (home.vmf garage/vehicule_list): wie ein Menü – Maus frei, kein Laufen/Umsehen
        bool carList = GAME_MODE_GARAGE.GarageListOpen;
        _t.Input.CursorLocked = !paused && (!_dealerMenu || _carIn == CarIn.Inside) && !GAME_MODE_LIVING.HomeMenuFreesMouse && !carList;
        _fp.LookEnabled = !paused && !_dealerMenu && GAME_MODE_LIVING.HomeAllowsLook && !carList;
        if (_ui != null) _ui.MouseEnabled = (_dealerMenu && _carIn != CarIn.Inside) || GAME_MODE_LIVING.HomeMenuFreesMouse || carList;
        PollFurniture();
        PollCars();
        GAME_MODE_SHOP.PollDealerPreview();                   // Händler: Ausstellungsauto mit der gewählten Ausstattung (auch nach dem Schließen)
        UpdateSpotExterior();                  // nachgeladene Exterior-Platzhalter (Unity UpdateInside)
        if (WorldHiddenWanted != (_t.Streamer?.HideWorld ?? false)) { HideWorld(); RenderFx.ProbeVersion++; }
        if (!paused) CarAccessories.UpdateAll(dt);   // Türen/Fenster der Ausstellungs-/Garagenautos (Unity: jedes Bild im Interieur)
        if (!paused && _carIn != CarIn.Inside && !carList) _fp.Update(dt, _t.Input, Phys);
        if (_t.Input.IsKeyDown(0x7A) && !_f11Was) Paradise.Core.Render.RenderFx.DebugPlanar = !Paradise.Core.Render.RenderFx.DebugPlanar;   // F11: Diagnose Spiegelbild (kann wieder raus)
        _f11Was = _t.Input.IsKeyDown(0x7A);
        // Fällt man trotzdem (Rampe/Loch), zurück an den Startpunkt (Unity CatchFall)
        if (_fp.Feet.Y < _floorPoint.Y - 6f)
        {
            var cam = _data.StartCam;
            if (cam != null) _fp.Place(Vector3.Transform(cam.Position, _holder), Vector3.TransformNormal(cam.Forward, _holder));
            Log.Warn("Interieur: durch den Boden gefallen – zurück zum Startpunkt");
        }
        // Wohnbereich: Tagebuch, MyTDULife, My House, TDU Media (Unity UpdateInside: vor dem Händler, jeweils return)
        if (!paused) UpdateBackWatch();   // Zurück-Überwachung (Unity TDU2MenuFlow), für alle Interieur-Menüs
        if (!paused && GAME_MODE_LIVING.UpdateHomeMenus(dt)) { ApplyCamera(); return Inside; }
        if (_dealerMenu && !paused)
        {
            // Autohändler/Garage: Mini-Menü, Daten, Vergleichen, Einsteigen. Ein Fehler darf das Spiel nicht im Menü festhalten
            // -> Menü schließen, Laufmodus (Unity UpdateInside).
            try { UpdateDealerMenu(dt); }
            catch (Exception ex) { Log.Error("Fahrzeugmenü: " + ex.Message); try { DealerForceClose(); } catch (Exception) { _dealerMenu = false; } }
            if (!Inside) return false;
        }
        if (_dealerMenu && _carIn == CarIn.Inside) PlaceDealerCam(); else ApplyCamera();
        if (paused || _dealerMenu || _fp == null || GAME_MODE_LIVING.HomeMenuOpen || carList) return Inside;

        // Autos: wie im Spiel über die Hülle des Autos. Das schon anvisierte Auto bleibt mit etwas größerer Hülle gewählt,
        // damit der Cursor beim leichten Umsehen nicht ständig wechselt (Unity UpdateInside).
        _aimTarget = null;
        var eye = _fp.Eye; var dir = _fp.Forward;
        float bestT = InteractDistance;
        DisplayCar? aimCar = null; float carT = CarAimDistance;
        foreach (var c in _cars)
        {
            float ex = ReferenceEquals(c, _uiAim) ? 0.5f : 0.15f;
            if (RayBox(eye, dir, c.BoxMin - new Vector3(ex), c.BoxMax + new Vector3(ex), out float ct) && ct < carT) { carT = ct; aimCar = c; }
        }
        if (aimCar != null && carT > 0.5f && Phys != null)
        {
            _aimQuery = true;   // Autos zählen nicht als Wand
            try { if (Phys.Raycast(eye, dir, carT, out var hit) && hit.Distance < carT - 0.5f) aimCar = null; }   // Wand/Säule davor
            finally { _aimQuery = false; }
        }
        if (aimCar != null) { _aimTarget = aimCar; bestT = MathF.Max(carT, 0.5f); _aimCarDist = carT; }
        // Hotspots (FpSelectable-Volumen, Spielraum der Szene)
        var o = Vector3.Transform(eye, _holderInv);
        var d = Vector3.TransformNormal(dir, _holderInv);
        // Original (unpacked.exe MngFirstPerson 0xCD60F0–0xCD63A4): Kandidat ist ein Auswahlpunkt, dessen Mitte vor der Kamera und
        // näher als 10 m liegt (Radius 0xCD24D0 = 10) und dessen Volumen die Strecke Kamera -> Kamera + Blickrichtung * 10 trifft
        // (0xCD3E40); gewählt wird der mit der kleinsten Entfernung der Mitte – ohne Wandtest. Vorher wählten wir nach dem Eintritts-
        // punkt des Strahls und dem kleinsten Volumen: das große Volumen des Tagebuchs gewann fast überall ("Info" im ganzen Raum).
        InteriorScene.Selectable? bestSel = null; float bestD2 = float.MaxValue;
        var dn = d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : d;
        foreach (var s in _data.Selectables)
        {
            if (!IsUsable(s)) continue;
            var v = SelectableCenter(s) - o;
            float d2 = v.LengthSquared();
            if (d2 >= SelectRadius * SelectRadius || d2 >= bestD2) continue;
            if (d2 > 1e-12f && Vector3.Dot(Vector3.Normalize(v), dn) <= 0f) continue;
            if (!s.Raycast(o, dn, out float t) || t > SelectRadius || t > bestT) continue;
            bestSel = s; bestD2 = d2;
        }
        if (bestSel != null) _aimTarget = bestSel;
        // Original 0xCD5B27: nach einem Wechsel 0,2 s kein neues Ziel (wohl aber "kein Ziel")
        if (_aimHold > 0f) _aimHold -= dt;
        if (!ReferenceEquals(_aimTarget, _uiAim))
        {
            if (_aimTarget != null && _aimHold > 0f) _aimTarget = _uiAim;
            else _aimHold = 0.2f;
        }
        UpdateUiAim(_aimTarget);
        // Fahrzeugdaten rechts (und Preis): öffnet der Film über Cursor "info"; ist danach noch nichts offen, selbst öffnen
        if (_infoHiddenFor != null && !ReferenceEquals(_infoHiddenFor, _uiAim)) _infoHiddenFor = null;
        if (_aimInfoAt > 0f && _time >= _aimInfoAt && _uiAim is DisplayCar && _ui != null)
        {
            _aimInfoAt = -1f;
            string cl = _ui.Label("/car_datas") ?? "";
            if (cl == "" || cl == "default" || cl == "close")
            {
                _ui.SetValue("/:ItemInfo", true);
                _ui.GotoLabel("/car_datas", "open", true);
                if (_kind == Kind.Dealer) _ui.GotoLabel("/price", "open", true);
            }
        }
        // Händler/Garage: Fahrzeugmenü der Hilfeleiste (Steuerkreuz unten links): Num4/Num6 Tür links/rechts, Num8/Num2 Fenster hoch/runter
        if (_aimTarget is DisplayCar ac && (_kind == Kind.Dealer || _kind == Kind.Garage)) CarPad(CarAccessories.For(ac.Model));
        // Rücktaste im Laufmodus: Fahrzeugdaten/Vergleich ausblenden, bis ein anderes Ziel anvisiert wird
        if (TDU2Controls.KeyPressed(0x08) && _uiAim is DisplayCar && _ui != null)
        {
            _ui.GotoLabel("/car_datas", "close", true);
            if (_kind == Kind.Dealer) _ui.GotoLabel("/price", "close", true);
            _ui.SetValue("/:ItemInfo", false);
            _aimInfoAt = -1f; _infoHiddenFor = _uiAim;
        }
        // Unity: walk_use, Bestätigen oder Linksklick bei gefangener Maus
        bool use = TDU2Controls.Pressed("walk_use") || TDU2Controls.ConfirmPressed() || (_t.Input.CursorLocked && _t.Input.LeftMousePressed);
        if (use && _aimTarget != null) Activate(_aimTarget);
        return Inside;
    }

    internal static void ApplyCamera()
    {
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam == null || st == null || _fp == null) return;
        _t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(_fp.Eye);
        cam.Override = (_fp.Forward, _fp.Up);
        _t.FieldOfView = 60f;     // Unity TDU2FirstPerson.FieldOfView
        _t.NearClip = 0.05f;      // sonst schneidet die Kamera Wände/Möbel direkt vor dem Gesicht ab
    }

    /// <summary>Reichweite der Auswahlpunkte im Original (Radius 10 m, unpacked.exe 0xCD24D0).</summary>
    internal const float SelectRadius = 10f;
    internal static float _aimHold;

    /// <summary>Mitte des Auswahlpunkts (Mitte des Volumens [-1,1]^3 im Spielraum der Szene).</summary>
    internal static Vector3 SelectableCenter(InteriorScene.Selectable s) =>
        Matrix4x4.Invert(s.Volume, out var w) ? Vector3.Transform(Vector3.Zero, w) : s.Position;

    internal static float HotspotVolume(InteriorScene.Selectable s)
    {
        // Volumen des Hotspots (Spielraum): Determinante der Abbildung [-0,5, 0,5]^3 -> Raum
        if (!Matrix4x4.Invert(s.Volume, out var w)) return float.MaxValue;
        return MathF.Abs(w.GetDeterminant());
    }

    internal static bool IsUsable(InteriorScene.Selectable s)
    {
        string n = s.Name ?? "";
        if (n.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Type == 5) return _spotBanks?.Garage != null && _kind != Kind.Garage;
        if (s.Type == 6) return _spotBanks?.Living != null && _kind != Kind.Living;
        if (s.Type == 9) return true;   // Tagebuch / My House (Unity: nur der Besitzer – Gäste gibt es hier nicht)
        return true;
    }

    internal static void Activate(object target)
    {
        if (target is DisplayCar dc)
        {
            // Autohändler/Garage: Original-Menü des Films (Garage: Losfahren, Einsteigen, Daten, Vergleichen)
            if ((_kind == Kind.Dealer || _kind == Kind.Garage) && OpenDealerMenu(dc)) return;
            if (_kind == Kind.Dealer || _kind == Kind.StickerShop) { Log.Info("Interieur: Menü für " + dc.Car.FileName + " in " + _kind + " noch nicht portiert"); return; }
            ExitInterior(dc.Car);
            return;
        }
        if (target is not InteriorScene.Selectable s) return;
        string n = s.Name ?? "";
        if (n.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)) { ExitInterior(_lastCar); return; }
        Log.Debug("Hotspot " + s.Name + " Typ " + s.Type + " in " + _kind);
        switch (s.Type)
        {
            // Raumwechsel; der Spielmodus (Oberfläche) folgt über Mode (GAME_MODE_LIVING.Run)
            case 5 when _spotBanks?.Garage != null: LoadScene(_spotBanks.Garage, Kind.Garage, null); break;
            case 6 when _spotBanks?.Living != null: LoadScene(_spotBanks.Living, Kind.Living, "cam_entree_garage"); break;
            case 54 when _spotBanks?.Living != null: LoadScene(_spotBanks.Living, Kind.Living, null); break;
            case 9 when _ui != null: GAME_MODE_LIVING.OpenDiary(); break;       // Tagebuch (home.vmf diary_in): MyTDULife, My House
            case 57: GAME_MODE_LIVING.OpenMedia(); break;                       // Fernseher: TDU Media (tdmedia.bnk)
            case 12 or 14 when _kind == Kind.Garage: GAME_MODE_GARAGE.OpenGarageList(); break;   // Plakat: Fahrzeugliste (home.vmf garage "list")
            default: Log.Info("Hotspot noch nicht portiert: " + Label(s) + " (Typ " + s.Type + ")"); break;
        }
    }

    /// <summary>Hotspot-Beschriftung (wie HST_LIVING_* / HST_CAR_DEALER* im Spiel).
    /// Das muss aus der Lang gelsesen werden wie alle Texte....
    /// </summary>
    internal static string Label(InteriorScene.Selectable s)
    {
        string n = s.Name ?? "";
        if (n.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)) return Lang(368, "Exit");
        return s.Type switch
        {
            1 => Lang(100, "Fahrzeug Liste"),
            5 => Lang(1888, "Garage"),
            6 => Lang(1887, "Wohnzimmer"),
            9 => Lang(1875, "MyTDULife"),
            10 => Lang(1890, "Gaderobe"),
            12 or 14 => Lang(1811, "Fahrzeug Liste"),
            50 => "Bar",
            51 => "Büro",
            54 => Lang(351, "klub"),
            57 => "TDU " + Lang(4919, "media"),
            _ => n,
        };
    }

    /// <summary>_root.SelItemType des Films (home.vmf ItemType: exit, garage, living, diary, dressing, club, billboard, td_media) zur Stelle.</summary>
    internal static string? SelItemTypeOf(InteriorScene.Selectable s)
    {
        string n = s.Name ?? "";
        if (n.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)) return "exit";
        return s.Type switch { 5 => "garage", 6 => "living", 9 => "diary", 10 => "dressing", 12 or 14 => "billboard", 54 => "club", 57 => "td_media", _ => null };
    }

    internal static string IconLabel(InteriorScene.Selectable s)
    {
        string n = s.Name ?? "";
        if (n.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)) return "exit";
        return s.Type switch { 5 => "garage", 6 => "living", 9 => "diary", 10 => "dressing", 12 or 14 => "billboard", 54 => "club", 57 => "game_plug", _ => "default" };
    }

    // ------------------------------------------------------------------ Original-Oberfläche (shopscar.bnk, home.bnk)
    internal static readonly HashSet<string> UiVisible = new(StringComparer.OrdinalIgnoreCase) { "cursor", "title_bar", "car_datas", "price", "help" };

    /// <summary>Film des Spielmodus übernehmen (GAME_MODE_SHOP/LIVING/GARAGE.Enter).</summary>
    internal static void AttachUi(VmfPlayer? ui)
    {
        if (_hooked != null && _hooked != ui) { _hooked.FSCommand -= OnUiCommand; _hooked = null; }
        _ui = ui;
        if (ui != null && _hooked != ui) { ui.FSCommand += OnUiCommand; _hooked = ui; }
        if (ui != null) ui.MouseEnabled = false;   // Laufen: Klicks gehören dem Interieur, nicht dem Film (Unity: MouseEnabled nur in Menüs)
        if (SceneReady) LoadUi();
    }

    /// <summary>Unity LoadUi: Label je Raum, nur Fadenkreuz, Titel, Fahrzeugdaten, Preis und Hilfeleiste sichtbar.</summary>
    internal static void LoadUi()
    {
        if (_ui == null) return;
        try
        {
            string stop = _kind == Kind.Dealer ? "cd_stop" : _kind == Kind.Garage ? "garage_stop" : "living_stop";
            _ui.GotoLabel("/", stop, false);
            foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
            _ui.SetVisible("/car_datas/datas_base/limits", false);
            _ui.SetVariable("/:Title", TitleFor(_kind));
            _ui.SetVariable("/:MyMoneyStr", " ");
            _ui.SetVariable("/:ClubMoney", " ");
            _ui.GotoLabel("/title_bar", "open", true);
            _ui.GotoLabel("/title_bar/ic", "garage", false);
            _ui.SetVisible("/cursor/low_bar/top_bar", false);
            _ui.GotoLabel("/car_datas", "default", false);
            _ui.GotoLabel("/price", "default", false);
            ResetUiAim();
        }
        catch (Exception ex) { Log.Error("Interieur-Oberfläche: " + ex.Message); }
    }

    /// <summary>
    /// GENAUSO AUS DEM LANG FILE....
    /// </summary>
    /// <param name="kind"></param>
    /// <returns></returns>
    internal static string TitleFor(Kind kind)
    {
        if (_spotBanks != null && _spotBanks.IsCasino)
            return Lang(4172, "Casino");
        if (kind == Kind.Garage)
            return Lang(1888, "Garage"); //Lang(3300, "Garage");
        if (kind == Kind.Living)
            return Lang(1887, "Wohnzimmer"); //"Wohnbereich";
        return _spotBanks?.Name ?? "";
    }

    /// <summary>Anvisiertes Ziel vergessen und den Cursor des Films wie beim Wegschauen schließen: /cursor "off" -> ItemAction("out")
    /// -> ItemType("out") schließt je SelItemType low_bar/Hilfeleiste/Fahrzeugdaten. Ersetzt GotoLabel("/cursor", "valid") aus dem
    /// Unity-Code: der Cursor-Clip (home.vmf S181) hat kein Label "valid" (nur default/on/info/on_stop/mini_menu/off/close), der
    /// Aufruf tat nichts – low_bar ("Info" vom Tagebuch) blieb stehen, und UpdateUiAim(null) kehrte bei _uiAim == null sofort zurück.</summary>
    internal static void ResetUiAim()
    {
        if (_ui != null)
        {
            Log.Info("Anvisiert: zurückgesetzt (vorher " + AimName(_uiAim) + ") – Cursor " + (_ui.Label("/cursor") ?? "-") + ", low_bar " + (_ui.Label("/cursor/low_bar") ?? "-")
                     + ", SelItemType " + (_ui.GetVariable("/:SelItemType") ?? "-"));
            _ui.GotoLabel("/cursor", "off", true);
        }
        _uiAim = null;
    }

    /// <summary>Unity TDU2Interiors.AimName (für das Protokoll).</summary>
    internal static string AimName(object? t) => t == null ? "-" : t is DisplayCar dc ? "Auto " + (dc.Car != null ? dc.Car.FileName : "?")
        : t is InteriorScene.Selectable s ? "Hotspot " + Label(s) : t.ToString() ?? "?";

    /// <summary>Wohnbereich/Garage: Oberfläche ist home.vmf – Cursor/Leiste steuert der Film selbst (wie im Original).</summary>
    internal static bool HomeFilm => _kind == Kind.Living || _kind == Kind.Garage;

    internal static void UpdateUiAim(object? target)
    {
        if (_ui == null || ReferenceEquals(target, _uiAim)) return;
        // Unity: Debug.Log("[TDU2] Anvisiert: …"); zusätzlich der Zustand des Cursor-Clips (Fehlersuche "low_bar bleibt stehen")
        Log.Info("Anvisiert: " + AimName(target) + (target is DisplayCar ? " in " + _aimCarDist.ToString("0.0") + " m" : "") + " (vorher " + AimName(_uiAim)
                 + ") – Cursor " + (_ui.Label("/cursor") ?? "-") + ", low_bar " + (_ui.Label("/cursor/low_bar") ?? "-") + ", SelItemType " + (_ui.GetVariable("/:SelItemType") ?? "-"));
        // altes Ziel schließen
        if (_uiAim is DisplayCar && _kind == Kind.StickerShop) _ui.GotoLabel("/cursor/low_bar", "close", true);
        else if (_uiAim is DisplayCar)
        {
            _ui.GotoLabel("/car_datas", "close", true);
            if (_kind == Kind.Dealer) _ui.GotoLabel("/price", "close", true);
            _ui.GotoLabel("/title_bar", "open", true);   // Titel wieder einblenden (Preis lag an seiner Stelle)
        }
        else if (_uiAim is InteriorScene.Selectable && !HomeFilm) _ui.GotoLabel("/cursor/low_bar", "close", true);
        _uiAim = target;
        if (target is not DisplayCar) _aimInfoAt = -1f;
        if (target is DisplayCar && _kind == Kind.StickerShop)
        {
            _ui.SetVariable("/:t_wm_destination", "Lackieren & bekleben");
            _ui.GotoLabel("/cursor", "on", true);
            _ui.GotoLabel("/cursor/low_bar", "open", true);
            _ui.GotoLabel("/cursor/low_bar/ic", "default", false);
        }
        else if (target is DisplayCar dc && (_kind == Kind.Dealer || _kind == Kind.Garage))
        {
            // wie im Spiel: Cursor "on" -> ItemAction("over") (Hilfeleiste "options_menu" = Fahrzeugmenü mit Steuerkreuz),
            // läuft selbst weiter nach "info" -> ItemType("info") öffnet Fahrzeugdaten (und beim Händler den Preis).
            FillCarData(dc.Car);
            _ui.SetVariable("/:SelItemType", "vehicle");
            if (_kind == Kind.Dealer) _ui.SetVariable("/:WhatPage", "car_dealer");
            _ui.SetValue("/:ItemInfo", false);
            _aimInfoAt = _time + 0.45f;
            _ui.SetValue("/:Input", true);
            _ui.GotoLabel("/car_datas", "default", false);
            if (_kind == Kind.Dealer) { _ui.GotoLabel("/price", "default", false); _ui.GotoLabel("/title_bar", "close", true); }
            _ui.GotoLabel("/cursor", "on", true);
        }
        else if (target is DisplayCar dc2)
        {
            FillCarData(dc2.Car);
            _ui.GotoLabel("/car_datas", "open", true);
            _ui.GotoLabel("/cursor", "info", true);
        }
        else if (target is InteriorScene.Selectable hs && HomeFilm)
        {
            // Original (unpacked.exe MngFirstPerson, Auswahlwechsel 0xCD5B3C–0xCD5E8E): nur _root.SelItemType = Name des Auswahlpunkts
            // setzen und "cursor" auf "on" (bzw. ohne Ziel "off") spielen. Alles Weitere – Text der Leiste (LowBarText -> t_wm_destination),
            // Symbol (low_bar.ic = SelItemType), Öffnen/Schließen der Leiste (disp_low_bar) – macht der Film in ItemAction/ItemType.
            // Vorher setzten wir low_bar/ic/t_wm_destination selbst und brachten den Zustand des Films (low_bar.opened) durcheinander:
            // die Leiste ("A Info") blieb ohne Ziel stehen.
            string? sel = SelItemTypeOf(hs);
            if (sel != null) _ui.SetVariable("/:SelItemType", sel);
            _ui.GotoLabel("/cursor", "on", true);
        }
        else if (target is InteriorScene.Selectable s)
        {
            // Wie im Spiel: _root.SelItemType der Stelle setzen – "on" ruft ItemAction("over") -> ItemType(), das je Art die
            // Anzeige schaltet (Ausgang/Garage/… schließt dabei die Fahrzeugdaten). Fehlt im Unity-Code: dort blieb "vehicle"
            // vom zuletzt anvisierten Auto stehen und der Film öffnete an der Tür wieder die Fahrzeugdaten.
            string sel = SelItemTypeOf(s);
            if (sel != null) _ui.SetVariable("/:SelItemType", sel);
            _ui.SetVariable("/:t_wm_destination", s.Type == 28 ? "Sticker-Shop" : Label(s));
            _ui.GotoLabel("/cursor", "on", true);
            _ui.GotoLabel("/cursor/low_bar", "open", true);
            _ui.GotoLabel("/cursor/low_bar/ic", IconLabel(s), false);
        }
        else _ui.GotoLabel("/cursor", "off", true);
    }

    internal static bool RayBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, out float t)
    {
        float t0 = 0f, t1 = float.MaxValue; t = 0f;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float a = i == 0 ? mn.X : i == 1 ? mn.Y : mn.Z, b = i == 0 ? mx.X : i == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(di) < 1e-8f) { if (oi < a || oi > b) return false; continue; }
            float u = (a - oi) / di, v = (b - oi) / di;
            if (u > v) (u, v) = (v, u);
            t0 = MathF.Max(t0, u); t1 = MathF.Min(t1, v);
            if (t0 > t1) return false;
        }
        t = t0; return true;
    }

    internal static string Lang(int id, string fallback) => FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, id) ?? fallback;

    /// <summary>
    /// Fahrzeugdaten wie GMShop.cpp (Unity FillCarData). Der Film füllt car_datas selbst: datas_base ruft SetDatas() auf und
    /// liest nur _root-Variablen – darum hier nur diese Variablen setzen (festgehalten) und SetDatas() aufrufen.
    /// </summary>
    /// <param name="asCompare">Vergleichsauto: dieselben Werte als comp_* / Comp_car (SetDatasCp zeigt sie neben dem angesehenen Auto).</param>
    internal static void FillCarData(CarPhysics? c, bool asCompare = false)
    {
        if (c == null || _ui == null) return;
        try
        {
            var r = c.Raw;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var de = Paradise.Core.Util.GermanFormat.Number;
            _ui.ReleaseForced("/car_datas/datas_base/limits");   // forb_* setzt SetDatas selbst (sichtbar = gesperrt)
            string version = r?.S("_version_name", "") ?? "";
            if (version == "##") version = "";
            string name = (string.IsNullOrEmpty(c.Brand) ? "" : c.Brand + " ") + c.ModelName + (version.Length > 0 ? " " + version : "");
            int cat = r != null ? r.I("_vehiclecategory") : 0;
            float bhp = r != null ? r.F("_power_bhp") : 0f;
            float acc = c.Accel0To100, vmax = c.MaxTheoricalSpeed;
            float brake = r != null ? r.F("_brakingdist") : 0f;
            int diff = r != null ? r.I("_difficulty") : 0;
            int price = r != null ? r.I("_price") : 0;
            // Kilometerzähler wie MngHud; Händlerwagen sind neu, eigene Autos aus dem gespeicherten Zähler
            double odo = _kind != Kind.Dealer ? VehicleHud.GetOdometer(c.Id) : 0;
            double km = odo / 1000.0, mi = odo / 1609.344;
            long kmW = (long)Math.Floor(km), miW = (long)Math.Floor(mi);
            int aAcc = acc > 0 ? (int)MathF.Round(Mathf.InverseLerp(12f, 2.5f, acc) * 100f) : 0;
            int aSpd = (int)MathF.Round(Mathf.InverseLerp(140f, 420f, vmax) * 100f);
            int aBrk = brake > 0 ? (int)MathF.Round(Mathf.InverseLerp(75f, 30f, brake) * 100f) : 50;
            bool isPaintable = r != null && r.I("_paintable") != 0;
            bool isStickerable = r != null && r.I("_stickerable") != 0;
            bool isTuningable = r != null && r.I("_tuningable") != 0;
            int engineType = r?.I("_engine_type", 1) ?? 1;
            int engineLoc = r != null ? r.I("_engine_localisation") : 1;
            string vehicleEngine = " - NA - " + engineType;
            switch (engineLoc)
            {
                case 1: _ui.PlayLabel("/car_datas/data_full/pattern_motor", "motor_front"); _ui.PinFrame("/car_datas/data_full/pattern_motor", 10); break;
                case 2: _ui.PlayLabel("/car_datas/data_full/pattern_motor", "motor_middle"); _ui.PinFrame("/car_datas/data_full/pattern_motor", 22); break;
                case 3: _ui.PlayLabel("/car_datas/data_full/pattern_motor", "motor_rear"); _ui.PinFrame("/car_datas/data_full/pattern_motor", 1); break;
            }
            int gearbox = r != null ? r.I("_gearbox") : 1;
            switch (gearbox)
            {
                case 1: _ui.PinFrame("/car_datas/data_full/pattern_wheel", 10); break;
                case 2: _ui.PinFrame("/car_datas/data_full/pattern_wheel", 22); break;
                case 4: _ui.PinFrame("/car_datas/data_full/pattern_wheel", 1); break;
            }
            switch (engineType)
            {
                case 1: vehicleEngine = Lang(2032, "4-Stroke"); break;
                case 2: vehicleEngine = Lang(2025, "Flathead 6 cylinder"); break;
                case 3: vehicleEngine = Lang(2031, "Inline 5"); break;
                case 4: vehicleEngine = Lang(2029, "Inline 3"); break;
                case 5: vehicleEngine = Lang(2020, "Inline 4"); break;
                case 6: vehicleEngine = Lang(2018, "Inline 6"); break;
                case 7: vehicleEngine = Lang(2028, "Rotary"); break;
                case 8: vehicleEngine = Lang(2023, "V10"); break;
                case 9: vehicleEngine = Lang(2024, "V12"); break;
                case 10: vehicleEngine = Lang(2030, "V16"); break;
                case 11: vehicleEngine = Lang(2027, "V-twin"); break;
                case 12: vehicleEngine = Lang(2022, "V6"); break;
                case 13: vehicleEngine = Lang(2019, "V8"); break;
                case 14: vehicleEngine = Lang(2026, "W12"); break;
                case 15: vehicleEngine = Lang(8118, "W16"); break;
                case 16: vehicleEngine = Lang(2025, "Flat 4 cylinder"); break;
            }
            if (asCompare)
            {
                using (_ui.PinScope())
                {
                    SetCommonData(c, r, vehicleEngine, isTuningable, isStickerable, isPaintable);
                    _ui.SetVariable("/:Comp_car", name);
                    _ui.SetVariable("/:comp_group", GroupLabel(cat));
                    _ui.SetVariable("/:comp_upgrade", "level0");
                    _ui.SetValue("/:comp_speed", Math.Clamp(aSpd, 1, 101));
                    _ui.SetValue("/:comp_accel", Math.Clamp(aAcc, 1, 101));
                    _ui.SetValue("/:comp_braking", Math.Clamp(aBrk, 1, 101));
                    _ui.SetValue("/:comp_difficulty", Math.Clamp(diff > 0 ? diff : 1, 1, 5));
                    _ui.SetVariable("/:comp_power_m", bhp > 0 ? (bhp * 1.01387f).ToString("0", ci) + " PS" : "-");
                    _ui.SetVariable("/:comp_power_i", bhp > 0 ? bhp.ToString("0", ci) + " bhp" : "-");
                    _ui.SetVariable("/:comp_max_speed_m", vmax > 0 ? vmax.ToString("0", ci) + " Km/h" : "-");
                    _ui.SetVariable("/:comp_max_speed_i", vmax > 0 ? (vmax / 1.609344f).ToString("0", ci) + " mph" : "-");
                    _ui.SetVariable("/:comp_0_100_m", acc > 0 ? acc.ToString("0.0", ci) + " sec" : "-");
                    _ui.SetVariable("/:comp_0_100_i", acc > 0 ? acc.ToString("0.0", ci) + " sec" : "-");
                }
                return;
            }
            using (_ui.PinScope())   // festhalten: der Film setzt einige davon beim Auf-/Zuklappen zurück
            {
                SetCommonData(c, r, vehicleEngine, isTuningable, isStickerable, isPaintable);
                _ui.SetValue("/:fake", false);
                _ui.SetValue("/:WhatPlatform", 1);
                _ui.SetVariable("/:MenuType", "pc");
                _ui.SetValue("/:IsPC_X360", true);
                _ui.SetValue("/:state_metric", c.Kmh ? 1 : 0);
                _ui.SetVariable("/:Current_car", name);
                _ui.SetVariable("/:cur_group", GroupLabel(cat));
                _ui.SetVariable("/:cur_upgrade", "level0");
                _ui.SetValue("/:cur_speed", Math.Clamp(aSpd, 1, 101));
                _ui.SetValue("/:cur_accel", Math.Clamp(aAcc, 1, 101));
                _ui.SetValue("/:cur_braking", Math.Clamp(aBrk, 1, 101));
                _ui.SetValue("/:cur_difficulty", Math.Clamp(diff > 0 ? diff : 1, 1, 5));
                _ui.SetVariable("/:cur_power_m", bhp > 0 ? (bhp * 1.01387f).ToString("0", ci) + " PS" : "-");
                _ui.SetVariable("/:cur_power_i", bhp > 0 ? bhp.ToString("0", ci) + " bhp" : "-");
                _ui.SetVariable("/:cur_max_speed_m", vmax > 0 ? vmax.ToString("0", ci) + " Km/h" : "-");
                _ui.SetVariable("/:cur_max_speed_i", vmax > 0 ? (vmax / 1.609344f).ToString("0", ci) + " mph" : "-");
                _ui.SetVariable("/:cur_0_100_m", acc > 0 ? acc.ToString("0.0", ci) + " sec" : "-");
                _ui.SetVariable("/:cur_0_100_i", acc > 0 ? acc.ToString("0.0", ci) + " sec" : "-");
                _ui.SetVariable("/:Km_m", (kmW % 100000000).ToString("D8"));
                _ui.SetVariable("/:Centaine_m", ((int)Math.Floor((km - kmW) * 10.0)).ToString());
                _ui.SetVariable("/:Km_i", (miW % 100000000).ToString("D8"));
                _ui.SetVariable("/:Centaine_i", ((int)Math.Floor((mi - miW) * 10.0)).ToString());
                _ui.SetValue("/:isCarPaintable", (r?.I("_paintable", 1) ?? 1) != 0);
                _ui.SetValue("/:isCarStickable", (r?.I("_stickerable", 1) ?? 1) != 0);
                _ui.SetValue("/:isCarTunable", (r?.I("_tuningable", 1) ?? 1) != 0);
                _ui.SetValue("/:IsBike", cat >= 18 && cat <= 24);
                if (string.IsNullOrEmpty(_ui.GetVariable("/:m_Speed"))) _ui.SetVariable("/:m_Speed", Lang(3511, "tdu2interiour.3511"));
                if (string.IsNullOrEmpty(_ui.GetVariable("/:m_Acceleration"))) _ui.SetVariable("/:m_Acceleration", Lang(2814, "tdu2interiour.2814"));
                if (string.IsNullOrEmpty(_ui.GetVariable("/:m_Braking"))) _ui.SetVariable("/:m_Braking", Lang(2813, "tdu2interiour.2813"));
                if (string.IsNullOrEmpty(_ui.GetVariable("/:m_Difficulty"))) _ui.SetVariable("/:m_Difficulty", Lang(6664, "tdu2interiour.6664"));
                _ui.SetVariable("/:current_price", price > 0 ? price.ToString("N0", de) + " $" : "");
            }
            // Vergleich aktiv (Mini-Menü "Vergleichen"): SetDatasCp zeigt das Vergleichsauto daneben und ruft SetDatas selbst auf
            _ui.SetValue("/:IsCompare", _compareCar != null);
            if (_compareCar == null || !_ui.CallClip("/car_datas/datas_base", "SetDatasCp")) _ui.CallClip("/car_datas/datas_base", "SetDatas");
        }
        catch (Exception ex) { Log.Warn("FillCarData: " + ex.Message); }
    }

    /// <summary>Unity FillCarData: Werte, die im Vergleich und für das angesehene Auto gleich gesetzt werden.</summary>
    internal static void SetCommonData(CarPhysics c, XmbNode? r, string vehicleEngine, bool tun, bool stick, bool paint)
    {
        _ui!.SetText("/:cur_type", vehicleEngine);
        _ui.SetText("/:cur_displacement_i", " ");
        _ui.SetText("/:cur_torque_i", c.TorqueNm + " Nm");
        _ui.SetText("/:cur_red_line_i", c.RedLine + " rpm");
        _ui.SetText("/:cur_type_gears", "Dual clutch");
        _ui.SetText("/:cur_nb_gears", c.NbGears + "");
        _ui.SetText("/car_datas/data_full:cur_weight", r?.I("_mass") + "kg");
        _ui.SetText("/car_datas/data_full:cur_bhp_t", " ");
        _ui.SetText("/:cur_tires_dim_r", "285/35/19");
        _ui.SetText("/:cur_tires_dim_f", "285/35/19");
        _ui.SetText("/:cur_brakes_front_i", r?.I("_brakes_dim_front") + "");
        _ui.SetText("/:cur_brakes_rear_i", r?.I("_brakes_dim_rear") + "");
        _ui.ForceVisible("/car_datas/datas_base/limits/forb_tun", tun);
        _ui.ForceVisible("/car_datas/datas_base/limits/forb_stick", stick);
        _ui.ForceVisible("/car_datas/datas_base/limits/forb_paint", paint);
    }

    /// <summary>DBCarPhysicsData._vehiclecategory -> Label von /car_datas/ca_name/group (A1–A7, B1–B5, C1–C4, mA/mB/mC).</summary>
    internal static string GroupLabel(int cat)
    {
        if (cat >= 1 && cat <= 7) return "A" + cat;
        if (cat >= 9 && cat <= 13) return "B" + (cat - 8);
        if (cat >= 14 && cat <= 17) return "C" + (cat - 13);
        if (cat >= 18 && cat <= 19) return "mA" + (cat - 17);
        if (cat >= 20 && cat <= 22) return "mB" + (cat - 19);
        if (cat >= 23 && cat <= 24) return "mC" + (cat - 22);
        return "empty";
    }

    /// <summary>Fahrzeugmenü der Hilfeleiste: Num4/Num6 Tür links/rechts, Num8 Fenster hoch, Num2 Fenster runter.</summary>
    internal static bool CarPad(CarAccessories? acc)
    {
        if (acc == null) return false;
        if (TDU2Controls.KeyPressed(0x64)) { acc.ToggleDoor(true); return true; }
        if (TDU2Controls.KeyPressed(0x66)) { acc.ToggleDoor(false); return true; }
        if (TDU2Controls.KeyPressed(0x68)) { acc.SetWindow(true, false); acc.SetWindow(false, false); return true; }
        if (TDU2Controls.KeyPressed(0x62)) { acc.SetWindow(true, true); acc.SetWindow(false, true); return true; }
        return false;
    }
}
