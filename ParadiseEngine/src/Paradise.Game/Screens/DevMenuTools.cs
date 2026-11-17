using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Gamemodes;
using Paradise.Game.World;
using System.Numerics;

namespace Paradise.Game.Screens;

/// <summary>
/// Port von Unity UI/TDU2DebugTools.cs (TDU2DebugStage): gemeinsame Hilfe für die Entwickler-Werkzeuge Cutscene Tester und
/// Animation Viewer. Parkt das Spielerauto (entfernt, Steuerung angehalten, Spielmodus CINE) und stellt es danach an derselben
/// Stelle wieder hin. Zeichnet die graue Bodenplatte der "Himmelsbühne". Bank-Listen (.cin / .anm) werden im Hintergrund gesucht.
/// </summary>
internal static class DebugStage
{
    static int _users;
    static CarPhysics? _car;
    static Vector3 _pos, _fwd = Vector3.UnitZ;
    static VehicleModule? _vehicles;
    static TerrainModule? _terrain;

    public static bool Active => _users > 0;

    public static void Enter(VehicleModule vehicles, TerrainModule terrain)
    {
        if (_users++ > 0) return;
        _vehicles = vehicles; _terrain = terrain;
        var vm = vehicles.Manager;
        _car = vm?.Current != null ? vm.CurrentCar : null;
        if (vm?.Current != null) { _pos = vm.PositionGame; _fwd = Flat(vm.ForwardGame); }
        else if (terrain.Streamer is { } st && terrain.Camera is { } cam) { _pos = st.RenderToGame(cam.Position); _fwd = Flat(cam.Forward); }
        vm?.Despawn();
        vehicles.Suspended = true;
        terrain.FlyCameraEnabled = false;
        GameMode.Push(Gamemode.GAME_MODE_CINE);
    }

    public static void Exit()
    {
        if (_users <= 0) return;
        if (--_users > 0) return;
        var v = _vehicles; var t = _terrain;
        _vehicles = null; _terrain = null;
        GameMode.Pop();
        if (t?.Camera != null) { t.Camera.Override = null; t.NearClip = 1f; t.FieldOfView = 60f; }
        if (v == null) return;
        v.Suspended = false;
        if (t != null) t.FlyCameraEnabled = true;
        var vm = v.Manager;
        if (_car != null && vm != null)
        {
            var p = _pos;
            if (t?.Streamer != null && t.Streamer.TryGetGroundHeight(p, out float gy)) p.Y = gy + 0.6f;
            if (t?.Camera != null && t.Streamer != null) t.Camera.Position = t.Streamer.GameToRender(p - _fwd * 7f + Vector3.UnitY * 2.5f);
            vm.SpawnCar(_car.FileName, p, _fwd);
        }
        _car = null;
    }

    static Vector3 Flat(Vector3 v) { v.Y = 0f; return v.LengthSquared() < 1e-4f ? Vector3.UnitZ : Vector3.Normalize(v); }

    // ------------------------------------------------------------------ Bodenplatte (Unity Floor: graue Ebene)
    static IGpuMaterial? _floorMat;
    static IRenderer? _floorOwner;

