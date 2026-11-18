using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Paradise.Core.Physics;

/// <summary>
/// Physics world (BepuPhysics2) – Replacement for the Unity physics (PhysX) in the Unity project:
///  - Static collision meshes for the loaded sectors (terrain + opaque objects, such as TDU2MapStreamer.AddColliders/MeshCollider)
///  - Raycasts (Physics.Raycast: vehicle tire contacts)
///  - Rigidbodies for vehicles (to follow in Level 3)
/// Coordinates: Game coordinates (meters), +Y up. Internally relative to <see cref="Origin"/> (floating origin as in the Unity project:
/// at 20 km world coordinates, float has only ~2 mm resolution – suspension travel/raycasts jitter). Vehicles operate in local coordinates.
/// </summary>
public sealed class PhysicsWorld : IDisposable
{
    public readonly BufferPool Pool = new();
    public readonly Simulation Simulation;
    readonly ThreadDispatcher _dispatcher;

    /// <summary>Schwerkraft (Unity: Physics.gravity, Standard -9,81).</summary>
    public Vector3 Gravity { get => _gravity.Value; set => _gravity.Value = value; }
    readonly GravityBox _gravity = new() { Value = new Vector3(0f, -9.81f, 0f) };

    /// <summary>Spielkoordinate, die intern (0,0,0) entspricht (ganzzahlig, damit Umrechnungen exakt bleiben).</summary>
    public Vector3 Origin { get; private set; }
    public Vector3 ToLocal(Vector3 game) => game - Origin;
    public Vector3 ToGame(Vector3 local) => local + Origin;
    readonly List<BodyHandle> _bodies = new();

    /// <summary>
    /// Shift world origin (Unity: FloatingOrigin.Shift): All static colliders and bodies are shifted.
    /// newOrigin is rounded to the nearest whole meter.
    /// </summary>
    public void ShiftOrigin(Vector3 newOrigin)
    {
        newOrigin = new Vector3(MathF.Round(newOrigin.X), MathF.Round(newOrigin.Y), MathF.Round(newOrigin.Z));
        var d = newOrigin - Origin;
        if (d == Vector3.Zero) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Alle statischen Kollider verschieben sich um denselben Betrag: Lagen direkt ändern und die Hüllen im Suchbaum der
        // Breitphase mitverschieben (vorher ApplyDescription je Kollider: 60–115 ms bei ~20 000 Kollidern = Ruckler alle paar Sekunden).
        foreach (var sc in _statics.Values)
            Simulation.Statics[sc.Handle].Pose.Position -= d;
        var tree = Simulation.BroadPhase.StaticTree;
        for (int i = 0; i < tree.NodeCount; i++)
        {
            ref var node = ref tree.Nodes[i];
            node.A.Min -= d; node.A.Max -= d;
            node.B.Min -= d; node.B.Max -= d;
        }
        foreach (var h in _bodies)
        {
            if (!Simulation.Bodies.BodyExists(h)) continue;
            var b = Simulation.Bodies[h];
            b.Pose.Position -= d;
        }
        Origin = newOrigin;
        LastShiftMs = sw.Elapsed.TotalMilliseconds;
        Shifted?.Invoke(d);
    }

    /// <summary>Dauer der letzten Ursprungsverschiebung (ms, Diagnose).</summary>
    public double LastShiftMs { get; private set; }

    /// <summary>Nach dem Verschieben (Verschiebung in Spielkoordinaten): lokale Positionen anpassen.</summary>
    public event Action<Vector3>? Shifted;

    public int StaticCount { get; private set; }
    public long TriangleCount { get; private set; }

    sealed class GravityBox { public Vector3 Value; }

    public PhysicsWorld()
    {
        _dispatcher = new ThreadDispatcher(System.Math.Max(1, Environment.ProcessorCount - 2));
        Simulation = Simulation.Create(Pool, new NarrowPhaseCallbacks(), new PoseIntegratorCallbacks(_gravity), new SolveDescription(8, 1));
    }

