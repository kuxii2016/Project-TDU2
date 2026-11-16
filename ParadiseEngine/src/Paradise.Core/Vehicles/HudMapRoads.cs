using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.World;
using System.Globalization;
using V3 = System.Numerics.Vector3;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Straßennetz der Minimap wie die exe (HudMap.cpp, Zeichnen 0xca1a10): je Straßentyp 0..6 (Abschnitts-Byte +0x11:
    /// 0 HIGHWAY, 1 SECONDARY_HIGHWAY, 2 ROAD, 3 OFFROAD, 4 CIRCUIT, 5 DEGRADED_ROAD, 6 TUNNEL; Tabelle 0x1282724) erst alle
    /// Abschnitte dieses Typs als Dreiecksstreifen, dann die Kreisverkehre, deren kleinster anschließender Typ dieser ist.
    ///
    /// Streifen je Abschnitt (halbe Breite w = 2 × SET_HUDMAP_ROAD_WIDTH, Meter, Spiel-XZ):
    ///   Anfang: erster Abschnitt einer Straße mit Startknoten, der kein Kreisverkehr ist → Punkt = Knotenmitte, Richtung Knoten → P0;
    ///           sonst Punkt = P0, Richtung = Bézier-Tangente bei t = 0 (0x5f5d40 / 0x5f5e90).
    ///   je Bézierstück: Punkt bei t = 0,5 (0x5f5c00, Gewichte 0,125/0,375) mit Tangente bei 0,5, dann Anker mit Tangente bei t = 1.
    ///   Ende: letzter Abschnitt mit Endknoten, der kein Kreisverkehr ist → Knotenmitte, Richtung Ende → Knoten.
    ///   Je Punkt zwei Ecken P ∓ N·w mit N = normalize(T × (0,1,0)) = normalize(−Tz, 0, Tx).
    /// Kreisverkehr (Knotenart 3, Maße aus JDET): r1 = 0,5 · Breite + Radius, r2 = r1 + Breite · w / 4, je 24 Punkte
    /// (Winkel i · 2π / 23). Erst Scheibe r1 mit Farbe 0 (Alpha 0), dann Scheibe r2 in Straßenfarbe – die innere Scheibe hält die
    /// äußere frei (Annahme: Schablone der HudMapRoads-Materialien, +0x40..+0x5c; Wirkung in der exe nicht nachgeprüft).
    /// Farbe: SET_HUDMAP_ROAD_COLOR_UNLOCKED, außer das Bit des Abschnitts in der Sperrliste ist gesetzt (0xa487d0) → _LOCKED.
    /// Die Sperrliste füllen wir noch nicht (CHECKLIST) – alle Abschnitte „unlocked“.
    /// Werte aus GamePC.cpr [HudMap]; ohne Datei die Vorgaben der exe-Daten (0x1687150 Breite 1, 0x1687174/78 Farben).
    /// </summary>
    public sealed class HudMapRoads
    {
        public const int Types = 7;

        /// <summary>Streifen: je Punkt Mitte (x,z) und Normale (x,z); Ecken = Mitte ∓ Normale · W (Xz = fertige Ecken mit W).</summary>
        public sealed class Strip
        {
            public int Type; public Color32 Color; public float W;
            public float[] Cn = System.Array.Empty<float>(), Xz = System.Array.Empty<float>();
            public float MinX, MinZ, MaxX, MaxZ;
        }
        /// <summary>Kreisverkehr: R2 = R1 + Breite · W / 4 (RingWidth = JDET-Breite).</summary>
        public sealed class Ring { public int Type; public Color32 Color; public float Cx, Cz, R1, R2, RingWidth, W; }

        public readonly float[] Width = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        public readonly Color32[] Unlocked = new Color32[8], Locked = new Color32[8];
        public readonly List<Strip> Strips = new();
        public readonly List<Ring> Rings = new();
        /// <summary>Kreispunkte der exe (0xca1f1a: 24 Punkte, Winkel i · 2π / 23).</summary>
        public static readonly float[] CircleCos = new float[24], CircleSin = new float[24];

        static HudMapRoads()
        {
            for (int i = 0; i < 24; i++)
            {
                float a = (float)(i * 6.2831854820251465 / 23.0);
                CircleCos[i] = MathF.Cos(a); CircleSin[i] = MathF.Sin(a);
            }
        }

        public HudMapRoads()
        {
            for (int i = 0; i < 8; i++)
            {
                // exe-Daten: unlocked 0xff00ff00, locked 0xff0000ff (Bytes R,G,B,A wie die Skriptbefehle sie ablegen, 0x518640)
                Unlocked[i] = i < 7 ? new Color32(0, 255, 0, 255) : new Color32(255, 0, 0, 255);
                Locked[i] = new Color32(255, 0, 0, 255);
            }
        }

        /// <summary>GamePC.cpr [HudMap]: SET_HUDMAP_ROAD_COLOR_LOCKED/UNLOCKED Typ R G B A, SET_HUDMAP_ROAD_WIDTH Typ Breite.</summary>
        public void ApplyConfig(GamePC? cfg)
        {
            if (cfg == null) return;
            foreach (var e in cfg.Entries)
            {
                bool locked = e.Key.Equals("SET_HUDMAP_ROAD_COLOR_LOCKED", StringComparison.OrdinalIgnoreCase);
                bool unlocked = e.Key.Equals("SET_HUDMAP_ROAD_COLOR_UNLOCKED", StringComparison.OrdinalIgnoreCase);
                if ((locked || unlocked) && e.Args.Length >= 5 && I(e.Args[0], out int t) && t >= 0 && t < 8
                    && I(e.Args[1], out int r) && I(e.Args[2], out int g) && I(e.Args[3], out int b) && I(e.Args[4], out int a))
                {
                    var c = new Color32((byte)r, (byte)g, (byte)b, (byte)a);
                    if (locked) Locked[t] = c; else Unlocked[t] = c;
                }
                else if (e.Key.Equals("SET_HUDMAP_ROAD_WIDTH", StringComparison.OrdinalIgnoreCase) && e.Args.Length >= 2
                         && I(e.Args[0], out int tw) && tw >= 0 && tw < 8
                         && float.TryParse(e.Args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
                    Width[tw] = w;
            }
        }

        static bool I(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        public static HudMapRoads Build(IaNetwork net, GamePC? cfg)
        {
            var h = new HudMapRoads();
            h.ApplyConfig(cfg);
            var ringType = new Dictionary<int, int>();   // Kreisverkehr-Knoten → kleinster Typ (Vorlauf 0xca2140, 0x42aea0)
            foreach (var road in net.Roads.Values)
            {
                IaNode? n0 = road.StartNode != 0 && net.Nodes.TryGetValue(road.StartNode, out var a) ? a : null;
                IaNode? n1 = road.EndNode != 0 && net.Nodes.TryGetValue(road.EndNode, out var b) ? b : null;
                for (int pi = 0; pi < road.Pieces.Count; pi++)
                {
                    var pc = road.Pieces[pi];
                    int t = pc.Type;
                    if (t >= Types || pc.Points.Length < 4) continue;
                    foreach (var n in new[] { n0, n1 })
                        if (n != null && n.Kind == 3)
                            ringType[n.Id] = ringType.TryGetValue(n.Id, out int old) ? System.Math.Min(old, t) : t;
                    var s = BuildStrip(pc, pi == 0 ? n0 : null, pi == road.Pieces.Count - 1 ? n1 : null, 2f * h.Width[t]);
                    if (s == null) continue;
                    s.Type = t;
                    s.Color = h.Unlocked[t];
                    h.Strips.Add(s);
                }
            }
            foreach (var kv in ringType)
            {
                if (!net.Junctions.TryGetValue(kv.Key, out var j) || j.RingWidth <= 0f) continue;
                int t = kv.Value;
                float w = 2f * h.Width[t];
                float r1 = 0.5f * j.RingWidth + j.RingRadius;
                h.Rings.Add(new Ring { Type = t, Color = h.Unlocked[t], Cx = j.RingCenter.X, Cz = j.RingCenter.Y, R1 = r1, R2 = r1 + j.RingWidth * w * 0.25f, RingWidth = j.RingWidth, W = w });
            }
            // Zeichenreihenfolge der exe: Typ 0..6
            h.Strips.Sort((x, y) => x.Type.CompareTo(y.Type));
            return h;
        }

        static Strip? BuildStrip(IaPiece pc, IaNode? start, IaNode? end, float w)
        {
            var P = pc.Points;
            int segs = (P.Length - 1) / 3;
            if (segs < 1) return null;
            var cn = new List<float>(4 * (2 * segs + 2));
            void Pair(V3 p, V3 t)
            {
                float nx = -t.Z, nz = t.X, l = MathF.Sqrt(nx * nx + nz * nz);
                if (l > 1e-6f) { nx /= l; nz /= l; } else { nx = 0f; nz = 0f; }
                cn.Add(p.X); cn.Add(p.Z); cn.Add(nx); cn.Add(nz);
            }
            // Anfang
            if (start != null && start.Kind != 3) Pair(start.Pos, P[0] - start.Pos);
            else Pair(P[0], Tangent(P, 0, 0f));
            for (int k = 0; k < segs; k++)
            {
                Pair(Bezier(P, k, 0.5f), Tangent(P, k, 0.5f));
                Pair(P[3 * k + 3], Tangent(P, k, 1f));
            }
            if (end != null && end.Kind != 3) Pair(end.Pos, end.Pos - P[3 * segs]);
            var s = new Strip { W = w, Cn = cn.ToArray(), MinX = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxZ = float.MinValue };
            s.Xz = Corners(s, 1f);
            for (int i = 0; i < s.Xz.Length; i += 2)
            {
                s.MinX = MathF.Min(s.MinX, s.Xz[i]); s.MaxX = MathF.Max(s.MaxX, s.Xz[i]);
                s.MinZ = MathF.Min(s.MinZ, s.Xz[i + 1]); s.MaxZ = MathF.Max(s.MaxZ, s.Xz[i + 1]);
            }
            return s;
        }

        /// <summary>Ecken des Streifens (Dreiecksstreifen: links, rechts je Punkt) mit Breitenfaktor mul (große Karte).</summary>
        public static float[] Corners(Strip s, float mul)
        {
            var cn = s.Cn;
            var r = new float[cn.Length];
            float w = s.W * mul;
            for (int i = 0; i < cn.Length; i += 4)
            {
                r[i] = cn[i] - cn[i + 2] * w; r[i + 1] = cn[i + 1] - cn[i + 3] * w;
                r[i + 2] = cn[i] + cn[i + 2] * w; r[i + 3] = cn[i + 1] + cn[i + 3] * w;
            }
            return r;
        }

        static V3 Bezier(V3[] P, int k, float t)
        {
            float u = 1f - t;
            return u * u * u * P[3 * k] + 3f * u * u * t * P[3 * k + 1] + 3f * u * t * t * P[3 * k + 2] + t * t * t * P[3 * k + 3];
        }

        /// <summary>Normierte Ableitung des Bézierstücks k (exe 0x5f5e90 → 0x431a9d, dann normalisiert).</summary>
        static V3 Tangent(V3[] P, int k, float t)
        {
            float u = 1f - t;
            V3 d = 3f * u * u * (P[3 * k + 1] - P[3 * k]) + 6f * u * t * (P[3 * k + 2] - P[3 * k + 1]) + 3f * t * t * (P[3 * k + 3] - P[3 * k + 2]);
            float l = d.Length();
            return l > 1e-6f ? d / l : V3.Zero;
        }
    }
}