    /// <summary>Graue quadratische Platte (Spielraum, Mitte und Drehung aus <paramref name="frame"/>), Seitenlänge <paramref name="size"/>.</summary>
    public static void DrawFloor(IRenderer r, Matrix4x4 frame, Matrix4x4 gameToRender, float size)
    {
        if (_floorMat == null || !ReferenceEquals(_floorOwner, r))
        {
            _floorMat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Particle, Name = "Debug Floor" });
            _floorOwner = r;
        }
        float h = size * 0.5f;
        var m = Matrix4x4.CreateTranslation(0f, -0.01f, 0f) * frame * gameToRender;
        uint col = ObjectVertex.PackColor(107, 110, 115, 255);   // Unity (0,42; 0,43; 0,45)
        Span<ObjectVertex> v = stackalloc ObjectVertex[4];
        v[0] = new ObjectVertex { Position = Vector3.Transform(new Vector3(-h, 0f, -h), m), Normal = Vector3.UnitY, Color = col };
        v[1] = new ObjectVertex { Position = Vector3.Transform(new Vector3(h, 0f, -h), m), Normal = Vector3.UnitY, Color = col };
        v[2] = new ObjectVertex { Position = Vector3.Transform(new Vector3(h, 0f, h), m), Normal = Vector3.UnitY, Color = col };
        v[3] = new ObjectVertex { Position = Vector3.Transform(new Vector3(-h, 0f, h), m), Normal = Vector3.UnitY, Color = col };
        Span<uint> i = stackalloc uint[6] { 0, 1, 2, 0, 2, 3 };
        r.DrawDynamic(v, i, _floorMat, Matrix4x4.Identity);
    }

    // ------------------------------------------------------------------ Bank-Listen (zwischengespeichert, im Hintergrund gesucht)
    static List<string>? _cineBanks, _animBanks;
    static Task? _scan;
    static string? _scanIsland;
    public static string ScanState { get; private set; } = "";

    /// <summary>Banken mit Kinos (.cin): scenarics/**, Exterior-Banken der Insel, interior/. null = Suche läuft.</summary>
    public static List<string>? CinematicBanks(string? bnkDir, MapStreamer? st) { StartScan(bnkDir, st); return _scan != null && _scan.IsCompleted ? _cineBanks : null; }

    /// <summary>Banken mit Avatar-Animationen (.anm). null = Suche läuft.</summary>
    public static List<string>? AnimationBanks(string? bnkDir, MapStreamer? st) { StartScan(bnkDir, st); return _scan != null && _scan.IsCompleted ? _animBanks : null; }

    public static void Rescan() { _scan = null; }

    static void StartScan(string? bnkDir, MapStreamer? st)
    {
        string island = st?.Index?.IslandName ?? "";
        if (_scan != null && _scanIsland == island) return;
        if (string.IsNullOrEmpty(bnkDir)) return;
        _scanIsland = island;
        string bnk = bnkDir; string? ext = st?.Index?.ExteriorDir;
        ScanState = "scanning …";
        _scan = Task.Run(() =>
        {
            var cine = new List<string>(); var anim = new List<string>();
            var files = new List<string>();
            try { var sc = Path.Combine(bnk, "scenarics"); if (GameFiles.DirectoryExists(sc)) files.AddRange(GameFiles.GetFiles(sc, "*.bnk", SearchOption.AllDirectories)); } catch (Exception) { }
            try { if (ext != null && GameFiles.DirectoryExists(ext)) files.AddRange(GameFiles.GetFiles(ext, "*.bnk")); } catch (Exception) { }
            try { var it = Path.Combine(bnk, "interior"); if (GameFiles.DirectoryExists(it)) files.AddRange(GameFiles.GetFiles(it, "*.bnk")); } catch (Exception) { }
            int n = 0;
            foreach (var f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.Contains("__") || name.EndsWith("_low", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var b = BnkArchive.Open(f);
                    bool hasCin = false, hasAnm = false;
                    foreach (var e in b.Entries)
                    {
                        if (e.Extension == ".cin") hasCin = true;
                        else if (e.Extension == ".anm") hasAnm = true;
                        if (hasCin && hasAnm) break;
                    }
                    if (hasCin) cine.Add(f);
                    if (hasAnm) anim.Add(f);
                }
                catch (Exception) { }
                n++;
            }
            Comparison<string> byName = (a, b2) => string.Compare(Rel(bnk, a), Rel(bnk, b2), StringComparison.OrdinalIgnoreCase);
            cine.Sort(byName); anim.Sort(byName);
            _cineBanks = cine; _animBanks = anim;
            ScanState = $"{cine.Count} cinematic banks, {anim.Count} animation banks ({n} scanned)";
        });
    }

    public static string Rel(string? bnkDir, string? path)
    {
        if (string.IsNullOrEmpty(bnkDir) || path == null) return path ?? "";
        string p = path.Replace('\\', '/'), b = bnkDir.Replace('\\', '/').TrimEnd('/') + "/";
        return p.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? p[b.Length..] : p;
    }
}

// =====================================================================================================================
/// <summary>
/// Port von Unity TDU2CutsceneTester (Entwicklermenü): lädt eine beliebige Bank mit Kinos (<see cref="SpotCinematics"/>) und spielt
/// ihre Entity__Cinematic-Einträge ab – auf einer Himmelsbühne (Bodenplatte 1500 m über der Insel), hier vor der Kamera oder am
/// Spot der Bank (Exterior-Banken: nächster Spot mit diesem Code; Story-Banken: ihr bekannter Story-Spot). Spieler-Avatar (eigenes
/// Aussehen) und das aktuelle Auto spielen "Avatar" / PLAYER_CAR. Die Bank-Szene wird gebaut (wie Unity TDU2StoryScene.Build);
/// am Spot eines Exteriors bleibt die gestreamte Kopie und bewegte Requisiten kommen über AttachProps (wie die Waschanlage).
/// </summary>
internal sealed class CutsceneTester
{
    public enum Stage { Sky, Here, Spot }
    static readonly string[] StageNames = { "Sky stage (1500 m up)", "Here (in front of the camera)", "At the bank's spot" };
    public static string StageName(Stage s) => StageNames[(int)s];

