using Paradise.Core.Formats;
using System.Numerics;

namespace Paradise.Game.Gameplay;

/// <summary>
/// Ein befahrbarer Weg als Polylinie in Inselkoordinaten (Spielraum): eine Spur einer Straße (<see cref="LaneTrack"/>) oder die Fahrt
/// durch eine Kreuzung bzw. einen Kreisverkehr (<see cref="JunctionTrack"/>). Mit Bogenlänge und Tempoprofil (Tempoklasse,
/// Kurvenradius, rechtzeitiges Bremsen vor langsameren Stellen). Port von Unity TDU2.Traffic.TrafficTrack.
/// </summary>
public class TrafficTrack
{
    public Vector3[] P = Array.Empty<Vector3>();
    public Vector3[] T = Array.Empty<Vector3>();   // Tangente (Einheitsvektor)
    public float[] S = Array.Empty<float>();       // Bogenlänge
    public float[] Limit = Array.Empty<float>();   // erlaubtes Tempo (m/s) inkl. Bremsweg zur nächsten langsameren Stelle
    public float Length;

    public const float LateralAccel = 2.6f;   // m/s² in Kurven
    public const float Brake = 2.2f;          // m/s² komfortables Bremsen

    public void Finish(float[] baseLimit)
    {
        int n = P.Length;
        S = new float[n]; T = new Vector3[n]; Limit = new float[n];
        for (int i = 1; i < n; i++) S[i] = S[i - 1] + Vector3.Distance(P[i - 1], P[i]);
        Length = n > 0 ? S[n - 1] : 0f;
        for (int i = 0; i < n; i++)
        {
            var d = P[System.Math.Min(n - 1, i + 1)] - P[System.Math.Max(0, i - 1)];
            T[i] = d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : (i > 0 ? T[i - 1] : Vector3.UnitZ);
        }
        // Kurvenradius aus der Richtungsänderung über ±1 Punkt
        for (int i = 0; i < n; i++)
        {
            float lim = baseLimit[i];
            if (i > 0 && i < n - 1)
            {
                float ang = Angle2(T[i - 1], T[i + 1]);
                float ds = MathF.Max(0.5f, S[i + 1] - S[i - 1]);
                float k = ang / ds;
                if (k > 1e-4f) lim = MathF.Min(lim, MathF.Sqrt(LateralAccel / k));
            }
            Limit[i] = MathF.Max(2.5f, lim);
        }
        // rückwärts: rechtzeitig bremsen
        for (int i = n - 2; i >= 0; i--)
            Limit[i] = MathF.Min(Limit[i], MathF.Sqrt(Limit[i + 1] * Limit[i + 1] + 2f * Brake * (S[i + 1] - S[i])));
    }

    /// <summary>Winkel (rad) zwischen zwei Richtungen in der Waagerechten.</summary>
    public static float Angle2(Vector3 a, Vector3 b)
    {
        var x = new Vector2(a.X, a.Z); var y = new Vector2(b.X, b.Z);
        float la = x.Length(), lb = y.Length();
        if (la < 1e-6f || lb < 1e-6f) return 0f;
        return MathF.Acos(System.Math.Clamp(Vector2.Dot(x, y) / (la * lb), -1f, 1f));
    }

    int Index(float s)
    {
        int lo = 0, hi = S.Length - 1;
        if (s <= 0f) return 0;
        if (s >= Length) return System.Math.Max(0, hi - 1);
        while (hi - lo > 1) { int m = (lo + hi) >> 1; if (S[m] <= s) lo = m; else hi = m; }
        return lo;
    }

    public void Sample(float s, out Vector3 pos, out Vector3 tan)
    {
        if (P.Length == 1) { pos = P[0]; tan = T[0]; return; }
        int i = Index(s);
        float seg = MathF.Max(1e-5f, S[i + 1] - S[i]);
        float k = System.Math.Clamp((s - S[i]) / seg, 0f, 1f);
        pos = Vector3.Lerp(P[i], P[i + 1], k);
        var t = Vector3.Lerp(T[i], T[i + 1], k);
        tan = t.LengthSquared() > 1e-8f ? Vector3.Normalize(t) : T[i];
        if (s > Length) pos += T[P.Length - 1] * (s - Length);
        else if (s < 0f) pos += T[0] * s;
    }

