using BepuPhysics;
using Paradise.Core.Diagnostics;
using Paradise.Core.Physics;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Port von Unity WorldActorBody: World Actors als umfallende Objekte wie im Spiel (worldactors_*.dhk: dynamischer Körper an
/// einer zerbrechlichen Verankerung; Schilder/Poller 11–20 kg, Ampeln 120 kg, Bänke 3500 kg). Bis zum Anstoß steht das Teil fest
/// (Box als statischer Kollider), gelöst wird über einen etwas größeren Auslöser (Unity: Trigger-Box +1,2 m), sobald ein
/// Fahrzeug mit mindestens BreakSpeed darauf zufährt – dann wird es ein beweglicher Körper, kippt in Stoßrichtung und
/// wird nach 90 s entfernt. Bänke und große Teile (Volumen &gt; 6 m³) bleiben statisch. Radstrahlen treffen Actors nie
/// (Unity: Layer 2). Kollision je Teil als Box statt Vollnetz; hohe Teile (Ampeln mit Ausleger) nur so breit wie der Mast unten.
/// </summary>
public sealed class WorldActorBodies
{
    /// <summary>Aktive Instanz (MapStreamer), Fahrzeug meldet sich hier je Physikschritt.</summary>
    public static WorldActorBodies? Current;

    sealed class Entry
    {
        public string Sector = "";
        public string Name = "";
        public RenderObject[] Objs = Array.Empty<RenderObject>();
        public Matrix4x4[] BaseModel = Array.Empty<Matrix4x4>();
        public Vector3[] BaseMin = Array.Empty<Vector3>(), BaseMax = Array.Empty<Vector3>(), BaseCenter = Array.Empty<Vector3>();
        public Vector3 Center;          // Box-Mitte (Spielkoordinaten)
        public Quaternion Rot;          // Ausrichtung der Instanz
        public Vector3 Size;            // Box-Größe (m)
        public Vector3 Trigger;         // halbe Auslöser-Größe (lokal)
        public float Mass, BreakSpeed;
        public StaticCollider? Static;
        public BodyHandle Body;
        public bool Free, Gone;
        public long FreeMs;
        public Matrix4x4 InvPose0;      // Box-Pose zum Zeitpunkt des Lösens (invers) für die Render-Matrizen
        public readonly List<Action<Matrix4x4>> Extras = new();   // z.B. Schildertext (Unity: Kind des Schilds)
    }

    readonly PhysicsWorld _w;
    readonly List<Entry> _fixed = new();
    readonly List<Entry> _free = new();
    readonly List<Entry> _heavy = new();   // Bänke/große Teile: bleiben statisch (für die Debug-Ansicht trotzdem zeigen)
    Entry? _last;

    /// <summary>
    /// Zusatz-Geometrie an die zuletzt angemeldete (bewegliche) Instanz hängen; setModel bekommt die Bewegung seit dem Lösen
    /// (Identität = Ursprungslage, Skalierung 0 = entfernt). false = Instanz bleibt fest (Geometrie normal bündeln).
    /// </summary>
    /// <summary>Zuletzt angemeldete Instanz kann umfallen.</summary>
    public bool LastLoose => _last != null;

    public bool AttachToLast(Action<Matrix4x4> setModel)
    {
        if (_last == null) return false;
        _last.Extras.Add(setModel);
        return true;
    }

    public WorldActorBodies(PhysicsWorld w) { _w = w; }

    /// <summary>
    /// Ein Teil wurde umgefahren (Sektor/Besitzer, Actor-Name, Aufprallgeschwindigkeit m/s) – Unity RacePropHit.OnCollisionEnter
    /// (relativeVelocity): die Prüfungen geben dafür Zeitstrafen (Game/Racing).
    /// </summary>
    public static event Action<string, string, float>? Broken;

    /// <summary>Unity WorldActorBody.Physicalize für eine gebaute Instanz. Rückgabe: statischer Box-Kollider (zum Sektor).</summary>
    public StaticCollider? Register(string sector, string actorName, List<RenderObject> objs, Matrix4x4 instWorld)
    {
        _last = null;
        if (objs.Count == 0) return null;
        string n = (actorName ?? "").ToUpperInvariant();
        if (!Matrix4x4.Decompose(instWorld, out var scale, out var rot, out _)) { scale = Vector3.One; rot = Quaternion.Identity; }
        if (!Matrix4x4.Invert(instWorld, out var inv)) return null;
        // Punkte der Teile im Raum der Instanz (Unity: Renderer-Bounds in go.transform)
        var pts = new List<Vector3>();
        foreach (var ro in objs)
        {
            if (ro.Lods.Length == 0) continue;
            bool any = false;
            foreach (var p in ro.Lods[0].Parts)
            {
                if (p.Collision == null) continue;
                foreach (var v in p.Collision) pts.Add(Vector3.Transform(Vector3.Transform(v, ro.Model), inv));
                any = true;
            }
            if (!any)
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) != 0 ? ro.BoundsMax.X : ro.BoundsMin.X, (i & 2) != 0 ? ro.BoundsMax.Y : ro.BoundsMin.Y, (i & 4) != 0 ? ro.BoundsMax.Z : ro.BoundsMin.Z);
                    pts.Add(Vector3.Transform(c, inv));
                }
        }
        if (pts.Count == 0) return null;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        var sc = Vector3.Abs(scale);
        var sizeW = (mx - mn) * sc;
        // Hohe Teile: nur der Fuß (unterste 1,2 m) zählt in der Breite
        if (sizeW.Y > 2.2f)
        {
            float yMax = mn.Y + 1.2f / MathF.Max(1e-3f, sc.Y);
            var fmn = new Vector3(float.MaxValue); var fmx = new Vector3(float.MinValue); bool f = false;
            foreach (var p in pts) if (p.Y <= yMax) { fmn = Vector3.Min(fmn, p); fmx = Vector3.Max(fmx, p); f = true; }
            if (f)
            {
                float cx = (fmn.X + fmx.X) * 0.5f, cz = (fmn.Z + fmx.Z) * 0.5f;
                float hx = MathF.Max(0.12f / sc.X, fmx.X - fmn.X + 0.05f / sc.X) * 0.5f, hz = MathF.Max(0.12f / sc.Z, fmx.Z - fmn.Z + 0.05f / sc.Z) * 0.5f;
                mn.X = cx - hx; mx.X = cx + hx; mn.Z = cz - hz; mx.Z = cz + hz;
            }
        }
        var size = Vector3.Max(new Vector3(0.08f), (mx - mn) * sc);
        // Unterkante 8 cm anheben (steckt sonst im Boden und springt beim Lösen hoch)
        float lift = MathF.Min(0.08f, size.Y * 0.2f);
        var centerLocal = (mn + mx) * 0.5f;
        var center = Vector3.Transform(centerLocal, instWorld) + Vector3.UnitY * (lift * 0.5f);
        size.Y -= lift;

        var e = new Entry { Sector = sector, Name = actorName ?? "", Center = center, Rot = Quaternion.Normalize(rot), Size = size };
        var stc = _w.AddStaticMesh(BoxTriangles(center, e.Rot, size), sector + " Actor " + actorName);
        if (stc != null) stc.IgnoreRays = true;
        e.Static = stc;

        bool heavy = n.StartsWith("SF_BEN") || n.StartsWith("SF_BNC") || size.X * size.Y * size.Z > 6f;
        if (heavy) { _heavy.Add(e); return stc; }

        float vol = size.X * size.Y * size.Z;
        e.Mass = n.StartsWith("SF_XIN") ? 120f : n.StartsWith("RS_") || n.StartsWith("SF_PANOR") || n.StartsWith("SF_STUD") ? 15f : System.Math.Clamp(vol * 80f, 5f, 150f);
        e.BreakSpeed = n.StartsWith("SF_XIN") ? 4f : 1.5f;
        e.Trigger = size * 0.5f + new Vector3(0.6f, 0.1f, 0.6f);
        e.Objs = objs.ToArray();
        e.BaseModel = new Matrix4x4[objs.Count]; e.BaseMin = new Vector3[objs.Count]; e.BaseMax = new Vector3[objs.Count]; e.BaseCenter = new Vector3[objs.Count];
        for (int i = 0; i < objs.Count; i++) { e.BaseModel[i] = objs[i].Model; e.BaseMin[i] = objs[i].BoundsMin; e.BaseMax[i] = objs[i].BoundsMax; e.BaseCenter[i] = objs[i].Center; }
        _fixed.Add(e);
        _last = e;
        return stc;
    }

    static Vector3[] BoxTriangles(Vector3 c, Quaternion q, Vector3 s)
    {
        var h = s * 0.5f;
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++)
            v[i] = c + Vector3.Transform(new Vector3((i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z), q);
        int[] t = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
        var o = new Vector3[t.Length];
        for (int i = 0; i < t.Length; i++) o[i] = v[t[i]];
        return o;
    }

    /// <summary>Sektor entladen: alle Einträge (feste und gelöste) entfernen. Statische Kollider entfernt der Sektor selbst.</summary>
    public void RemoveSector(string sector)
    {
        _fixed.RemoveAll(e => e.Sector == sector);
        _heavy.RemoveAll(e => e.Sector == sector);
        for (int i = _free.Count - 1; i >= 0; i--)
            if (_free[i].Sector == sector) { _w.RemoveBody(_free[i].Body); _free.RemoveAt(i); }
    }

    /// <summary>
    /// Physikschritt: Fahrzeug (Schwerpunkt, Geschwindigkeit in Spielkoordinaten, halbe Ausdehnung der Karosserie) gegen die
    /// Auslöser prüfen (Unity: OnTriggerEnter/Stay -> TryTrigger).
    /// </summary>
    public void CheckHits(Vector3 carCom, Vector3 carVel, float carRadius)
    {
        float speed = carVel.Length();
        if (speed < 1.5f) return;
        for (int i = _fixed.Count - 1; i >= 0; i--)
        {
            var e = _fixed[i];
            var d = carCom - e.Center;
            float r = carRadius + MathF.Max(e.Trigger.X, e.Trigger.Z);
            if (d.X * d.X + d.Z * d.Z > r * r || MathF.Abs(d.Y) > carRadius + e.Trigger.Y) continue;
            if (speed < e.BreakSpeed) continue;
            // nur, wenn das Fahrzeug auf das Teil zu fährt
            var to = e.Center - carCom; to.Y = 0f;
            if (to.LengthSquared() > 1e-4f && Vector3.Dot(carVel, to) <= 0f) continue;
            // genauer: Abstand im Raum der Box (Auslöser-Box + Fahrzeugradius)
            var local = Vector3.Transform(carCom - e.Center, Quaternion.Inverse(e.Rot));
            if (MathF.Abs(local.X) > e.Trigger.X + carRadius || MathF.Abs(local.Z) > e.Trigger.Z + carRadius) continue;
            _fixed.RemoveAt(i);
            Break(e, carVel);
        }
    }

    void Break(Entry e, Vector3 hitVel)
    {
        if (e.Static != null) { _w.Remove(e.Static); e.Static = null; }
        e.Body = _w.AddLooseBox(e.Center, e.Rot, e.Size, e.Mass);
        var b = _w.Body(e.Body);
        // Teil wird mitgerissen und kippt (Drehachse quer zur Stoßrichtung)
        var h = hitVel; h.Y = 0f;
        b.Velocity.Linear = h * 0.9f + Vector3.UnitY * MathF.Min(2f, h.Length() * 0.15f);
        var dir = h.LengthSquared() > 1e-4f ? Vector3.Normalize(h) : Vector3.Transform(Vector3.UnitZ, e.Rot);
        var axis = Vector3.Cross(Vector3.UnitY, dir);
        b.Velocity.Angular = axis * System.Math.Clamp(h.Length() * 0.4f, 2f, 10f) + new Vector3(Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f);
        b.Awake = true;
        e.Free = true; e.FreeMs = Environment.TickCount64;
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(e.Rot) * Matrix4x4.CreateTranslation(e.Center), out e.InvPose0);
        _free.Add(e);
        Broken?.Invoke(e.Sector, e.Name, hitVel.Length());
    }

    /// <summary>
    /// Boxen aller geladenen World Actors in Spielkoordinaten (Mitte, Drehung, Größe, umfallbar) für die Debug-Ansicht.
    /// Feste Teile kommen aus der Registrierung, umgefahrene aus dem Physikkörper; schwere Teile sind nicht umfallbar.
    /// </summary>
    public IEnumerable<(Vector3 center, Quaternion rot, Vector3 size, bool breakable)> DebugBoxes()
    {
        foreach (var e in _fixed)
            yield return (e.Center, e.Rot, e.Size, true);
        foreach (var e in _heavy)
            yield return (e.Center, e.Rot, e.Size, false);
        foreach (var e in _free)
            if (_w.Simulation.Bodies.BodyExists(e.Body))
            {
                var pose = _w.Body(e.Body).Pose;
                yield return (_w.ToGame(pose.Position), pose.Orientation, e.Size, true);
            }
    }

    /// <summary>Je Bild: Render-Matrizen der gelösten Teile aus den Körpern übernehmen; nach 90 s entfernen.</summary>
    public void Sync()
    {
        long now = Environment.TickCount64;
        for (int i = _free.Count - 1; i >= 0; i--)
        {
            var e = _free[i];
            if (now - e.FreeMs > 90000)
            {
                _w.RemoveBody(e.Body);
                foreach (var ro in e.Objs) ro.Active = false;
                foreach (var x in e.Extras) x(Matrix4x4.CreateScale(0f));
                _free.RemoveAt(i);
                continue;
            }
            var b = _w.Body(e.Body);
            var pos = _w.ToGame(b.Pose.Position);
            var pose = Matrix4x4.CreateFromQuaternion(b.Pose.Orientation) * Matrix4x4.CreateTranslation(pos);
            var delta = e.InvPose0 * pose;
            var move = pos - e.Center;
            float pad = e.Size.Length();
            foreach (var x in e.Extras) x(delta);
            for (int k = 0; k < e.Objs.Length; k++)
            {
                var ro = e.Objs[k];
                ro.Model = e.BaseModel[k] * delta;
                ro.Center = e.BaseCenter[k] + move;
                ro.BoundsMin = e.BaseMin[k] + move - new Vector3(pad);
                ro.BoundsMax = e.BaseMax[k] + move + new Vector3(pad);
            }
        }
    }
}