    /// <summary>Story-Banken und ihr Spot (Ibiza), wie Unity KnownSpots: (Spot, geschlossener Raum, Höhe).</summary>
    static readonly Dictionary<string, (string spot, bool closed, float alt)> KnownSpots = new(StringComparer.OrdinalIgnoreCase)
    {
        { "new_intro", ("EPH_L3DI_5067I", false, 0f) },
        { "garage", ("EPH_L3DI_5067I", true, 1500f) },
        { "meeting_tess", ("E_HOTELA_1266I", false, 0f) },
        { "solar_crown_ext", ("ECH_IA_1243I", false, 0f) },
        { "solar_crown_int", ("ECH_IA_1243I", true, 0f) },
        { "pack_events13", ("ECH_IA_1243I", false, 0f) },
    };

    static CutsceneTester? _instance;
    public static CutsceneTester Get(IRenderer r, TerrainModule t, VehicleModule v) => _instance ??= new CutsceneTester(r, t, v);
    public static CutsceneTester? Instance => _instance;

    readonly IRenderer _r; readonly TerrainModule _t; readonly VehicleModule _v;
    CutsceneTester(IRenderer r, TerrainModule t, VehicleModule v) { _r = r; _t = t; _v = v; }

    public bool AutoPlay = true, ShowFloor = true, CastAvatar = true, CastCar = true;
    public Stage Where = Stage.Sky;
    /// <summary>Freie Ansicht ohne Kino-Kamera: Umlauf um die Bühne.</summary>
    public float ViewAngle = 35f, ViewDistance = 14f, ViewHeight = 5f, ViewSpeed = 8f;
    /// <summary>Zeitfaktor der Kinos (Unity Time.timeScale).</summary>
    public float TimeScale = 1f;

    public SpotCinematics? Scene { get; private set; }
    public bool Loaded => Scene != null && _sceneTask == null && _modelTask == null;
    public string? BankPath { get; private set; }
    public string Status { get; private set; } = "idle";
    public bool Active => Scene != null || _sceneTask != null;

    Task<SpotCinematics?>? _sceneTask;
    Task<BankModel>? _modelTask;
    MapStreamer.SceneHandle? _built;
    Matrix4x4 _frame = Matrix4x4.Identity;
    bool _staged, _cast, _skyFloor, _props;
    string? _spotCode;
    string? _pending; bool _pendingBg;

