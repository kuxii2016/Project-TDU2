using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Flugzeuge der Insel - C#-Port von game/freeride/aircraft.{h,cpp} (siehe re/aircraft_birds_spec.md).
/// Gelesen werden islands/&lt;island&gt;/scenarics/packs/world_aircrafts.bnk (Entity__AircraftPath ->
/// Entity__Volume) und aircrafts.bnk (Entity__Actor__Aircraft -> ObjectInCommonRef). Die Geometrie liegt als
/// Library-Node in commonworld.bnk/library.3dd und wird von allen Flugzeugen geteilt, es werden keine
/// GPU-Ressourcen pro Flugzeug angelegt.
///
/// Alle Positionen/Matrizen bleiben im Spielkoordinatensystem. Die Render-Matrix wird erst beim Zeichnen
/// mit MapStreamer.RootMatrix multipliziert - deshalb funktioniert der Port auch mit Floating Origin /
/// World Shift ohne weitere Korrektur.
/// </summary>
public sealed class AircraftSystem
{
    /// <summary>Terrainabfrage in Spielkoordinaten (wie C++ CinematicCar::SurfaceFunction).</summary>
    public delegate bool SurfaceQuery(float x, float z, out float y);

    const float Pi = MathF.PI;
    const int Subdivisions = 10;          // Samples je Punktpaar
    const float GroundTolerance = 60f;    // Punkt ist "am Boden" wenn die Daten Type 0 sagen und das Terrain passt
    const int MaxPlanesPerRoute = 3;
    const float Accel = 6f, Brake = 8f;   // m/s^2 statt Speed-Sprung
    const float MaxBank = 32f * Pi / 180f;
    const float MaxPitch = 18f * Pi / 180f;
    const float LookAhead = 45f;          // Tangente voraus, damit vor der Kurve gedreht wird
    public const float ViewRadius = 9000f;

    public sealed class RouteDef
    {
        public string Name = "";
        public int TypeId;
        public float GroundSpeed = 15f, AirSpeed = 80f;
        public float MinSpacing = 3000f, MaxSpacing = 5000f;
        public readonly List<Vector3> Points = new();
        public readonly List<float> Distances = new();
        public readonly List<byte> Ground = new();
        public float Length;
        public int RunwayStart = -1;
        public int NumGroundPoints;
        public readonly List<float> SpawnDistances = new();
    }

    public sealed class ModelDef
    {
        public int TypeId = -1;
        public ulong Key;
        public string Name = "";
        public LibraryObject? Object;
        public Vector3 BoundsMin, BoundsMax, Centre;
        public float Length;
        public bool Ready => Object != null;
    }

    public sealed class PlaneState
    {
        public int Route;
        public int ModelType;
        public float Distance, Speed, Heading, Pitch, Bank;
        public Vector3 Position;
        public bool Ground;
        public int Segment;
    }

    readonly List<RouteDef> _routes = new();
    readonly List<ModelDef> _models = new();
    readonly List<PlaneState> _planes = new();
    int _events;
    bool _log, _park, _bound;
    float _logTimer;
    int _loggedPlanes = -1, _loggedGround = -1;

    public int RouteCount => _routes.Count;
    public int PlaneCount => _planes.Count;
    public int ModelCount => _models.Count;
    public bool IsBound => _bound;
    public int GroundCount
    {
        get { int n = 0; foreach (var p in _planes) if (p.Ground) n++; return n; }
    }

    /// <summary>Starts/landings since the last call (ground segment entered/left).</summary>
    public int TakeEvents()
    {
        int n = _events; _events = 0; return n;
    }

    public void Unload()
    {
        _routes.Clear();
        _models.Clear();
        _planes.Clear();
        _events = 0;
        _logTimer = 0f;
        _loggedPlanes = -1;
        _loggedGround = -1;
        _bound = false;
        // Kein Destroy(): Meshes/Materialien gehören dem ObjectLibrary-Cache der MapStreamer.
    }

