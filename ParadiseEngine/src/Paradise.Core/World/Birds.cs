using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Vögel der Insel - C#-Port von game/freeride/birds.{h,cpp} (siehe re/aircraft_birds_spec.md).
/// birds.bnk enthält nur fünf 64x64-DXT5-Sprites (birds.2dm, COLOR-Slot), keine Geometrie. Die Schwärme
/// werden als kamera-zugewandte Quads im transparenten Pass gezeichnet.
///
/// Simulation und Schwarmpositionen liegen in Spielkoordinaten; die Vertices werden erst beim Zeichnen
/// mit RootOffset in den Render-Raum überführt. Ein World-Shift muss die Vögel deshalb nicht bewegen.
/// Statt des eigenen bird.vert/frag (Vulkan-Pipeline) nutzt der Port das vorhandene Particle-Material:
/// eine Texturbindung und ein Draw pro Art, Alpha/Flügelschlag/Himmeltönung in der Vertexfarbe.
/// </summary>
public sealed class Birds : IDisposable
{
    /// <summary>Wie C++ CinematicCar::SurfaceFunction: y = Start höhe, out = Terrainhöhe.</summary>
    public delegate bool SurfaceQuery(float x, float z, float y, out float height);

    const int MaxBirds = 96, MaxSpecies = 8, MaxFlocks = 5;
    const int BirdsPerFlockMin = 5, BirdsPerFlockMax = 9;
    const float SpawnMinFar = 250f, SpawnMaxFar = 700f;
    const float SpawnMinNear = 60f, SpawnMaxNear = 200f;
    const float FlockRange = 1100f, DrawRadius = 800f;
    const float Pi2 = 2f * MathF.PI;

    sealed class Species
    {
        public string Name = "";
        public float Wingspan = 1.2f;
        public float FadeAlpha = 1f;
        public IGpuTexture? Texture;
        public IGpuMaterial? Material;
    }

    sealed class Flock
    {
        public Vector3 Centre;
        public Vector3 Heading;
        public float Speed = 12f, Turn, Altitude = 40f, Ground, GroundTimer;
        public int Species;
        public bool Alive;
    }

    sealed class Bird
    {
        public int Flock = -1;
        public Vector3 Offset, Position;
        public float Orbit, Angle, Flap, FlapRate, Size = 1f;
        public bool Alive;
    }

    readonly List<Species> _species = new();
    readonly List<Flock> _flocks = new();
    readonly List<Bird> _birds = new();
    readonly List<ObjectVertex>[] _vertices = new List<ObjectVertex>[MaxSpecies];
    readonly List<uint>[] _indices = new List<uint>[MaxSpecies];
    readonly Random _random = new(0x5eed1234);

    bool _loaded, _near, _front, _log;
    float _time, _rain, _sun = 1f;
    Vector3 _cameraPosition, _cameraForward = new(0f, 0f, 1f);
    bool _hasPose;
    int _drawn;
    float _logTimer;
    int _loggedDrawn = -1, _loggedFlocks = -1;

    public bool Loaded => _loaded;
    public int SpeciesCount => _species.Count;
    public int FlockCount => _flocks.Count;
    public int BirdCount => _birds.Count;
    public int DrawnCount => _drawn;
    public bool NearMode => _near;

    static readonly (string Name, float Span)[] s_spans =
    {
        ("MOUETTE", 1.35f), ("FAUCON", 1.10f), ("PIGEON", 0.70f),
        ("MOINEAU", 0.28f), ("NOIR", 0.95f),
    };

    static float Span(string name)
    {
        foreach (var s in s_spans) if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s.Span;
        return 0.9f;
    }

    static float Sat(float v) => System.Math.Clamp(v, 0f, 1f);
    float Rnd(float min, float max) => min + (float)_random.NextDouble() * (max - min);