    public void Load(string bankPath)
    {
        Unload(false);
        var vm = _v.Manager; var st = _t.Streamer;
        if (vm == null || st == null || string.IsNullOrEmpty(bankPath)) return;
        if (!_staged) { DebugStage.Enter(_v, _t); _staged = true; }
        BankPath = bankPath;
        string stem = Path.GetFileNameWithoutExtension(bankPath);
        var where = Where; bool fellBack = false;
        Vector3 pos = Vector3.Zero; float heading = 0f;
        bool build = true; _spotCode = null; _props = false;
        if (where == Stage.Spot)
        {
            bool exterior = st.Index?.ExteriorDir != null && bankPath.Replace('\\', '/').Contains("/exterior/", StringComparison.OrdinalIgnoreCase);
            var spot = exterior ? NearestSpot(stem) : null;
            if (spot != null)
            {
                // wie die Waschanlage: gestreamtes Exterior bleibt, bewegte Requisiten als eigene Szene
                pos = spot.Position; heading = spot.Value1; build = false; _props = true; _spotCode = spot.Code;
            }
            else if (KnownSpots.TryGetValue(stem, out var k) && FindSpot(k.spot) is { } ks)
            {
                pos = ks.Position; heading = ks.Value1;
                if (k.closed) pos += Vector3.UnitY * k.alt;
            }
            else { where = Stage.Sky; fellBack = true; }
        }
        if (where == Stage.Here && _t.Camera is { } cam)
        {
            var f = cam.Override?.forward ?? cam.Forward; f.Y = 0f;
            f = f.LengthSquared() < 1e-4f ? Vector3.UnitZ : Vector3.Normalize(f);
            var p = st.RenderToGame(cam.Position) + f * 8f;
            if (st.TryGetGroundHeight(p, out float gy)) p.Y = gy;
            pos = p; heading = MathF.Atan2(f.X, f.Z);
        }
        if (where == Stage.Sky) { pos = new Vector3(0f, 1500f, 0f); heading = 0f; }
        _frame = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, heading)) * Matrix4x4.CreateTranslation(pos);
        _skyFloor = where == Stage.Sky;
        _cast = false;
        var r = _r;
        _sceneTask = Task.Run(() => SpotCinematics.Open(r, bankPath));
        if (build) _modelTask = Task.Run(() => st.LoadSceneModel(bankPath));
        Status = "loading " + stem + " (" + StageName(where) + (fellBack ? " – no spot for this bank on this island" : "") + ")";
    }

    Spot? FindSpot(string code)
    {
        var l = _t.Streamer?.Spots?.Spots;
        return l?.Find(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    Spot? NearestSpot(string code)
    {
        var st = _t.Streamer;
        if (st?.Spots == null || _t.Camera == null) return null;
        var g = st.RenderToGame(_t.Camera.Position);
        Spot? best = null; float bd = float.MaxValue;
        foreach (var s in st.Spots.Spots)
        {
            if (!string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)) continue;
            float d = (s.Position - g).LengthSquared();
            if (d < bd) { bd = d; best = s; }
        }
        return best;
    }

    public void Play(string name, bool background)
    {
        if (!Active) return;
        if (!Loaded) { _pending = name; _pendingBg = background; Status = "queued " + name; return; }
        bool ok = background ? Scene!.PlayBackground(name, false) : Scene!.PlayMain(name);
        Status = ok ? (background ? "background " : "playing ") + name : name + " failed";
    }

    public void StopAll() { if (Scene == null) return; Scene.StopMain(); Scene.StopBackground(); Status = "stopped"; }
    public void Skip() => Scene?.SkipMain();

    /// <summary>Jedes Bild (Entwicklermenü Update): Laden abschließen, Kinos weiter, Kamera.</summary>
    public void Update(float dt)
    {
        if (!Active) return;
        var st = _t.Streamer;
        if (_sceneTask != null && _sceneTask.IsCompleted)
        {
            var t = _sceneTask; _sceneTask = null;
            Scene = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (Scene == null) { Status = "failed: " + (t.Exception?.GetBaseException().Message ?? "bank not readable"); Unload(false); return; }
            Scene.Frame = _frame;
            Scene.Indoor = _skyFloor;
            Scene.CastPlayer = CastAvatar;
            if (CastCar) Scene.LoadCar(_v.Manager, CarForScene());
            if (_props && st != null && BankPath != null) Scene.AttachProps(st, BankPath, _spotCode);
        }
        if (_modelTask != null && _modelTask.IsCompleted)
        {
            var t = _modelTask; _modelTask = null;
            if (t.Status == TaskStatus.RanToCompletion && t.Result != null && st != null)
            {
                try { _built = st.BuildScene(t.Result, _frame, true); st.Scenes.Add(_built); }
                catch (Exception ex) { Log.Warn("Cutscene Tester: Szene – " + ex.Message); t.Result.Release(); }
            }
            else Log.Warn("Cutscene Tester: Szene nicht geladen – " + t.Exception?.GetBaseException().Message);
        }
        if (Scene == null) return;
        if (Loaded && !_cast)
        {
            _cast = true;
            if (Status.StartsWith("loading", StringComparison.Ordinal)) Status = "ready – " + Scene.Definitions.Count + " cinematics (Enter on a name = play)";
            if (AutoPlay && _pending == null)
                foreach (var d in Scene.Definitions.Values)
                    if (d.Cin.Camera(d.Scenes!, 0f, out _, out _, out _)) { _pending = d.Name; _pendingBg = false; break; }
        }
        if (Loaded && _pending != null) { var n = _pending; _pending = null; Play(n, _pendingBg); }
        Scene.Update(dt * System.Math.Max(0f, TimeScale));
        if (Scene.MainName is { } mn && Status.StartsWith("playing", StringComparison.Ordinal) && Scene.Find(mn) is { } md)
            Status = $"playing {mn}  {Scene.MainTime:0.0} / {md.Cin.Duration:0.0} s";
        foreach (var e in Scene.TakeEndEvents()) Status = "ended " + e;
        ApplyCamera(dt);
    }

    CarPhysics? CarForScene()
    {
        var vm = _v.Manager;
        if (vm == null) return null;
        CarPhysics? car = null;
        var save = Paradise.Core.Online.TDU2Session.Save;
        if (save != null && !string.IsNullOrEmpty(save.currentCar)) car = vm.FindCar(save.currentCar);
        return car ?? vm.FindCar(vm.StartCar) ?? (vm.Available.Count > 0 ? vm.Available[0] : null);
    }

    /// <summary>Kino-Kamera; ohne laufende Kino-Kamera langsamer Umlauf um die Bühne (Unity LateUpdate).</summary>
    void ApplyCamera(float dt)
    {
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam == null || st == null || Scene == null) return;
        _t.FlyCameraEnabled = false;
        if (Scene.Camera(out var eye, out var fwd, out var up, out float fov))
        {
            cam.Position = st.GameToRender(eye);
            cam.Override = (fwd, up);
            _t.FieldOfView = fov;
            _t.NearClip = 0.05f;
            return;
        }
        ViewAngle += ViewSpeed * dt;
        var c = Vector3.Transform(Vector3.UnitY * 1.2f, _frame);
        var frameFwd = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, _frame));
        var dir = Vector3.Transform(frameFwd, Quaternion.CreateFromAxisAngle(Vector3.UnitY, ViewAngle * MathF.PI / 180f));
        var p = c + dir * ViewDistance + Vector3.UnitY * ViewHeight;
        cam.Position = st.GameToRender(p);
        var f = Vector3.Normalize(c - p);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, f));
        cam.Override = (f, Vector3.Normalize(Vector3.Cross(f, right)));
        _t.FieldOfView = 60f;
        _t.NearClip = 0.05f;
    }

    /// <summary>Darsteller und Bodenplatte zeichnen (Entwicklermenü Render).</summary>
    public void Render()
    {
        var st = _t.Streamer;
        if (st == null || !Active) return;
        if (_skyFloor && ShowFloor) DebugStage.DrawFloor(_r, _frame, st.RootMatrix, 600f);
        Scene?.Render(st.RootMatrix);
    }

    /// <summary>Szene schließen (restore = Auto zurück, sonst bleibt die Bühne für die nächste Bank).</summary>
    public void Unload(bool restore = true)
    {
        Scene?.StopMain(); Scene?.StopBackground();
        Scene?.Dispose(); Scene = null;
        if (_sceneTask != null) { var t = _sceneTask; _sceneTask = null; t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result?.Dispose(); }); }
        if (_modelTask != null) { var t = _modelTask; _modelTask = null; t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result?.Release(); }); }
        if (_built != null) { _t.Streamer?.DestroyScene(_built); _built = null; }
        BankPath = null; _pending = null;
        if (restore && _staged) { _staged = false; DebugStage.Exit(); Status = "idle"; }
    }
}