    public Vector3 PointAt(float s) { Sample(s, out var p, out _); return p; }

    public float LimitAt(float s)
    {
        if (Limit.Length == 0) return 10f;
        int i = Index(s);
        return MathF.Min(Limit[i], Limit[System.Math.Min(Limit.Length - 1, i + 1)]);
    }

    /// <summary>Niedrigstes erlaubtes Tempo innerhalb der nächsten <paramref name="ahead"/> Meter (für die Planung).</summary>
    public float MinLimit(float from, float ahead)
    {
        float m = float.MaxValue;
        int i = Index(from);
        for (; i < S.Length && S[i] <= from + ahead; i++) m = MathF.Min(m, Limit[i]);
        return m == float.MaxValue ? LimitAt(from) : m;
    }

    public Vector3 Start => P[0];
    public Vector3 End => P[P.Length - 1];
    public Vector3 StartDir => T[0];
    public Vector3 EndDir => T[T.Length - 1];
}

/// <summary>Spur einer Straße (Index 0 = innen, an der Mittellinie) in Fahrtrichtung.</summary>
public sealed class LaneTrack : TrafficTrack
{
    public int RoadId;
    public bool Forward;          // true = vom Start- zum Endknoten (Spuren rechts der Straßenrichtung)
    public int Lane, LaneCount;
    public int FromNode, ToNode;
    public float BaseSpeed;       // Tempoklasse (m/s) am Spurende
}

/// <summary>Fahrt durch eine Kreuzung oder einen Kreisverkehr.</summary>
public sealed class JunctionTrack : TrafficTrack
{
    public LaneTrack From = null!, To = null!;
    public int Node;
    /// <summary>-1 links, 0 geradeaus, 1 rechts (für die Blinker).</summary>
    public int Turn;
    /// <summary>Kreisverkehr: Mitte (x,z), Radius der Fahrbahnmitte, Einfahrtswinkel (rad, atan2(z, x)).</summary>
    public bool Ring;
    public Vector2 RingCenter;
    public float RingLaneRadius, EntryAngle;
}

/// <summary>
/// Spuren des Verkehrsnetzes (bei Bedarf erzeugt und zwischengespeichert), Nachfolger an Kreuzungen, Kreisverkehre und ein Raster
/// für "welche Straßen liegen in der Nähe" (Spawnen, Wiedereinfädeln). Port von Unity TDU2.Traffic.TrafficLanes, erweitert um die
/// Kreisverkehre des Straßennetzes (Knotenart 3 bzw. JDET-Ring: Innenradius + Fahrbahnbreite): die Fahrt durch einen solchen Knoten
/// folgt dem Ring gegen den Uhrzeigersinn von oben (Rechtsverkehr: Mitte links vom Fahrer) statt quer über die Mittelinsel.
/// </summary>
public sealed class TrafficLanes
{
    public readonly IaNetwork Net;
    readonly Dictionary<(int, bool, int), LaneTrack?> _lanes = new();
    readonly Dictionary<(LaneTrack, LaneTrack), JunctionTrack> _junctions = new();
    readonly Dictionary<(int, int), List<int>> _grid = new();
    public const float Cell = 128f;
    /// <summary>Radius der Kreuzungsfläche je Knoten (Abstand der Straßenenden zum Mittelpunkt).</summary>
    public readonly Dictionary<int, float> NodeRadius = new();