    public bool Load(string path, GlobalTextureCache textures, IRenderer renderer)
    {
        Dispose();
        if (!GameFiles.Exists(path)) { Log.Warn($"Birds: {path} not found (no birds)"); return false; }
        var bank = BnkArchive.Open(path);
        var dmEntry = bank.All(".2dm").FirstOrDefault();
        if (dmEntry == null) { Log.Warn($"Birds: {path} does not contain birds.2dm"); return false; }
        var materials = Material2DM.Parse(bank.Read(dmEntry));
        textures.IndexBank(bank);   // die 2db-Sprites liegen in derselben Bank

        foreach (var def in materials.Materials.Values)
        {
            if (_species.Count >= MaxSpecies) break;
            if (!def.TryGetLayer(MaterialDef.SemColor, out var layer)) continue;
            var tex = textures.Get(layer.TextureKey, TextureWrap.Clamp, TextureWrap.Clamp);
            if (tex == null) { Log.Warn($"Birds: Material ‘{def.Name}’: Texture 0x{layer.TextureKey:X16} is missing"); continue; }
            float fade = 1f;
            if (def.TryGetParam("FADEALFA", out var p) && p.X > 0f) fade = Sat(p.X);
            var mat = renderer.CreateMaterial(new MaterialDesc
            {
                Kind = MaterialKind.Particle,
                Name = "Bird " + def.Name,
                Main = tex,
                Lit = 0f,             // Tönung kommt aus der Vertexfarbe (C++: sunBrightness)
                Additive = false,
                TwoSided = true,
                Color = Vector4.One,
            });
            _species.Add(new Species { Name = def.Name, Wingspan = Span(def.Name), FadeAlpha = fade, Texture = tex, Material = mat });
        }
        if (_species.Count == 0) { Log.Warn($"Birds: {path} has no usable species"); return false; }
        for (int i = 0; i < MaxSpecies; i++)
        {
            _vertices[i] = new List<ObjectVertex>(256);
            _indices[i] = new List<uint>(384);
        }
        _loaded = true;
        return true;
    }

    public void Dispose()
    {
        foreach (var s in _species) s.Material?.Dispose();   // Texturen gehören dem GlobalTextureCache
        _species.Clear();
        _flocks.Clear();
        _birds.Clear();
        _drawn = 0;
        _loaded = false;
        _hasPose = false;
        _logTimer = 0f;
        _loggedDrawn = -1;
        _loggedFlocks = -1;
    }

    public int TakeDrawn() { int n = _drawn; _drawn = 0; return n; }