// =====================================================================================================================
/// <summary>
/// Port von Unity TDU2AnimViewer (Entwicklermenü): ein Avatar (Spieler-Aussehen oder eine DBPNJ-Figur) auf einer Bodenplatte hoch
/// über der Insel, Kamera kreist um ihn, spielt jeden .anm-Clip aus avatarsdriver.bnk oder einer gewählten Bank (Schleife,
/// Tempo, Pause, Zeit schieben).
/// </summary>
internal sealed class AnimViewer
{
    static AnimViewer? _instance;
    public static AnimViewer Get(IRenderer r, TerrainModule t, VehicleModule v) => _instance ??= new AnimViewer(r, t, v);
    public static AnimViewer? Instance => _instance;

    readonly IRenderer _r; readonly TerrainModule _t; readonly VehicleModule _v;
    AnimViewer(IRenderer r, TerrainModule t, VehicleModule v) { _r = r; _t = t; _v = v; }

    public string Model { get; private set; } = "";      // "" = Spieler-Avatar, sonst DBPNJ-Konfiguration
    public string ClipBank { get; private set; } = "";   // "" = avatarsdriver.bnk
    public string ClipName { get; private set; } = "";
    public AnmClip? Clip { get; private set; }
    public float Time_, Speed = 1f, Orbit = 30f, Distance = 3.2f, Height = 1.1f, OrbitSpeed = 0f;
    public bool Paused, Loop = true, AutoPlay = true, ShowFloor = true;
    public bool Active { get; private set; }