    public bool Load(string islandDir, SurfaceQuery? surface, bool log, bool park)
    {
        Unload();
        _log = log;
        _park = park;
        string folder = Path.Combine(islandDir, "scenarics", "packs");

        // 1) aircrafts.bnk: Type -> Library-Node (ObjectInCommonRef ist ein gepackter 8-Zeichen-Name).
        string modelPath = Path.Combine(folder, "aircrafts.bnk");
        if (!GameFiles.Exists(modelPath)) { Log.Warn($"Flugzeuge: {modelPath} fehlt"); return false; }
        var modelBank = BnkArchive.Open(modelPath);
        var modelXmb = Entities(modelBank);
        var modelArray = modelXmb?.Get("ExportType_Entity")?.Get("ArrayOf_Entity__Actor__Aircraft");
        if (modelArray != null)
        {
            foreach (var actor in modelArray.Items)
            {
                var m = new ModelDef
                {
                    TypeId = actor.I("Type", -1),
                    Key = actor.U("ObjectInCommonRef"),
                    Name = actor.S("__Name", "") ?? "",
                };
                _models.Add(m);
                if (_log) Log.Info($"Aircraft Type {m.TypeId} ‘{m.Name}’ -> {Bin.KeyToString(m.Key)}");
            }
        }
        if (_models.Count == 0) { Log.Warn($"Aircraft: {modelPath} has no Entity__Actor__Aircraft"); return false; }

        // 2) world_aircrafts.bnk: Volume-Punkte und die geschlossenen Routen.
        string routePath = Path.Combine(folder, "world_aircrafts.bnk");
        if (!GameFiles.Exists(routePath)) { Log.Info($"Airplanes: {routePath} is missing (no routes)"); return false; }
        var routeBank = BnkArchive.Open(routePath);
        var routeXmb = Entities(routeBank);
        if (routeXmb == null) { Log.Warn($"Aircraft: {routePath} does not have an entities.xmb file"); return false; }

        var volumes = new Dictionary<ulong, Vector3>();
        var volumeArray = routeXmb.Get("ExportType_Volume")?.Get("ArrayOf_Entity__Volume");
        if (volumeArray != null)
        {
            foreach (var v in volumeArray.Items)
            {
                var pos = v.Get("orient_pos_scale")?.Get("pos");
                if (pos == null) continue;
                volumes[v.U("__Hc")] = new Vector3(pos.F("X"), pos.F("Y"), pos.F("Z"));
            }
        }

        var routes = routeXmb.Get("ExportType_Entity")?.Get("ArrayOf_Entity__AircraftPath");
        if (routes != null)
        {
            foreach (var e in routes.Items)
            {
                var route = new RouteDef
                {
                    Name = e.S("__Name", "") ?? "",
                    TypeId = e.I("AircraftType"),
                    GroundSpeed = e.F("SpeedOnGround", 15f),
                    AirSpeed = e.F("SpeedOnAir", 80f),
                    MinSpacing = e.F("MinDistBetween2Aircraft", 3000f),
                };
                route.MaxSpacing = MathF.Max(e.F("MaxDistBetween2Aircraft", 5000f), route.MinSpacing);
                var points = new List<Vector3>();
                var types = new List<int>();
                var pathPoints = e.Get("PathPoints");
                if (pathPoints != null)
                {
                    foreach (var pt in pathPoints.Items)
                    {
                        if (!volumes.TryGetValue(pt.U("positionVolume"), out var p))
                        {
                            if (_log) Log.Warn($"Aircraft: ‘{route.Name}’ Point: Volume 0x{pt.U("positionVolume"):X16} is missing");
                            continue;
                        }
                        points.Add(p);
                        types.Add(pt.I("Type"));
                    }
                }
                if (points.Count < 3) { Log.Warn($"Airplanes: ‘{route.Name}’ has {points.Count} points, skipped"); continue; }
                BuildRoute(route, points, types, surface);
                if (route.Length < 100f) continue;
                _routes.Add(route);
            }
        }
        if (_routes.Count == 0) { Log.Warn($"Airplanes: {routePath} has no usable route"); return false; }

        // 3) Die Flugzeuge selbst: Abstand aus den Daten, erster Start auf der Landebahn.
        foreach (var route in _routes)
        {
            float spacing = MathF.Max((route.MinSpacing + route.MaxSpacing) * 0.5f, 1f);
            int count = System.Math.Clamp((int)(route.Length / spacing), 1, MaxPlanesPerRoute);
            // Der Library-Node wird erst beim Binden aufgelöst; die Typzuordnung steht aber schon fest.
            int modelType = _models.FindIndex(m => m.TypeId == route.TypeId);
            if (modelType < 0) modelType = 0;
            for (int p = 0; p < count; p++)
            {
                var plane = new PlaneState { Route = _routes.IndexOf(route), ModelType = modelType, Speed = route.AirSpeed };
                if (p == 0 && route.RunwayStart >= 0)
                {
                    float runway = route.Distances[route.RunwayStart];
                    plane.Distance = _park ? runway + 25f : runway * 0.5f;
                }
                else
                {
                    plane.Distance = Mod(route.Length * p / (float)count + route.MinSpacing * 0.5f, route.Length);
                }
                PlacePlane(plane);
                plane.Speed = plane.Ground ? route.GroundSpeed : route.AirSpeed;
                route.SpawnDistances.Add(plane.Distance);
                _planes.Add(plane);
            }
        }
        return _planes.Count > 0;
    }

