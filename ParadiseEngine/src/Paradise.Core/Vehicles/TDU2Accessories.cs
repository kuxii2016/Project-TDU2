using Paradise.Core.Math;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Zubehör eines Autos wie im Spiel – Port von Unity TDU2Accessories (Türen, Fenster, Verdeck, Blinker):
    ///   * Türen und Fenster nach den Original-Knoten der Karosserie (.3dd): DOOR_L/DOOR_R haben ihren Ursprung am Scharnier vorne
    ///     und schwenken um die Hochachse nach außen; die Türverkleidung des Interieurs (INT_HR_L/R der Cockpit-Bank) schwenkt mit.
    ///     WND_x2 ist ein leerer Platzhalter = Lage der geöffneten Scheibe WND_x1. Ohne WND_x2 fährt die Scheibe um ihre Höhe nach
    ///     unten (ältere GLASS_LEFT/RIGHT).
    ///   * Klänge aus CarVSTConfig: DoorOpen/DoorClose, WindowStart + WindowOpen/WindowClose + WindowStop.
    ///   * Zustand als Bits für die Online-Synchronisation: 1 Tür links, 2 Tür rechts, 4 Fenster links, 8 Fenster rechts, 16 Verdeck.
    /// Rechnet wie Unity im Fahrzeugraum (Elternraum des Visual-Knotens). Die Knoten bekommen ihre Lage als Matrix (RawLocal),
    /// Unity: localPosition = Pivot + q * (Pos0 - Pivot), localRotation = q * Rot0.
    /// Noch nicht portiert: Teilen von GLASS_I (Innenseite der Scheiben im Türbereich ausblenden).
    /// </summary>
    public sealed class TDU2Accessories
    {
        public const int BitDoorL = 1, BitDoorR = 2, BitWinL = 4, BitWinR = 8, BitRoof = 16;
        /// <summary>Öffnungswinkel der Türen (Grad).</summary>
        public static float DoorAngle = 62f;

        public readonly CarModel Model;
        /// <summary>Klang (Tür/Fenster); null = stumm.</summary>
        public TDU2VehicleAudio? Audio;

        sealed class Door
        {
            public CarNode T = null!; public bool Left;
            public Matrix4x4 Local0; public Vector3 PivotL, AxisL;
            public float Sign = 1f, Cur, Target;
        }
        sealed class Window
        {
            public CarNode T = null!; public bool Left;
            public Matrix4x4 Local0, Local1;
            public float Cur, Target; public bool Moving;
        }
        readonly List<Door> _doors = new();
        readonly List<Window> _windows = new();
        readonly List<CarNode> _roof = new();

        public bool HasWindows => _windows.Count > 0;
        public bool HasDoors => _doors.Count > 0;
        public bool HasRoof => _roof.Count > 0;
        /// <summary>Lichter des Autos (Blinker im Zubehörmenü) – Unity TDU2Accessories.Lights.</summary>
        public TDU2VehicleLights? Lights;
        public bool HasTurnSignals => Lights != null;
        public bool WindowsOpen { get { foreach (var w in _windows) if (w.Target > 0.5f) return true; return false; } }
        public bool DoorsOpen { get { foreach (var d in _doors) if (d.Target > 0.5f) return true; return false; } }
        public bool RoofOpen { get; private set; }

        TDU2Accessories(CarModel model)
        {
            Model = model;
            if (model?.Visual == null) return;
            foreach (var side in new[] { "L", "R" })
            {
                var door = model.Find("DOOR_" + side);
                // Türverkleidung des Interieurs (Cockpit-Bank, Lage in Fahrzeugkoordinaten) schwenkt mit der Tür
                var panel = model.Find("INT_HR_" + side);
                if (door != null && panel != null && model.CockpitRoot != null && IsChildOf(panel, model.CockpitRoot) && !IsChildOf(panel, door))
                    SetParentKeepWorld(panel, door);
                if (door != null) AddDoor(door, side == "L");
                var w1 = model.Find("WND_" + side + "1");
                var w2 = model.Find("WND_" + side + "2");
                if (w1 != null) AddWindow(w1, w2, side == "L");
            }
            if (_windows.Count == 0)
                foreach (var n in new[] { "GLASS_LEFT", "GLASS_RIGHT" })
                {
                    var t = model.Find(n);
                    if (t != null) AddWindow(t, null, n == "GLASS_LEFT");
                }
            foreach (var kv in model.Nodes)
            {
                string n = kv.Key.ToUpperInvariant();
                if (kv.Value != null && (n.Contains("HARDTOP") || n.Contains("ROOF")))
                    foreach (var c in kv.Value.SelfAndDescendants()) if (c.HasMesh && c.RendererEnabled && !_roof.Contains(c)) _roof.Add(c);
            }
        }

        static bool IsChildOf(CarNode n, CarNode p) { for (var c = n; c != null; c = c.Parent) if (c == p) return true; return false; }

        /// <summary>Unity SetParent(p, worldPositionStays: true).</summary>
        void SetParentKeepWorld(CarNode n, CarNode p)
        {
            var world = Model.LocalToVisualParent(n);
            Matrix4x4.Invert(Model.LocalToVisualParent(p), out var pInv);
            n.SetParent(p);
            n.RawLocal = world * pInv;
        }

        /// <summary>Hülle der sichtbaren Teile eines Teilbaums im Fahrzeugraum.</summary>
        bool GetBounds(CarNode t, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue); bool any = false;
            foreach (var n in t.SelfAndDescendants())
            {
                if (!n.HasMesh || !n.RendererEnabled) continue;
                var m = Model.LocalToVisualParent(n);
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) != 0 ? n.MeshMax.X : n.MeshMin.X, (i & 2) != 0 ? n.MeshMax.Y : n.MeshMin.Y, (i & 4) != 0 ? n.MeshMax.Z : n.MeshMin.Z);
                    var q = Vector3.Transform(c, m); min = Vector3.Min(min, q); max = Vector3.Max(max, q); any = true;
                }
            }
            return any;
        }

        /// <summary>Elternraum des Knotens -> Fahrzeugraum.</summary>
        Matrix4x4 ParentToRoot(CarNode t) => t.Parent != null ? Model.LocalToVisualParent(t.Parent) : Matrix4x4.Identity;

        void AddDoor(CarNode t, bool left)
        {
            if (t.Parent == null || !GetBounds(t, out var bmin, out var bmax)) return;
            var d = new Door { T = t, Left = left, Local0 = t.Local };
            var p2r = ParentToRoot(t);
            Matrix4x4.Invert(p2r, out var r2p);
            // Scharnier: Ursprung der Tür (Original), sonst Vorderkante der Tür
            float minZ = bmin.Z, maxZ = bmax.Z;
            var o = Model.LocalToVisualParent(t).Translation;
            d.PivotL = d.Local0.Translation;
            if (maxZ - o.Z > 0.35f * MathF.Max(0.1f, maxZ - minZ))
                d.PivotL = Vector3.Transform(new Vector3(o.X, o.Y, maxZ - 0.04f), r2p);
            d.AxisL = Vector3.TransformNormal(Vector3.UnitY, r2p);
            d.AxisL = d.AxisL.LengthSquared() < 0.25f ? Vector3.UnitY : Vector3.Normalize(d.AxisL);
            // Richtung: die Tür muss nach außen schwenken (Mitte der Tür entfernt sich von der Fahrzeugmitte)
            var c = (bmin + bmax) * 0.5f;
            Matrix4x4.Invert(Model.LocalToVisualParent(t), out var r2t);
            var cLocal = Vector3.Transform(c, r2t);
            float x0 = MathF.Abs(c.X);
            Pose(d, 30f);
            float x1 = MathF.Abs(Vector3.Transform(cLocal, Model.LocalToVisualParent(t)).X);
            if (x1 < x0) d.Sign = -1f;
            Pose(d, 0f);
            _doors.Add(d);
        }

        static void Pose(Door d, float angle)
        {
            var q = Quaternion.CreateFromAxisAngle(d.AxisL, angle * d.Sign * MathF.PI / 180f);
            d.T.RawLocal = d.Local0 * Matrix4x4.CreateTranslation(-d.PivotL) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(d.PivotL);
        }

        void AddWindow(CarNode t, CarNode? openDummy, bool left)
        {
            if (t.Parent == null || !GetBounds(t, out var bmin, out var bmax)) return;
            var w = new Window { T = t, Left = left, Local0 = t.Local };
            bool dummyEmpty = openDummy != null && openDummy.Parent == t.Parent;
            if (dummyEmpty) foreach (var n in openDummy!.SelfAndDescendants()) if (n.HasMesh) { dummyEmpty = false; break; }
            if (dummyEmpty)
            {
                // Lage der geöffneten Scheibe (Position/Drehung des Platzhalters, Skalierung der Scheibe)
                var dl = openDummy!.Local;
                w.Local1 = Matrix4x4.CreateScale(t.LocalScale) * Matrix4x4.CreateFromQuaternion(openDummy.LocalRotation) * Matrix4x4.CreateTranslation(dl.Translation);
                if (openDummy.RawLocal != null || t.RawLocal != null) w.Local1 = w.Local0 * Matrix4x4.CreateTranslation(dl.Translation - w.Local0.Translation);
            }
            else
            {
                // Scheibe so weit absenken, wie sie hoch ist (in die Tür)
                float drop = Mathf.Clamp((bmax.Y - bmin.Y) * 0.92f, 0.05f, 0.6f);
                Matrix4x4.Invert(ParentToRoot(t), out var r2p);
                w.Local1 = w.Local0 * Matrix4x4.CreateTranslation(Vector3.TransformNormal(-Vector3.UnitY * drop, r2p));
            }
            _windows.Add(w);
        }

        static readonly Dictionary<CarModel, TDU2Accessories> _byModel = new();

        /// <summary>Zubehör eines Modells (einmal je Modell angelegt).</summary>
        public static TDU2Accessories? For(CarModel? model)
        {
            if (model == null) return null;
            if (!_byModel.TryGetValue(model, out var a)) _byModel[model] = a = new TDU2Accessories(model);
            return a;
        }

        /// <summary>Zubehör eines Modells mit seinen Lichtern (Unity For(model, lights)).</summary>
        public static TDU2Accessories? For(CarModel? model, TDU2VehicleLights? lights)
        {
            var a = For(model);
            if (a != null && lights != null) a.Lights = lights;
            return a;
        }

        /// <summary>Blinker links/rechts umschalten (Zubehörmenü), Warnblinker aus – Unity ToggleIndicator.</summary>
        public void ToggleIndicator(bool left)
        {
            if (Lights == null) return;
            if (left) { Lights.IndicatorLeft = !Lights.IndicatorLeft; Lights.IndicatorRight = false; }
            else { Lights.IndicatorRight = !Lights.IndicatorRight; Lights.IndicatorLeft = false; }
            Lights.Hazard = false;
        }

        public static TDU2Accessories? Existing(CarModel? model) => model != null && _byModel.TryGetValue(model, out var a) ? a : null;
        public static void Forget(CarModel? model) { if (model != null) _byModel.Remove(model); }

        /// <summary>Alle angelegten Zubehörteile animieren (Händler-/Garagenautos).</summary>
        public static void UpdateAll(float dt) { foreach (var a in _byModel.Values) a.Update(dt); }

        void Play(string name) => Audio?.Play(name);

        // ------------------------------------------------------------------ Türen
        public bool DoorOpen(bool left) { foreach (var d in _doors) if (d.Left == left) return d.Target > 0.5f; return false; }
        public bool HasDoor(bool left) { foreach (var d in _doors) if (d.Left == left) return true; return false; }

        public void SetDoor(bool left, bool open)
        {
            foreach (var d in _doors)
            {
                if (d.Left != left) continue;
                float t = open ? 1f : 0f;
                if (Mathf.Approximately(d.Target, t)) continue;
                if (open && d.Cur < 0.05f) Play("DoorOpen");
                d.Target = t;
            }
        }

        public void ToggleDoor(bool left) => SetDoor(left, !DoorOpen(left));
        public void ToggleDoors() { bool open = !DoorsOpen; SetDoor(true, open); SetDoor(false, open); }

        // ------------------------------------------------------------------ Fenster
        public bool WindowOpen(bool left) { foreach (var w in _windows) if (w.Left == left) return w.Target > 0.5f; return false; }

        public void SetWindow(bool left, bool open)
        {
            foreach (var w in _windows)
            {
                if (w.Left != left) continue;
                float t = open ? 1f : 0f;
                if (Mathf.Approximately(w.Target, t)) continue;
                if (!w.Moving) { Play("WindowStart"); Play(open ? "WindowOpen" : "WindowClose"); }
                w.Target = t; w.Moving = true;
            }
        }

        public void ToggleWindow(bool left) => SetWindow(left, !WindowOpen(left));
        public void ToggleWindows() { if (!HasWindows) return; bool open = !WindowsOpen; SetWindow(true, open); SetWindow(false, open); }

        // ------------------------------------------------------------------ Verdeck
        public void ToggleRoof() => SetRoof(!RoofOpen);

        void SetRoof(bool open)
        {
            if (!HasRoof || RoofOpen == open) return;
            RoofOpen = open;
            foreach (var r in _roof) r.RendererEnabled = !RoofOpen;
        }

        // ------------------------------------------------------------------ Zustand (Online)
        public int Bits
        {
            get
            {
                int b = 0;
                if (DoorOpen(true)) b |= BitDoorL;
                if (DoorOpen(false)) b |= BitDoorR;
                if (WindowOpen(true)) b |= BitWinL;
                if (WindowOpen(false)) b |= BitWinR;
                if (RoofOpen) b |= BitRoof;
                return b;
            }
        }

        public void ApplyBits(int bits)
        {
            SetDoor(true, (bits & BitDoorL) != 0);
            SetDoor(false, (bits & BitDoorR) != 0);
            SetWindow(true, (bits & BitWinL) != 0);
            SetWindow(false, (bits & BitWinR) != 0);
            SetRoof((bits & BitRoof) != 0);
        }

        // ------------------------------------------------------------------ Jedes Bild
        /// <summary>Türen (ca. 1 s auf, 0,8 s zu) und Fenster (ca. 2 s) fahren.</summary>
        public void Update(float dt)
        {
            foreach (var d in _doors)
            {
                if (Mathf.Approximately(d.Cur, d.Target)) continue;
                bool closing = d.Target < d.Cur;
                d.Cur = Mathf.MoveTowards(d.Cur, d.Target, dt / (closing ? 0.8f : 1.0f));
                // auf: weich auslaufen; zu: zum Ende hin schneller (Tür fällt ins Schloss)
                float e = closing ? d.Cur * d.Cur * (2f - d.Cur) : 1f - (1f - d.Cur) * (1f - d.Cur);
                Pose(d, e * DoorAngle);
                if (closing && d.Cur <= 0f) Play("DoorClose");
            }
            foreach (var w in _windows)
            {
                if (!w.Moving) continue;
                w.Cur = Mathf.MoveTowards(w.Cur, w.Target, dt / 2f);
                float e = Mathf.SmoothStep(0f, 1f, w.Cur);
                w.T.RawLocal = Lerp(w.Local0, w.Local1, e);
                if (Mathf.Approximately(w.Cur, w.Target)) { w.Moving = false; Play("WindowStop"); }
            }
        }

        /// <summary>Unity Vector3.Lerp(Pos0, Pos1) + Quaternion.Slerp(Rot0, Rot1) – über die Matrizen.</summary>
        static Matrix4x4 Lerp(Matrix4x4 a, Matrix4x4 b, float t)
        {
            if (Matrix4x4.Decompose(a, out var sa, out var ra, out var pa) && Matrix4x4.Decompose(b, out var sb, out var rb, out var pb)
                && a.GetDeterminant() * b.GetDeterminant() > 0f && a.GetDeterminant() > 0f)
                return Matrix4x4.CreateScale(Vector3.Lerp(sa, sb, t)) * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra, rb, t)) * Matrix4x4.CreateTranslation(Vector3.Lerp(pa, pb, t));
            return Matrix4x4.Lerp(a, b, t);
        }

        /// <summary>Türen und Fenster zufahren (mit Klang).</summary>
        public void CloseAll()
        {
            SetDoor(true, false); SetDoor(false, false);
            SetWindow(true, false); SetWindow(false, false);
        }

        /// <summary>Alles sofort wieder zu (Verdeck, Türen, Fenster).</summary>
        public void Reset()
        {
            foreach (var d in _doors) { d.Cur = d.Target = 0f; Pose(d, 0f); }
            foreach (var w in _windows) { w.Cur = w.Target = 0f; w.Moving = false; w.T.RawLocal = w.Local0; }
            SetRoof(false);
        }
    }
}
