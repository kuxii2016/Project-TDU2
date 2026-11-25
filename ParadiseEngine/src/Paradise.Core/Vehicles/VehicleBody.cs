using BepuPhysics;
using Paradise.Core.Math;
using Paradise.Core.Physics;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Unity-Hilfen für die 1:1 portierte Fahrzeuglogik (Quaternion * Vector3, AngleAxis in Grad, LookRotation, ProjectOnPlane …).
    /// Mathematisch identisch zu UnityEngine (Hamilton-Produkt, gleiche Achsformeln).
    /// </summary>
    public static class U
    {
        public static Vector3 Rot(Quaternion q, Vector3 v) => Vector3.Transform(v, q);
        public static Quaternion AngleAxis(float deg, Vector3 axis)
        {
            if (axis.LengthSquared() < 1e-12f) return Quaternion.Identity;
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), deg * Mathf.Deg2Rad);
        }
        /// <summary>Unity Quaternion.Euler: erst Z, dann X, dann Y.</summary>
        public static Quaternion Euler(float x, float y, float z) => AngleAxis(y, Vec.Up) * AngleAxis(x, Vec.Right) * AngleAxis(z, Vec.Forward);
        /// <summary>Unity Quaternion.LookRotation: +Z -> forward, +Y möglichst nach up.</summary>
        public static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            if (forward.LengthSquared() < 1e-12f) return Quaternion.Identity;
            var z = Vector3.Normalize(forward);
            var x = Vector3.Cross(up, z);
            if (x.LengthSquared() < 1e-12f) x = Vector3.Cross(System.MathF.Abs(z.Y) < 0.9f ? Vec.Up : Vec.Forward, z);
            x = Vector3.Normalize(x);
            var y = Vector3.Cross(z, x);
            var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        }
        public static Vector3 ProjectOnPlane(Vector3 v, Vector3 n)
        {
            float d = Vector3.Dot(n, n);
            return d < 1e-12f ? v : v - n * (Vector3.Dot(v, n) / d);
        }
        public static float Angle(Vector3 a, Vector3 b)
        {
            float d = System.MathF.Sqrt(a.LengthSquared() * b.LengthSquared());
            if (d < 1e-15f) return 0f;
            return System.MathF.Acos(Mathf.Clamp(Vector3.Dot(a, b) / d, -1f, 1f)) * Mathf.Rad2Deg;
        }
        public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
        {
            float a = Angle(from, to);
            return Vector3.Dot(axis, Vector3.Cross(from, to)) < 0f ? -a : a;
        }
        public static Vector3 Normalized(Vector3 v) { float l = v.Length(); return l > 1e-5f ? v / l : Vector3.Zero; }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => Vector3.Lerp(a, b, Mathf.Clamp01(t));
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t) => Quaternion.Slerp(a, b, Mathf.Clamp01(t));

        /// <summary>Spiegelung Unity-Raum &lt;-&gt; Spielraum (+X = links): x -&gt; -x.</summary>
        public static Vector3 Mirror(Vector3 v) => new Vector3(-v.X, v.Y, v.Z);
        /// <summary>Drehung gespiegelt (S R S) bzw. Pseudovektor (Winkelgeschwindigkeit, Drehmoment).</summary>
        public static Quaternion Mirror(Quaternion q) => new Quaternion(q.X, -q.Y, -q.Z, q.W);
        public static Vector3 MirrorAxial(Vector3 v) => new Vector3(v.X, -v.Y, -v.Z);
    }

    public enum ForceMode { Force, Acceleration, Impulse, VelocityChange }

    /// <summary>Form des Karosserie-Kolliders im Physikkörper.</summary>
    public enum BodyColliderMode
    {
        /// <summary>Eine Box – entspricht dem BoxCollider des Unity-Originals.</summary>
        Simple,
        /// <summary>Drei Boxen: Kabine in der Mitte (volle Höhe), flachere Kästen für Haube und Heck.</summary>
        Extended,
    }

    /// <summary>
    /// Ersatz für UnityEngine.Rigidbody (+ BoxCollider) des Fahrzeugs auf einem Bepu-Körper. Die API arbeitet wie in Unity im
    /// Unity-Raum (+X = rechts) und relativ zum Physik-Ursprung (Floating Origin, PhysicsWorld.Origin); die Physikwelt liegt in
    /// Spielkoordinaten (+X = links) -> an der Grenze wird gespiegelt. Unity-lokal = Mirror(Spiel - Origin).
    /// Kräfte werden wie in Unity bis zum nächsten Physikschritt gesammelt (<see cref="ApplyForces"/>).
    /// </summary>
    public sealed class VehicleRigidbody : IDisposable
    {
        readonly PhysicsWorld _w;
        BodyHandle _h;
        bool _created;
        Vector3 _force, _torque;

        public float mass = 1f;
        /// <summary>Schwerpunkt im Fahrzeugraum (Unity, relativ zum Fahrzeugursprung).</summary>
        public Vector3 centerOfMass;
        public Vector3 inertiaTensor = Vector3.One;
        public float angularDamping;
        public float maxAngularVelocity = 7f;
        public bool isKinematic;

        public VehicleRigidbody(PhysicsWorld world) { _w = world; }

        public BodyHandle Handle => _h;
        public bool Created => _created;
        public PhysicsWorld World => _w;
        /// <summary>Karosserieform neuer Fahrzeuge – gesetzt aus Game/Config/VehicleConfig.cs.</summary>
        public static BodyColliderMode BodyCollider = BodyColliderMode.Simple;
        /// <summary>Alle Karosserie-Kästen im Fahrzeugraum (Unity) – für die Debug-Ansicht der Kollisionen.</summary>
        public IReadOnlyList<(Vector3 center, Vector3 size)> DebugBoxes => _debugBoxes;
        readonly List<(Vector3 center, Vector3 size)> _debugBoxes = new();
        /// <summary>Hauptkasten (für bestehende Nutzer der alten Debug-Eigenschaften).</summary>
        public Vector3 DebugBoxCenter => _debugBoxes.Count > 0 ? _debugBoxes[0].center : Vector3.Zero;
        public Vector3 DebugBoxSize => _debugBoxes.Count > 0 ? _debugBoxes[0].size : Vector3.Zero;

        /// <summary>
        /// Körper anlegen (nach dem Setzen von mass/centerOfMass/inertiaTensor). Maße im Fahrzeugraum (Unity).
        /// Extended: Kabine in der Mitte in voller Höhe, dazu flachere Kästen für Haube und Heck. Alle Kästen enden
        /// unten auf derselben Höhe wie bei Simple, damit die Karosserie nicht unter die Räder greift.
        /// </summary>
        public void Create(Vector3 position, Quaternion rotation, Vector3 boxCenter, Vector3 boxSize, bool extendedAllowed = true)
        {
            if (_created) _w.RemoveBody(_h);
            _debugBoxes.Clear();

            List<(Vector3 center, Vector3 size)> parts;
            if (BodyCollider == BodyColliderMode.Extended && extendedAllowed)
            {
                float w = boxSize.X, h = boxSize.Y, l = boxSize.Z;
                parts = new()
                {
                    (boxCenter, new Vector3(w, h, l * 0.52f)),
                    (boxCenter + new Vector3(0f, -h * 0.22f, l * 0.37f), new Vector3(w * 0.9f, h * 0.56f, l * 0.26f)),
                    (boxCenter + new Vector3(0f, -h * 0.22f, -l * 0.37f), new Vector3(w * 0.9f, h * 0.56f, l * 0.26f)),
                };
            }
            else parts = new() { (boxCenter, boxSize) };

            foreach (var p in parts) _debugBoxes.Add(p);
            var comW = position + U.Rot(rotation, centerOfMass);
            var local = new (Vector3 offset, Vector3 size)[parts.Count];
            for (int i = 0; i < parts.Count; i++)
                // Nur der Offset wird gespiegelt; die Kantenlängen bleiben positiv.
                local[i] = (U.Mirror(parts[i].center - centerOfMass), parts[i].size);
            _h = _w.AddDynamicBoxes(U.Mirror(comW), U.Mirror(rotation), local, mass, inertiaTensor);
            _created = true;
        }

        BodyReference B => _w.Body(_h);

        public Quaternion rotation
        {
            get => _created ? U.Mirror(B.Pose.Orientation) : Quaternion.Identity;
            set { if (!_created) return; var p = position; var b = B; b.Pose.Orientation = U.Mirror(Quaternion.Normalize(value)); b.Pose.Position = U.Mirror(p + U.Rot(value, centerOfMass)); }
        }

        /// <summary>Fahrzeugursprung (Unity: rb.position / transform.position).</summary>
        public Vector3 position
        {
            get => _created ? U.Mirror(B.Pose.Position) - U.Rot(rotation, centerOfMass) : Vector3.Zero;
            set { if (!_created) return; var b = B; b.Pose.Position = U.Mirror(value + U.Rot(rotation, centerOfMass)); }
        }

        public Vector3 worldCenterOfMass => _created ? U.Mirror(B.Pose.Position) : Vector3.Zero;

        public Vector3 linearVelocity
        {
            get => _created ? U.Mirror(B.Velocity.Linear) : Vector3.Zero;
            set { if (_created) { var b = B; b.Velocity.Linear = U.Mirror(value); } }
        }

        public Vector3 angularVelocity
        {
            get => _created ? U.MirrorAxial(B.Velocity.Angular) : Vector3.Zero;
            set { if (_created) { var b = B; b.Velocity.Angular = U.MirrorAxial(value); } }
        }

        public void AddForce(Vector3 f, ForceMode mode = ForceMode.Force)
        {
            if (mode == ForceMode.Acceleration) f *= mass;
            _force += f;
        }

        public void AddForceAtPosition(Vector3 f, Vector3 p)
        {
            _force += f;
            _torque += Vector3.Cross(p - worldCenterOfMass, f);
        }

        public void AddTorque(Vector3 t, ForceMode mode = ForceMode.Force)
        {
            if (mode == ForceMode.Acceleration)
            {
                // Winkelbeschleunigung -> Drehmoment über den Trägheitstensor (lokal)
                var r = rotation;
                var l = U.Rot(Quaternion.Inverse(r), t) * inertiaTensor;
                t = U.Rot(r, l);
            }
            _torque += t;
        }

        public Vector3 GetPointVelocity(Vector3 p) => linearVelocity + Vector3.Cross(angularVelocity, p - worldCenterOfMass);

        /// <summary>Gesammelte Kräfte als Impuls für den Schritt dt anwenden, Winkeldämpfung wie Unity (angularDrag).</summary>
        public void ApplyForces(float dt)
        {
            if (!_created) { _force = _torque = Vector3.Zero; return; }
            var b = B;
            b.Awake = true;
            if (isKinematic) { b.Velocity.Linear = Vector3.Zero; b.Velocity.Angular = Vector3.Zero; _force = _torque = Vector3.Zero; return; }
            b.ApplyLinearImpulse(U.Mirror(_force * dt));
            b.ApplyAngularImpulse(U.MirrorAxial(_torque * dt));
            var av = b.Velocity.Angular;
            if (angularDamping > 0f) av *= 1f / (1f + dt * angularDamping);
            float l = av.Length();
            if (l > maxAngularVelocity) av *= maxAngularVelocity / l;
            b.Velocity.Angular = av;
            _force = _torque = Vector3.Zero;
        }

        /// <summary>Unity Physics.Raycast im Unity-Raum (eigener Körper ausgenommen).</summary>
        public bool Raycast(Vector3 origin, Vector3 dir, out VehicleRayHit hit, float maxDistance)
        {
            hit = default;
            if (!_w.RaycastLocal(U.Mirror(origin), U.Mirror(dir), maxDistance, out var h, _created ? _h : null)) return false;
            hit.point = U.Mirror(h.Point);
            hit.normal = U.Mirror(h.Normal);
            hit.distance = h.Distance;
            hit.collider = _w.StaticOf(h.Collidable);
            hit.surface = _w.SurfaceOf(h);
            return true;
        }

        public void Dispose()
        {
            if (_created) _w.RemoveBody(_h);
            _created = false;
        }
    }

    /// <summary>Unity RaycastHit (Unity-Raum).</summary>
    public struct VehicleRayHit
    {
        public Vector3 point, normal;
        public float distance;
        public StaticCollider? collider;
        /// <summary>Untergrund des getroffenen Dreiecks (0 Asphalt, 1 Gras, 2 Erde), -1 = unbekannt.</summary>
        public int surface;
    }

    /// <summary>Lage des Fahrzeugs (Unity: Transform des Auto-GameObjects) im Unity-Raum.</summary>
    public struct VehiclePose
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 forward => U.Rot(rotation, Vec.Forward);
        public Vector3 up => U.Rot(rotation, Vec.Up);
        public Vector3 right => U.Rot(rotation, Vec.Right);
        public Vector3 TransformPoint(Vector3 p) => position + U.Rot(rotation, p);
        public Vector3 TransformDirection(Vector3 d) => U.Rot(rotation, d);
        public Vector3 InverseTransformPoint(Vector3 p) => U.Rot(Quaternion.Inverse(rotation), p - position);
        public Vector3 InverseTransformDirection(Vector3 d) => U.Rot(Quaternion.Inverse(rotation), d);
    }
}