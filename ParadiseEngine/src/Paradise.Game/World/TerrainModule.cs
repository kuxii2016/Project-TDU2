using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Physics;
using Paradise.Core.Platform;
using Paradise.Core.Render;
using Paradise.Core.World;
using Paradise.Game.Config;
using System.Numerics;

namespace Paradise.Game.World;

/// <summary>
/// World gameplay page: Flight camera + MapStreamer
/// Loading, building, and rendering take place entirely in the Core.
/// </summary>
public sealed class TerrainModule : GameModule
{
    private readonly IRenderer _renderer;
    private readonly WorldModule _world;
    private readonly IGameWindow _window;
    private readonly DebugOverlay? _overlay;
    private LightingConfig _light = new();
    private FrameGlobals _globals = FrameGlobals.Default;
    private MapStreamer? _streamer;
    private Birds? _birds;
    private PhysicsWorld? _physics;
    private string _groundInfo = "";

    /// <summary>Physics world with the collision meshes of the loaded sectors (for vehicles).</summary>
    public PhysicsWorld? Physics => _physics;
    public MapStreamer? Streamer => _streamer;
    /// <summary>Weather/time of day on the island (null = LightingConfig.UseWeather is disabled or not yet loaded) – for the developer menu.</summary>
    public WorldWeather? Weather => _weather;
    /// <summary>Free-Flight Camera (Render Room).</summary>
    public FlyCamera? Camera => _cam;
    /// <summary>Window input (mouse deltas, mouse trapped inside the window).</summary>
    public InputState Input => _window.Input;
    /// <summary>false, solange ein Fahrzeug die Kamera führt (Unity: FlyCamera.enabled).</summary>
    public bool FlyCameraEnabled = true;
    /// <summary>Kamera-Öffnungswinkel/Nahebene (Fahrzeugkamera: camerasingame.xmb).</summary>
    public float FieldOfView = FovDeg, NearClip = Near;
    /// <summary>Fernebene (große Karte: weit über der Insel).</summary>
    public float FarClip = Far;
    /// <summary>Freiflug-Kamera trotz Fahrzeug (Entwicklermenü F1, Unity TDU2FlyCameraTool): Auto bleibt stehen.</summary>
    public bool FreeFly;
    /// <summary>Große Karte (Unity TDU2BigMap: RenderSettings.fog aus, Schatten aus) – Ansicht von oben auf die Insel.</summary>
    public bool MapView;
    /// <summary>
    /// Fester Streaming-Mittelpunkt (Render-Raum) während der großen Karte. Unity: die Karte hat eine eigene Kamera,
    /// der MapStreamer (Viewer) und FloatingOrigin bleiben an der Spielkamera – Sektoren/Kollision um das Auto bleiben geladen,
    /// der Render-Ursprung verschiebt sich nicht.
    /// </summary>
    public Vector3? StreamFocus;
    /// <summary>Hintergrund der Kartenkamera (BigMap.SeaColor, sRGB).</summary>
    public Vector3 MapSeaColor = new(0f, 18f / 255f, 47f / 255f);
    /// <summary>Große Karte: Meter je Bildpunkt für die Straßenbreite (0 = keine Straßen).</summary>
    public float MapMetersPerPixel;
    /// <summary>Karte zeigt das Kartengelände statt der Spielwelt (Unity UseMapTerrain: alle Kacheln fertig).</summary>
    public bool MapTerrainShown => MapView && _streamer != null && _streamer.MapTerrainReady;
    Matrix4x4 _lastViewProj = Matrix4x4.Identity;
    private WorldWeather? _weather;
    private FlyCamera? _cam;
    private double _nextStats;
    private bool _f10Was;
    private List<string> _pick = new();

    private const float Near = 1f, Far = 5000f, FovDeg = 60f;

    public TerrainModule(IRenderer renderer, WorldModule world, IGameWindow window, DebugOverlay? overlay = null)
    {
        _renderer = renderer;
        _world = world;
        _window = window;
        _overlay = overlay;
    }

    public override void Load()
    {
        if (_world.Root == null)
        {
            Log.Warn("TerrainModule: No game folder found.");
            return;
        }
        _light = new LightingConfig(); 
        _light.ApplyAvatars();
        _globals = _light.ToFrameGlobals();
        _physics = new PhysicsWorld();
        if (!BuildIsland("ibiza")) return;
        var s = _streamer!;
        _overlay?.AddSection("Welt", () => _streamer?.DebugLines() ?? Array.Empty<string>());
        if (_weather != null)
            _overlay?.AddSection("Tag/Wetter", () => new[]
            {
                $"Time {(int)WorldWeather.Hour:00}:{(int)(WorldWeather.Hour % 1f * 60f):00}   {WorldWeather.Kind}   Night {WorldWeather.Night:0.00}",
                $"Rain {WorldWeather.Rain:0.00}   Wetness {WorldWeather.GroundWet:0.00}   Lights {_globals.LampCount}   Traffic Lights {TrafficLights.Count}",
            });
        _overlay?.AddSection("Auswahl (F10: Objekt in Blickmitte)", () => _pick);
        _overlay?.AddSection("Physik", () => new[]
        {
            $"Colliders {_physics?.StaticCount ?? 0}   Triangles {(_physics?.TriangleCount ?? 0):N0}   Last build {_streamer?.ColliderBuildMs ?? 0:0.0} ms (background), Add {_streamer?.ColliderAddMs ?? 0:0.0} ms",
            _groundInfo,
        });
        _cam = new FlyCamera(s.StartPosition, 0f, -35f * MathF.PI / 180f);
    }

