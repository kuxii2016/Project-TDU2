// Portiert aus dem Unity-Projekt (Runtime/UI/TDU2Gps.cs).
using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using V3 = System.Numerics.Vector3;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// GPS wie im Spiel: Ziel auf der großen Karte setzen (Mini-Menü "Ziel GPS" bzw. Rechtsklick), kürzester Weg über das
    /// Straßennetz (ROAD_MAP/ROAD_DETAIL.IA, Dijkstra, Einbahnstraßen über die Spuren), Anzeige auf der Minimap
    /// (Pfad, Restentfernung /HUD/minimap/gps_distance mit /:obj_Distance + /:gps_Unit, Richtungs-Kranz
    /// /HUD/minimap/distance/obj_dist_01..61 – Bild 1 = hell, 101 = aus –, Meldung /HUD/minimap/gps_status "open" mit
    /// /:IsGPSremoved). Am Ziel wird es entfernt. Ansagen aus Sound/Voices/&lt;Sprache&gt;/GPS.bnk (VFGPSVoiceNNN_.wav,
    /// Zuordnung in <see cref="TDU2GpsSettings"/>). Wird von <see cref="Minimap"/> angelegt und aktualisiert.
    /// Alle Positionen in Spielkoordinaten (Insel).
    /// </summary>
    public sealed class Gps
    {
        public static Gps? Instance { get; private set; }
        public Gps() { Instance = this; }

        public bool HasTarget { get; private set; }
        /// <summary>Ziel in Spielkoordinaten (Insel).</summary>
        public V3 TargetGame { get; private set; }
        /// <summary>Restweg in Metern (-1 = unbekannt).</summary>
        public float Remaining { get; private set; } = -1f;
        /// <summary>Route (Spielkoordinaten), Auto -> Ziel.</summary>
        public readonly List<V3> Route = new();
        /// <summary>Zählt jede Änderung der Route (neu berechnet/entfernt) – für zwischengespeicherte Meshes.</summary>
        public int RouteVersion { get; private set; }
        public event Action? Changed;

        // ------------------------------------------------------------------ Straßennetz als Graph
        sealed class RoadPoly { public int Id, Start, End; public bool Fwd, Back; public V3[] P = Array.Empty<V3>(); public float[] S = Array.Empty<float>(); public float Len; }
        string? _bankFor;
        IaNetwork? _graphNet;
        readonly Dictionary<int, RoadPoly> _roads = new();
        readonly Dictionary<int, List<RoadPoly>> _adj = new();
        readonly Dictionary<long, List<(RoadPoly r, int seg)>> _grid = new();
        // Perf: Dijkstra-Arbeitsdaten wiederverwenden (Profiler: GPS-Route alle 0,75 s mit mehreren frischen Collections).
        readonly Dictionary<int, float> _spDist = new();
        readonly Dictionary<int, (int node, RoadPoly road)> _spPrev = new();
        readonly Heap _spHeap = new();
        readonly HashSet<int> _spDone = new();
        readonly List<V3> _spPath = new();
        readonly List<(int node, RoadPoly road)> _spChain = new();
        const float Cell = 64f;
        Task<IaNetwork>? _netTask;
        Task? _buildTask;
        string? _bnkDir;

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        bool GraphReady => _graphNet != null && _buildTask == null && _roads.Count > 0;

        /// <summary>Geladenes Straßennetz der Insel (null, solange es lädt) – auch für das Tunnellicht (World/TunnelLight).</summary>
        public IaNetwork? Network => _graphNet;

        /// <summary>Straßennetz der Insel (Unity: Minimap.Network) im Hintergrund laden und als Graph aufbereiten.</summary>
        public void Bind(string? bnkDir, string? islandBank)
        {
            _bnkDir = bnkDir;
            if (islandBank == null || islandBank == _bankFor) return;
            _bankFor = islandBank;
            _graphNet = null; _buildTask = null;
            lock (_roads) { _roads.Clear(); _adj.Clear(); _grid.Clear(); }
            string b = islandBank;
            _netTask = Task.Run(() => IaNetwork.LoadFromBank(b));
        }

        void EnsureGraph()
        {
            if (_netTask != null && _netTask.IsCompleted)
            {
                if (_netTask.IsFaulted) Log.Warn("GPS: Unable to load road network: " + _netTask.Exception?.GetBaseException().Message);
                else
                {
                    var net = _netTask.Result;
                    _graphNet = net;
                    _buildTask = Task.Run(() => BuildGraph(net));
                }
                _netTask = null;
            }
            if (_buildTask != null && _buildTask.IsCompleted)
            {
                _buildTask = null;
            }
        }

        void BuildGraph(IaNetwork net)
        {
            var roads = new Dictionary<int, RoadPoly>();
            var adj = new Dictionary<int, List<RoadPoly>>();
            var grid = new Dictionary<long, List<(RoadPoly, int)>>();
            foreach (var r in net.Roads.Values)
            {
                var pts = new List<V3>();
                foreach (var pc in r.Pieces)
                {
                    var p = pc.Points;
                    if (p == null || p.Length == 0) continue;
                    for (int k = 0; k + 3 < p.Length; k += 3)
                    {
                        V3 a = p[k], c1 = p[k + 1], c2 = p[k + 2], b = p[k + 3];
                        float est = (c1 - a).Length() + (c2 - c1).Length() + (b - c2).Length();
                        int n = System.Math.Clamp((int)MathF.Ceiling(est / 12f), 1, 24);
                        for (int i = (pts.Count == 0 ? 0 : 1); i <= n; i++)
                        {
                            float t = i / (float)n, u = 1f - t;
                            pts.Add(u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b);
                        }
                    }
                    if (p.Length == 1 && pts.Count == 0) pts.Add(p[0]);
                }
                if (pts.Count < 2) continue;
                var rp = new RoadPoly { Id = r.Id, Start = r.StartNode, End = r.EndNode, P = pts.ToArray(), S = new float[pts.Count] };
                for (int i = 1; i < pts.Count; i++) rp.S[i] = rp.S[i - 1] + V3.Distance(pts[i - 1], pts[i]);
                rp.Len = rp.S[pts.Count - 1];
                // Einbahnstraßen: Spuren mit (Fwd) bzw. gegen (Back) die Straßenrichtung; ohne Angabe beide Richtungen
                rp.Fwd = r.LanesFwd > 0 || r.LanesBack == 0;
                rp.Back = r.LanesBack > 0 || r.LanesFwd == 0;
                roads[r.Id] = rp;
                if (rp.Start != 0) { if (!adj.TryGetValue(rp.Start, out var l)) adj[rp.Start] = l = new List<RoadPoly>(); l.Add(rp); }
                if (rp.End != 0 && rp.End != rp.Start) { if (!adj.TryGetValue(rp.End, out var l2)) adj[rp.End] = l2 = new List<RoadPoly>(); l2.Add(rp); }
                for (int i = 0; i + 1 < rp.P.Length; i++)
                {
                    V3 a = rp.P[i], b = rp.P[i + 1];
                    int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X) / Cell), x1 = (int)MathF.Floor(MathF.Max(a.X, b.X) / Cell);
                    int z0 = (int)MathF.Floor(MathF.Min(a.Z, b.Z) / Cell), z1 = (int)MathF.Floor(MathF.Max(a.Z, b.Z) / Cell);
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++)
                        {
                            long key = Key(x, z);
                            if (!grid.TryGetValue(key, out var gl)) grid[key] = gl = new List<(RoadPoly, int)>();
                            gl.Add((rp, i));
                        }
                }
            }
            lock (_roads)
            {
                foreach (var kv in roads) _roads[kv.Key] = kv.Value;
                foreach (var kv in adj) _adj[kv.Key] = kv.Value;
                foreach (var kv in grid) _grid[kv.Key] = kv.Value;
            }
        }

        /// <summary>Nächster Punkt auf einer Straße (Spielkoordinaten); s = Weg ab Straßenanfang.</summary>
        bool Project(V3 g, float maxDist, out RoadPoly? road, out float s, out V3 point)
        {
            road = null; s = 0f; point = g;
            float best = maxDist * maxDist;
            int r = (int)MathF.Ceiling(maxDist / Cell);
            int cx = (int)MathF.Floor(g.X / Cell), cz = (int)MathF.Floor(g.Z / Cell);
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (!_grid.TryGetValue(Key(x, z), out var l)) continue;
                    foreach (var (rp, i) in l)
                    {
                        V3 a = rp.P[i], b = rp.P[i + 1];
                        float abx = b.X - a.X, abz = b.Z - a.Z, agx = g.X - a.X, agz = g.Z - a.Z, ab2 = abx * abx + abz * abz;
                        float t = ab2 > 1e-6f ? System.Math.Clamp((agx * abx + agz * abz) / ab2, 0f, 1f) : 0f;
                        var q = V3.Lerp(a, b, t);
                        float dx = q.X - g.X, dz = q.Z - g.Z, d = dx * dx + dz * dz;
                        if (d < best) { best = d; road = rp; point = q; s = rp.S[i] + (rp.S[i + 1] - rp.S[i]) * t; }
                    }
                }
            return road != null;
        }

        /// <summary>Teilstück einer Straße von s0 nach s1 (rückwärts, wenn s1 &lt; s0).</summary>
        static void AppendSub(List<V3> o, RoadPoly r, float s0, float s1)
        {
            V3 At(float s)
            {
                int i = Array.BinarySearch(r.S, s);
                if (i >= 0) return r.P[i];
                i = ~i;
                if (i <= 0) return r.P[0];
                if (i >= r.P.Length) return r.P[r.P.Length - 1];
                float t = (s - r.S[i - 1]) / MathF.Max(1e-4f, r.S[i] - r.S[i - 1]);
                return V3.Lerp(r.P[i - 1], r.P[i], t);
            }
            o.Add(At(s0));
            if (s1 >= s0) { for (int i = 0; i < r.P.Length; i++) if (r.S[i] > s0 && r.S[i] < s1) o.Add(r.P[i]); }
            else { for (int i = r.P.Length - 1; i >= 0; i--) if (r.S[i] < s0 && r.S[i] > s1) o.Add(r.P[i]); }
            o.Add(At(s1));
        }

        sealed class Heap
        {
            readonly List<(float d, int n)> _h = new();
            public int Count => _h.Count;
            public void Clear() => _h.Clear();
            public void Push(float d, int n)
            {
                _h.Add((d, n)); int i = _h.Count - 1;
                while (i > 0) { int p = (i - 1) / 2; if (_h[p].d <= _h[i].d) break; (_h[p], _h[i]) = (_h[i], _h[p]); i = p; }
            }
            public (float d, int n) Pop()
            {
                var top = _h[0]; var last = _h[_h.Count - 1]; _h.RemoveAt(_h.Count - 1);
                if (_h.Count > 0)
                {
                    _h[0] = last; int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1, r = l + 1, m = i;
                        if (l < _h.Count && _h[l].d < _h[m].d) m = l;
                        if (r < _h.Count && _h[r].d < _h[m].d) m = r;
                        if (m == i) break;
                        (_h[m], _h[i]) = (_h[i], _h[m]); i = m;
                    }
                }
                return top;
            }
        }

        /// <summary>Kürzester Weg von (ra, sa) nach (rb, sb) über die Knoten; null = keiner.</summary>
        List<V3>? ShortestPath(RoadPoly ra, float sa, RoadPoly rb, float sb, bool oneWay, out float len, List<(int node, int idx)>? junctions = null)
        {
            len = 0f;
            junctions?.Clear();
            float bestDirect = float.MaxValue;
            if (ra == rb && ((sb >= sa && (!oneWay || ra.Fwd)) || (sb < sa && (!oneWay || ra.Back)))) bestDirect = MathF.Abs(sb - sa);

            var dist = _spDist; dist.Clear();
            var prev = _spPrev; prev.Clear();
            var heap = _spHeap; heap.Clear();
            var done = _spDone; done.Clear();
            if (ra.Start != 0 && (!oneWay || ra.Back)) { dist[ra.Start] = sa; prev[ra.Start] = (0, ra); heap.Push(sa, ra.Start); }
            if (ra.End != 0 && (!oneWay || ra.Fwd) && (!dist.TryGetValue(ra.End, out var de) || ra.Len - sa < de)) { dist[ra.End] = ra.Len - sa; prev[ra.End] = (0, ra); heap.Push(ra.Len - sa, ra.End); }
            float best = bestDirect; int bestNode = 0; bool bestAtStart = false;
            while (heap.Count > 0)
            {
                var (d, n) = heap.Pop();
                if (!done.Add(n)) continue;
                if (d >= best) break;
                if (n == rb.Start && (!oneWay || rb.Fwd) && d + sb < best) { best = d + sb; bestNode = n; bestAtStart = true; }
                if (n == rb.End && (!oneWay || rb.Back) && d + rb.Len - sb < best) { best = d + rb.Len - sb; bestNode = n; bestAtStart = false; }
                if (!_adj.TryGetValue(n, out var list)) continue;
                foreach (var r in list)
                {
                    int to; bool ok;
                    if (r.Start == n) { to = r.End; ok = !oneWay || r.Fwd; } else { to = r.Start; ok = !oneWay || r.Back; }
                    if (!ok || to == 0) continue;
                    float nd = d + r.Len;
                    if (!dist.TryGetValue(to, out var od) || nd < od) { dist[to] = nd; prev[to] = (n, r); heap.Push(nd, to); }
                }
            }
            if (best == float.MaxValue) return null;
            var o = _spPath; o.Clear();
            len = best;
            if (bestNode == 0) { AppendSub(o, ra, sa, sb); return o; }
            // Knotenkette rückwärts
            var chain = _spChain; chain.Clear();
            int cur = bestNode;
            int guard = 0;
            while (cur != 0 && guard++ < 100000)
            {
                var pv = prev[cur];
                chain.Add((cur, pv.road));
                cur = pv.node;
            }
            chain.Reverse();
            // erstes Stück: vom Auto zum ersten Knoten
            var first = chain[0];
            AppendSub(o, ra, sa, first.node == ra.Start && (ra.End != first.node || sa < ra.Len * 0.5f) ? 0f : ra.Len);
            for (int i = 1; i < chain.Count; i++)
            {
                var road = chain[i].road;
                int from = chain[i - 1].node;
                junctions?.Add((from, o.Count - 1));
                if (road.Start == from) AppendSub(o, road, 0f, road.Len); else AppendSub(o, road, road.Len, 0f);
            }
            junctions?.Add((bestNode, o.Count - 1));
            AppendSub(o, rb, bestAtStart ? 0f : rb.Len, sb);
            return o;
        }

        // ------------------------------------------------------------------ Ziel
        /// <summary>Ziel setzen (Spielkoordinaten); wird auf die nächste Straße gelegt.</summary>
        public void SetTarget(V3 game, bool announce = true)
        {
            EnsureGraph();
            if (GraphReady && Project(game, 400f, out _, out _, out var p)) game = p;
            TargetGame = game; HasTarget = true; Route.Clear(); Remaining = -1f; RouteVersion++;
            _nextRoute = 0f; _lastCue = null; _prevRemaining = -1f; _played.Clear(); _jPrev.Clear(); _uturnSince = -1f; _uturnDone = false;
            ShowStatus(false);
            if (announce) Voice(TDU2GpsSettings.VoiceTargetSet);
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Error("GPS: " + ex.Message); }
        }

        /// <summary>Ziel entfernen (am Ziel angekommen: <paramref name="arrived"/>).</summary>
        public void ClearTarget(bool arrived = false)
        {
            if (!HasTarget) return;
            HasTarget = false; Route.Clear(); Remaining = -1f; RouteVersion++;
            ShowStatus(true);
            if (arrived) Voice(TDU2GpsSettings.VoiceArrived);
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Error("GPS: " + ex.Message); }
        }

        /// <summary>Ziel in der Nähe dieses Punkts (Spielkoordinaten)?</summary>
        public bool IsTargetNear(V3 game, float radius = 25f)
        {
            if (!HasTarget) return false;
            var d = TargetGame - game; d.Y = 0f;
            return d.LengthSquared() < radius * radius;
        }

        // ------------------------------------------------------------------ Ablauf
        float _nextRoute, _time;
        string? _lastCue;
        // Perf: SetVariable nur bei Änderung (die HUD-Variable selbst wird sonst jeden Frame neu aufgelöst).
        VmfPlayer? _textHud; string? _lastDistText, _lastUnitText;
        // Jede Ansage je Abzweig nur einmal (Route wird alle 0,75 s neu gerechnet, Punkte verschieben sich leicht)
        readonly HashSet<string> _played = new();
        // Wenden wie im Original (0xCFC066): erst nach 10 s Abweichung, einmalig bis zur Rückkehr auf die Route
        float _uturnSince = -1f; bool _uturnDone;
        int _pendingVoice;
        // Knoten des Straßennetzes entlang der Route (Knoten-Id, Index in Route) – Abbiege-Ansagen nur an echten Kreuzungen
        readonly List<(int node, int idx)> _junctions = new();
        // Abstand je Kreuzung beim letzten Durchlauf (Schwellen-Unterschreitung wie im Original)
        readonly Dictionary<int, float> _jPrev = new();
        const float TurnFar = 200f, TurnNear = 50f;   // unpacked.exe: [GPS+0x1D00] = 200, [GPS+0x1D04] = 50
        VmfPlayer? _hud;

        /// <summary>
        /// Unity Update. carGame/forwardGame = Lage des Autos (Spielkoordinaten); hud = hud.bnk der Minimap (null = ohne Anzeige).
        /// </summary>
        public void Update(float dt, VmfPlayer? hud, V3? carGame, V3 forwardGame)
        {
            _time += dt;
            _hud = hud;
            PumpVoice();
            EnsureGraph();
            if (!HasTarget || carGame == null) { UpdateHud(hud, null, default, false); return; }
            var car = carGame.Value;
            if (Flat(TargetGame - car) < TDU2GpsSettings.ArriveDistance) { ClearTarget(true); UpdateHud(hud, null, default, false); return; }
            if (GraphReady && _time >= _nextRoute)
            {
                _nextRoute = _time + 0.75f;
                ComputeRoute(car);
                DistanceCues();
            }
            UpdateCues(car, forwardGame);
            UpdateHud(hud, car, forwardGame, true);
        }

        void ComputeRoute(V3 car)
        {
            RouteVersion++;
            Route.Clear(); Remaining = -1f; _junctions.Clear();
            if (!Project(car, 300f, out var ra, out var sa, out var pa) || ra == null) { Remaining = Flat(TargetGame - car); return; }
            if (!Project(TargetGame, 400f, out var rb, out var sb, out var pb) || rb == null) { Remaining = Flat(TargetGame - car); return; }
            var path = ShortestPath(ra, sa, rb, sb, true, out float len, _junctions) ?? ShortestPath(ra, sa, rb, sb, false, out len, _junctions);
            if (path == null) { _junctions.Clear(); Remaining = Flat(TargetGame - car); return; }
            for (int k = 0; k < _junctions.Count; k++) _junctions[k] = (_junctions[k].node, _junctions[k].idx + 1);   // Route[0] = Auto
            Route.Add(car);
            Route.AddRange(path);
            Route.Add(TargetGame);
            Remaining = len + Flat(pa - car) + Flat(TargetGame - pb);
        }

        static float Flat(V3 v) { v.Y = 0f; return v.Length(); }

        // ------------------------------------------------------------------ Entfernungsansagen wie im Original-Spiel
        // Aus unpacked.exe (GPS-Ansage, Tabelle VFGPSVoice001..025 bei 0x164B5B0, Aufrufe bei 0xCFA6E0): beim Unterschreiten von
        // 3 / 2 / 1,5 / 1 / 0,5 km (bzw. Meilen, Restweg x 0,6215) spielt das Spiel VFGPSVoice010/009/008/007/006 (metrisch)
        // bzw. 015/014/013/012/011 (Meilen) – nur wenn der Restweg nahe an der Schwelle liegt (kein Ansagen nach Sprüngen).
        static readonly float[] DistThresholds = { 3000f, 2000f, 1500f, 1000f, 500f };
        static readonly int[] DistVoiceMetric = { 10, 9, 8, 7, 6 };
        static readonly int[] DistVoiceMiles = { 15, 14, 13, 12, 11 };
        float _prevRemaining = -1f;

        void DistanceCues()
        {
            float cur = Remaining;
            float prev = _prevRemaining;
            _prevRemaining = cur;
            if (!TDU2GpsSettings.Voice || !TDU2GpsSettings.DistanceVoices || cur < 0f || prev < 0f) return;
            float k = TDU2GpsSettings.Miles ? 0.6215f : 1f;
            float c = cur * k, p = prev * k;
            for (int i = 0; i < DistThresholds.Length; i++)
            {
                float t = DistThresholds[i];
                if (p > t && c <= t && MathF.Abs(c - t) < TDU2GpsSettings.DistanceVoiceTolerance)
                {
                    Voice(TDU2GpsSettings.Miles ? DistVoiceMiles[i] : DistVoiceMetric[i]);
                    return;
                }
            }
        }

        /// <summary>Vector3.SignedAngle(a, b, up) wie in Unity (Grad, im Uhrzeigersinn von oben positiv), nur XZ.</summary>
        static float SignedAngleUnity(V3 a, V3 b)
        {
            float cross = a.Z * b.X - a.X * b.Z, dot = a.X * b.X + a.Z * b.Z;
            return MathF.Atan2(cross, dot) * (180f / MathF.PI);
        }

        // ------------------------------------------------------------------ Ansagen (Abbiegen)
        void UpdateCues(V3 car, V3 forwardGame)
        {
            if (Route.Count < 3 || !TDU2GpsSettings.Voice) return;
            var fw = forwardGame; fw.Y = 0f;
            if (fw.LengthSquared() < 1e-4f) return;
            fw = V3.Normalize(fw);
            // Route läuft entgegen der Fahrtrichtung -> wenden
            var first = Route[System.Math.Min(2, Route.Count - 1)] - car; first.Y = 0f;
            bool wrongWay = first.Length() > 8f && V3.Dot(V3.Normalize(first), fw) < -0.6f;
            if (!wrongWay) { _uturnSince = -1f; _uturnDone = false; }
            else
            {
                if (_uturnSince < 0f) _uturnSince = _time;
                if (!_uturnDone && _time - _uturnSince >= 10f) { _uturnDone = true; Cue("uturn", TDU2GpsSettings.VoiceUTurn); }
                return;
            }
            // Wie im Original (0xCFB43D -> 0xCFAFB0): Ansage nur an Kreuzungen des Straßennetzes (Knoten mit >= 3 Straßen), Winkel
            // zwischen ankommender und abgehender Straße; je Knoten "gleich" und "jetzt" höchstens einmal (Knoten-Id bleibt bei
            // jeder Neuberechnung gleich). Vorher: jede Krümmung der Route -> Kurven und der Anfang der Route wurden als Abbiegen
            // angesagt, und alle 0,75 s neu.
            if (_junctions.Count == 0) return;
            float along = 0f; int ji = 0;
            for (int i = 1; i < Route.Count && along < 400f && ji < _junctions.Count; i++)
            {
                along += Flat(Route[i] - Route[i - 1]);
                while (ji < _junctions.Count && _junctions[ji].idx < i) ji++;
                if (ji >= _junctions.Count || _junctions[ji].idx != i) continue;
                int node = _junctions[ji].node;
                if (!_adj.TryGetValue(node, out var roads) || roads.Count < 3) continue;   // keine Kreuzung, nur Straßenstück
                var a = Dir(i, -1); var b = Dir(i, +1);
                if (a == V3.Zero || b == V3.Zero) continue;
                float ang = SignedAngleUnity(a, b);
                if (MathF.Abs(ang) < 35f) continue;   // geradeaus über die Kreuzung
                // Unity: Winkel im (gespiegelten) Spielraum, daher umgedreht (root.lossyScale.x < 0)
                bool right = !(ang > 0f);
                string id = (right ? "R" : "L") + ":" + node;
                // Original: nur beim Unterschreiten der Schwelle (vorheriger Abstand darüber) – nicht, wenn die Kreuzung beim Setzen
                // des Ziels schon näher liegt; erste Messung je Kreuzung sagt nichts an
                bool seen = _jPrev.TryGetValue(node, out float prev);
                _jPrev[node] = along;
                // Ziel liegt vor dem Abbiegen (Rest der Route hinter der Kreuzung kürzer als der Ankunftsabstand) -> keine Ansage
                float after = 0f;
                for (int k = i + 1; k < Route.Count && after < TDU2GpsSettings.ArriveDistance + 10f; k++) after += Flat(Route[k] - Route[k - 1]);
                if (after < TDU2GpsSettings.ArriveDistance + 10f) return;
                // wie im Original nur bei echter Annäherung (|Abstand - Schwelle| klein), nicht nach Sprüngen durch Neuberechnung
                if (seen && prev - along < 15f)
                {
                    if (prev > TurnNear && along <= TurnNear) Cue("now" + id, right ? TDU2GpsSettings.VoiceTurnRightNow : TDU2GpsSettings.VoiceTurnLeftNow);
                    else if (prev > TurnFar && along <= TurnFar) Cue("soon" + id, right ? TDU2GpsSettings.VoiceTurnRightSoon : TDU2GpsSettings.VoiceTurnLeftSoon);
                }
                return;   // nur die nächste Abbiegung
            }
        }

        V3 Dir(int i, int sign)
        {
            float acc = 0f; int j = i;
            // Route[0] = Auto, Route[^1] = Zielpunkt (kann neben der Straße liegen) -> nur Straßenpunkte für die Richtung
            while (acc < 25f) { int k = j + sign; if (k < 1 || k >= Route.Count - 1) break; acc += Flat(Route[k] - Route[j]); j = k; }
            var d = sign > 0 ? Route[j] - Route[i] : Route[i] - Route[j];
            d.Y = 0f;
            return d.LengthSquared() > 1f ? V3.Normalize(d) : V3.Zero;
        }

        void Cue(string id, int voice)
        {
            if (_lastCue == id) return;
            _lastCue = id;
            if (id != "uturn" && !_played.Add(id)) return;
            Voice(voice);
        }

        // ------------------------------------------------------------------ Stimme (Sound/Voices/<lang>/GPS.bnk)
        readonly AudioVoice _audio = new() { IgnorePause = true };   // Unity: ignoreListenerPause
        BnkArchive? _voiceBank; string? _voiceLang;
        readonly Dictionary<int, AudioClip?> _clips = new();

        /// <summary>Ansage Nummer <paramref name="index"/> (VFGPSVoice001..) abspielen; 0 = keine.</summary>
        public void Voice(int index)
        {
            if (index <= 0 || !TDU2GpsSettings.Voice) return;
            var clip = Clip(index);
            if (clip == null) { Log.Warn("GPS " + index + ": VFGPSVoice" + index.ToString("000") + "_.wav not found"); return; }
            // laufende Ansage nicht abbrechen: die neueste wartet und kommt danach
            if (_audio.IsPlaying) { _pendingVoice = index; return; }
            _audio.Stop();
            _audio.Clip = clip;
            _audio.Volume = TDU2GpsSettings.VoiceVolume;
            _audio.Play();
        }

        void PumpVoice()
        {
            if (_pendingVoice <= 0 || _audio.IsPlaying) return;
            int v = _pendingVoice; _pendingVoice = 0;
            Voice(v);
        }

        /// <summary>Anzahl der Ansagen in der GPS-Bank der aktuellen Sprache.</summary>
        public int VoiceCount { get { EnsureVoiceBank(); int n = 0; if (_voiceBank != null) foreach (var e in _voiceBank.All(".wav")) n++; return n; } }

        void EnsureVoiceBank()
        {
            string lang = string.IsNullOrEmpty(TDU2GpsSettings.VoiceLanguage) ? FlashHost.Language : TDU2GpsSettings.VoiceLanguage;
            if (_voiceBank != null && _voiceLang == lang) return;
            _voiceLang = lang; _voiceBank = null;
            _clips.Clear();
            string? bnk = _bnkDir;
            if (string.IsNullOrEmpty(bnk)) return;
            foreach (var l in new[] { lang, "us" })
            {
                string dir = Path.Combine(bnk, "sound", "voices", l);
                string? f = GameFiles.DirectoryExists(dir) ? CarDatabase.FindFile(dir, "gps.bnk") : null;
                if (f == null) continue;
                try { _voiceBank = BnkArchive.Open(f); break; } catch (Exception ex) { Log.Error("GPS-Voice: " + ex.Message); }
            }
        }

        AudioClip? Clip(int index)
        {
            EnsureVoiceBank();
            if (_voiceBank == null) return null;
            if (_clips.TryGetValue(index, out var c)) return c;
            var e = _voiceBank.Find("VFGPSVoice" + index.ToString("000") + "_.wav");
            c = e != null ? AudioClip.FromWav("GPS " + index, _voiceBank.Read(e)) : null;
            _clips[index] = c;
            return c;
        }

        // ------------------------------------------------------------------ HUD (hud.vmf /HUD/minimap)
        readonly List<(string path, float ang)> _dots = new();
        VmfPlayer? _dotsFor;
        bool _hudShown;
        readonly int[] _dotFrame = new int[64];

        void ShowStatus(bool removed)
        {
            var hud = _hud;
            if (hud == null) return;
            hud.SetVisible("/HUD/minimap/gps_status", true);
            hud.SetValue("/:IsGPSremoved", removed);
            hud.GotoLabel("/HUD/minimap/gps_status", "open", true);
        }

        void InitDots(VmfPlayer hud)
        {
            _dots.Clear(); _dotsFor = hud;
            var ring = hud.Find("/HUD/minimap/distance");
            if (ring?.Sprite == null) return;
            var pts = new List<(string, float, float)>();
            foreach (var d in ring.Sprite.List.Values)
                if (d.Name != null && d.Name.StartsWith("obj_dist_", StringComparison.OrdinalIgnoreCase)) pts.Add(("/HUD/minimap/distance/" + d.Name, d.Matrix.Tx, d.Matrix.Ty));
            if (pts.Count == 0) return;
            float cx = 0f, cy = 0f; foreach (var p in pts) { cx += p.Item2; cy += p.Item3; }
            cx /= pts.Count; cy /= pts.Count;
            foreach (var (path, x, y) in pts) _dots.Add((path, MathF.Atan2(x - cx, -(y - cy)) * (180f / MathF.PI)));   // 0° = oben (Fahrtrichtung), im Uhrzeigersinn
            for (int i = 0; i < _dotFrame.Length; i++) _dotFrame[i] = -1;
        }

        void UpdateHud(VmfPlayer? hud, V3? car, V3 forwardGame, bool active)
        {
            if (hud == null) return;
            if (!active)
            {
                if (_hudShown)
                {
                    _hudShown = false;
                    hud.SetVisible("/HUD/minimap/distance", false);
                    hud.SetVisible("/HUD/minimap/gps_distance", false);
                    hud.SetVariable("/:obj_Distance", ""); hud.SetVariable("/:gps_Unit", "");
                }
                _textHud = hud; _lastDistText = ""; _lastUnitText = "";
                return;
            }
            if (_textHud != hud) { _textHud = hud; _lastDistText = null; _lastUnitText = null; }
            if (_dotsFor != hud || _dots.Count == 0) InitDots(hud);
            _hudShown = true;
            hud.SetVisible("/HUD/minimap/distance", true);
            hud.SetVisible("/HUD/minimap/gps_distance", true);
            float m = Remaining >= 0f ? Remaining : 0f;
            var de = Paradise.Core.Util.GermanFormat.Number;
            string dist, unit;
            if (TDU2GpsSettings.Miles)
            {
                float mi = m / 1609.344f;
                dist = mi < 0.1f ? ((int)MathF.Round(m * 1.09361f)).ToString() : mi.ToString(mi < 10f ? "0.0" : "0", de);
                unit = mi < 0.1f ? "yd" : "mi";
            }
            else
            {
                dist = m < 1000f ? ((int)MathF.Round(m / 10f) * 10).ToString() : (m / 1000f).ToString(m < 10000f ? "0.0" : "0", de);
                unit = m < 1000f ? "m" : "km";
            }
            if (dist != _lastDistText) { hud.SetVariable("/:obj_Distance", dist); _lastDistText = dist; }
            if (unit != _lastUnitText) { hud.SetVariable("/:gps_Unit", unit); _lastUnitText = unit; }
            // Richtungs-Kranz: Punkte in Richtung Ziel (Luftlinie) hell, nach außen ausblendend
            if (car == null) return;
            var tw = TargetGame - car.Value; tw.Y = 0f;
            var fw = forwardGame; fw.Y = 0f;
            if (tw.LengthSquared() < 1e-4f || fw.LengthSquared() < 1e-6f) return;
            // Unity: Winkel im Weltraum (Spiel X gespiegelt)
            float bearing = SignedAngleUnity(new V3(-fw.X, 0f, fw.Z), new V3(-tw.X, 0f, tw.Z));
            for (int i = 0; i < _dots.Count && i < _dotFrame.Length; i++)
            {
                float diff = MathF.Abs(DeltaAngle(_dots[i].ang, bearing));
                float lit = System.Math.Clamp(1f - diff / TDU2GpsSettings.RingSpread, 0f, 1f);
                int frame = 1 + (int)MathF.Round((1f - lit) * 100f);   // Bild 1 = voll, 101 = unsichtbar
                if (frame == _dotFrame[i]) continue;
                _dotFrame[i] = frame;
                hud.SetFrame(_dots[i].path, frame);
            }
        }

        static float DeltaAngle(float a, float b)
        {
            float d = (b - a) % 360f;
            if (d > 180f) d -= 360f; else if (d < -180f) d += 360f;
            return d;
        }
    }

    /// <summary>GPS-Einstellungen (Grundwerte in Game/Config/GpsConfig.cs). Ansagen: Nummer der Datei VFGPSVoiceNNN_.wav in
    /// Sound/Voices/&lt;Sprache&gt;/GPS.bnk, 0 = keine.</summary>
    public static class TDU2GpsSettings
    {
        public static bool Voice = true;
        public static float VoiceVolume = 1f;
        /// <summary>Sprache der Ansagen ("ge", "us", "fr" …); leer = Spielsprache.</summary>
        public static string VoiceLanguage;
        public static int VoiceTargetSet = 0;
        public static int VoiceArrived = 0;
        public static int VoiceTurnLeftSoon = 0;
        public static int VoiceTurnRightSoon = 0;
        public static int VoiceTurnLeftNow = 0;
        public static int VoiceTurnRightNow = 0;
        public static int VoiceUTurn = 0;
        /// <summary>Ziel erreicht unter diesem Abstand (m).</summary>
        public static float ArriveDistance = 30f;
        /// <summary>Breite des Pfads relativ zum Sichtradius der Minimap.</summary>
        public static float PathWidth = 0.020f;
        public static Color32 PathColor = new Color32(255, 150, 20, 255);
        /// <summary>Öffnungswinkel des Richtungs-Kranzes (Grad bis ganz dunkel).</summary>
        public static float RingSpread = 30f;
        public static bool Miles = false;
        /// <summary>Entfernungsansagen wie im Original (3 / 2 / 1,5 / 1 / 0,5 km bzw. Meilen).</summary>
        public static bool DistanceVoices = true;
        /// <summary>Höchstabstand des Restwegs zur Schwelle für eine Entfernungsansage (m bzw. Milli-Meilen).</summary>
        public static float DistanceVoiceTolerance = 150f;
    }
}