    public TrafficLanes(IaNetwork net)
    {
        Net = net;
        foreach (var r in net.Roads.Values)
        {
            var seen = new HashSet<(int, int)>();
            foreach (var pc in r.Pieces)
                for (int i = 0; i < pc.Points.Length; i += 3) AddCell(r.Id, pc.Points[i], seen);
            // Lücken in langen Segmenten: Zwischenpunkte
            foreach (var pc in r.Pieces)
                for (int i = 0; i + 3 < pc.Points.Length; i += 3)
                {
                    var a = pc.Points[i]; var b = pc.Points[i + 3];
                    int steps = (int)MathF.Ceiling(Vector3.Distance(a, b) / (Cell * 0.5f));
                    for (int k = 1; k < steps; k++) AddCell(r.Id, Vector3.Lerp(a, b, k / (float)steps), seen);
                }
            // Kreuzungsradius
            var first = r.Pieces[0].Points[0];
            var lastPc = r.Pieces[r.Pieces.Count - 1];
            var last = lastPc.Points[lastPc.Points.Length - 1];
            if (net.Nodes.TryGetValue(r.StartNode, out var ns)) Grow(ns, first);
            if (net.Nodes.TryGetValue(r.EndNode, out var ne)) Grow(ne, last);
        }
    }

    void AddCell(int roadId, Vector3 p, HashSet<(int, int)> seen)
    {
        var key = ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Z / Cell));
        if (!seen.Add(key)) return;
        if (!_grid.TryGetValue(key, out var l)) _grid[key] = l = new List<int>();
        l.Add(roadId);
    }

    void Grow(IaNode n, Vector3 p)
    {
        float d = new Vector2(p.X - n.Pos.X, p.Z - n.Pos.Z).Length();
        if (d > 80f) return;
        NodeRadius.TryGetValue(n.Id, out var cur);
        NodeRadius[n.Id] = MathF.Max(cur, d);
    }

    /// <summary>Kreisverkehr am Knoten (Knotenart 3 oder JDET-Ring mit Radius)?</summary>
    public bool IsRoundabout(int nodeId, out IaJunction? ring)
    {
        ring = null;
        if (!Net.Junctions.TryGetValue(nodeId, out var j) || !(j.RingRadius > 1f && j.RingRadius < 500f) || !(j.RingWidth > 0.5f && j.RingWidth < 100f)) return false;
        ring = j;
        return true;
    }

    public int LaneCount(IaRoad r, bool forward) => forward ? r.LanesFwd : r.LanesBack;

    /// <summary>Straßen in der Nähe (Rasterzellen im Umkreis).</summary>
    public IEnumerable<int> RoadsNear(Vector3 p, float radius)
    {
        int r = (int)MathF.Ceiling(radius / Cell);
        int cx = (int)MathF.Floor(p.X / Cell), cz = (int)MathF.Floor(p.Z / Cell);
        var seen = new HashSet<int>();
        for (int x = cx - r; x <= cx + r; x++)
            for (int z = cz - r; z <= cz + r; z++)
                if (_grid.TryGetValue((x, z), out var l))
                    foreach (var id in l) if (seen.Add(id)) yield return id;
    }

    /// <summary>Rasterzellen mit Straßen in einem Ring um p (für zufällige Spawnpunkte).</summary>
    public void CellsInRing(Vector3 p, float rMin, float rMax, List<(int, int)> outCells)
    {
        outCells.Clear();
        int r = (int)MathF.Ceiling(rMax / Cell);
        int cx = (int)MathF.Floor(p.X / Cell), cz = (int)MathF.Floor(p.Z / Cell);
        for (int x = cx - r; x <= cx + r; x++)
            for (int z = cz - r; z <= cz + r; z++)
            {
                float c = new Vector2((x + 0.5f) * Cell - p.X, (z + 0.5f) * Cell - p.Z).Length();
                if (c < rMin - Cell || c > rMax + Cell) continue;
                if (_grid.ContainsKey((x, z))) outCells.Add((x, z));
            }
    }

    public List<int>? RoadsInCell((int, int) cell) => _grid.TryGetValue(cell, out var l) ? l : null;

    // ------------------------------------------------------------------ Spuren
    public LaneTrack? Lane(int roadId, bool forward, int lane)
    {
        if (!Net.Roads.TryGetValue(roadId, out var road)) return null;
        int count = LaneCount(road, forward);
        if (count <= 0) return null;
        lane = System.Math.Clamp(lane, 0, count - 1);
        var key = (roadId, forward, lane);
        if (_lanes.TryGetValue(key, out var t)) return t;
        t = BuildLane(road, forward, lane, count);
        _lanes[key] = t;
        return t;
    }

    LaneTrack? BuildLane(IaRoad road, bool forward, int lane, int count)
    {
        if (road.Pieces.Count == 0) return null;
        var pts = new List<Vector3>();
        var off = new List<float>();
        var hgt = new List<float>();
        var lim = new List<float>();
        foreach (var pc in road.Pieces)
        {
            Net.Profiles.TryGetValue(pc.ProfileId, out var prof);
            var centers = prof?.LaneCenters(forward);
            float o, h;
            if (centers != null && centers.Count > 0)
            {
                var c = centers[System.Math.Clamp(lane, 0, centers.Count - 1)];
                o = c.offset; h = c.height;
            }
            else
            {
                float w = 3.5f;
                int lanesOther = forward ? road.LanesBack : road.LanesFwd;
                float baseOff = lanesOther > 0 ? 0f : -count * w * 0.5f;
                o = baseOff + (lane + 0.5f) * w;
                if (!forward) o = -o;
                h = 0.1f;
            }
            float speed = Net.SpeedOf(pc.SpeedClass);
            for (int i = 0; i + 3 < pc.Points.Length; i += 3)
            {
                var a = pc.Points[i]; var c1 = pc.Points[i + 1]; var c2 = pc.Points[i + 2]; var b = pc.Points[i + 3];
                float chord = Vector3.Distance(a, c1) + Vector3.Distance(c1, c2) + Vector3.Distance(c2, b);
                int steps = System.Math.Clamp((int)MathF.Ceiling(chord / 3f), 1, 64);
                for (int k = pts.Count == 0 ? 0 : 1; k <= steps; k++)
                {
                    float t = k / (float)steps, u = 1f - t;
                    var p = u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b;
                    if (pts.Count > 0 && (p - pts[^1]).LengthSquared() < 0.01f) continue;
                    pts.Add(p); off.Add(o); hgt.Add(h); lim.Add(speed);
                }
            }
            if (pc.Points.Length == 1 && pts.Count == 0) { pts.Add(pc.Points[0]); off.Add(o); hgt.Add(h); lim.Add(speed); }
        }
        int n = pts.Count;
        if (n < 2)
        {
            var only = n == 1 ? pts[0] : Vector3.Zero;
            float o0 = off.Count > 0 ? off[0] : 0f;
            pts.Clear(); off.Clear(); hgt.Clear(); lim.Clear();
            pts.Add(only); pts.Add(only + Vector3.UnitZ * 0.5f);
            off.Add(o0); off.Add(o0); hgt.Add(0.1f); hgt.Add(0.1f); lim.Add(8f); lim.Add(8f);
            n = 2;
        }
        // Spurwechsel zwischen Abschnitten (andere Profile) weich verteilen
        SmoothOffsets(pts, off, 18f);

        // Mittellinie -> Spur: rechts der Straßenrichtung = (-t.z, 0, t.x)
        var lanePts = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var d = pts[System.Math.Min(n - 1, i + 1)] - pts[System.Math.Max(0, i - 1)];
            var right = new Vector3(-d.Z, 0f, d.X);
            right = right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : Vector3.UnitX;
            lanePts[i] = pts[i] + right * off[i] + Vector3.UnitY * hgt[i];
        }
        var limits = lim.ToArray();
        if (!forward) { Array.Reverse(lanePts); Array.Reverse(limits); }
        var t2 = new LaneTrack
        {
            RoadId = road.Id,
            Forward = forward,
            Lane = lane,
            LaneCount = count,
            FromNode = forward ? road.StartNode : road.EndNode,
            ToNode = forward ? road.EndNode : road.StartNode,
            P = lanePts,
            BaseSpeed = limits[^1],
        };
        t2.Finish(limits);
        return t2;
    }

    static void SmoothOffsets(List<Vector3> pts, List<float> off, float dist)
    {
        int n = off.Count;
        bool change = false;
        for (int i = 1; i < n; i++) if (MathF.Abs(off[i] - off[i - 1]) > 0.05f) { change = true; break; }
        if (!change) return;
        var cum = new float[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Vector3.Distance(pts[i - 1], pts[i]);
        var src = off.ToArray();
        int lo = 0, hi = 0;
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            while (hi < n && cum[hi] <= cum[i] + dist * 0.5f) { sum += src[hi]; hi++; }
            while (lo < n && cum[lo] < cum[i] - dist * 0.5f) { sum -= src[lo]; lo++; }
            off[i] = sum / System.Math.Max(1, hi - lo);
        }
    }

    // ------------------------------------------------------------------ Kreuzungen
    /// <summary>Mögliche Fortsetzungen am Ende einer Spur (Straße + Richtung), ohne Wenden – es sei denn, es gibt nichts anderes.</summary>
    public void Successors(LaneTrack from, List<(int road, bool forward)> outList)
    {
        outList.Clear();
        if (from.ToNode == 0 || !Net.Nodes.TryGetValue(from.ToNode, out var node))
        {
            // Sackgasse: wenden, falls Gegenspur vorhanden
            if (Net.Roads.TryGetValue(from.RoadId, out var self) && LaneCount(self, !from.Forward) > 0) outList.Add((from.RoadId, !from.Forward));
            return;
        }
        bool ring = IsRoundabout(node.Id, out _);
        foreach (var rid in node.Roads)
        {
            if (!Net.Roads.TryGetValue(rid, out var r)) continue;
            if (r.StartNode == node.Id && r.LanesFwd > 0) Add(rid, true);
            if (r.EndNode == node.Id && r.LanesBack > 0) Add(rid, false);
        }
        void Add(int rid, bool fwd)
        {
            if (rid == from.RoadId && fwd != from.Forward && Net.Roads[rid].StartNode != Net.Roads[rid].EndNode) return;   // Wenden später
            var next = Lane(rid, fwd, 0);
            if (next == null) return;
            // scharfe Kehren (> 150°) auslassen – im Kreisverkehr geht jede Ausfahrt (der Ring führt herum)
            if (!ring && TrafficTrack.Angle2(from.EndDir, next.StartDir) > 150f * MathF.PI / 180f) return;
            outList.Add((rid, fwd));
        }
        // Kreisverkehr: Wenden über den Ring ist erlaubt (Ausfahrt = eigene Straße zurück)
        if (ring && Net.Roads.TryGetValue(from.RoadId, out var back) && LaneCount(back, !from.Forward) > 0 && !outList.Contains((from.RoadId, !from.Forward)))
            outList.Add((from.RoadId, !from.Forward));
        if (outList.Count == 0 && Net.Roads.TryGetValue(from.RoadId, out var me) && LaneCount(me, !from.Forward) > 0) outList.Add((from.RoadId, !from.Forward));
    }

    /// <summary>Zielspur auf der nächsten Straße: gleiche Position von außen gezählt (rechte Spur bleibt rechts).</summary>
    public LaneTrack? NextLane(LaneTrack from, int road, bool forward)
    {
        if (!Net.Roads.TryGetValue(road, out var r)) return null;
        int count = LaneCount(r, forward);
        if (count <= 0) return null;
        int fromOuter = from.LaneCount - 1 - from.Lane;
        int lane = System.Math.Clamp(count - 1 - fromOuter, 0, count - 1);
        return Lane(road, forward, lane);
    }

    public JunctionTrack? Junction(LaneTrack? from, LaneTrack? to)
    {
        if (from == null || to == null) return null;
        if (_junctions.TryGetValue((from, to), out var j)) return j;
        j = IsRoundabout(from.ToNode, out var ring) && ring != null ? BuildRing(from, to, ring) : BuildCross(from, to);
        _junctions[(from, to)] = j;
        return j;
    }

    static Vector3 Hermite(Vector3 a, Vector3 ta, Vector3 b, Vector3 tb, float m, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * a + (t3 - 2 * t2 + t) * m * ta + (-2 * t3 + 3 * t2) * b + (t3 - t2) * m * tb;
    }

    /// <summary>Normale Kreuzung: Hermite-Kurve vom Spurende zum Spuranfang (Unity).</summary>
    JunctionTrack BuildCross(LaneTrack from, LaneTrack to)
    {
        var j = new JunctionTrack { From = from, To = to, Node = from.ToNode };
        var a = from.End; var b = to.Start;
        var ta = from.EndDir; var tb = to.StartDir;
        float dist = Vector3.Distance(a, b);
        bool uturn = Vector3.Dot(ta, tb) < -0.5f;
        float m = uturn ? MathF.Max(dist, 8f) * 1.6f : MathF.Max(dist, 1f);
        int steps = System.Math.Clamp((int)MathF.Ceiling(dist / 2f) + 2, 4, 40);
        var pts = new Vector3[steps + 1];
        for (int i = 0; i <= steps; i++) pts[i] = Hermite(a, ta, b, tb, m, i / (float)steps);
        j.P = pts;
        float baseLim = MathF.Min(MathF.Min(from.BaseSpeed, to.Limit.Length > 0 ? to.Limit[0] : 10f), 17f);
        var lim = new float[pts.Length];
        Array.Fill(lim, baseLim);
        j.Finish(lim);
        float cy = ta.Z * tb.X - ta.X * tb.Z;   // > 0: links
        float dot = Vector3.Dot(Vector3.Normalize(new Vector3(ta.X, 0, ta.Z)), Vector3.Normalize(new Vector3(tb.X, 0, tb.Z)));
        j.Turn = dot > 0.9f ? 0 : (cy > 0f ? -1 : 1);
        return j;
    }

    /// <summary>
    /// Kreisverkehr (JDET: Innenradius R, Fahrbahnbreite W, Mitte): Einfädeln vom Spurende auf die Ringspur (Radius R + 0,55 W),
    /// dem Ring in Fahrtrichtung folgen (Rechtsverkehr: Mitte links, Tangente (r.z, -r.x) = abnehmender Winkel atan2(z, x)),
    /// an der Ausfahrt auf den Spuranfang der nächsten Straße.
    /// </summary>
    JunctionTrack BuildRing(LaneTrack from, LaneTrack to, IaJunction ring)
    {
        var j = new JunctionTrack { From = from, To = to, Node = from.ToNode, Ring = true, RingCenter = ring.RingCenter };
        var a = from.End; var b = to.Start;
        var c = new Vector3(ring.RingCenter.X, 0f, ring.RingCenter.Y);
        float da = new Vector2(a.X - c.X, a.Z - c.Z).Length(), db = new Vector2(b.X - c.X, b.Z - c.Z).Length();
        float rl = ring.RingRadius + ring.RingWidth * 0.55f;
        rl = System.Math.Clamp(rl, 3f, MathF.Max(3f, MathF.Min(da, db) - 1f));
        j.RingLaneRadius = rl;
        float thA = MathF.Atan2(a.Z - c.Z, a.X - c.X), thB = MathF.Atan2(b.Z - c.Z, b.X - c.X);
        j.EntryAngle = thA;
        // Ein-/Ausfädeln über ~ 9 m Ringbogen
        float delta = System.Math.Clamp(9f / rl, 0.12f, 0.6f);
        float th0 = thA - delta, th1 = thB + delta;
        float sweep = th0 - th1;
        while (sweep < 0.15f) sweep += MathF.PI * 2f;
        while (sweep > MathF.PI * 2f + 0.15f) sweep -= MathF.PI * 2f;
        float y0 = a.Y, y1 = b.Y;
        Vector3 RingPt(float th, float k) => new(c.X + MathF.Cos(th) * rl, y0 + (y1 - y0) * k, c.Z + MathF.Sin(th) * rl);
        Vector3 RingTan(float th) => Vector3.Normalize(new Vector3(MathF.Sin(th), 0f, -MathF.Cos(th)));   // (r.z, -r.x)

        var pts = new List<Vector3>();
        // Einfädeln: Hermite vom Spurende zum Ringpunkt
        var p0 = RingPt(th0, 0f); var t0 = RingTan(th0);
        float mIn = MathF.Max(2f, Vector3.Distance(a, p0));
        int nIn = System.Math.Clamp((int)MathF.Ceiling(mIn / 2f) + 2, 3, 20);
        for (int i = 0; i < nIn; i++) pts.Add(Hermite(a, from.EndDir, p0, t0, mIn, i / (float)nIn));
        // Ringbogen
        float arc = sweep * rl;
        int nArc = System.Math.Clamp((int)MathF.Ceiling(arc / 2f), 3, 200);
        for (int i = 0; i <= nArc; i++)
        {
            float k = i / (float)nArc;
            pts.Add(RingPt(th0 - sweep * k, 0.15f + 0.7f * k));
        }
        // Ausfädeln
        var p1 = RingPt(th1, 1f); var t1 = RingTan(th1);
        float mOut = MathF.Max(2f, Vector3.Distance(p1, b));
        int nOut = System.Math.Clamp((int)MathF.Ceiling(mOut / 2f) + 2, 3, 20);
        for (int i = 1; i <= nOut; i++) pts.Add(Hermite(p1, t1, b, to.StartDir, mOut, i / (float)nOut));
        // doppelte Punkte entfernen
        var clean = new List<Vector3>(pts.Count);
        foreach (var p in pts) if (clean.Count == 0 || (p - clean[^1]).LengthSquared() > 0.04f) clean.Add(p);
        if (clean.Count < 2) clean.Add(b);
        j.P = clean.ToArray();
        float baseLim = MathF.Min(MathF.Min(from.BaseSpeed, to.Limit.Length > 0 ? to.Limit[0] : 10f), 14f);
        var lim = new float[j.P.Length];
        Array.Fill(lim, baseLim);
        j.Finish(lim);
        // Blinker: erste Ausfahrt rechts, weit herum links, sonst geradeaus
        j.Turn = sweep < 2.2f ? 1 : sweep > 4.0f ? -1 : 0;
        return j;
    }

    /// <summary>Strecke (m) entlang der Ringspur, bis ein Punkt auf dem Ring die Einfahrt einer Ringfahrt erreicht (Fahrtrichtung).</summary>
    public static float RingDistanceTo(JunctionTrack entry, Vector3 p)
    {
        float th = MathF.Atan2(p.Z - entry.RingCenter.Y, p.X - entry.RingCenter.X);
        float d = th - entry.EntryAngle;   // Fahrtrichtung = abnehmender Winkel: Punkte mit größerem Winkel kommen auf uns zu
        while (d < 0f) d += MathF.PI * 2f;
        while (d >= MathF.PI * 2f) d -= MathF.PI * 2f;
        return d * entry.RingLaneRadius;
    }

    /// <summary>Nächster Spurpunkt zu p, passend zur Richtung (Wiedereinfädeln nach einem Unfall).</summary>
    public bool Nearest(Vector3 p, Vector3 dir, float maxDist, out LaneTrack? best, out float bestS)
    {
        best = null; bestS = 0f;
        float bestD = maxDist;
        foreach (var rid in RoadsNear(p, maxDist + 40f))
        {
            var r = Net.Roads[rid];
            for (int f = 0; f < 2; f++)
            {
                bool fwd = f == 0;
                int cnt = LaneCount(r, fwd);
                for (int l = 0; l < cnt; l++)
                {
                    var t = Lane(rid, fwd, l);
                    if (t == null) continue;
                    for (int i = 0; i < t.P.Length; i++)
                    {
                        var d = t.P[i] - p; d.Y *= 0.5f;
                        float dd = d.Length();
                        if (dd >= bestD) continue;
                        if (dir.LengthSquared() > 0.01f && Vector3.Dot(t.T[i], dir) < 0.6f) continue;
                        bestD = dd; best = t; bestS = t.S[i];
                    }
                }
            }
        }
        return best != null;
    }
}
