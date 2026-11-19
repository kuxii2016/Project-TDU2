// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceRoadPath.cs).
using Paradise.Core.Diagnostics;
using Paradise.Core.Math;
using Paradise.Core.Racing;
using Paradise.Game.Config;
using Paradise.Game.Gameplay;
using System.Numerics;

namespace Paradise.Game.Racing;

/// <summary>
/// Route einer Prüfung entlang der Straßen (Verkehrsnetz _ROAD_MAP/_ROAD_DETAIL.IA, <see cref="TrafficLanes"/>):
/// zwischen zwei Wegpunkten wird über die Spuren der kürzeste Weg gesucht (Dijkstra/A*, Start in beide Richtungen,
/// Ziel = erste Spur, die am Wegpunkt vorbeiführt). Ergebnis: Polylinie in Inselkoordinaten.
/// Wird für Minimap-Linie, Fahrbahnmarkierung (Trajectory), Startrichtung und die Gegner benutzt.
/// Ohne Verkehrsnetz oder ohne Treffer: gerade Verbindung.
/// </summary>
public static class RaceRoadPath
{
    /// <summary>Verkehrsnetz der Insel (Unity FindLanes: TDU2Traffic.Lanes) – gesetzt von der laufenden Prüfung.</summary>
    public static TrafficLanes? Lanes;

    public static TrafficLanes? FindLanes() => Lanes;

