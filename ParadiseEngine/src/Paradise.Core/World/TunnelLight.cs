using Paradise.Core.Formats;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Tunnelfaktor des Spielerautos wie im Original (unpacked.exe, GS_CarDisplay):
///
/// 0x75c470 – Abstand zum nächsten Tunnelende: Ausgehend von der Straßenposition des Autos läuft ein Iterator über die
///   Abschnitte derselben Straße vorwärts (0x5ee410 mit 1) und rückwärts (mit 0), solange der Abschnitt ein Tunnel ist
///   (0x5ee6e0: Straßenart [Abschnitt+0x11] == 6, siehe <see cref="IaPiece.Type"/>). Ergebnis = kleinerer der beiden
///   Abstände entlang der Straße (0x449ef4); steht das Auto auf keinem Tunnelabschnitt, 0.
/// 0x718140 – je Bild, nur für das Spielerauto und nur, wenn die Kamera höchstens 100 m entfernt ist (Abstand² &lt; 10000,
///   0x148bf44): Ziel = clamp((Abstand − 10) / 10, 0, 1) (0x1660870 = 10, 0x1660874 = 10) nach [+0x2ad4];
///   geglättet [+0x2ad0] = Ziel · (1 − k) + alt · k, k = 0,3^(dt / (1/30)) (0x148cfe0, 0x14abad0).
/// 0x744990 / 0x7148e0 lesen [+0x2ad0]: Autolicht zu tunnel_light_* überblenden (World/CarLighting).
///
/// Die Straßenposition des Autos kommt im Original aus dem Navigationssystem (0x189e0b0). Hier: nächster Punkt auf den
/// Tunnelabschnitten innerhalb der halben Straßenbreite des Abschnitts (IaProfile.TotalWidth) und 5 m Höhe.
/// </summary>
public static class TunnelLight
{
    /// <summary>Geglätteter Tunnelfaktor (0 = draußen, 1 = mindestens 20 m im Tunnel).</summary>
    public static float Factor { get; private set; }
    /// <summary>Ungeglättetes Ziel (Original +0x2ad4).</summary>
    public static float Target { get; private set; }
    /// <summary>Abstand zum nächsten Tunnelende entlang der Straße (0 = nicht im Tunnel).</summary>
    public static float Distance { get; private set; }

    sealed class Run
    {
        public Vector3[] P = System.Array.Empty<Vector3>();
        public float[] S = System.Array.Empty<float>();
        public float[] HalfWidth = System.Array.Empty<float>();
        public float Len;
        public Vector3 Min, Max;
    }

    static IaNetwork? _net;
    static readonly List<Run> _runs = new();

    const float FullAt = 10f, Ramp = 10f;          // 0x1660870 / 0x1660874
    const float CameraRange2 = 10000f;              // 0x148bf44
    const float SmoothBase = 0.3f, SmoothStep = 1f / 30f;   // 0x148cfe0 / 0x14abad0
    const float HeightTolerance = 5f;

    public static void Reset() { Factor = 0f; Target = 0f; Distance = 0f; }

    public static void Update(float dt, IaNetwork? net, Vector3 carGame, Vector3 cameraGame)
    {
        if (!ReferenceEquals(net, _net)) Build(net);
        if (Vector3.DistanceSquared(carGame, cameraGame) < CameraRange2)
        {
            Distance = DistanceToTunnelEnd(carGame);
            Target = System.Math.Clamp((Distance - FullAt) / Ramp, 0f, 1f);
        }
        float k = MathF.Pow(SmoothBase, dt / SmoothStep);
        Factor = Target * (1f - k) + Factor * k;
    }

    static void Build(IaNetwork? net)
    {
        _net = net;
        _runs.Clear();
        if (net == null) return;
        foreach (var road in net.Roads.Values)
        {
            List<Vector3>? pts = null; List<float>? hw = null;
            foreach (var pc in road.Pieces)
            {
                if (!pc.IsTunnel) { Flush(); continue; }
                pts ??= new List<Vector3>(); hw ??= new List<float>();
                float half = net.Profiles.TryGetValue(pc.ProfileId, out var prof) && prof.TotalWidth > 0f ? prof.TotalWidth * 0.5f : 6f;
                var p = pc.Points;
                for (int k = 0; k + 3 < p.Length; k += 3)
                {
                    Vector3 a = p[k], c1 = p[k + 1], c2 = p[k + 2], b = p[k + 3];
                    float est = (c1 - a).Length() + (c2 - c1).Length() + (b - c2).Length();
                    int n = System.Math.Clamp((int)MathF.Ceiling(est / 2f), 1, 256);
                    for (int i = pts.Count == 0 ? 0 : 1; i <= n; i++)
                    {
                        float t = i / (float)n, u = 1f - t;
                        pts.Add(u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b);
                        hw.Add(half);
                    }
                }
            }
            Flush();

            void Flush()
            {
                if (pts != null && pts.Count >= 2)
                {
                    var r = new Run { P = pts.ToArray(), HalfWidth = hw!.ToArray(), S = new float[pts.Count] };
                    r.Min = r.Max = r.P[0];
                    for (int i = 1; i < r.P.Length; i++)
                    {
                        r.S[i] = r.S[i - 1] + Vector3.Distance(r.P[i - 1], r.P[i]);
                        r.Min = Vector3.Min(r.Min, r.P[i]); r.Max = Vector3.Max(r.Max, r.P[i]);
                    }
                    r.Len = r.S[^1];
                    _runs.Add(r);
                }
                pts = null; hw = null;
            }
        }
    }

    /// <summary>min(Abstand zum Tunnelanfang, Abstand zum Tunnelende) entlang der Straße, 0 = nicht auf einem Tunnelabschnitt.</summary>
    static float DistanceToTunnelEnd(Vector3 c)
    {
        float best = float.MaxValue, result = 0f;
        foreach (var r in _runs)
        {
            float m = 20f;
            if (c.X < r.Min.X - m || c.X > r.Max.X + m || c.Z < r.Min.Z - m || c.Z > r.Max.Z + m) continue;
            for (int i = 0; i + 1 < r.P.Length; i++)
            {
                Vector2 a = new(r.P[i].X, r.P[i].Z), b = new(r.P[i + 1].X, r.P[i + 1].Z), p = new(c.X, c.Z);
                Vector2 ab = b - a;
                float l2 = ab.LengthSquared();
                float t = l2 > 1e-6f ? System.Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
                float lateral = Vector2.Distance(p, a + ab * t);
                float y = r.P[i].Y + (r.P[i + 1].Y - r.P[i].Y) * t;
                float half = r.HalfWidth[i];
                if (lateral > half || MathF.Abs(c.Y - y) > HeightTolerance || lateral >= best) continue;
                best = lateral;
                float s = r.S[i] + (r.S[i + 1] - r.S[i]) * t;
                result = MathF.Min(s, r.Len - s);
            }
        }
        return result;
    }
}