    void SpawnFlock(Flock flock, int index, SurfaceQuery surface)
    {
        bool pathFlock = index == 0 && !_front && !_near;
        float minRing = _front ? 25f : pathFlock ? 300f : _near ? SpawnMinNear : SpawnMinFar;
        float maxRing = _front ? 70f : pathFlock ? 500f : _near ? SpawnMaxNear : SpawnMaxFar;
        Vector3 heading = _cameraForward.LengthSquared() > 1e-8f ? Vector3.Normalize(_cameraForward) : Vector3.UnitZ;
        float headingAngle = MathF.Atan2(heading.X, heading.Z);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float distance = Rnd(minRing, maxRing);
            float angle = _front || pathFlock ? headingAngle + Rnd(-0.35f, 0.35f) : Rnd(0f, Pi2);
            // Yaw-Konvention der Engine: x = sin, z = cos (siehe atan2(x, z)).
            var offset = new Vector3(MathF.Sin(angle) * distance, 0f, MathF.Cos(angle) * distance);
            if (!_front && !pathFlock && distance < maxRing * 0.75f &&
                offset.X * heading.X + offset.Z * heading.Z > 0.35f * distance) continue;
            var centre = _cameraPosition + offset;
            float ground = 0f;
            bool hasGround = false;
            foreach (float lift in new[] { 40f, 100f, -20f, -80f })
                if (surface(centre.X, centre.Z, _cameraPosition.Y + lift, out ground)) { hasGround = true; break; }
            if (!hasGround) continue;
            flock.Altitude = _front ? Rnd(6f, 16f) : pathFlock ? Rnd(18f, 32f) : _near ? Rnd(10f, 26f) : Rnd(25f, 70f);
            flock.Centre = new Vector3(centre.X, ground + flock.Altitude, centre.Z);
            flock.Ground = ground;
            float crossAngle = pathFlock
                ? headingAngle + (_random.Next(2) == 0 ? MathF.PI * 0.5f : -MathF.PI * 0.5f) + Rnd(-0.5f, 0.5f)
                : angle;
            flock.Heading = new Vector3(MathF.Sin(crossAngle), 0f, MathF.Cos(crossAngle));
            flock.Speed = Rnd(8f, 15f);
            flock.Turn = Rnd(-0.12f, 0.12f);
            flock.GroundTimer = 0f;
            flock.Species = _random.Next(_species.Count);
            flock.Alive = true;
            return;
        }
        flock.Alive = false;   // kein Terrain in der Nähe: nächstes Frame erneut versuchen
    }

    void RebuildBirds()
    {
        _birds.Clear();
        for (int f = 0; f < _flocks.Count && _birds.Count < MaxBirds; f++)
        {
            var flock = _flocks[f];
            if (!flock.Alive) continue;
            int count = _random.Next(BirdsPerFlockMin, BirdsPerFlockMax + 1);
            for (int i = 0; i < count && _birds.Count < MaxBirds; i++)
            {
                float spread = Rnd(12f, 18f);
                _birds.Add(new Bird
                {
                    Flock = f,
                    Offset = new Vector3(Rnd(-spread, spread), Rnd(-4f, 4f), Rnd(-spread, spread)),
                    Orbit = Rnd(0.05f, 0.25f) * (_random.Next(2) == 0 ? -1f : 1f),
                    Angle = Rnd(0f, Pi2),
                    Flap = Rnd(0f, Pi2),
                    FlapRate = Rnd(6f, 11f),
                    Size = Rnd(0.85f, 1.15f),
                    Position = flock.Centre,
                    Alive = true,
                });
            }
        }
    }

    public void Tick(float dt, float rain, float sun, SurfaceQuery surface)
    {
        if (!_loaded || dt <= 0f || !_hasPose) return;
        _time += dt;
        _rain = Sat(rain);
        _sun = Sat(sun);
        bool groundThem = _rain > 0.55f;
        while (_flocks.Count < MaxFlocks) _flocks.Add(new Flock());
        bool rebuild = false;
        for (int i = 0; i < _flocks.Count; i++)
        {
            var flock = _flocks[i];
            var delta = flock.Centre - _cameraPosition;
            if (flock.Alive && (groundThem || delta.LengthSquared() > FlockRange * FlockRange))
            {
                flock.Alive = false;
                rebuild = true;
            }
            if (!flock.Alive)
            {
                if (!groundThem) { SpawnFlock(flock, i, surface); rebuild |= flock.Alive; }
                continue;
            }
            flock.Turn = System.Math.Clamp(flock.Turn + Rnd(-0.05f, 0.05f) * dt, -0.15f, 0.15f);
            float heading = MathF.Atan2(flock.Heading.X, flock.Heading.Z) + flock.Turn * dt;
            flock.Heading = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
            flock.Centre += flock.Heading * (flock.Speed * dt);
            flock.GroundTimer -= dt;
            if (flock.GroundTimer <= 0f)
            {
                flock.GroundTimer = 2f;
                if (surface(flock.Centre.X, flock.Centre.Z, flock.Centre.Y + 20f, out float g)) flock.Ground = g;
            }
            float target = flock.Ground + flock.Altitude;
            flock.Centre = new Vector3(flock.Centre.X,
                flock.Centre.Y + (target - flock.Centre.Y) * System.Math.Clamp(dt * 0.8f, 0f, 1f), flock.Centre.Z);
            if (flock.Centre.Y < flock.Ground + 12f) flock.Centre = new Vector3(flock.Centre.X, flock.Ground + 12f, flock.Centre.Z);
        }
        if (rebuild) RebuildBirds();
        foreach (var bird in _birds)
        {
            if (bird.Flock < 0 || bird.Flock >= _flocks.Count) { bird.Alive = false; continue; }
            var flock = _flocks[bird.Flock];
            bird.Alive = flock.Alive;
            if (!bird.Alive) continue;
            bird.Angle += bird.Orbit * dt;
            float ca = MathF.Cos(bird.Angle), sa = MathF.Sin(bird.Angle);
            var orbit = new Vector3(bird.Offset.X * ca - bird.Offset.Z * sa, bird.Offset.Y,
                                    bird.Offset.X * sa + bird.Offset.Z * ca);
            float bob = MathF.Sin(_time * 1.7f + bird.Offset.X * 0.3f) * 0.8f;
            bird.Position = flock.Centre + orbit + new Vector3(0f, bob, 0f);
            bird.Flap += bird.FlapRate * dt;
            if (bird.Flap > Pi2) bird.Flap -= Pi2;
        }
        if (!_log) return;
        _logTimer += dt;
        if (_logTimer < 5f) return;
        _logTimer = 0f;
        if (_drawn == _loggedDrawn && _flocks.Count == _loggedFlocks) return;
        _loggedDrawn = _drawn; _loggedFlocks = _flocks.Count;
    }

    /// <summary>
    /// view = Kamera-View-Matrix (Render-Raum). Die Blickrichtung kommt bewusst aus deren dritter Spalte:
    /// -view.M13/M23/M33 (die C++-Falle mit CameraDirection/Quaternion gibt die Gegenrichtung).
    /// </summary>
    public int Draw(IRenderer renderer, Vector3 cameraGame, Vector3 rootOffset, Matrix4x4 view, float sun)
    {
        _drawn = 0;
        if (!_loaded) return 0;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        if (right.LengthSquared() < 1e-8f || up.LengthSquared() < 1e-8f) { right = Vector3.UnitX; up = Vector3.UnitY; }
        right = Vector3.Normalize(right);
        up = Vector3.Normalize(up);
        _cameraPosition = cameraGame;
        var forward = new Vector3(-view.M13, 0f, -view.M33);
        _hasPose = forward.LengthSquared() > 0.01f;
        if (_hasPose) _cameraForward = Vector3.Normalize(forward);
        if (_birds.Count == 0) return 0;

        float sunBright = 0.35f + 0.65f * Sat(sun);
        byte tint = (byte)(255f * sunBright);
        float fadeStart = DrawRadius * 0.72f;
        for (int i = 0; i < MaxSpecies; i++) { _vertices[i].Clear(); _indices[i].Clear(); }

        foreach (var bird in _birds)
        {
            if (!bird.Alive || bird.Flock < 0 || bird.Flock >= _flocks.Count) continue;
            int si = _flocks[bird.Flock].Species;
            if (si < 0 || si >= _species.Count) continue;
            var species = _species[si];
            var delta = bird.Position - cameraGame;
            float distance = delta.Length();
            if (distance > DrawRadius) continue;
            float alpha = 1f - Sat((distance - fadeStart) / MathF.Max(DrawRadius - fadeStart, 1f));
            alpha *= Sat((distance - 2f) / 6f) * species.FadeAlpha;
            if (alpha <= 0.01f) continue;
            float half = 0.5f * species.Wingspan * bird.Size;
            float flap = 0.45f + 0.55f * MathF.Abs(MathF.Cos(bird.Flap));
            var x = right * (half * flap);
            var y = up * half;
            var p = bird.Position + rootOffset;   // Spielkoordinaten -> Render-Raum (nur Translation)
            uint color = ObjectVertex.PackColor(tint, tint, tint, (byte)(255f * alpha));
            var corners = new[] { p - x - y, p + x - y, p + x + y, p - x + y };
            var uv = new[] { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) };
            int b = _vertices[si].Count;
            for (int c = 0; c < 4; c++)
                _vertices[si].Add(new ObjectVertex { Position = corners[c], Normal = Vector3.Zero, Color = color, Uv0 = uv[c] });
            foreach (int c in new[] { 0, 1, 2, 0, 2, 3 }) _indices[si].Add((uint)(b + c));
            _drawn++;
        }

        for (int i = 0; i < _species.Count && i < MaxSpecies; i++)
        {
            if (_vertices[i].Count == 0 || _species[i].Material == null) continue;
            renderer.DrawDynamic(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vertices[i]),
                                 System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_indices[i]),
                                 _species[i].Material!, Matrix4x4.Identity);
        }
        return _drawn;
    }
}