    /// <summary>
    /// Statisches Dreiecksnetz (Spielkoordinaten, je 3 Punkte ein Dreieck). surfaces = Untergrund je Dreieck
    /// (0 Asphalt, 1 Gras, 2 Erde; Unity: SurfaceOf je Collider). Rückgabe null, wenn leer.
    /// </summary>
    public StaticCollider? AddStaticMesh(ReadOnlySpan<Vector3> triangles, string name = "", ReadOnlySpan<byte> surfaces = default)
    {
        int n = triangles.Length / 3;
        if (n == 0) return null;
        // Netz relativ zum eigenen Mittelpunkt (Genauigkeit bei großen Weltkoordinaten)
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var p in triangles) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var center = (min + max) * 0.5f;
        // Bepu-Dreiecke sind einseitig; Unity-MeshCollider wirken für Fahrzeuge praktisch beidseitig (Straßen, Brücken, Wände
        // aus beiden Richtungen). Jedes Dreieck daher in beiden Wicklungen eintragen.
        Pool.Take<Triangle>(n * 2, out var buffer);
        byte[]? surf = surfaces.Length >= n ? new byte[n * 2] : null;
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            var a = triangles[i * 3] - center; var b = triangles[i * 3 + 1] - center; var c = triangles[i * 3 + 2] - center;
            if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-12f) continue;   // entartet
            if (surf != null) { surf[k] = surfaces[i]; surf[k + 1] = surfaces[i]; }
            buffer[k++] = new Triangle(a, b, c);
            buffer[k++] = new Triangle(a, c, b);
        }
        if (k == 0) { Pool.Return(ref buffer); return null; }
        var mesh = new Mesh(buffer.Slice(k), Vector3.One, Pool);
        var shape = Simulation.Shapes.Add(mesh);
        var handle = Simulation.Statics.Add(new StaticDescription(center - Origin, shape));
        StaticCount++; TriangleCount += k;
        var sc = new StaticCollider(handle, shape, k, name) { Surfaces = surf };
        _statics[handle.Value] = sc;
        return sc;
    }

    public void Remove(StaticCollider? c)
    {
        if (c == null || c.Removed) return;
        c.Removed = true;
        _statics.Remove(c.Handle.Value);
        Simulation.Statics.Remove(c.Handle);
        if (c.OwnPool != null)
        {
            // im Hintergrund gebautes Netz: eigener Pool -> komplett freigeben
            Simulation.Shapes.Remove(c.Shape);
            c.OwnPool.Clear();
            System.Threading.Interlocked.Add(ref _bgBytes, -c.OwnBytes);
            c.OwnPool = null;
        }
        else if (!c.SharedShape) Simulation.Shapes.RemoveAndDispose(c.Shape, Pool);
        StaticCount--; TriangleCount -= c.Triangles;
    }

    // Bepu baut beim Anlegen eines Mesh den Suchbaum (BVH) – bei einem Sektor (Gelände + Objekte) 20–70 ms. Das lief im
    // Hauptthread (Ruckler beim Nachladen). Jetzt: Dreiecke + Baum in einem Task, im Hauptthread nur noch Shapes.Add/Statics.Add.
    // Jedes Netz hat einen eigenen Pool: Bepu-Pools geben Speicher sonst nie an das System zurück (gemeinsamer Pool wuchs auf > 1 GB);
    // beim Entladen wird der Pool geleert und der Speicher ist wieder frei.
    long _bgBytes;

    /// <summary>Fertig gebautes Kollisionsnetz, noch nicht in der Welt (<see cref="AddPrepared"/> / <see cref="Discard"/>).</summary>
    public sealed class PreparedMesh
    {
        internal Mesh Mesh;
        internal BufferPool Pool = null!;
        internal long Bytes;
        internal Vector3 Center;
        internal int Triangles;
        internal byte[]? Surfaces;
        internal bool Used;
        public double BuildMs;
    }

    /// <summary>Wie <see cref="AddStaticMesh"/>, aber ohne die Welt zu ändern – in jedem Thread aufrufbar.</summary>
    public PreparedMesh? PrepareStaticMesh(ReadOnlySpan<Vector3> triangles, ReadOnlySpan<byte> surfaces = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = triangles.Length / 3;
        if (n == 0) return null;
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var p in triangles) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var center = (min + max) * 0.5f;
        byte[]? surf = surfaces.Length >= n ? new byte[n * 2] : null;
        var pool = new BufferPool();
        pool.Take<Triangle>(n * 2, out var buffer);
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            var a = triangles[i * 3] - center; var b = triangles[i * 3 + 1] - center; var c = triangles[i * 3 + 2] - center;
            if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-12f) continue;   // entartet
            if (surf != null) { surf[k] = surfaces[i]; surf[k + 1] = surfaces[i]; }
            buffer[k++] = new Triangle(a, b, c);
            buffer[k++] = new Triangle(a, c, b);   // beidseitig (wie AddStaticMesh)
        }
        if (k == 0) { pool.Clear(); return null; }
        var mesh = new Mesh(buffer.Slice(k), Vector3.One, pool);
        long bytes = (long)pool.GetTotalAllocatedByteCount();
        System.Threading.Interlocked.Add(ref _bgBytes, bytes);
        return new PreparedMesh { Mesh = mesh, Pool = pool, Bytes = bytes, Center = center, Triangles = k, Surfaces = surf, BuildMs = sw.Elapsed.TotalMilliseconds };
    }

    /// <summary>Vorbereitetes Netz in die Welt eintragen (Hauptthread).</summary>
    public StaticCollider? AddPrepared(PreparedMesh? pm, string name = "")
    {
        if (pm == null || pm.Used) return null;
        pm.Used = true;
        var shape = Simulation.Shapes.Add(pm.Mesh);
        var handle = Simulation.Statics.Add(new StaticDescription(pm.Center - Origin, shape));
        StaticCount++; TriangleCount += pm.Triangles;
        var sc = new StaticCollider(handle, shape, pm.Triangles, name) { Surfaces = pm.Surfaces, OwnPool = pm.Pool, OwnBytes = pm.Bytes };
        _statics[handle.Value] = sc;
        return sc;
    }

    /// <summary>Nicht mehr benötigtes vorbereitetes Netz freigeben (Sektor inzwischen entladen).</summary>
    public void Discard(PreparedMesh? pm)
    {
        if (pm == null || pm.Used) return;
        pm.Used = true;
        pm.Pool.Clear();
        System.Threading.Interlocked.Add(ref _bgBytes, -pm.Bytes);
    }

    /// <summary>Speicher der Bepu-Pools (Diagnose).</summary>
    public (ulong main, ulong background) PoolBytes()
    {
        return (Pool.GetTotalAllocatedByteCount(), (ulong)System.Math.Max(0L, System.Threading.Interlocked.Read(ref _bgBytes)));
    }

    readonly Dictionary<(object key, Vector3 scale), (TypedIndex shape, int tris)> _sharedShapes = new();

    /// <summary>
    /// Statisches Objekt mit gemeinsam genutztem Netz (Unity: MeshCollider je Instanz, gleiches Mesh). localTris = Dreiecke
    /// im Objektraum (Vorderseite = Cross(b-a, c-a)), Lage per Matrix (Spielkoordinaten; Skalierung wird ins Netz übernommen).
    /// </summary>
    public StaticCollider? AddStaticInstance(object key, Vector3[] localTris, Matrix4x4 world, string name = "", byte surface = 0)
    {
        if (localTris.Length < 3 || !Matrix4x4.Decompose(world, out var scale, out var rot, out var pos)) return null;
        var sk = new Vector3(MathF.Round(scale.X * 20f) / 20f, MathF.Round(scale.Y * 20f) / 20f, MathF.Round(scale.Z * 20f) / 20f);
        if (!_sharedShapes.TryGetValue((key, sk), out var sh))
        {
            int n = localTris.Length / 3;
            Pool.Take<Triangle>(n * 2, out var buffer);
            bool flip = sk.X * sk.Y * sk.Z < 0f;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                var a = localTris[i * 3] * sk; var b = localTris[i * 3 + 1] * sk; var c = localTris[i * 3 + 2] * sk;
                if (flip) (b, c) = (c, b);
                if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-12f) continue;
                buffer[k++] = new Triangle(a, b, c);
                buffer[k++] = new Triangle(a, c, b);   // beidseitig
            }
            if (k == 0) { Pool.Return(ref buffer); _sharedShapes[(key, sk)] = (default, 0); return null; }
            sh = (Simulation.Shapes.Add(new Mesh(buffer.Slice(k), Vector3.One, Pool)), k);
            _sharedShapes[(key, sk)] = sh;
        }
        if (sh.tris == 0) return null;
        var handle = Simulation.Statics.Add(new StaticDescription(new RigidPose(pos - Origin, Quaternion.Normalize(rot)), sh.shape));
        StaticCount++; TriangleCount += sh.tris;
        var sc = new StaticCollider(handle, sh.shape, sh.tris, name) { SharedShape = true, Surface = surface };
        _statics[handle.Value] = sc;
        return sc;
    }

    readonly Dictionary<int, StaticCollider> _statics = new();

    /// <summary>Statischer Kollider eines Treffers (Unity: RaycastHit.collider), null bei Körpern.</summary>
    public StaticCollider? StaticOf(CollidableReference c)
        => c.Mobility == CollidableMobility.Static && _statics.TryGetValue(c.StaticHandle.Value, out var s) ? s : null;

    /// <summary>
    /// Dynamischer Körper mit einer Box als Kollider. Körperursprung = Schwerpunkt (Bepu); die Box liegt um
    /// <paramref name="boxOffset"/> (lokal, relativ zum Schwerpunkt) verschoben. inertiaDiag = Hauptträgheitsmomente (lokal).
    /// </summary>
    /// <summary>Maximaler spekulativer Rand der Fahrzeug-Karosserie (m).</summary>
    public float BodySpeculativeMargin = 0.1f;

    public BodyHandle AddDynamicBox(Vector3 centerOfMass, Quaternion orientation, Vector3 boxSize, Vector3 boxOffset, float mass, Vector3 inertiaDiag)
    {
        var box = new Box(System.MathF.Max(0.05f, boxSize.X), System.MathF.Max(0.05f, boxSize.Y), System.MathF.Max(0.05f, boxSize.Z));
        using var builder = new CompoundBuilder(Pool, Simulation.Shapes, 1);
        builder.Add(box, new RigidPose(boxOffset), 1f);
        builder.BuildKinematicCompound(out var children);
        var shape = Simulation.Shapes.Add(new Compound(children));
        var inertia = new BodyInertia
        {
            InverseMass = 1f / System.MathF.Max(1f, mass),
            InverseInertiaTensor = new Symmetric3x3
            {
                XX = 1f / System.MathF.Max(1e-3f, inertiaDiag.X),
                YX = 0f,
                YY = 1f / System.MathF.Max(1e-3f, inertiaDiag.Y),
                ZX = 0f,
                ZY = 0f,
                ZZ = 1f / System.MathF.Max(1e-3f, inertiaDiag.Z),
            },
        };
        var desc = BodyDescription.CreateDynamic(new RigidPose(centerOfMass, orientation), new BodyVelocity(), inertia,
            // Unity: CollisionDetectionMode.ContinuousDynamic. Vorher spekulativer Rand ohne Obergrenze: bei 230 km/h ~0,6 m je Schritt –
            // die Box bekam Kontakte von Dreieckskanten/Nahtstellen vor dem Auto ("Wand", Platte höher). Jetzt Sweep-Test (kein
            // Durchfallen nach Sprüngen) mit kleinem Rand.
            new CollidableDescription(shape, BodySpeculativeMargin, ContinuousDetection.Continuous()),
            new BodyActivityDescription(-1f));   // nie schlafen (Kräfte kommen jedes Mal von außen)
        var h = Simulation.Bodies.Add(desc);
        _bodies.Add(h);
        return h;
    }

    /// <summary>Körper, die Strahlen nicht treffen (Unity Layer 2: umgefahrene World Actors).</summary>
    public readonly HashSet<int> RayIgnoreBodies = new();
    /// <summary>
    /// Treffer auf statischen Kollidern verwerfen (Spielkoordinaten des Treffers) – Unity TDU2Interiors.DisableWorldColliders /
    /// SetExteriorVisible: Welt-Kollider im Interieur sind abgeschaltet. null = alle Treffer zählen.
    /// </summary>
    public Func<StaticCollider, Vector3, bool>? RayRejectStatic;

    /// <summary>
    /// Loser Quader (Unity: Rigidbody + konvexer Box-Kollider, ContinuousDynamic), z.B. umgefahrenes Schild. Darf schlafen;
    /// Strahlen treffen ihn nicht. center = Spielkoordinaten.
    /// </summary>
    public BodyHandle AddLooseBox(Vector3 center, Quaternion orientation, Vector3 size, float mass)
    {
        var box = new Box(System.MathF.Max(0.05f, size.X), System.MathF.Max(0.05f, size.Y), System.MathF.Max(0.05f, size.Z));
        var inertia = box.ComputeInertia(System.MathF.Max(0.5f, mass));
        var shape = Simulation.Shapes.Add(box);
        var desc = BodyDescription.CreateDynamic(new RigidPose(center - Origin, orientation), new BodyVelocity(), inertia,
            new CollidableDescription(shape, 0.1f, ContinuousDetection.Continuous()), new BodyActivityDescription(0.01f));
        var h = Simulation.Bodies.Add(desc);
        _bodies.Add(h);
        RayIgnoreBodies.Add(h.Value);
        return h;
    }

    public void RemoveBody(BodyHandle h)
    {
        _bodies.Remove(h);
        RayIgnoreBodies.Remove(h.Value);
        if (!Simulation.Bodies.BodyExists(h)) return;
        var shape = Simulation.Bodies[h].Collidable.Shape;
        Simulation.Bodies.Remove(h);
        Simulation.Shapes.RecursivelyRemoveAndDispose(shape, Pool);
    }

    public BodyReference Body(BodyHandle h) => Simulation.Bodies[h];

    public struct RayHit
    {
        public Vector3 Point, Normal;
        public float Distance;
        public CollidableReference Collidable;
        /// <summary>Dreieck im Netz (Untergrund: <see cref="SurfaceOf"/>).</summary>
        public int Child;
    }

    /// <summary>Untergrund eines Treffers (0 Asphalt, 1 Gras, 2 Erde), -1 = unbekannt/Körper.</summary>
    public int SurfaceOf(in RayHit hit)
    {
        var s = StaticOf(hit.Collidable);
        if (s == null) return -1;
        if (s.Surfaces != null && hit.Child >= 0 && hit.Child < s.Surfaces.Length) return s.Surfaces[hit.Child];
        return s.Surface;
    }

    /// <summary>Nächster Treffer entlang dir (normiert) bis maxDistance, Spielkoordinaten.</summary>
    public bool Raycast(Vector3 origin, Vector3 dir, float maxDistance, out RayHit hit, BodyHandle? ignore = null)
    {
        bool ok = RaycastLocal(origin - Origin, dir, maxDistance, out hit, ignore);
        if (ok) hit.Point += Origin;
        return ok;
    }

    /// <summary>Raycast in lokalen Koordinaten (relativ zu <see cref="Origin"/>).</summary>
    public bool RaycastLocal(Vector3 origin, Vector3 dir, float maxDistance, out RayHit hit, BodyHandle? ignore = null)
    {
        var h = new ClosestHit { Ignore = ignore, T = float.MaxValue, Statics = _statics, IgnoreBodies = RayIgnoreBodies, Reject = RayRejectStatic, WorldOrigin = Origin };
        Simulation.RayCast(origin, dir, maxDistance, Pool, ref h);
        hit = default;
        if (h.T == float.MaxValue) return false;
        hit.Distance = h.T;
        hit.Point = origin + dir * h.T;
        var nl = h.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(h.Normal) : Vector3.UnitY;
        if (Vector3.Dot(nl, dir) > 0f) nl = -nl;   // Normale zur Strahlquelle
        hit.Normal = nl;
        hit.Collidable = h.Collidable;
        hit.Child = h.Child;
        return true;
    }

    struct ClosestHit : IRayHitHandler
    {
        public BodyHandle? Ignore;
        public Dictionary<int, StaticCollider> Statics;
        public HashSet<int> IgnoreBodies;
        public Func<StaticCollider, Vector3, bool>? Reject;
        public Vector3 WorldOrigin;
        public float T;
        public Vector3 Normal;
        public CollidableReference Collidable;
        public int Child;

        public bool AllowTest(CollidableReference c)
        {
            if (c.Mobility == CollidableMobility.Static)
                return !(Statics != null && Statics.TryGetValue(c.StaticHandle.Value, out var s) && s.IgnoreRays);
            if (IgnoreBodies != null && IgnoreBodies.Contains(c.BodyHandle.Value)) return false;
            return !(Ignore.HasValue && c.BodyHandle.Value == Ignore.Value.Value);
        }
        public bool AllowTest(CollidableReference c, int childIndex) => true;
        public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal, CollidableReference collidable, int childIndex)
        {
            if (t >= T) return;
            if (Reject != null && collidable.Mobility == CollidableMobility.Static && Statics != null && Statics.TryGetValue(collidable.StaticHandle.Value, out var s)
                && Reject(s, WorldOrigin + ray.Origin + ray.Direction * t)) return;
            T = t; Normal = normal; Collidable = collidable; Child = childIndex; maximumT = t;
        }
    }

    /// <summary>Simulationsschritt (fester Takt).</summary>
    public void Step(float dt)
    {
        if (dt > 0f) Simulation.Timestep(dt, _dispatcher);
    }

    public void Dispose()
    {
        Simulation.Dispose();
        _dispatcher.Dispose();
        Pool.Clear();
        foreach (var sc in _statics.Values) { sc.OwnPool?.Clear(); sc.OwnPool = null; }
    }

    /// <summary>Kontakte Körper/Welt mitschreiben (für [DIAG Roll]); Einträge: Zeit (ms), Gegenstück, Normale, Versatz zum Körper, Tiefe.</summary>
    public static volatile bool ContactDiag = true;
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long ms, CollidableReference other, Vector3 normal, Vector3 offset, float depth)> RecentContacts = new();

    struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation simulation) { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
            => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            // Karosserie gegen Welt: wenig Reibung (Reifenkräfte kommen aus dem Fahrzeugmodell), etwas Rückprall
            if (ContactDiag && manifold.Count > 0)
            {
                // Diagnose ([DIAG Roll]): tiefster Kontakt dieses Paars (Normale zeigt von B nach A, Bepu)
                float best = float.MinValue; Vector3 bn = default, bo = default;
                for (int i = 0; i < manifold.Count; i++)
                {
                    manifold.GetContact(i, out var off, out var nrm, out var depth, out _);
                    if (depth > best) { best = depth; bn = nrm; bo = off; }
                }
                var other = pair.A.Mobility == CollidableMobility.Static ? pair.A : pair.B;
                if (RecentContacts.Count < 256) RecentContacts.Enqueue((Environment.TickCount64, other, bn, bo, best));
            }
            pairMaterial.FrictionCoefficient = 0.3f;
            pairMaterial.MaximumRecoveryVelocity = 2f;
            pairMaterial.SpringSettings = new SpringSettings(30, 1);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

        public void Dispose() { }
    }

    struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        readonly GravityBox _g;
        Vector3Wide _gravityDt;

        public PoseIntegratorCallbacks(GravityBox g) { _g = g; _gravityDt = default; }

        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public readonly bool AllowSubstepsForUnconstrainedBodies => false;
        public readonly bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation) { }

        public void PrepareForIntegration(float dt) => _gravityDt = Vector3Wide.Broadcast(_g.Value * dt);

        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia,
            Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            // Unity-Rigidbody ohne Dämpfung (linearDamping 0); Schwerkraft wie Physics.gravity
            velocity.Linear += _gravityDt;
        }
    }
}

/// <summary>Ein statisches Kollisionsnetz in der Physikwelt.</summary>
public sealed class StaticCollider
{
    public readonly StaticHandle Handle;
    public readonly TypedIndex Shape;
    public readonly int Triangles;
    public readonly string Name;
    public bool Removed;
    /// <summary>Strahlen (Radaufhängung, Kamera, Boden) treffen ihn nicht – Unity: World Actors auf Layer 2 „Ignore Raycast“.</summary>
    public bool IgnoreRays;
    /// <summary>Netz wird von mehreren Instanzen geteilt (nicht mit dem Kollider freigeben).</summary>
    public bool SharedShape;
    /// <summary>Untergrund je Dreieck (null = <see cref="Surface"/> für alle).</summary>
    public byte[]? Surfaces;
    public byte Surface;
    /// <summary>Netz aus <see cref="PhysicsWorld.PrepareStaticMesh"/>: eigener Pool, wird mit dem Kollider geleert.</summary>
    internal BufferPool? OwnPool;
    internal long OwnBytes;
    internal StaticCollider(StaticHandle h, TypedIndex s, int tris, string name) { Handle = h; Shape = s; Triangles = tris; Name = name; }
}