    /// <summary>Hauptthread: Library-Objekte (einmal pro Typ) mit den GPU-Ressourcen der Insel verknüpfen.</summary>
    public void Bind(ObjectLibrary library, GlobalTextureCache textures, ObjectBuildOptions opt, IRenderer renderer)
    {
        _bound = true;   // auch wenn kein Node gefunden wird: nicht jede Frame erneut versuchen
        foreach (var m in _models)
        {
            m.Object = library.Get(m.Key, textures, opt, renderer);
            if (m.Object == null || m.Object.Lods.Length == 0) continue;
            m.BoundsMin = m.Object.BoundsMin;
            m.BoundsMax = m.Object.BoundsMax;
            m.Centre = (m.BoundsMin + m.BoundsMax) * 0.5f;
            m.Length = MathF.Max(m.BoundsMax.X - m.BoundsMin.X, m.BoundsMax.Z - m.BoundsMin.Z);
        }
    }

    public void Tick(float dt)
    {
        if (_planes.Count == 0) return;
        if (_park)
        {
            foreach (var p in _planes) { p.Speed = 0f; PlacePlane(p); }
        }
        else
        {
            foreach (var p in _planes) UpdatePlane(p, dt);
        }
        if (!_log) return;
        _logTimer += dt;
        if (_logTimer < 5f) return;
        _logTimer = 0f;
        int transitions = TakeEvents(), ground = GroundCount;
        if (_loggedPlanes == _planes.Count && _loggedGround == ground && transitions == 0) return;
        _loggedPlanes = _planes.Count; _loggedGround = ground;
    }

