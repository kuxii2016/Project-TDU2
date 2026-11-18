using Paradise.Core.Math;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Glühende Bremsscheiben wie im Spiel (Material der Scheibe, z.B. Mat_TS_Disk_03, hat den Parameter HEATCOLOR): nach langem,
    /// starkem Bremsen glühen die Scheiben erst dunkelrot, dann orange, und kühlen beim Fahren langsam ab.
    ///   Wärme je Rad: Bremsleistung (Bremsmoment × Raddrehzahl) erwärmt die Scheibe, Abkühlung wächst mit dem Fahrtwind.
    ///   Eine Vollbremsung aus ~200 km/h bringt eine Vorderscheibe auf etwa 0,5 (kaum sichtbar), mehrere hintereinander zum Glühen.
    ///   Darstellung: TDU2/Car "_Heat" (0..1) auf eigenen Materialkopien je Rad (Scheiben unter DISC_FL/FR/RL/RR, Name mit "disk"/"disc").
    /// Mitspieler: die Wärme kommt aus dem UDP-Zustand (<see cref="SetHeat"/>). Portiert aus TDU2BrakeGlow (Unity).
    /// </summary>
    public sealed class BrakeGlow
    {
        static readonly string[] WheelNames = { "FL", "FR", "RL", "RR" };
        /// <summary>Wärmemenge (J), die eine Scheibe von kalt auf 1 bringt.</summary>
        //public static float HeatCapacity = 1.6e6f;
        /// <summary>Ab dieser Wärme beginnt das Glühen.</summary>
        //public static float GlowStart = 0.35f;

        //Faster Heat simulation, but less realistic. The original values are too high for the current physics simulation.
        public static float HeatCapacity = 650000f;
        public static float GlowStart = 0.25f; //.18f

        readonly CarMaterial[]?[] _mats = new CarMaterial[4][];
        readonly float[] _shown = { -1f, -1f, -1f, -1f };
        CarModel _model = null!;
        /// <summary>Wärme je Rad (FL, FR, RL, RR), 0..1.</summary>
        public readonly float[] Heat = new float[4];
        public bool HasDiscs { get; private set; }

        BrakeGlow() { }

        /// <summary>Scheibenmaterialien des Modells suchen und je Rad kopieren; null, wenn das Auto keine hat.</summary>
        public static BrakeGlow? Create(CarModel? model, Render.IRenderer r)
        {
            if (model == null) return null;
            var g = new BrakeGlow { _model = model };
            for (int i = 0; i < 4; i++)
            {
                var disc = model.Find("DISC_" + WheelNames[i]);
                if (disc == null) continue;
                var list = new List<CarMaterial>();
                var copies = new Dictionary<CarMaterial, CarMaterial>();
                foreach (var n in disc.SelfAndDescendants())
                    foreach (var part in n.Parts)
                    {
                        var m = part.Material;
                        if (m == null || !m.IsCarShader || !IsDisc(m.Name)) continue;
                        if (!copies.TryGetValue(m, out var c))
                        {
                            c = new CarMaterial(m) { Name = m.Name + " (" + WheelNames[i] + ")" };
                            c.Apply(r);
                            copies[m] = c; list.Add(c); model.Owned.Add(c); model.AllMaterials.Add(c);
                        }
                        part.Material = c;
                    }
                if (list.Count > 0) { g._mats[i] = list.ToArray(); g.HasDiscs = true; }
            }
            return g.HasDiscs ? g : null;
        }

        static bool IsDisc(string? n)
        {
            n = (n ?? "").ToLowerInvariant();
            return (n.Contains("disk") || n.Contains("disc")) && !n.Contains("rim") && !n.Contains("tire");
        }

        /// <summary>Eigenes Auto: Wärme aus Bremsmoment und Raddrehzahl, Abkühlung über den Fahrtwind.</summary>
        public void Simulate(Vehicle? v, float dt)
        {
            if (v == null || dt <= 0f) return;
            float speed = v.SpeedKmh / 3.6f;
            float cool = 0.03f + 0.002f * Mathf.Abs(speed);
            foreach (var w in v.Wheels)
            {
                int i = Array.IndexOf(WheelNames, w.Name);
                if (i < 0) continue;
                float power = Mathf.Abs(w.BrakeTorque * w.Omega);   // W
                float h = Heat[i] + power * dt / HeatCapacity;
                h -= h * cool * dt;
                Heat[i] = Mathf.Clamp01(h);
            }
            Apply();
        }

        /// <summary>Mitspieler: Wärme aus dem Zustand (weich nachgeführt).</summary>
        public void SetHeat(int wheel, float heat, float dt)
        {
            if (wheel < 0 || wheel > 3) return;
            Heat[wheel] = Mathf.MoveTowards(Heat[wheel], Mathf.Clamp01(heat), dt * 0.5f);
        }

        /// <summary>Wärme auf die Materialien (nur bei Änderung).</summary>
        public void Apply()
        {
            for (int i = 0; i < 4; i++)
            {
                var ms = _mats[i];
                if (ms == null) continue;
                float glow = Mathf.Clamp01((Heat[i] - GlowStart) / (1f - GlowStart));
                glow = glow * glow * (3f - 2f * glow);
                if (Mathf.Abs(glow - _shown[i]) < 0.004f) continue;
                _shown[i] = glow;
                foreach (var m in ms) { m.Desc.Heat = glow; _model.ApplyMaterial(m); }
            }
        }

        /// <summary>Wärme als Byte (0..255) für den UDP-Zustand.</summary>
        public byte HeatByte(int wheel) => (byte)Mathf.RoundToInt(Mathf.Clamp01(Heat[wheel]) * 255f);
    }
}