    /// <summary>Straßenroute durch alle Wegpunkte. seg[i] = Index des Wegpunkt-Abschnitts (0 = erster -> zweiter).</summary>
    public static List<Vector3> Route(TrafficLanes? lanes, IList<Vector3>? pts, out List<int> seg, out int roadLegs)
    {
        var res = new List<Vector3>(); seg = new List<int>(); roadLegs = 0;
        if (pts == null || pts.Count == 0) return res;
        res.Add(pts[0]); seg.Add(0);
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var leg = lanes != null ? Leg(lanes, pts[i], pts[i + 1]) : null;
            if (leg != null && leg.Count > 1) roadLegs++;
            else leg = new List<Vector3> { pts[i], pts[i + 1] };
            for (int k = 1; k < leg.Count; k++) { res.Add(leg[k]); seg.Add(i); }
        }
        return res;
    }

    sealed class Node
    {
        public LaneTrack Lane = null!; public float SIn; public float G; public Node? Prev;
    }

    /// <summary>Weg a -> b über die Spuren (null = keiner gefunden oder Umweg &gt; 4x Luftlinie).</summary>
    static List<Vector3>? Leg(TrafficLanes lanes, Vector3 a, Vector3 b)
    {
        float direct = Flat(b - a).Length();
        if (direct < 3f) return new List<Vector3> { a, b };

        // Startspuren: alle Spuren (Spur 0 je Richtung) nahe a, ab dem nächsten Punkt
        var open = new List<Node>();
        var best = new Dictionary<LaneTrack, float>();
        foreach (var t in LanesNear(lanes, a, RaceConfig.RoadSnapRadius))
        {
            float s = NearestS(t, a, out float d);
            if (d > RaceConfig.RoadSnapRadius) continue;
            open.Add(new Node { Lane = t, SIn = s, G = d });
        }
        if (open.Count == 0) return null;

        var succ = new List<(int road, bool forward)>();
        Node? goal = null; float goalS = 0f, goalCost = float.MaxValue;
        int expand = 0;
        while (open.Count > 0 && expand++ < RaceConfig.RoadMaxExpand)
        {
            // kleinstes f = g + h; h = Luftlinie von der aktuellen Position auf der Spur zum Ziel.
            // Nicht vom Spurenende messen: sonst ist h keine zulässige untere Schranke und die
            // Suche bevorzugt Ecken abkürzende Spuren (gleiches Prinzip wie in Gps.ShortestPath).
            int bi = 0; float bf = float.MaxValue;
            for (int i = 0; i < open.Count; i++)
            {
                var o = open[i];
                o.Lane.Sample(o.SIn, out var at, out _);
                float f = o.G + Flat(b - at).Length();
                if (f < bf) { bf = f; bi = i; }
            }
            var cur = open[bi]; open.RemoveAt(bi);
            if (cur.G >= goalCost) break;
            if (best.TryGetValue(cur.Lane, out float bg) && bg <= cur.G) continue;
            best[cur.Lane] = cur.G;

            // Ziel auf dieser Spur (hinter dem Einstieg)?
            float gs = NearestS(cur.Lane, b, out float gd, cur.SIn);
            if (gd <= RaceConfig.RoadGoalRadius)
            {
                float cost = cur.G + LegCost(cur.Lane, cur.SIn, gs) + gd;
                if (cost < goalCost) { goalCost = cost; goal = cur; goalS = gs; }
                continue;
            }
            float g2 = cur.G + LegCost(cur.Lane, cur.SIn, cur.Lane.Length);
            if (g2 > direct * 6f + 300f) continue;
            lanes.Successors(cur.Lane, succ);
            foreach (var (road, fwd) in succ)
            {
                var nl = lanes.NextLane(cur.Lane, road, fwd);
                if (nl == null) continue;
                var j = lanes.Junction(cur.Lane, nl);
                float jl = j != null ? LegCost(j, 0f, j.Length) : 0f;
                open.Add(new Node { Lane = nl, SIn = 0f, G = g2 + jl, Prev = cur });
            }
        }
        if (goal == null) return null;

        // Polylinie zusammensetzen
        var chain = new List<Node>();
        for (var n = goal; n != null; n = n.Prev) chain.Add(n);
        chain.Reverse();
        var pts = new List<Vector3> { a };
        for (int i = 0; i < chain.Count; i++)
        {
            var n = chain[i];
            float sEnd = i == chain.Count - 1 ? goalS : n.Lane.Length;
            AddTrack(pts, n.Lane, n.SIn, sEnd);
            if (i + 1 < chain.Count)
            {
                var j = lanes.Junction(n.Lane, chain[i + 1].Lane);
                if (j != null) AddTrack(pts, j, 0f, j.Length);
            }
        }
        pts.Add(b);
        float len = 0f;
        for (int i = 1; i < pts.Count; i++) len += Flat(pts[i] - pts[i - 1]).Length();
        if (len > direct * 6f + 300f) return null;  // unplausibler Umweg (z.B. Tore abseits der Straße)
        return RaceConfig.RoadSmooth ? Smooth(pts) : pts;
    }

    /// <summary>
    /// Fügt den Abschnitt s0..s1 exakt hinzu (Interpolation an den Enden, rückwärts wenn s1 &lt; s0).
    /// Entspricht Gps.AppendSub: werden nur vorhandene Stützpunkte übernommen, schneidet die Linie an
    /// Ein-/Austritten und Kreuzungen gerade ab – genau das lässt den Race-Pfad nicht der Straße folgen.
    /// </summary>
    static void AddTrack(List<Vector3> pts, TrafficTrack? t, float s0, float s1)
    {
        if (t == null || t.P == null || t.P.Length < 2) return;
        float lo = Mathf.Clamp(s0 < s1 ? s0 : s1, 0f, t.Length);
        float hi = Mathf.Clamp(s0 < s1 ? s1 : s0, 0f, t.Length);
        bool back = s1 < s0;

        void Push(Vector3 v)
        {
            if (pts.Count == 0 || (pts[^1] - v).LengthSquared() > 0.25f) pts.Add(v);
        }

        t.Sample(back ? hi : lo, out var first, out _);
        Push(first);
        if (!back)
        {
            for (int i = 0; i < t.P.Length; i++)
                if (t.S[i] > lo + 0.01f && t.S[i] < hi - 0.01f) Push(t.P[i]);
        }
        else
        {
            for (int i = t.P.Length - 1; i >= 0; i--)
                if (t.S[i] < hi - 0.01f && t.S[i] > lo + 0.01f) Push(t.P[i]);
        }
        t.Sample(back ? lo : hi, out var last, out _);
        Push(last);
    }

    static IEnumerable<LaneTrack> LanesNear(TrafficLanes lanes, Vector3 p, float r)
    {
        foreach (var rid in lanes.RoadsNear(p, r + 40f))
        {
            if (!lanes.Net.Roads.TryGetValue(rid, out var road)) continue;
            for (int f = 0; f < 2; f++)
            {
                bool fwd = f == 0;
                int count = lanes.LaneCount(road, fwd);
                for (int lane = 0; lane < count; lane++)
                {
                    var t = lanes.Lane(rid, fwd, lane);
                    if (t != null) yield return t;
                }
            }
        }
    }

    static float NearestS(TrafficTrack t, Vector3 p, out float dist, float minS = 0f)
    {
        dist = float.MaxValue; float bs = 0f;
        for (int i = 0; i + 1 < t.P.Length; i++)
        {
            if (t.S[i + 1] < minS) continue;
            var a = t.P[i]; var b = t.P[i + 1];
            var ab = Flat(b - a); float l2 = ab.LengthSquared();
            float u = l2 > 1e-6f ? Mathf.Clamp01(Vector3.Dot(Flat(p - a), ab) / l2) : 0f;
            var q = a + (b - a) * u;
            float d = Flat(p - q).Length();
            float s = t.S[i] + (t.S[i + 1] - t.S[i]) * u;
            if (s < minS) continue;
            if (d < dist) { dist = d; bs = s; }
        }
        return bs;
    }

    static Vector3 Flat(Vector3 v) { v.Y = 0f; return v; }

    /// <summary>
    /// Kosten eines Spurabschnitts in "äquivalenten Metern": enge/langsame Kurven kosten mehr als
    /// schnelle Geraden. Die Suche wählt damit den SCHNELLSTEN Weg (optimale Rennlinie), nicht nur
    /// den kürzesten. Das Gewicht ist immer &gt;= 1, daher bleibt die euklidische Heuristik zulässig.
    /// </summary>
    static float LegCost(TrafficTrack t, float s0, float s1)
    {
        if (!RaceConfig.RoadPreferFast || s1 <= s0 + 1e-3f) return s1 - s0;
        float cost = 0f;
        for (float s = s0; s < s1;)
        {
            float e = Mathf.Min(s1, s + 10f);
            cost += (e - s) * CostPerMeter(t.LimitAt((s + e) * 0.5f));
            s = e;
        }
        return cost;
    }

    static float CostPerMeter(float speed)
    {
        const float Ref = 110f;       // m/s, über jedem Straßenlimit -> Gewicht >= 1
        const float Strength = 0.45f; // 0 = reine Länge, 1 = volle Zeitgewichtung
        float v = Mathf.Clamp(speed, 2.5f, Ref);
        return 1f + (Ref / v - 1f) * Strength;
    }

    /// <summary>
    /// Glättet kleine Kinks der Straßenpolylinie (Spurwechsel, Kreuzungsübergänge), ohne große Bögen
    /// abzuschneiden: gleichmäßige Abtastung nach Bogenlänge, dann quadratische Bézier NUR an Ecken
    /// über RoadSmoothMinAngle. Die Abweichung bleibt &lt;= RoadSmoothCut und damit auf der Fahrbahn –
    /// eine Catmull-Rom-Kurve durch alle Punkte würde dagegen wieder quer über die Straße schneiden.
    /// </summary>
    static List<Vector3> Smooth(List<Vector3> src)
    {
        int n = src.Count;
        if (n < 4) return src;
        var cum = new float[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Flat(src[i] - src[i - 1]).Length();
        float total = cum[n - 1];
        float step = Mathf.Max(0.5f, RaceConfig.RoadSmoothStep);
        if (total < step * 2f) return src;

        // 1) gleichmäßig abtasten: die neuen Punkte liegen auf der ursprünglichen Straßenpolylinie
        var uni = new List<Vector3>(n + 8) { src[0] };
        int cursor = 1;
        for (float s = step; s < total; s += step)
        {
            int i = cursor;
            while (i < n - 1 && cum[i] < s) i++;
            cursor = i;
            float seg = Mathf.Max(1e-5f, cum[i] - cum[i - 1]);
            uni.Add(Vector3.Lerp(src[i - 1], src[i], (s - cum[i - 1]) / seg));
        }
        uni.Add(src[^1]);

        // 2) nur scharfe Ecken abrunden, gerade/curvige Abschnitte unverändert lassen
        float cutMax = Mathf.Min(RaceConfig.RoadSmoothCut, step * 0.4f);
        float cosMin = Mathf.Cos(RaceConfig.RoadSmoothMinAngle * Mathf.PI / 180f);
        int sub = Mathf.Max(1, RaceConfig.RoadSmoothSegments);
        var res = new List<Vector3>(uni.Count + 8) { uni[0] };
        for (int i = 1; i + 1 < uni.Count; i++)
        {
            var p = uni[i - 1]; var c = uni[i]; var q = uni[i + 1];
            var f1 = c - p; var f2 = q - c;
            var v1 = Flat(f1); var v2 = Flat(f2);
            float l1 = v1.Length(), l2 = v2.Length();
            if (l1 < 1e-3f || l2 < 1e-3f) { res.Add(c); continue; }
            if (Vector3.Dot(v1, v2) / (l1 * l2) > cosMin) { res.Add(c); continue; }
            float d = Mathf.Min(cutMax, l1 * 0.45f, l2 * 0.45f);
            var a = c - f1 * (d / Mathf.Max(1e-5f, f1.Length()));
            var b = c + f2 * (d / Mathf.Max(1e-5f, f2.Length()));
            for (int k = 1; k <= sub; k++)
            {
                float t = k / (float)sub, u = 1f - t;
                res.Add(u * u * a + 2f * u * t * c + t * t * b);
            }
        }
        res.Add(uni[^1]);
        return res;
    }

    /// <summary>Route der Prüfung berechnen und an der Definition speichern (Start -> Tore in Fahrreihenfolge).</summary>
    public static void Compute(RaceDefinition? race)
    {
        if (race == null) return;
        var lanes = FindLanes();
        var seq = race.TargetSequence();
        var pts = new List<Vector3> { race.StartPosition };
        foreach (int i in seq) pts.Add(race.Checkpoints[i].Position);
        race.RoadPath = Route(lanes, pts, out var seg, out int roadLegs);
        // Abschnitt -> Checkpoint-Index des Ziel-Tors - 1 (Färbung wie RaceRoute.SegmentColor)
        race.RoadPathSeg = new List<int>(seg.Count);
        foreach (int k in seg) race.RoadPathSeg.Add(k < seq.Count && seq[k] > 0 ? seq[k] - 1 : race.Checkpoints.Count - 1);
        Log.Info($"Straßenroute {race.Name}: {pts.Count - 1} Abschnitte, {roadLegs} über Straßen, {race.RoadPath.Count} Punkte, Netz {(lanes != null ? "da" : "FEHLT")}");
    }
}