    /// <summary>MapStreamer (+ Wetter) einer Insel anlegen; die Physikwelt bleibt dieselbe.</summary>
    bool BuildIsland(string island)
    {
        var s = new MapStreamer(_renderer) { Island = island };
        _light.ApplyTo(s);
        s.Physics = _physics;
        if (!s.Initialize(_world.Root!)) { s.Dispose(); return false; }
        if (_light.UseWeather && s.Index != null)
        {
            _weather = new WorldWeather();
            _light.ApplyTo(_weather);
            _weather.Init(s.Index);
        }
        _streamer = s;
        if (s.Index != null)
        {
            var birds = new Birds();
            try
            {
                string birdBank = Path.Combine(s.Index.FxDir, "details", "birds.bnk");
                if (birds.Load(birdBank, s.Textures, _renderer)) _birds = birds;
                else birds.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Failed load Birds: " + ex.Message);
                birds.Dispose();
            }
        }
        return true;
    }

    /// <summary>
    /// Island change, Release old island
    /// (tiles, colliders, weather), build destination island (Bnk/islands/name: ibiza, hawai, casino). Move camera to starting point;
    /// The arrival point is set by Gamemodes/Utils/IslandTravel. If the destination island fails to load, the old one is reloaded.
    /// </summary>
    public bool ChangeIsland(string island)
    {
        if (_world.Root == null || _physics == null || string.IsNullOrEmpty(island)) return false;
        string? previous = _streamer?.Island;
        var oldStreamer = _streamer; var oldWeather = _weather; var oldBirds = _birds;
        _streamer = null; _weather = null; _birds = null;
        oldBirds?.Dispose();
        oldWeather?.Dispose();
        oldStreamer?.Dispose();
        _pick = new List<string>();
        bool ok = BuildIsland(island);
        if (!ok && previous != null) BuildIsland(previous);
        if (_streamer != null && _cam != null) { _cam.Override = null; _cam.Position = _streamer.StartPosition; }
        return ok;
    }

    static readonly ProfilerMarker s_mStreamer = new("MapStreamer.Update", ProfilerCategory.Streaming);
    static readonly ProfilerMarker s_mWeather = new("WorldWeather.Update", ProfilerCategory.Scripts);
    static readonly ProfilerMarker s_mBirds = new("Birds.Tick", ProfilerCategory.Animation);
    static readonly ProfilerMarker s_mRaycast = new("Terrain.RaycastDown (camera)", ProfilerCategory.Physics);
    static readonly ProfilerMarker s_mStreamerRender = new("MapStreamer.Render", ProfilerCategory.Rendering);
    static readonly ProfilerMarker s_mMapTerrain = new("MapStreamer.RenderMapTerrain", ProfilerCategory.Rendering);
    static readonly ProfilerMarker s_mWeatherFx = new("Birds / Rain / Lightning draw", ProfilerCategory.Rendering);

    public override void Update(in GameTime time)
    {
        if (_cam == null || _streamer == null) return;
        if (FlyCameraEnabled) { _cam.Override = null; _cam.Update(time.DeltaTime, _window.Input); FieldOfView = FovDeg; NearClip = Near; }

        var focus = StreamFocus ?? _cam.Position;
        if (_streamer.ShiftRenderOrigin(focus, out var shift))
        {
            _cam.Position -= shift;
            if (StreamFocus.HasValue) StreamFocus = StreamFocus.Value - shift;
            focus -= shift;
        }
        long tp = System.Diagnostics.Stopwatch.GetTimestamp();
        using (s_mStreamer.Auto())
            _streamer.Update(focus, time.DeltaTime);

        if (_weather != null && _weather.Loaded)
        {
            double now = TrafficLights.Clock();
            tp = System.Diagnostics.Stopwatch.GetTimestamp();
            _weather.CameraForward = _cam.Override?.forward ?? _cam.Forward;
            var strm = _streamer;
            _weather.GroundHeight ??= (lx, lz) => strm.TryGetMapGroundHeight(lx, lz, out float lh) ? lh : null;
            using (s_mWeather.Auto())
                _weather.Update(now, time.TotalTime, time.DeltaTime, _streamer.ViewerGame, _streamer.RootOffset, _streamer.FindPlacementInstances);
            var g = _weather.Globals;
            g.ShadowDistance = GraphicsSettings.ShadowDistance > 0f ? GraphicsSettings.ShadowDistance : _light.ShadowDistance;
            g.ShadowStrength *= _light.ShadowStrength > 0f ? 1f : 0f;
            _globals = g;
        }

        if (_birds != null)
        {
            var strm = _streamer;
            using (s_mBirds.Auto())
                _birds.Tick(time.DeltaTime, _globals.RainSpecs.X, 1f - _globals.NightK,
                    (float x, float z, float y, out float h) => strm.TryGetMapGroundHeight(x, z, out h));
        }


        var camGame = _streamer.ViewerGame;
        tp = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ground;
        float gy;
        using (s_mRaycast.Auto())
            ground = _streamer.RaycastDown(camGame, 2f, 3000f, out gy);
        _groundInfo = ground
            ? $"Boden unter Kamera: y {gy:0.00}  (Abstand {camGame.Y - gy:0.0} m)"
            : "Boden unter Kamera: kein Kollider";

        #if DEBUG
        bool f10 = _window.Input.IsKeyDown(0x79);
        if (f10 && !_f10Was) _pick = _streamer.Pick(_cam.Position, _cam.Forward);
        _f10Was = f10;
        _nextStats -= time.DeltaTime;
        #endif
    }