    static readonly Vector3 StagePos = new(0f, 1600f, 0f);
    AvatarModel? _avatar;
    BnkArchive? _bank;
    List<string>? _bankClips;

    public void Open()
    {
        if (Active || _t.Streamer == null) return;
        DebugStage.Enter(_v, _t);
        Active = true;
        SetModel(Model);
    }

    public void Close()
    {
        if (!Active) return;
        _avatar?.Dispose(); _avatar = null;
        Active = false;
        DebugStage.Exit();
    }

    public void SetModel(string? pnjId)
    {
        Model = pnjId ?? "";
        if (!Active) return;
        _avatar?.Dispose(); _avatar = null;
        var cfg = string.IsNullOrEmpty(Model) ? AvatarConfigs.Local : AvatarLibrary.PnjConfig(Model);
        _avatar = AvatarModel.Create(_r, cfg ?? AvatarConfig.Default());
    }

    /// <summary>Clip-Quelle: "" = avatarsdriver.bnk, sonst Pfad einer Bank.</summary>
    public void SetBank(string? bankPath)
    {
        ClipBank = bankPath ?? "";
        _bank = null; _bankClips = null;
        if (string.IsNullOrEmpty(ClipBank)) return;
        try { _bank = BnkArchive.Open(ClipBank); } catch (Exception ex) { Log.Warn("Animation Viewer: " + ex.Message); }
    }

    public List<string> Clips()
    {
        if (string.IsNullOrEmpty(ClipBank)) return AvatarLibrary.ClipNames();
        if (_bankClips != null) return _bankClips;
        _bankClips = new List<string>();
        if (_bank != null) foreach (var e in _bank.All(".anm")) _bankClips.Add(Path.GetFileNameWithoutExtension(e.Name));
        _bankClips.Sort(StringComparer.OrdinalIgnoreCase);
        return _bankClips;
    }

    public void SetClip(string? name)
    {
        ClipName = name ?? "";
        Clip = string.IsNullOrEmpty(ClipName) ? null : string.IsNullOrEmpty(ClipBank) ? AvatarLibrary.Clip(ClipName) : CineScene.LoadClip(_bank, ClipName);
        Time_ = 0f; Paused = !AutoPlay;
    }

    public void Step(int dir)
    {
        var l = Clips();
        if (l.Count == 0) return;
        int i = l.IndexOf(ClipName);
        i = ((i + dir) % l.Count + l.Count) % l.Count;
        SetClip(l[i]);
    }

    /// <summary>Jedes Bild: Zeit und Kamera (Unity LateUpdate).</summary>
    public void Update(float dt)
    {
        if (!Active) return;
        if (Clip != null && !Paused)
        {
            Time_ += dt * Speed;
            float len = MathF.Max(0.01f, Clip.Length);
            if (Time_ > len) Time_ = Loop ? Time_ % len : len;
            if (Time_ < 0f) Time_ = Loop ? len + Time_ % len : 0f;
        }
        _avatar?.Update();
        Orbit += OrbitSpeed * dt;
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam == null || st == null) return;
        _t.FlyCameraEnabled = false;
        var c = StagePos + Vector3.UnitY * Height;
        var dir = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Orbit * MathF.PI / 180f));
        cam.Position = st.GameToRender(c + dir * Distance + Vector3.UnitY * 0.25f);
        var f = Vector3.Normalize(-dir - Vector3.UnitY * 0.08f);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, f));
        cam.Override = (f, Vector3.Normalize(Vector3.Cross(f, right)));
        _t.FieldOfView = 50f;
        _t.NearClip = 0.05f;
    }

    public void Render()
    {
        var st = _t.Streamer;
        if (!Active || st == null) return;
        var frame = Matrix4x4.CreateTranslation(StagePos);
        if (ShowFloor) DebugStage.DrawFloor(_r, frame, st.RootMatrix, 60f);
        var a = _avatar;
        if (a == null || !a.Ready || a.Rig == null) return;
        a.Rig.BeginPose();
        if (Clip != null) a.Rig.ApplyClip(Clip, Time_, 1f, true);
        a.Draw(frame * st.RootMatrix);
    }
}
