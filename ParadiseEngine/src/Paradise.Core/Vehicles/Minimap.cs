using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using V3 = System.Numerics.Vector3;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Minimap wie im Spiel (HudMap.cpp), portiert aus TDU2Minimap (Unity): Straßennetz aus &lt;Insel&gt;.bnk / &lt;Insel&gt;_ROAD_DETAIL.IA,
    /// eingeblendet im Flash-Clip /HUD/minimap aus frontend/&lt;res&gt;/hud.bnk an Stelle des Platzhalters "mmap_lure". Rahmen,
    /// Hintergrund, Spielerpfeil ("cursor") und Nordmarke ("north") sind die Originalgrafiken. Fahrtrichtung oben, Zoom nach Tempo.
    /// Engine: die Straßen werden statt mit einer Ortho-Kamera in eine Textur gerastert (gleiche Breiten/Farben/Reihenfolge).
    ///
    /// ROAD_DETAIL.IA ("DI41DAEH"):
    ///   u32 Anzahl Datensätze, danach je Datensatz "ATED", u32 id, u32 nSeg, nSeg x u32 Offset (relativ zum Datensatz)
    ///   Segment: u16 nPunkte, u16 Straßentyp (1 Feldweg, 3 Stadt, 4/5/6 Land/Haupt), u32, u32 Flags, u32 Hash, u8, u8, u16
    ///            Punkt 0: f32 x,y,z + Pad (16 B); je Bézierstück: Kontrollpunkt 1 (12 B), Kontrollpunkt 2 (12 B), Anker (16 B)
    ///   Koordinaten im Spielraum.
    /// </summary>
    public sealed class Minimap
    {
        public bool ShowMinimap = true;
        /// <summary>Sichtradius in Metern im Stand / bei hoher Geschwindigkeit</summary>
        public float RangeSlow = 170f, RangeFast = 420f;
        // Farben wie GamePC.cpr [HudMap] (HIGHWAY 29 180 215, ROAD 0 255 255)
        public Color32 RoadMain = new Color32(29, 180, 215, 255);
        public Color32 RoadTown = new Color32(0, 233, 233, 255);
        public Color32 RoadDirt = new Color32(181, 120, 2, 255);
        public bool OnlineStatus = false;
        public int PlayerCount = 1;
        /// <summary>Aktualisierungen der Kartentextur je Sekunde (Unity: jedes Bild per Kamera).</summary>
        public float RefreshRate = 20f;
        public string? Error { get; private set; }

        // mmap_lure: Shape 0..108.75 x 0..98 skaliert 1.67/1.84 -> Kartenfeld 181.6 x 180.3 (Bühnenpixel) im Clip minimap
        static readonly Vector2 LureCenter = new Vector2(90.8f, 90.2f);
        const float NorthRadius = 80f;
        static readonly string[] HiddenMinimapParts =
        {
            "objectif_01", "objectif_02", "objectif_03", "objectif_04", "objectif_05", "objectif_06", "objectif_07", "objectif_08",
            "barrage", "gps_status", "emp", "distance", "gps_distance",
        };

        sealed class Segment { public int Type; public V3[] Pts = System.Array.Empty<V3>(); }
        /// <summary>Abgetastete Straßenlinie (Spiel-XZ), Klasse 1 Feldweg, 3 Stadt, 6 Land/Haupt.</summary>
        struct Line { public float Ax, Az, Bx, Bz; public byte Cls; public float W; }   // W = Breitenfaktor (Kreuzungsscheibe 1,15/1,1)
        /// <summary>Kreuzungsfläche (JDET, Unity AddJunctionAreas): Umrisse (x,z) – gefüllt nach Gerade-Ungerade-Regel, so bleibt
        /// bei Kreiseln die Mittelinsel (innerer Umriss) frei.</summary>
        sealed class Area { public List<float[]> Rings = new(); public byte Cls; public float MinX, MinZ, MaxX, MaxZ; }

        VmfPlayer? _hud;
        string? _bnkDir;
        /// <summary>Datenbank (Händler-Logos) – Unity: TDU2VehicleManager setzt _minimap.Database.</summary>
        public CarDatabase? Database;
        bool _enterShown;
        string? _enterLabel;
        /// <summary>HUD-Film ohne Minimap zeigen (Fahrzeugmenü im Auto beim Händler, /HUD/ig_menu) – Unity TDU2Minimap.HudOverlay.</summary>
        public static bool HudOverlay;
        bool _overlayActive;
        /// <summary>hud.bnk (Minimap, Radio-Anzeige /HUD/radio – Unity TDU2Radio.SetHudPlayer).</summary>
        public VmfPlayer? Hud => _hud;
        string? _hudBank, _bank;
        Task<(Line[] lines, Dictionary<long, List<int>> grid, int segs, Area[] areas)>? _task;
        Line[] _lines = System.Array.Empty<Line>();
        Area[] _areas = System.Array.Empty<Area>();
        Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
        const float Cell = 128f;
        Texture2D? _tex;
        /// <summary>GPS (Unity: von der Minimap angelegt) – Ziel, Route, Anzeige in hud.bnk.</summary>
        public readonly Gps Gps = new();
        Color32[]? _px;
        byte[]? _bytes;
        int _texSize;
        float _range, _refreshT;
        bool _visible;
        readonly HashSet<int> _seen = new HashSet<int>();
        readonly List<int> _vis = new List<int>();

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        /// <summary>hud.bnk laden (einmal) und Straßennetz der Insel (Hintergrund-Task) – Unity Bind.</summary>
        public void Bind(string? bnkDir, string? islandBank)
        {
            _bnkDir = bnkDir;
            LoadHud(bnkDir);
            Gps.Bind(bnkDir, islandBank);
            if (islandBank == null || islandBank == _bank) return;
            _bank = islandBank; Error = null;
            _lines = System.Array.Empty<Line>(); _grid = new Dictionary<long, List<int>>();
            string b = islandBank;
            // Laden + Aufbereiten (Kurven, Kreuzungen, Raster) komplett im Hintergrund
            _areas = System.Array.Empty<Area>();
            _task = Task.Run(() => { var (segs, jn) = Load(b); var (l, g) = Build(segs); return (l, g, segs.Count, BuildAreas(jn, segs)); });
        }

        void LoadHud(string? bnkDir)
        {
            if (bnkDir == null || _hud != null) return;
            string? bank = null;
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                bank = CarDatabase.FindFile(System.IO.Path.Combine(bnkDir, "frontend", res), "hud.bnk");
                if (bank != null) break;
            }
            if (bank == null || bank == _hudBank) { if (bank == null) Error = "frontend/*/hud.bnk fehlt"; return; }
            try
            {
                _hud = FlashHost.Load(bank, 1, true, p => { });
                if (_hud == null) { Error = "hud.bnk nicht ladbar"; return; }
                _hudBank = bank;
                _hud.GotoLabel("/", "hud", false);
                _hud.GotoLabel("/HUD", "16_9", false);
                _hud.GotoLabel("/HUD/minimap", "default", false);
                // nur die Minimap aus hud.vmf zeigen (Rest des Spiel-HUDs: Rennen, Telefon, Hilfe ...)
                foreach (var d in _hud.Root.List.Values) d.Visible = string.Equals(d.Name, "HUD", StringComparison.OrdinalIgnoreCase);
                var hudClip = _hud.Find("/HUD");
                if (hudClip?.Sprite != null)
                    foreach (var d in hudClip.Sprite.List.Values)
                        d.Visible = string.Equals(d.Name, "minimap", StringComparison.OrdinalIgnoreCase) || string.Equals(d.Name, "enter", StringComparison.OrdinalIgnoreCase);


                _hud.GotoLabel("/HUD/enter", "off", false);
                _enterShown = false;
                foreach (var n in HiddenMinimapParts) _hud.SetVisible("/HUD/minimap/" + n, false);
                // Platzhaltertexte der Navi-Anzeige ("2500" + Einheit) leeren – ohne aktive Route zeigt das Spiel nichts
                _hud.SetVariable("/:obj_Distance", ""); _hud.SetVariable("/:gps_Unit", "");
                _hud.SetPosition("/HUD/minimap/cursor", LureCenter);
                _hud.GotoLabel("/HUD/minimap/ic_status", OnlineStatus ? "connected" : "disconnected", true);
                _hud.SetVariable("/:IsOnline", OnlineStatus ? "1" : "0");
                _hud.SetVariable("/:NbPlayers", PlayerCount.ToString());
                _hud.SetVariable("/:nb_FRplayers", PlayerCount.ToString());
                _hud.SetVariable("/:nb_players", PlayerCount.ToString());
                //_hud.Visible = false;
                Log.Info("Flash: hud.bnk (Minimap) geladen");
            }
            catch (Exception ex)
            {
                Error = "hud.bnk nicht nutzbar: " + ex.Message;
                Log.Warn("Minimap: " + Error);
                if (_hud != null) { FlashHost.Remove(_hud); _hud = null; }
                _hudBank = null;
            }
        }

        static (List<Segment> segs, List<List<float[]>> junctions) Load(string bankPath)
        {
            var bank = BnkArchive.Open(bankPath);
            BnkEntry? e = null;
            foreach (var x in bank.Entries) if (x.Name.IndexOf("ROAD_DETAIL", StringComparison.OrdinalIgnoreCase) >= 0) { e = x; break; }
            if (e == null) throw new System.IO.FileNotFoundException("ROAD_DETAIL.IA nicht in " + System.IO.Path.GetFileName(bankPath));
            var jn = new List<List<float[]>>();
            var segs = Parse(bank.Read(e), jn);
            return (segs, jn);
        }

        static List<Segment> Parse(byte[] d, List<List<float[]>> junctions)
        {
            var list = new List<Segment>();
            if (d.Length < 12 || d[0] != (byte)'D' || d[1] != (byte)'I') throw new System.IO.InvalidDataException("kein ROAD_DETAIL (DI41DAEH)");
            int n = BitConverter.ToInt32(d, 8), p = 12;
            for (int r = 0; r < n && p + 12 <= d.Length; r++)
            {
                if (d[p] != (byte)'A' || d[p + 1] != (byte)'T' || d[p + 2] != (byte)'E' || d[p + 3] != (byte)'D') break;
                int ns = BitConverter.ToInt32(d, p + 8);
                int end = p + 12 + 4 * ns;
                for (int s = 0; s < ns; s++)
                {
                    int q = p + BitConverter.ToInt32(d, p + 12 + 4 * s);
                    if (q < 0 || q + 36 > d.Length) continue;
                    int np = BitConverter.ToUInt16(d, q), type = BitConverter.ToUInt16(d, q + 2);
                    q += 20;
                    int pieces = System.Math.Max(0, (np - 1) / 3);
                    if (q + 16 + pieces * 40 > d.Length) continue;
                    var pts = new V3[1 + 3 * pieces];
                    pts[0] = V(d, q); q += 16;
                    for (int k = 0; k < pieces; k++)
                    {
                        pts[1 + 3 * k] = V(d, q); pts[2 + 3 * k] = V(d, q + 12); pts[3 + 3 * k] = V(d, q + 24);
                        q += 40;
                    }
                    if (pieces > 0) list.Add(new Segment { Type = type, Pts = pts });
                    if (q > end) end = q;
                }
                p = end;
            }
            // Kreuzungsflächen (JDET) am Ende von ROAD_DETAIL (Unity IaNetwork.Parse): u32 Anzahl, je Eintrag "JDET", u32 Knoten,
            // u8[3] Punktzahlen, 16 Byte 0, dann je Umriss (f32 x, f32 z)-Punkte
            try
            {
                int nJ = p + 4 <= d.Length ? BitConverter.ToInt32(d, p) : 0;
                int j = p + 4;
                for (int i = 0; i < nJ && j + 28 <= d.Length && d[j] == (byte)'T' && d[j + 1] == (byte)'E' && d[j + 2] == (byte)'D' && d[j + 3] == (byte)'J'; i++)
                {
                    int q = j + 28;
                    var rings = new List<float[]>();
                    foreach (int cnt in new[] { (int)d[j + 8], d[j + 9], d[j + 10] })
                    {
                        if (cnt <= 0 || q + 8 * cnt > d.Length) { q += 8 * System.Math.Max(0, cnt); continue; }
                        var pts = new float[cnt * 2];
                        for (int k = 0; k < cnt; k++) { pts[2 * k] = BitConverter.ToSingle(d, q); pts[2 * k + 1] = BitConverter.ToSingle(d, q + 4); q += 8; }
                        rings.Add(pts);
                    }
                    junctions.Add(rings);
                    j = q;
                }
            }
            catch (Exception) { /* JDET optional */ }
            return list;
        }

        /// <summary>
        /// Unity AddJunctionAreas: Umrisse bereinigen (Punkte &gt; 120 m vom Median weg, z.B. Ibiza JDET 14), Farbe nach der
        /// anschließenden Straße (Haupt-/Landstraße, sonst Stadt; Feldwege haben keine Kreuzungsflächen).
        /// Engine: die Straßenklasse kommt vom nächsten Straßenende (statt über ROAD_MAP-Knoten).
        /// </summary>
        static Area[] BuildAreas(List<List<float[]>> junctions, List<Segment> segs)
        {
            var res = new List<Area>();
            foreach (var rings in junctions)
            {
                var a = new Area { MinX = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxZ = float.MinValue };
                foreach (var o in rings)
                {
                    var c = CleanOutline(o);
                    if (c == null) continue;
                    a.Rings.Add(c);
                    for (int i = 0; i < c.Length; i += 2)
                    {
                        a.MinX = MathF.Min(a.MinX, c[i]); a.MaxX = MathF.Max(a.MaxX, c[i]);
                        a.MinZ = MathF.Min(a.MinZ, c[i + 1]); a.MaxZ = MathF.Max(a.MaxZ, c[i + 1]);
                    }
                }
                if (a.Rings.Count == 0) continue;
                float cx = (a.MinX + a.MaxX) * 0.5f, cz = (a.MinZ + a.MaxZ) * 0.5f, best = float.MaxValue;
                int type = 3;
                foreach (var sg in segs)
                    foreach (var e in new[] { sg.Pts[0], sg.Pts[^1] })
                    {
                        float d2 = (e.X - cx) * (e.X - cx) + (e.Z - cz) * (e.Z - cz);
                        if (d2 < best) { best = d2; type = sg.Type; }
                    }
                a.Cls = (byte)(type == 1 || type == 3 ? 3 : 6);
                res.Add(a);
            }
            return res.ToArray();
        }

        static float[]? CleanOutline(float[] o)
        {
            int n = o.Length / 2;
            if (n < 3) return null;
            var xs = new float[n]; var zs = new float[n];
            for (int i = 0; i < n; i++) { xs[i] = o[2 * i]; zs[i] = o[2 * i + 1]; }
            System.Array.Sort(xs); System.Array.Sort(zs);
            float mx = xs[n / 2], mz = zs[n / 2];
            var r = new List<float>(o.Length);
            for (int i = 0; i < n; i++)
            {
                float dx = o[2 * i] - mx, dz = o[2 * i + 1] - mz;
                if (dx * dx + dz * dz <= 120f * 120f) { r.Add(o[2 * i]); r.Add(o[2 * i + 1]); }
            }
            return r.Count < 6 ? null : r.ToArray();
        }

        static V3 V(byte[] d, int o) => new V3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));

        /// <summary>Bézierkurven wie Unity BuildRoadMesh abtasten (Schritte ~20 m, 1..16 je Stück) und ins Raster legen.</summary>
        static (Line[] lines, Dictionary<long, List<int>> grid) Build(List<Segment> segs)
        {
            var lines = new List<Line>(segs.Count * 8);
            var ends = new List<(float x, float z, byte cls, float dx, float dz)>();
            var samples = new List<(float x, float z, int seg)>();
            foreach (var s in segs)
            {
                byte cls = (byte)(s.Type == 1 ? 1 : s.Type == 3 ? 3 : 6);
                // Endrichtung nach außen (Bézier-Tangente am Anfang/Ende)
                V3 p0 = s.Pts[0], p1 = s.Pts[1], pn = s.Pts[^1], pm = s.Pts[^2];
                int segId = ends.Count;
                ends.Add((p0.X, p0.Z, cls, Dir(p0.X - p1.X, p0.Z - p1.Z).x, Dir(p0.X - p1.X, p0.Z - p1.Z).z));
                ends.Add((pn.X, pn.Z, cls, Dir(pn.X - pm.X, pn.Z - pm.Z).x, Dir(pn.X - pm.X, pn.Z - pm.Z).z));
                var prev = s.Pts[0];
                samples.Add((prev.X, prev.Z, segId));
                for (int k = 0; k + 3 < s.Pts.Length; k += 3)
                {
                    V3 a = s.Pts[k], b = s.Pts[k + 1], c = s.Pts[k + 2], e = s.Pts[k + 3];
                    float len = (b - a).Length() + (c - b).Length() + (e - c).Length();
                    int steps = System.Math.Clamp((int)MathF.Ceiling(len / 20f), 1, 16);
                    for (int i = 1; i <= steps; i++)
                    {
                        float t = i / (float)steps, u = 1 - t;
                        V3 pt = u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * e;
                        lines.Add(new Line { Ax = prev.X, Az = prev.Z, Bx = pt.X, Bz = pt.Z, Cls = cls, W = 1f });
                        prev = pt;
                        samples.Add((pt.X, pt.Z, segId));
                    }
                }
            }
            AddJunctions(ends, samples, lines);
            var arr = lines.ToArray();
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < arr.Length; i++)
            {
                var l = arr[i];
                int x0 = (int)MathF.Floor(MathF.Min(l.Ax, l.Bx) / Cell), x1 = (int)MathF.Floor(MathF.Max(l.Ax, l.Bx) / Cell);
                int z0 = (int)MathF.Floor(MathF.Min(l.Az, l.Bz) / Cell), z1 = (int)MathF.Floor(MathF.Max(l.Az, l.Bz) / Cell);
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long k = Key(x, z);
                        if (!grid.TryGetValue(k, out var lst)) grid[k] = lst = new List<int>();
                        lst.Add(i);
                    }
            }
            return (arr, grid);
        }

        static (float x, float z) Dir(float x, float z)
        {
            float l = MathF.Sqrt(x * x + z * z);
            return l > 1e-3f ? (x / l, z / l) : (0f, 0f);
        }

        /// <summary>
        /// Unity AddJunctions: Segmentenden zusammenführen (unter 22 m, oder bis 45 m wenn beide Enden aufeinander zeigen),
        /// je Gruppe Linien zum Mittelpunkt + Kreuzungsscheibe (größte Breite x 1,15); offene Enden in Fahrtrichtung bis zur
        /// nächsten Straße verlängern (+ Scheibe x 1,1). Die Straßen enden im Spiel an den Kreuzungsrändern, der Belag fehlt sonst.
        /// </summary>
        static void AddJunctions(List<(float x, float z, byte cls, float dx, float dz)> ends, List<(float x, float z, int seg)> samples, List<Line> lines)
        {
            const float RNear = 22f, RFar = 45f;
            int n = ends.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int gx = (int)MathF.Floor(ends[i].x / RFar), gz = (int)MathF.Floor(ends[i].z / RFar);
                for (int ddx = -1; ddx <= 1; ddx++)
                    for (int ddz = -1; ddz <= 1; ddz++)
                        if (grid.TryGetValue(Key(gx + ddx, gz + ddz), out var l))
                            foreach (int j in l)
                            {
                                if ((i >> 1) == (j >> 1)) continue;   // beide Enden desselben Segments
                                float vx = ends[j].x - ends[i].x, vz = ends[j].z - ends[i].z;
                                float dist = MathF.Sqrt(vx * vx + vz * vz);
                                if (dist >= RFar) continue;
                                bool join = dist < RNear;
                                if (!join && dist > 1e-3f)
                                {
                                    float ux = vx / dist, uz = vz / dist;
                                    join = ends[i].dx * ux + ends[i].dz * uz > 0.5f && -(ends[j].dx * ux + ends[j].dz * uz) > 0.5f;
                                }
                                if (join) parent[Find(i)] = Find(j);
                            }
                if (!grid.TryGetValue(Key(gx, gz), out var own)) grid[Key(gx, gz)] = own = new List<int>();
                own.Add(i);
            }
            var clusters = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!clusters.TryGetValue(r, out var l)) clusters[r] = l = new List<int>();
                l.Add(i);
            }
            var singles = new List<int>();
            foreach (var cl in clusters.Values)
            {
                if (cl.Count < 2) { singles.Add(cl[0]); continue; }
                float cx = 0, cz = 0; byte maxCls = 0; float spread = 0f;
                foreach (int i in cl) { cx += ends[i].x; cz += ends[i].z; }
                cx /= cl.Count; cz /= cl.Count;
                foreach (int i in cl)
                {
                    spread = MathF.Max(spread, MathF.Sqrt((ends[i].x - cx) * (ends[i].x - cx) + (ends[i].z - cz) * (ends[i].z - cz)));
                    if (HalfOf(ends[i].cls) > HalfOf(maxCls)) maxCls = ends[i].cls;
                }
                if (spread < 1f) continue;   // reine Fortsetzung (Enden liegen aufeinander)
                foreach (int i in cl)
                {
                    float ex = ends[i].x - cx, ez = ends[i].z - cz;
                    if (ex * ex + ez * ez > 0.25f) lines.Add(new Line { Ax = ends[i].x, Az = ends[i].z, Bx = cx, Bz = cz, Cls = ends[i].cls, W = 1f });
                }
                lines.Add(new Line { Ax = cx, Az = cz, Bx = cx, Bz = cz, Cls = maxCls, W = 1.15f });
            }
            if (singles.Count == 0) return;
            // offene Enden in Fahrtrichtung bis zur nächsten Straße verlängern
            var sgrid = new Dictionary<long, List<int>>();
            for (int k = 0; k < samples.Count; k++)
            {
                long key = Key((int)MathF.Floor(samples[k].x / RFar), (int)MathF.Floor(samples[k].z / RFar));
                if (!sgrid.TryGetValue(key, out var l)) sgrid[key] = l = new List<int>();
                l.Add(k);
            }
            foreach (int i in singles)
            {
                var e = ends[i];
                if (e.dx == 0f && e.dz == 0f) continue;
                int gx = (int)MathF.Floor(e.x / RFar), gz = (int)MathF.Floor(e.z / RFar);
                float best = float.MaxValue, tx = 0, tz = 0;
                for (int ddx = -1; ddx <= 1; ddx++)
                    for (int ddz = -1; ddz <= 1; ddz++)
                        if (sgrid.TryGetValue(Key(gx + ddx, gz + ddz), out var l))
                            foreach (int k in l)
                            {
                                if (samples[k].seg == (i & ~1)) continue;   // eigenes Segment
                                float vx = samples[k].x - e.x, vz = samples[k].z - e.z;
                                float dist = MathF.Sqrt(vx * vx + vz * vz);
                                if (dist < 1f || dist >= RFar || dist >= best) continue;
                                if ((e.dx * vx + e.dz * vz) / dist > 0.8f) { best = dist; tx = samples[k].x; tz = samples[k].z; }
                            }
                if (best < RFar)
                {
                    lines.Add(new Line { Ax = e.x, Az = e.z, Bx = tx, Bz = tz, Cls = e.cls, W = 1f });
                    lines.Add(new Line { Ax = tx, Az = tz, Bx = tx, Bz = tz, Cls = e.cls, W = 1.1f });
                }
            }
        }

        static float HalfOf(byte cls) => cls == 0 ? 0f : cls == 1 ? 3.2f : cls == 3 ? 3.8f : 5.5f;

        /// <summary>Straßenart an einer Stelle (Spielkoordinaten): 1 Feldweg, 3 Stadt, 6 Land/Haupt; 0 = keine Straße im Umkreis.
        /// Passt nicht zum Orginal spiel, Wir haben "Autobahnen" was eigentlich keine Sind.
        /// TODO:Recheck</summary>
        public int NearestRoadType(V3 game, float maxDist)
        {
            if (_lines.Length == 0) return 0;
            int cx = (int)MathF.Floor(game.X / Cell), cz = (int)MathF.Floor(game.Z / Cell);
            float best = maxDist * maxDist; int type = 0;
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int z = cz - 1; z <= cz + 1; z++)
                {
                    if (!_grid.TryGetValue(Key(x, z), out var lst)) continue;
                    foreach (int i in lst)
                    {
                        var l = _lines[i];
                        float d2 = SegDist2(game.X, game.Z, l.Ax, l.Az, l.Bx, l.Bz);
                        if (d2 < best) { best = d2; type = l.Cls; }
                    }
                }
            return type;
        }

        /// <summary>Nächster Straßenpunkt im Umkreis maxDist (Spiel-XZ, Y = 0) – Unity NearestRoad (Teleport der großen Karte).</summary>
        public bool NearestRoad(V3 game, float maxDist, out V3 point)
        {
            point = default;
            if (_lines.Length == 0) return false;
            int r = (int)MathF.Ceiling(maxDist / Cell);
            int cx = (int)MathF.Floor(game.X / Cell), cz = (int)MathF.Floor(game.Z / Cell);
            float best = maxDist * maxDist; bool found = false;
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (!_grid.TryGetValue(Key(x, z), out var lst)) continue;
                    foreach (int i in lst)
                    {
                        var l = _lines[i];
                        float dx = l.Bx - l.Ax, dz = l.Bz - l.Az, l2 = dx * dx + dz * dz;
                        float t = l2 > 1e-8f ? System.Math.Clamp(((game.X - l.Ax) * dx + (game.Z - l.Az) * dz) / l2, 0f, 1f) : 0f;
                        float px = l.Ax + dx * t, pz = l.Az + dz * t, ex = px - game.X, ez = pz - game.Z, d2 = ex * ex + ez * ez;
                        if (d2 < best) { best = d2; point = new V3(px, 0f, pz); found = true; }
                    }
                }
            return found;
        }

        static float SegDist2(float px, float pz, float ax, float az, float bx, float bz)
        {
            float dx = bx - ax, dz = bz - az;
            float l2 = dx * dx + dz * dz;
            float t = l2 > 1e-8f ? System.Math.Clamp(((px - ax) * dx + (pz - az) * dz) / l2, 0f, 1f) : 0f;
            float ex = ax + dx * t - px, ez = az + dz * t - pz;
            return ex * ex + ez * ez;
        }

        /// <summary>
        /// Unity LateUpdate. carGame/forwardGame/rightGame = Lage des Autos in Spielkoordinaten (Blickrichtung oben, rechts = rechts im Bild),
        /// unityForward = Vorwärts im Unity-Raum (Nordmarke wie im Unity-Projekt).
        /// </summary>
        public void LateUpdate(float dt, bool active, V3 carGame, V3 forwardGame, V3 rightGame, V3 unityForward, float speedKmh)
        {
            if (_task != null && _task.IsCompleted)
            {
                if (_task.IsFaulted) { Error = _task.Exception?.GetBaseException().Message; Log.Warn("Minimap: " + Error); }
                else
                {
                    var res = _task.Result;
                    _lines = res.lines; _grid = res.grid; _areas = res.areas;
                    Log.Info($"Minimap: {res.segs} Straßensegmente, {_lines.Length} Linien, {_areas.Length} Kreuzungsflächen");
                }
                _task = null;
            }
            Gps.Update(dt, _hud, carGame, forwardGame);
            bool show = ShowMinimap && active;
            bool overlay = HudOverlay && !show;
            if (_hud == null) return;
            if (_visible != (show || overlay)) { _visible = show || overlay; _hud.Visible = _visible; }
            if (overlay != _overlayActive)
            {
                _overlayActive = overlay;
                _hud.SetVisible("/HUD/minimap", !overlay);
                _hud.SetVisible("/HUD/enter", !overlay);
            }
            if (!show || _lines.Length == 0) return;

            int px = 192;
            var r = _hud.ScreenRect("/HUD/minimap/mmap_lure");
            if (r.HasValue) px = System.Math.Clamp((int)MathF.Round(MathF.Max(r.Value.width, r.Value.height)), 64, 512);
            float target = Mathf.Lerp(RangeSlow, RangeFast, Mathf.Clamp01(speedKmh / 220f));
            _range = _range <= 0 ? target : Mathf.Lerp(_range, target, 1f - MathF.Exp(-dt * 1.5f));

            // Nordmarke wandert am Kartenrand entlang (Bühne: y nach unten)
            float h = MathF.Atan2(unityForward.X, unityForward.Z);
            var dir = new Vector2(-MathF.Sin(h), -MathF.Cos(h));
            float kk = (NorthRadius / MathF.Max(MathF.Abs(dir.x), MathF.Abs(dir.y))) * 1.45f;
            _hud.SetPosition("/HUD/minimap/north", LureCenter + dir * kk);

            _refreshT -= dt;
            if (_refreshT > 0f && _tex != null && _texSize == px) return;
            _refreshT = 1f / MathF.Max(1f, RefreshRate);
            Render(px, carGame, forwardGame, rightGame);
            _hud.SetDynamicTexture("mmap_lure", _tex!, true);
        }

        /// <summary>
        /// Straßen von oben in die Textur zeichnen (Unity: Ortho-Kamera, orthographicSize = Sichtradius, Bild-oben = Fahrtrichtung):
        /// Feldwege unten, Hauptstraßen oben; halbe Breiten 3,2 / 3,8 / 5,5 m; Kanten geglättet. Zeile 0 = unten (upright).
        /// </summary>
        void Render(int px, V3 car, V3 fwd, V3 right)
        {
            if (_px == null || _texSize != px) { _px = new Color32[px * px]; _texSize = px; _tex?.Dispose(); _tex = new Texture2D(px, px); }
            System.Array.Clear(_px);
            float scale = px / (2f * _range);   // Pixel je Meter
            float cxp = px * 0.5f, cyp = px * 0.5f;
            fwd.Y = 0; right.Y = 0;
            fwd = fwd.LengthSquared() > 1e-6f ? V3.Normalize(fwd) : V3.UnitZ;
            right = right.LengthSquared() > 1e-6f ? V3.Normalize(right) : V3.UnitX;
            float reach = _range * 1.45f + 8f;
            int c0x = (int)MathF.Floor((car.X - reach) / Cell), c1x = (int)MathF.Floor((car.X + reach) / Cell);
            int c0z = (int)MathF.Floor((car.Z - reach) / Cell), c1z = (int)MathF.Floor((car.Z + reach) / Cell);
            var seen = _seen; seen.Clear();
            var vis = _vis; vis.Clear();
            for (int x = c0x; x <= c1x; x++)
                for (int z = c0z; z <= c1z; z++)
                    if (_grid.TryGetValue(Key(x, z), out var lst))
                        foreach (int i in lst) if (seen.Add(i)) vis.Add(i);
            // Kreuzungsflächen / Kreisel (JDET) unter den Straßen derselben Klasse
            foreach (var ar in _areas)
            {
                if (ar.MaxX < car.X - reach || ar.MinX > car.X + reach || ar.MaxZ < car.Z - reach || ar.MinZ > car.Z + reach) continue;
                FillArea(ar, car, fwd, right, scale, cxp, cyp, ar.Cls == 6 ? RoadMain : RoadTown, px);
            }
            foreach (byte pass in new byte[] { 1, 3, 6 })
            {
                var col = pass == 1 ? RoadDirt : pass == 3 ? RoadTown : RoadMain;
                float half = MathF.Max(HalfOf(pass) * scale, 0.75f);
                foreach (int i in vis)
                {
                    var l = _lines[i];
                    if (l.Cls != pass) continue;
                    float ax = (l.Ax - car.X) * right.X + (l.Az - car.Z) * right.Z, ay = (l.Ax - car.X) * fwd.X + (l.Az - car.Z) * fwd.Z;
                    float bx = (l.Bx - car.X) * right.X + (l.Bz - car.Z) * right.Z, by = (l.Bx - car.X) * fwd.X + (l.Bz - car.Z) * fwd.Z;
                    DrawLine(cxp + ax * scale, cyp + ay * scale, cxp + bx * scale, cyp + by * scale, half * l.W, col, px);
                }
            }
            // GPS-Route über den Straßen (Unity DrawGpsRoute: Band, Breite relativ zum Sichtradius)
            var route = Gps.HasTarget ? Gps.Route : null;
            if (route != null && route.Count >= 2)
            {
                float half = MathF.Max(1.5f, _range * TDU2GpsSettings.PathWidth) * scale;
                var pc = TDU2GpsSettings.PathColor;
                for (int i = 0; i + 1 < route.Count; i++)
                {
                    var a = route[i]; var b = route[i + 1];
                    float ax = (a.X - car.X) * right.X + (a.Z - car.Z) * right.Z, ay = (a.X - car.X) * fwd.X + (a.Z - car.Z) * fwd.Z;
                    float bx = (b.X - car.X) * right.X + (b.Z - car.Z) * right.Z, by = (b.X - car.X) * fwd.X + (b.Z - car.Z) * fwd.Z;
                    if (MathF.Max(MathF.Abs(ax), MathF.Abs(bx)) > reach && MathF.Max(MathF.Abs(ay), MathF.Abs(by)) > reach
                        && System.Math.Sign(ax) == System.Math.Sign(bx) && System.Math.Sign(ay) == System.Math.Sign(by)) continue;
                    DrawLine(cxp + ax * scale, cyp + ay * scale, cxp + bx * scale, cyp + by * scale, half, pc, px);
                }
            }
            // Pixel -> RGBA; erste Textur anlegen, danach nur den Inhalt ersetzen (ohne GPU-Warten)
            _bytes ??= new byte[px * px * 4];
            if (_bytes.Length != px * px * 4) _bytes = new byte[px * px * 4];
            for (int i = 0; i < _px.Length; i++)
            {
                var c = _px[i];
                int o = i * 4;
                _bytes[o] = c.r;
                _bytes[o + 1] = c.g;
                _bytes[o + 2] = c.b;
                //Orginal: ist Dunkler Background
                _bytes[o + 3] = 64;// c.a; 
            }
            if (_tex!.Gpu == null) { _tex.SetPixels32(_px); _tex.Apply(); }
            else FlashHost.Renderer?.UpdateTexture(_tex.Gpu, _bytes);
        }

        readonly List<float> _xs = new List<float>();

        /// <summary>Kreuzungsfläche füllen (Zeilen-Scan, Gerade-Ungerade-Regel über alle Umrisse: Mittelinsel bleibt frei).</summary>
        void FillArea(Area ar, V3 car, V3 fwd, V3 right, float scale, float cxp, float cyp, Color32 col, int px)
        {
            var pts = new List<float[]>(ar.Rings.Count);
            float y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var r in ar.Rings)
            {
                var q = new float[r.Length];
                for (int i = 0; i < r.Length; i += 2)
                {
                    float dx = r[i] - car.X, dz = r[i + 1] - car.Z;
                    q[i] = cxp + (dx * right.X + dz * right.Z) * scale;
                    q[i + 1] = cyp + (dx * fwd.X + dz * fwd.Z) * scale;
                    y0 = MathF.Min(y0, q[i + 1]); y1 = MathF.Max(y1, q[i + 1]);
                }
                pts.Add(q);
            }
            int ya = System.Math.Max(0, (int)MathF.Floor(y0)), yb = System.Math.Min(px - 1, (int)MathF.Ceiling(y1));
            var p = _px!;
            for (int y = ya; y <= yb; y++)
            {
                float sy = y + 0.5f;
                _xs.Clear();
                foreach (var q in pts)
                {
                    int n = q.Length / 2;
                    for (int i = 0, k = n - 1; i < n; k = i++)
                    {
                        float ay = q[2 * i + 1], by = q[2 * k + 1];
                        if ((ay > sy) == (by > sy)) continue;
                        float ax = q[2 * i], bx = q[2 * k];
                        _xs.Add(ax + (sy - ay) * (bx - ax) / (by - ay));
                    }
                }
                if (_xs.Count < 2) continue;
                _xs.Sort();
                for (int i = 0; i + 1 < _xs.Count; i += 2)
                {
                    int xa = System.Math.Max(0, (int)MathF.Ceiling(_xs[i] - 0.5f)), xb = System.Math.Min(px - 1, (int)MathF.Floor(_xs[i + 1] - 0.5f));
                    for (int x = xa; x <= xb; x++) p[y * px + x] = col;
                }
            }
        }

        void DrawLine(float ax, float ay, float bx, float by, float half, Color32 col, int px)
        {
            float pad = half + 1f;
            int x0 = System.Math.Max(0, (int)MathF.Floor(MathF.Min(ax, bx) - pad)), x1 = System.Math.Min(px - 1, (int)MathF.Ceiling(MathF.Max(ax, bx) + pad));
            int y0 = System.Math.Max(0, (int)MathF.Floor(MathF.Min(ay, by) - pad)), y1 = System.Math.Min(px - 1, (int)MathF.Ceiling(MathF.Max(ay, by) + pad));
            if (x0 > x1 || y0 > y1) return;
            var p = _px!;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = MathF.Sqrt(SegDist2(x + 0.5f, y + 0.5f, ax, ay, bx, by));
                    float a = half + 0.5f - d;
                    if (a <= 0f) continue;
                    if (a > 1f) a = 1f;
                    int o = y * px + x;
                    var dst = p[o];
                    // über die darunterliegende Straße (oder transparent) mischen
                    float da = dst.a / 255f, sa = a * col.a / 255f, oa = sa + da * (1f - sa);
                    if (oa <= 0f) continue;
                    p[o] = new Color32(
                        (byte)((col.r * sa + dst.r * da * (1f - sa)) / oa),
                        (byte)((col.g * sa + dst.g * da * (1f - sa)) / oa),
                        (byte)((col.b * sa + dst.b * da * (1f - sa)) / oa),
                        (byte)((col.a * sa + dst.a * da * (1f - sa)) / oa));
                }
        }

        // ------------------------------------------------------------------ Straßennetz der großen Karte (Unity BuildMapRoads/RoadMesh)
        /// <summary>Straßennetz geladen (für die große Karte).</summary>
        public bool HasRoads => _lines.Length > 0;
        /// <summary>Mindestbreite der Straßen auf der großen Karte in Bildpunkten (Unity TDU2BigMap.RoadMinPixels).</summary>
        public float RoadMinPixels = 1f;
        readonly Dictionary<int, Paradise.Core.Render.IGpuMesh> _mapRoads = new();
        Paradise.Core.Render.IGpuMaterial? _roadMat;
        Line[]? _mapRoadsFor;

        /// <summary>
        /// Unity TDU2BigMap.DrawRoads: Straßennetz über dem Kartengelände (Vertexfarbe, ZTest Always). Breitenstufe nach
        /// Metern je Bildpunkt (7 m = übliche Straßenbreite), je Stufe ein Mesh (zwischengespeichert).
        /// </summary>
        public void DrawMapRoads(Paradise.Core.Render.IRenderer r, System.Numerics.Matrix4x4 root, float metersPerPixel, Func<float, float, float> height)
        {
            if (!HasRoads) return;
            if (!ReferenceEquals(_mapRoadsFor, _lines)) { ClearMapRoads(); _mapRoadsFor = _lines; }
            float mul = MathF.Max(1f, RoadMinPixels * metersPerPixel / 7f);
            int lvl = System.Math.Clamp((int)MathF.Ceiling(MathF.Log2(mul) - 1e-3f), 0, 8);
            if (!_mapRoads.TryGetValue(lvl, out var mesh))
            {
                var (v, i) = BuildMapRoads(MathF.Pow(2f, lvl), height);
                if (i.Length == 0) return;
                mesh = r.CreateMesh(v, i);
                _mapRoads[lvl] = mesh;
            }
            _roadMat ??= r.CreateMaterial(new Paradise.Core.Render.MaterialDesc { Kind = Paradise.Core.Render.MaterialKind.MapRoad, Name = "TDU2 Kartenstraßen" });
            r.DrawMesh(mesh, _roadMat, root);
            DrawMapRoute(r, root, MathF.Pow(2f, lvl), height);
        }

        // GPS-Route auf der großen Karte: die Straßen der Route in der GPS-Farbe (wie die Minimap), über dem Straßennetz
        Paradise.Core.Render.IGpuMesh? _routeMesh;
        int _routeMeshVersion = -1; float _routeMeshMul;

        void DrawMapRoute(Paradise.Core.Render.IRenderer r, System.Numerics.Matrix4x4 root, float widthMul, Func<float, float, float> height)
        {
            var route = Gps.HasTarget ? Gps.Route : null;
            if (route == null || route.Count < 2 || _roadMat == null) return;
            if (_routeMesh == null || _routeMeshVersion != Gps.RouteVersion || _routeMeshMul != widthMul)
            {
                _routeMesh?.Dispose(); _routeMesh = null;
                _routeMeshVersion = Gps.RouteVersion; _routeMeshMul = widthMul;
                var verts = new List<(float x, float z, Color32 c)>(route.Count * 4);
                var idx = new List<uint>(route.Count * 6);
                var col = TDU2GpsSettings.PathColor;
                float half = HalfOf(6) * widthMul;   // so breit wie eine Hauptstraße
                for (int i = 0; i + 1 < route.Count; i++) AddQuad(verts, idx, route[i].X, route[i].Z, route[i + 1].X, route[i + 1].Z, half, col);
                if (idx.Count == 0) return;
                var ov = new Paradise.Core.Render.ObjectVertex[verts.Count];
                for (int k = 0; k < ov.Length; k++)
                {
                    var (x, z, c) = verts[k];
                    ov[k] = new Paradise.Core.Render.ObjectVertex
                    {
                        Position = new V3(x, height(x, z), z),
                        Normal = V3.UnitY,
                        Color = c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24)
                    };
                }
                _routeMesh = r.CreateMesh(ov, idx.ToArray());
            }
            if (_routeMesh != null) r.DrawMesh(_routeMesh, _roadMat, root);
        }

        void ClearMapRoads()
        {
            foreach (var m in _mapRoads.Values) m.Dispose();
            _mapRoads.Clear();
        }

        /// <summary>Unity BuildMapRoads/BuildRoadMesh: Kreuzungsflächen, dann Feldwege, Stadt-, Hauptstraßen; Höhe +1 m über dem Boden.</summary>
        (Paradise.Core.Render.ObjectVertex[] v, uint[] i) BuildMapRoads(float widthMul, Func<float, float, float> height)
        {
            var verts = new List<(float x, float z, Color32 c)>(1 << 18);
            var idx = new List<uint>(1 << 19);
            foreach (var ar in _areas) AddArea(verts, idx, ar, ar.Cls == 6 ? RoadMain : RoadTown);
            foreach (byte pass in new byte[] { 1, 3, 6 })
            {
                var col = pass == 1 ? RoadDirt : pass == 3 ? RoadTown : RoadMain;
                float half = HalfOf(pass) * widthMul;
                foreach (var l in _lines)
                {
                    if (l.Cls != pass) continue;
                    if (MathF.Abs(l.Ax - l.Bx) < 1e-3f && MathF.Abs(l.Az - l.Bz) < 1e-3f) AddDisc(verts, idx, l.Ax, l.Az, half * l.W, col);
                    else AddQuad(verts, idx, l.Ax, l.Az, l.Bx, l.Bz, half * l.W, col);
                }
            }
            var ov = new Paradise.Core.Render.ObjectVertex[verts.Count];
            for (int k = 0; k < ov.Length; k++)
            {
                var (x, z, c) = verts[k];
                float y = height(x, z);
                ov[k] = new Paradise.Core.Render.ObjectVertex
                {
                    Position = new V3(x, y, z),
                    Normal = V3.UnitY,
                    Color = c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24)
                };
            }
            Log.Info($"Karte: Straßennetz {verts.Count} Vertices, {idx.Count / 3} Dreiecke (Breite x{widthMul:0.##})");
            return (ov, idx.ToArray());
        }

        /// <summary>Unity AddQuad: Rechteck mit Überstand 0,6 x halbe Breite (schließt Fugen in Kurven).</summary>
        static void AddQuad(List<(float x, float z, Color32 c)> v, List<uint> idx, float ax, float az, float bx, float bz, float half, Color32 col)
        {
            float dx = bx - ax, dz = bz - az, l = MathF.Sqrt(dx * dx + dz * dz);
            if (l < 1e-3f) return;
            dx /= l; dz /= l;
            float nx = -dz * half, nz = dx * half, ex = dx * half * 0.6f, ez = dz * half * 0.6f;
            ax -= ex; az -= ez; bx += ex; bz += ez;
            uint i0 = (uint)v.Count;
            v.Add((ax - nx, az - nz, col)); v.Add((ax + nx, az + nz, col)); v.Add((bx + nx, bz + nz, col)); v.Add((bx - nx, bz - nz, col));
            idx.Add(i0); idx.Add(i0 + 1); idx.Add(i0 + 2); idx.Add(i0); idx.Add(i0 + 2); idx.Add(i0 + 3);
        }

        /// <summary>Kreuzungsscheibe (Mittelpunkt einer verbundenen Gruppe von Straßenenden).</summary>
        static void AddDisc(List<(float x, float z, Color32 c)> v, List<uint> idx, float cx, float cz, float r, Color32 col)
        {
            const int N = 16;
            uint b0 = (uint)v.Count;
            v.Add((cx, cz, col));
            for (int k = 0; k < N; k++) { float a = k * MathF.Tau / N; v.Add((cx + MathF.Cos(a) * r, cz + MathF.Sin(a) * r, col)); }
            for (uint k = 0; k < N; k++) { idx.Add(b0); idx.Add(b0 + 1 + k); idx.Add(b0 + 1 + (k + 1) % N); }
        }

        /// <summary>Unity AddJunctionAreas: Umriss in einem anderen = Mittelinsel (Kreisel) -> nur den Ring, sonst Fächer.</summary>
        static void AddArea(List<(float x, float z, Color32 c)> v, List<uint> idx, Area ar, Color32 col)
        {
            int n = ar.Rings.Count;
            var hole = new bool[n];
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++)
                    if (a != b && !hole[b] && Contains(ar.Rings[b], ar.Rings[a])) hole[a] = true;
            for (int a = 0; a < n; a++)
            {
                if (hole[a]) continue;
                float[]? inner = null;
                for (int b = 0; b < n; b++) if (hole[b] && Contains(ar.Rings[a], ar.Rings[b])) { inner = ar.Rings[b]; break; }
                if (inner != null) AddRing(v, idx, ar.Rings[a], inner, col);
                else AddPolygon(v, idx, ar.Rings[a], col);
            }
        }

        static bool Contains(float[] outer, float[] inner)
        {
            for (int i = 0; i + 1 < inner.Length; i += 2) if (!PointIn(outer, inner[i], inner[i + 1])) return false;
            return true;
        }

        static bool PointIn(float[] poly, float px, float pz)
        {
            bool c = false;
            int n = poly.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float ax = poly[2 * i], az = poly[2 * i + 1], bx = poly[2 * j], bz = poly[2 * j + 1];
                if ((az > pz) != (bz > pz) && px < (bx - ax) * (pz - az) / (bz - az) + ax) c = !c;
            }
            return c;
        }

        /// <summary>Weitester Schnitt eines Strahls von (cx,cz) in Richtung (dx,dz) mit dem Umriss (0 = kein Treffer).</summary>
        static float RayPoly(float cx, float cz, float dx, float dz, float[] poly)
        {
            float best = 0f;
            int n = poly.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float ax = poly[2 * j], az = poly[2 * j + 1], ex = poly[2 * i] - ax, ez = poly[2 * i + 1] - az;
                float den = dx * ez - dz * ex;
                if (MathF.Abs(den) < 1e-6f) continue;
                float wx = ax - cx, wz = az - cz;
                float t = (wx * ez - wz * ex) / den, u = (wx * dz - wz * dx) / den;
                if (t > 0f && u >= 0f && u <= 1f && t > best) best = t;
            }
            return best;
        }

        /// <summary>Unity AddRing: Kreiselfläche zwischen Außenumriss und Mittelinsel (radial abgetastet, beidseitig).</summary>
        static void AddRing(List<(float x, float z, Color32 c)> v, List<uint> idx, float[] outer, float[] inner, Color32 col)
        {
            float cx = 0f, cz = 0f; int m = inner.Length / 2;
            for (int i = 0; i < m; i++) { cx += inner[2 * i]; cz += inner[2 * i + 1]; }
            cx /= m; cz /= m;
            const int N = 48;
            uint b0 = (uint)v.Count;
            for (int k = 0; k < N; k++)
            {
                float a = k * MathF.Tau / N, dx = MathF.Cos(a), dz = MathF.Sin(a);
                float ri = RayPoly(cx, cz, dx, dz, inner), ro = RayPoly(cx, cz, dx, dz, outer);
                if (ro < ri) ro = ri;
                v.Add((cx + dx * ri, cz + dz * ri, col));
                v.Add((cx + dx * ro, cz + dz * ro, col));
            }
            for (uint k = 0; k < N; k++)
            {
                uint i0 = b0 + 2 * k, i1 = b0 + 2 * ((k + 1) % N);
                idx.Add(i0); idx.Add(i0 + 1); idx.Add(i1 + 1);
                idx.Add(i0); idx.Add(i1 + 1); idx.Add(i1);
            }
        }

        /// <summary>Unity AddPolygon: (nahezu) konvexer Umriss als Fächer um den Schwerpunkt.</summary>
        static void AddPolygon(List<(float x, float z, Color32 c)> v, List<uint> idx, float[] poly, Color32 col)
        {
            int n = poly.Length / 2;
            if (n < 3) return;
            float cx = 0f, cz = 0f;
            for (int i = 0; i < n; i++) { cx += poly[2 * i]; cz += poly[2 * i + 1]; }
            uint b0 = (uint)v.Count;
            v.Add((cx / n, cz / n, col));
            for (int i = 0; i < n; i++) v.Add((poly[2 * i], poly[2 * i + 1], col));
            for (uint i = 0; i < n; i++) { idx.Add(b0); idx.Add(b0 + 1 + i); idx.Add(b0 + 1 + (i + 1) % (uint)n); }
        }

        // ------------------------------------------------------------------ Einfahrt / Spot-Symbole (Unity TDU2Minimap)

        /// <summary>Markenlogo eines Händler-Spots (Code aus spots.bin) aus logotexturepage.bnk; null wenn unbekannt.
        /// Unity LogoTexture -> hier FlashTextures.BrandLogo (gemeinsam mit der großen Karte).</summary>
        public Texture2D? DealerLogoTexture(string? code)
        {
            int li = Database != null && code != null ? Database.DealerLogo(code) : -1;
            return FlashTextures.BrandLogo(_bnkDir, li);
        }

        /// <summary>Händler-Icon wie im Spiel: "ic" auf "brands" + Logo in ic/logo/lure; ohne Logo das Auto-Symbol ("lod").</summary>
        void ApplyDealerLogo(string spotPath, string code)
        {
            if (_hud == null) return;
            var tex = DealerLogoTexture(code);
            if (tex != null)
            {
                _hud.GotoLabel(spotPath + "/ic", "brands", false);
                _hud.SetClipTexture(spotPath + "/ic/logo/lure", tex);
            }
            else _hud.GotoLabel(spotPath + "/ic", "lod", false);
        }

        /// <summary>Zusätzliche Spots (z.B. Meisterschaftsrennen, Unity Racing.TDU2Championships): Code -> Symbol-Label / Anzeigename.</summary>
        public static readonly Dictionary<string, string> ExtraSpotLabels = new Dictionary<string, string>();
        public static readonly Dictionary<string, string> ExtraSpotNames = new Dictionary<string, string>();
        /// <summary>Bild für den Karten-Tooltip zusätzlicher Spots: Pfad unter frontend/AllRes/MapScreens ohne .bnk (z.B. "contest/cl-4-1").</summary>
        public static readonly Dictionary<string, string> ExtraSpotPictures = new Dictionary<string, string>();

        /// <summary>Spot-Typ (Code aus spots.bin) -> Label des Spot-Icons (Sprite "Spot" in hud.vmf, wie auf der großen Karte).</summary>
        public static string? SpotLabel(string? code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (ExtraSpotLabels.TryGetValue(code, out var extra)) return extra;
            var c = code.ToUpperInvariant();
            if (c.StartsWith("ECDC")) return "carDealer";
            if (c.StartsWith("ECD")) return "carDealer";
            if (c.StartsWith("EBD")) return "bikeDealer";
            if (c.StartsWith("EPH")) return "home";
            if (c.StartsWith("ECH")) return "club";
            if (c.StartsWith("ETUN")) return "tuning";
            if (c.StartsWith("ECW")) return "carWash";
            if (c.StartsWith("ESTICK")) return "carPaint";
            if (c.StartsWith("ESTOL")) return "stolenCar";
            if (c.StartsWith("ERS")) return "rschool";   // Renn-Akademie (ERS_A/B/C)
            if (c.StartsWith("ERE")) return "estates";
            if (c.StartsWith("EAS")) return "clothes";
            if (c.StartsWith("EHD")) return "hair";
            if (c.StartsWith("ESUR")) return "surgery";
            if (c.StartsWith("EGAM")) return "gamblingHall";
            if (c.StartsWith("ELANDCAA")) return "casino";   // Hawaii: Landeplatz, Flug zur Casino-Insel
            if (c.StartsWith("ELAND")) return "airport";     // Casino-Insel: Landeplatz, Rückflug nach Hawaii
            if (c.StartsWith("EAIR")) return "airport";
            if (c.StartsWith("ECAS")) return "casino";
            if (c.StartsWith("EUC")) return "auctionHall";
            if (c.StartsWith("EPHOT")) return "photograph";
            return null;
        }

        /// <summary>Original-Einfahrtsanzeige (/HUD/enter) mit Spot-Icon; null = ausblenden.</summary>
        public void ShowEnter(string? spotCode)
        {
            if (_hud == null) return;
            if (spotCode == null)
            {
                if (_enterShown) { _hud.GotoLabel("/HUD/enter", "out", true); _enterShown = false; _enterLabel = null; }
                return;
            }
            string label = SpotLabel(spotCode) ?? "default";
            if (!_enterShown || _enterLabel != label)
            {
                // Texte aus der Sprachdatenbank (DBLoc m_Enter = 273, m_Locked = 559) in der eingestellten Sprache – alle Filme einheitlich
                var texts = FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language);
                if (texts.TryGetValue("m_Enter", out var tEnter)) _hud.SetVariable("/:m_Enter", tEnter);
                if (texts.TryGetValue("m_Locked", out var tLocked)) _hud.SetVariable("/:m_Locked", tLocked);
                // Zustand, den das Spiel vor "on" setzt (Frame-Skripte von /HUD/enter werten ihn aus):
                // Spot-Art fürs Icon, nicht gesperrt -> enter_txt "ok" statt "locked"
                _hud.SetVariable("/:WhatSpot", label);
                _hud.SetVariable("/:WhatSpotType", "spot");
                _hud.SetVariable("/:IsSpotLocked", "0");
                _hud.SetVariable("/:IsChaLocked", "0");
                _hud.SetVariable("/:reward_type", "0");
                _hud.SetVariable("/:IsCarOK", "1");
                _hud.GotoLabel("/HUD/enter", "on", true);
                _hud.GotoLabel("/HUD/enter/enter_txt", "ok", false);
                _hud.GotoLabel("/HUD/enter/Spot", label, false);
                if (label == "carDealer" || label == "bikeDealer") ApplyDealerLogo("/HUD/enter/Spot", spotCode);
                _enterShown = true; _enterLabel = label;
            }
        }

        public void Dispose()
        {
            if (_hud != null) { FlashHost.Remove(_hud); _hud = null; }
            ClearMapRoads();
            _routeMesh?.Dispose(); _routeMesh = null;
            _roadMat?.Dispose(); _roadMat = null;
            _tex?.Dispose(); _tex = null;
            _hudBank = null;
        }
    }
}