    /// <summary>Zeichnet die Flugzeuge in Spielkoordinaten; root = MapStreamer.RootMatrix.</summary>
    public int Draw(IRenderer renderer, Vector3 cameraGame, Matrix4x4 root, Vector4[] frustum)
    {
        if (_planes.Count == 0) return 0;
        float radiusSq = ViewRadius * ViewRadius;
        int drawn = 0;
        foreach (var p in _planes)
        {
            var model = _models[p.ModelType];
            if (!model.Ready) continue;
            var delta = p.Position - cameraGame;
            if (delta.LengthSquared() > radiusSq) continue;
            var centre = Vector3.Transform(p.Position, root);   // nur Translation, Radius bleibt in Metern
            if (!MapStreamer.SphereInFrustum(frustum, centre, MathF.Max(20f, model.Length * 0.6f))) continue;
            var lod = SelectLod(model.Object!, delta.Length());
            if (lod == null) continue;
            // In der Zeilenvektor-Schreibweise des C++: T(pos)*Ry*Rx*Rz*T(-centre).
            // System.Numerics nutzt Spaltenvektoren -> umgekehrt, Vorzeichen bleiben wie im C++.
            float offsetY = p.Ground ? -model.BoundsMin.Y : -model.Centre.Y;
            var world = Matrix4x4.CreateTranslation(-model.Centre.X, offsetY, -model.Centre.Z)
                      * Matrix4x4.CreateRotationZ(-p.Bank)
                      * Matrix4x4.CreateRotationX(-p.Pitch)
                      * Matrix4x4.CreateRotationY(p.Heading)
                      * Matrix4x4.CreateTranslation(p.Position)
                      * root;
            foreach (var part in lod.Parts) renderer.DrawMesh(part.Mesh, part.Material, world);
            drawn++;
        }
        return drawn;
    }

    static LibraryLod? SelectLod(LibraryObject obj, float distance)
    {
        foreach (var lod in obj.Lods) if (distance <= lod.MaxDistance) return lod;
        return obj.Lods.Length > 0 ? obj.Lods[^1] : null;
    }

    static float Mod(float a, float b) => b <= 0f ? 0f : a - MathF.Floor(a / b) * b;

    static float WrapPi(float a)
    {
        while (a > Pi) a -= 2f * Pi;
        while (a <= -Pi) a += 2f * Pi;
        return a;
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return (p1 * 2f + (p2 - p0) * t + (p0 * 2f - p1 * 5f + p2 * 4f - p3) * t2 +
                (p1 * 3f - p0 - p2 * 3f + p3) * t3) * 0.5f;
    }

    static XmbNode? Entities(BnkArchive bank)
    {
        var e = bank.Find("entities.xmb");
        if (e == null) return null;
        var bytes = bank.Read(e);
        return XmbFile.Parse(Unwrap(bytes));
    }

    static byte[] Unwrap(byte[] b)
    {
        if (XmbFile.IsXmb(b)) return b;
        for (int i = 1; i + 4 <= b.Length && i < 64; i++)
            if (b[i] == 'X' && b[i + 1] == 'M' && b[i + 2] == 'B' && b[i + 3] == 'F')
            {
                var r = new byte[b.Length - i];
                Array.Copy(b, i, r, 0, r.Length);
                return r;
            }
        return b;
    }