    public override void Render(in GameTime time)
    {
        if (_cam == null || _streamer == null) return;
        var view = _cam.View;
        var proj = PerspectiveReversedZ(FieldOfView * (MathF.PI / 180f), _renderer.AspectRatio, NearClip, FarClip);
        _renderer.SetCamera(view, proj);
        _lastViewProj = view * proj;
        var g = _globals;
        if (MapView)
        {
            g.FogEnd = g.FogStart; g.ShadowStrength = 0f;
            g.NoSky = true; g.BackgroundColor = MapSeaColor;
            if (MapTerrainShown) { using (s_mMapTerrain.Auto()) _streamer.RenderMapTerrain(_cam.Position, g); return; }
        }
        using (s_mStreamerRender.Auto())
            _streamer.Render(_cam.Position, view * proj, g);
        using (s_mWeatherFx.Auto())
        {
            if (!MapView) _birds?.Draw(_renderer, _streamer.RenderToGame(_cam.Position), _streamer.RootOffset, view, 1f - g.NightK);
            if (!MapView) _weather?.DrawRain(_renderer, new Vector3(view.M11, view.M21, view.M31));
            if (!MapView) _weather?.DrawLightning(_renderer, _streamer.RootOffset, _cam.Override?.forward ?? _cam.Forward);
        }
    }

    public override void Unload()
    {
        _birds?.Dispose();
        _birds = null;
        _weather?.Dispose();
        _streamer?.Dispose();
        _streamer = null;
        _physics?.Dispose();
        _physics = null;
    }

    /// <summary>
    /// Strahl durch einen Bildpunkt (Pixel, Ursprung oben links) aus der zuletzt gezeichneten Kamera – Render-Raum
    /// (Unity Camera.ScreenPointToRay).
    /// </summary>
    public bool ScreenRay(float px, float py, out Vector3 origin, out Vector3 dir)
    {
        origin = dir = Vector3.Zero;
        if (!Matrix4x4.Invert(_lastViewProj, out var inv) || _renderer.Width <= 0 || _renderer.Height <= 0) return false;
        float x = px / _renderer.Width * 2f - 1f, y = py / _renderer.Height * 2f - 1f;   // Vulkan: y nach unten
        var a = Vector4.Transform(new Vector4(x, y, 1f, 1f), inv);     // Nahebene (Reversed-Z: 1)
        var b = Vector4.Transform(new Vector4(x, y, 0.5f, 1f), inv);
        if (MathF.Abs(a.W) < 1e-12f || MathF.Abs(b.W) < 1e-12f) return false;
        origin = new Vector3(a.X, a.Y, a.Z) / a.W;
        var p = new Vector3(b.X, b.Y, b.Z) / b.W;
        dir = Vector3.Normalize(p - origin);
        return float.IsFinite(dir.X);
    }

    /// <summary>
    /// Render-Raum -> Bildpunkt (oben links = 0,0), aus der aktuellen Kameralage (Unity Camera.WorldToScreenPoint, y gespiegelt).
    /// false = hinter der Kamera.
    /// </summary>
    public bool WorldToScreen(Vector3 render, out Vector2 px)
    {
        px = default;
        if (_cam == null || _renderer.Width <= 0 || _renderer.Height <= 0) return false;
        var vp = _cam.View * PerspectiveReversedZ(FieldOfView * (MathF.PI / 180f), _renderer.AspectRatio, NearClip, FarClip);
        var c = Vector4.Transform(new Vector4(render, 1f), vp);
        if (c.W <= 1e-6f) return false;
        px = new Vector2((c.X / c.W * 0.5f + 0.5f) * _renderer.Width, (c.Y / c.W * 0.5f + 0.5f) * _renderer.Height);
        return true;
    }

    /// <summary>Vulkan-Projektion (Y nach unten) mit Reversed-Z: near -> 1, far -> 0 (Depth-Clear 0, GreaterOrEqual).</summary>
    private static Matrix4x4 PerspectiveReversedZ(float fovY, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovY * 0.5f);
        return new Matrix4x4(
            f / aspect, 0f, 0f, 0f,
            0f, -f, 0f, 0f,
            0f, 0f, near / (far - near), -1f,
            0f, 0f, far * near / (far - near), 0f);
    }
}