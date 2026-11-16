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
/// Gameplay-Seite der Welt: Flugkamera + MapStreamer (Paradise.Core, Port von TDU2MapStreamer).
/// Laden/Bauen/Zeichnen passiert vollständig im Core.
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
    private PhysicsWorld? _physics;
    private string _groundInfo = "";

    /// <summary>Physikwelt mit den Kollisionsnetzen der geladenen Sektoren (für Fahrzeuge).</summary>
    public PhysicsWorld? Physics => _physics;
    public MapStreamer? Streamer => _streamer;
    /// <summary>Wetter/Tageszeit der Insel (null = LightingConfig.UseWeather aus oder noch nicht geladen) – für das Entwicklermenü.</summary>
    public WorldWeather? Weather => _weather;
    /// <summary>Freiflug-Kamera (Render-Raum).</summary>
    public FlyCamera? Camera => _cam;
    /// <summary>Eingabe des Fensters (Maus-Deltas, gefangene Maus im Interieur).</summary>
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
    /// <summary>Hintergrund der Kartenkamera (Unity TDU2BigMap.SeaColor, sRGB).</summary>
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

    // wie im Unity-Projekt (Camera: far 60000, near 1)
    private const float Near = 1f, Far = 60000f, FovDeg = 60f;

    public TerrainModule(IRenderer renderer, WorldModule world, IGameWindow window, DebugOverlay? overlay = null)
    {
        _renderer = renderer;
        _world = world;
        _window = window;
        _overlay = overlay;
    }

    public override void Load()
    {
        if (_world.Root == null) { Log.Warn("TerrainModule: kein Spielordner gefunden."); return; }
        _light = new LightingConfig();   // Werte: Game/Config/LightingConfig.cs
        _globals = _light.ToFrameGlobals();
        // Kollision der Welt (Unity: GenerateColliders) – vor Initialize, damit schon die ersten Sektoren eingetragen werden
        _physics = new PhysicsWorld();
        if (!BuildIsland("ibiza")) return;
        var s = _streamer!;
        // Abschnitte lesen immer den aktuellen Streamer (Inselwechsel baut ihn neu)
        _overlay?.AddSection("Welt", () => _streamer?.DebugLines() ?? Array.Empty<string>());
        if (_weather != null)
            _overlay?.AddSection("Tag/Wetter", () => new[]
            {
                $"Uhr {(int)WorldWeather.Hour:00}:{(int)(WorldWeather.Hour % 1f * 60f):00}   {WorldWeather.Kind}   Nacht {WorldWeather.Night:0.00}",
                $"Regen {WorldWeather.Rain:0.00}   Nässe {WorldWeather.GroundWet:0.00}   Lampen {_globals.LampCount}   Ampeln {TrafficLights.Count}",
            });
        _overlay?.AddSection("Auswahl (F10: Objekt in Blickmitte)", () => _pick);
        _overlay?.AddSection("Physik", () => new[]
        {
            $"Kollider {_physics?.StaticCount ?? 0}   Dreiecke {(_physics?.TriangleCount ?? 0):N0}   letzter Bau {_streamer?.ColliderBuildMs ?? 0:0.0} ms (Hintergrund), Eintragen {_streamer?.ColliderAddMs ?? 0:0.0} ms",
            _groundInfo,
        });
        // Speicher-Diagnose ([DIAG Mem] alle 30 s): Welt, Physik, GPU
        MemoryLog.Register("Welt", () => _streamer?.MemoryInfo() ?? "-");
        MemoryLog.Register("Physik", () =>
        {
            var (main, bg) = _physics?.PoolBytes() ?? (0UL, 0UL);
            return $"Kollider {_physics?.StaticCount ?? 0}, Dreiecke {(_physics?.TriangleCount ?? 0):N0}, Bepu-Pool {MemoryLog.Mb(main)} + Hintergrund {MemoryLog.Mb(bg)}";
        });
        MemoryLog.Register("GPU", () =>
        {
            var rs = _renderer.Stats;
            return $"Allokation {MemoryLog.Mb(rs.GpuMemoryAllocated)}, Texturen {rs.Textures}, Materialien {rs.Materials}, Meshes {rs.Meshes}";
        });
        // Unity: Viewer.rotation = Quaternion.Euler(35, 0, 0) -> 35° nach unten, Blick +Z
        _cam = new FlyCamera(s.StartPosition, 0f, -35f * MathF.PI / 180f);
        Log.Info($"TerrainModule: Start bei {s.StartPosition} (Spiel {s.RenderToGame(s.StartPosition)})");
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
        return true;
    }

    /// <summary>
    /// Inselwechsel (Unity TDU2IslandTravel: Szene neu laden mit TDU2MapStreamer.IslandOverride): alte Insel freigeben
    /// (Kacheln, Kollider, Wetter), Zielinsel (Bnk/islands/&lt;name&gt;: ibiza, hawai, casino) aufbauen. Kamera an den Startpunkt;
    /// die Ankunftsstelle setzt Gamemodes/Utils/IslandTravel. Schlägt die Zielinsel fehl, wird die alte wieder geladen.
    /// </summary>
    public bool ChangeIsland(string island)
    {
        if (_world.Root == null || _physics == null || string.IsNullOrEmpty(island)) return false;
        string? previous = _streamer?.Island;
        var oldStreamer = _streamer; var oldWeather = _weather;
        _streamer = null; _weather = null;
        oldWeather?.Dispose();
        oldStreamer?.Dispose();   // entlädt alle Kacheln und trägt ihre Kollider aus der Physikwelt aus
        _pick = new List<string>();
        bool ok = BuildIsland(island);
        if (!ok && previous != null) BuildIsland(previous);
        if (_streamer != null && _cam != null) { _cam.Override = null; _cam.Position = _streamer.StartPosition; }
        Log.Info(ok ? "TerrainModule: Insel " + island + " geladen" : "TerrainModule: Insel " + island + " nicht ladbar");
        return ok;
    }

    public override void Update(in GameTime time)
    {
        if (_cam == null || _streamer == null) return;
        if (FlyCameraEnabled) { _cam.Override = null; _cam.Update(time.DeltaTime, _window.Input); FieldOfView = FovDeg; NearClip = Near; }
        // Floating Origin (Unity: FloatingOrigin): Render-Ursprung zur Kamera, sonst wackelt bei großen Koordinaten alles
        var focus = StreamFocus ?? _cam.Position;
        if (_streamer.ShiftRenderOrigin(focus, out var shift))
        {
            _cam.Position -= shift;
            if (StreamFocus.HasValue) StreamFocus = StreamFocus.Value - shift;
            focus -= shift;
        }
        long tp = System.Diagnostics.Stopwatch.GetTimestamp();
        _streamer.Update(focus, time.DeltaTime);
        PerfLog.Add("Terrain: Streamer gesamt", System.Diagnostics.Stopwatch.GetElapsedTime(tp).TotalMilliseconds);
        MemoryLog.Update(time.TotalTime);
        PerfLog.Frame(time.TotalTime, time.DeltaTime);

        // Tag/Nacht + Wetter (Serverzeit -> für alle gleich)
        if (_weather != null && _weather.Loaded)
        {
            double now = TrafficLights.Clock();
            tp = System.Diagnostics.Stopwatch.GetTimestamp();
            // Blitz (World/Lightning): Ort vor der Kamera in waagrechter Blickrichtung, Fußpunkt auf dem Boden
            _weather.CameraForward = _cam.Override?.forward ?? _cam.Forward;
            var strm = _streamer;
            _weather.GroundHeight ??= (lx, lz) => strm.TryGetMapGroundHeight(lx, lz, out float lh) ? lh : null;
            _weather.Update(now, time.TotalTime, time.DeltaTime, _streamer.ViewerGame, _streamer.RootOffset, _streamer.FindPlacementInstances);
            PerfLog.Add("Terrain: Wetter/Lampen", System.Diagnostics.Stopwatch.GetElapsedTime(tp).TotalMilliseconds);
            var g = _weather.Globals;
            g.ShadowDistance = GraphicsSettings.ShadowDistance > 0f ? GraphicsSettings.ShadowDistance : _light.ShadowDistance;   // Effektqualität (Optionen)
            g.ShadowStrength *= _light.ShadowStrength > 0f ? 1f : 0f;
            _globals = g;
        }

        // Physik-Test: Boden unter der Kamera per Raycast (wie die Radkontakte der Fahrzeuge)
        var camGame = _streamer.ViewerGame;
        tp = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ground = _streamer.RaycastDown(camGame, 2f, 3000f, out float gy);
        PerfLog.Add("Terrain: Boden-Strahl (Debug-Anzeige)", System.Diagnostics.Stopwatch.GetElapsedTime(tp).TotalMilliseconds);
        _groundInfo = ground
            ? $"Boden unter Kamera: y {gy:0.00}  (Abstand {camGame.Y - gy:0.0} m)"
            : "Boden unter Kamera: kein Kollider";

        // F10: Objekt in der Bildmitte mit Materialdaten anzeigen/loggen (Debug)
        bool f10 = _window.Input.IsKeyDown(0x79);
        if (f10 && !_f10Was) _pick = _streamer.Pick(_cam.Position, _cam.Forward);
        _f10Was = f10;


        _nextStats -= time.DeltaTime;
        if (_nextStats <= 0)
        {
            _nextStats = 5;
            Log.Debug($"Welt: Areas {_streamer.LoadedAreas}, Sektoren {_streamer.LoadedSectors}, Objekte gezeichnet {_streamer.DrawnObjects}, Spiel {_streamer.ViewerGame}");
        }
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
            // große Karte (Unity TDU2BigMap): kein Nebel, keine Schatten, Kamera SolidColor (Meer) statt Skybox
            g.FogEnd = g.FogStart; g.ShadowStrength = 0f;
            g.NoSky = true; g.BackgroundColor = MapSeaColor;
            if (MapTerrainShown) { _streamer.RenderMapTerrain(_cam.Position, g); return; }   // nur die Kartenkacheln
        }
        _streamer.Render(_cam.Position, view * proj, g);
        if (!MapView) _weather?.DrawRain(_renderer, new Vector3(view.M11, view.M21, view.M31));
        if (!MapView) _weather?.DrawLightning(_renderer, _streamer.RootOffset, _cam.Override?.forward ?? _cam.Forward);
    }

    public override void Unload()
    {
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