    void BuildRoute(RouteDef route, List<Vector3> points, List<int> types, SurfaceQuery? surface)
    {
        int count = points.Count;
        var ground = new bool[count];
        for (int i = 0; i < count; i++)
        {
            if (types[i] != 0) continue;
            bool isGround = surface != null && surface(points[i].X, points[i].Z, out float h) &&
                            MathF.Abs(h - points[i].Y) <= GroundTolerance;
            ground[i] = isGround;
            if (isGround) route.NumGroundPoints++;
        }
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            var a = points[i];
            var b = points[next];
            bool isGround = ground[i] && ground[next];
            if (isGround && route.RunwayStart < 0) route.RunwayStart = route.Ground.Count;
            var previous = points[(i + count - 1) % count];
            var after = points[(i + 2) % count];
            for (int k = 0; k < Subdivisions; k++)
            {
                float t = k / (float)Subdivisions;
                route.Points.Add(isGround ? Vector3.Lerp(a, b, t) : CatmullRom(previous, a, b, after, t));
                route.Ground.Add(isGround ? (byte)1 : (byte)0);
            }
        }
        for (int i = 0; i < route.Points.Count; i++)
        {
            route.Distances.Add(route.Length);
            var p = route.Points[i];
            var q = route.Points[(i + 1) % route.Points.Count];
            route.Length += Vector3.Distance(p, q);
        }
        byte last = route.Ground.Count > 0 ? route.Ground[^1] : (byte)0;
        while (route.Ground.Count < route.Points.Count) route.Ground.Add(last);
        if (route.RunwayStart < 0) route.RunwayStart = 0;
    }

    void SampleRoute(RouteDef route, float distance, out Vector3 position, out float heading, out int segment)
    {
        int samples = route.Points.Count;
        if (samples < 2) { position = Vector3.Zero; heading = 0f; segment = 0; return; }
        float s = Mod(distance, route.Length);
        int k = 0;
        while (k + 1 < samples && route.Distances[k + 1] <= s) k++;
        int next = (k + 1) % samples;
        float span = next > k ? route.Distances[next] - route.Distances[k] : route.Length - route.Distances[k];
        float t = span > 0.001f ? System.Math.Clamp((s - route.Distances[k]) / span, 0f, 1f) : 0f;
        position = Vector3.Lerp(route.Points[k], route.Points[next], t);
        segment = k;
        // Die Haltung schaut 45 m voraus: das Flugzeug dreht vor der Kurve, nicht in ihr.
        float wrapped = s + LookAhead;
        if (wrapped >= route.Length) wrapped -= route.Length;
        int j = 0;
        while (j + 1 < samples && route.Distances[j + 1] <= wrapped) j++;
        int jNext = (j + 1) % samples;
        float jSpan = jNext > j ? route.Distances[jNext] - route.Distances[j] : route.Length - route.Distances[j];
        float jt = jSpan > 0.001f ? System.Math.Clamp((wrapped - route.Distances[j]) / jSpan, 0f, 1f) : 0f;
        var ahead = Vector3.Lerp(route.Points[j], route.Points[jNext], jt);
        var delta = ahead - position;
        heading = MathF.Atan2(delta.X, delta.Z);
    }

    void UpdatePlane(PlaneState plane, float dt)
    {
        var route = _routes[plane.Route];
        bool wasGround = plane.Ground;
        if (dt > 0f)
        {
            plane.Distance += plane.Speed * dt;
            if (plane.Distance >= route.Length) plane.Distance -= route.Length * MathF.Floor(plane.Distance / route.Length);
        }
        PlacePlane(plane);
        float target = plane.Ground ? route.GroundSpeed : route.AirSpeed;
        if (dt > 0f)
        {
            float step = (target < plane.Speed ? Brake : Accel) * dt;
            plane.Speed += System.Math.Clamp(target - plane.Speed, -step, step);
        }
        else plane.Speed = target;
        if (plane.Ground != wasGround && dt > 0f) _events++;
    }

    void PlacePlane(PlaneState plane)
    {
        var route = _routes[plane.Route];
        SampleRoute(route, plane.Distance, out var position, out float heading, out int segment);
        SampleRoute(route, plane.Distance + 60f, out var ahead, out float aheadHeading, out _);
        var flat = new Vector3(ahead.X - position.X, 0f, ahead.Z - position.Z);
        float flatLength = flat.Length();
        bool onGround = route.Ground.Count > 0 && route.Ground[segment] != 0;
        float pitch = flatLength > 1f ? MathF.Atan2(ahead.Y - position.Y, flatLength) : 0f;
        if (onGround) pitch = 0f;
        float turn = WrapPi(aheadHeading - heading);
        float yawRate = flatLength > 1f ? plane.Speed / flatLength * turn : 0f;
        float bank = onGround ? 0f : MathF.Atan(plane.Speed * yawRate / 9.81f);
        plane.Position = position;
        plane.Heading = heading;
        plane.Pitch = System.Math.Clamp(pitch, -MaxPitch, MaxPitch);
        plane.Bank = System.Math.Clamp(bank, -MaxBank, MaxBank);
        plane.Segment = segment;
        plane.Ground = onGround;
    }
}