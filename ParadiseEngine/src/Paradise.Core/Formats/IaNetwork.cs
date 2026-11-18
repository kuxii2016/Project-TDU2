using System.Numerics;

namespace Paradise.Core.Formats
{
    /// <summary>Intersection (NODE) of the transportation network.</summary>
    public sealed class IaNode
    {
        public int Id;
        public byte Flag;
        /// <summary>Node type (EDON +10, exe node +0xa, 0x5f14d0): 3 = roundabout (Ibiza: 11 nodes, all with JDET ring).</summary>
        public byte Kind;
        public Vector3 Pos;          // Inselkoordinaten
        public int[] Roads = System.Array.Empty<int>();          // Straßen, die hier enden oder beginnen
    }

    /// <summary>Cross-section (PROF): Points (distance from centerline, elevation, type) – Type 2 = lane against the direction of travel, 3 = lane in the direction of travel.</summary>
    public sealed class IaProfile
    {
        public int Id;
        public int LanesBack, LanesFwd;
        public float Width, TotalWidth;
        public float[] Offset = System.Array.Empty<float>(), Height = System.Array.Empty<float>();
        public int[] Kind = System.Array.Empty<int>();

        /// <summary>Lane centers (distance from the centerline; positive = to the right of the direction of travel) – sorted from the inside to the outside.</summary>
        public List<(float offset, float height)> LaneCenters(bool forward)
        {
            var r = new List<(float, float)>();
            int want = forward ? 3 : 2;
            for (int i = 0; i + 1 < Offset.Length; i++)
                if (Kind[i] == want) r.Add(((Offset[i] + Offset[i + 1]) * 0.5f, (Height[i] + Height[i + 1]) * 0.5f));
            // innen -> außen: mit der Richtung aufsteigend (rechts der Mitte), gegen die Richtung absteigend (links der Mitte)
            if (forward) r.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            else r.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            return r;
        }
    }

    /// <summary>Section of a road (from ROAD_DETAIL): Bézier curve, speed class, profile.</summary>
    public sealed class IaPiece
    {
        public int SpeedClass;       // 1-basiert
        public int ProfileId;
        public int LanesBack, LanesFwd;
        public int NameOffset;
        public float Length;
        /// <summary>
        /// Road type (byte 17 of the section; exe reads it at 0x5f10f0 as [section+0x11]): 6 = Tunnel (0x5ee6e0: type == 6),
        /// Names in the exe table at 0x15047xx (… OFFROAD, CIRCUIT, DEGRADED_ROAD, TUNNEL).
        /// </summary>
        public byte Type;
        public bool IsTunnel => Type == 6;
        /// <summary>Points: Nodes (index 3k) and two control points between each pair (cubic Bézier segments).</summary>
        public Vector3[] Points = System.Array.Empty<Vector3>();
        /// <summary>Lateral inclination per node (radians).</summary>
        public float[] Bank = System.Array.Empty<float>();
    }

    public sealed class IaRoad
    {
        public int Id;
        public int LanesBack, LanesFwd;
        public float Length;
        public int StartNode, EndNode;   // 0 = Sackgasse
        public byte[] EndBytes = new byte[6];
        public readonly List<IaPiece> Pieces = new List<IaPiece>();
    }

    /// <summary>Intersection area (JDET from ROAD_DETAIL): up to three outlines (x, z) in island coordinates.</summary>
    public sealed class IaJunction
    {
        public int NodeId;                       // entspricht der NODE-Id in ROAD_MAP
        /// <summary>Roundabout (JDET +0x0c/+0x10/+0x14/+0x18, exe HudMap 0xca2f6e): radius, width, center (x, z); otherwise 0.</summary>
        public float RingRadius, RingWidth;
        public Vector2 RingCenter;
        public readonly List<Vector2[]> Outlines = new List<Vector2[]>();
    }

    /// <summary>
    /// An island's road network (<Island>_ROAD_MAP.IA + <Island>_ROAD_DETAIL.IA in level/<island>.bnk);
    /// Chunk tags are reversed (“EDON” = NODE). All positions are in island coordinates (meters, Y up, right-handed).
    /// Roads run from the start node to the end node; lanes against the direction of travel are on the left (negative distance): right-hand traffic.
    /// </summary>
    public sealed class IaNetwork
    {
        public readonly Dictionary<int, IaNode> Nodes = new Dictionary<int, IaNode>();
        public readonly Dictionary<int, IaRoad> Roads = new Dictionary<int, IaRoad>();
        public readonly Dictionary<int, IaProfile> Profiles = new Dictionary<int, IaProfile>();
        public readonly Dictionary<int, IaJunction> Junctions = new Dictionary<int, IaJunction>();
        /// <summary>Speed classes 1–n in m/s.</summary>
        public float[] Speeds = System.Array.Empty<float>();
        byte[]? _names;

        /// <summary>Length of the NAME chunk (street name buffer) in bytes; 0 = island without a street name.</summary>
        public int NameBytes => _names?.Length ?? 0;

        public float SpeedOf(int speedClass) => Speeds.Length == 0 ? 13.9f : Speeds[System.Math.Clamp(speedClass - 1, 0, Speeds.Length - 1)];

        public string StreetName(int offset)
        {
            if (_names == null || offset < 0 || offset >= _names.Length) return "";
            int e = offset; while (e < _names.Length && _names[e] != 0) e++;
            return System.Text.Encoding.ASCII.GetString(_names, offset, e - offset);
        }

        static bool Tag(byte[] d, int o, string reversed) =>
            o + 4 <= d.Length && d[o] == reversed[0] && d[o + 1] == reversed[1] && d[o + 2] == reversed[2] && d[o + 3] == reversed[3];

        /// <summary>Load the network from the island database.</summary>
        public static IaNetwork LoadFromBank(string islandBank)
        {
            var bank = BnkArchive.Open(islandBank);
            BnkEntry? map = null, det = null;
            foreach (var e in bank.Entries)
            {
                if (e.Name.IndexOf("ROAD_MAP", StringComparison.OrdinalIgnoreCase) >= 0 && e.Extension == ".ia") map = e;
                else if (e.Name.IndexOf("ROAD_DETAIL", StringComparison.OrdinalIgnoreCase) >= 0 && e.Extension == ".ia") det = e;
            }
            if (map == null || det == null) throw new FileNotFoundException("ROAD_MAP/ROAD_DETAIL.IA not in " + Path.GetFileName(islandBank));
            return Parse(bank.Read(map), bank.Read(det));
        }

        public static IaNetwork Parse(byte[] m, byte[] d)
        {
            if (!Tag(m, 0, "RI41")) throw new InvalidDataException("no ROAD_MAP (HEAD14IR)");
            if (!Tag(d, 0, "DI41")) throw new InvalidDataException("no ROAD_DETAIL (HEAD14ID)");
            var net = new IaNetwork();
            int nNodes = Bin.I32(m, 24), nRoads = Bin.I32(m, 28), nProf = Bin.I32(m, 32);
            var offs = new int[5];
            for (int i = 0; i < 5; i++) offs[i] = 36 + i * 4 + Bin.I32(m, 36 + i * 4);
            int ns = Bin.I32(m, 60);
            net.Speeds = new float[System.Math.Max(0, ns)];
            for (int i = 0; i < ns; i++) net.Speeds[i] = Bin.F32(m, 64 + 4 * i);

            // Knoten
            int o = 64 + 4 * ns;
            for (int i = 0; i < nNodes && Tag(m, o, "EDON"); i++)
            {
                var n = new IaNode { Id = Bin.I32(m, o + 4), Flag = m[o + 9], Kind = m[o + 10] };
                int cnt = m[o + 8];
                n.Pos = new Vector3(Bin.F32(m, o + 12), Bin.F32(m, o + 16), Bin.F32(m, o + 20));
                n.Roads = new int[cnt];
                for (int k = 0; k < cnt; k++) n.Roads[k] = Bin.I32(m, o + 28 + 4 * k);
                net.Nodes[n.Id] = n;
                o += 28 + 4 * cnt;
            }

            // Straßen (je 48 Byte)
            o = offs[0];
            for (int i = 0; i < nRoads && Tag(m, o, "DAOR"); i++)
            {
                var r = new IaRoad
                {
                    Id = Bin.I32(m, o + 4),
                    LanesBack = m[o + 8],
                    LanesFwd = m[o + 9],
                    Length = Bin.U16(m, o + 16),
                    StartNode = Bin.I32(m, o + 28),
                    EndNode = Bin.I32(m, o + 32),
                };
                Buffer.BlockCopy(m, o + 18, r.EndBytes, 0, 6);
                net.Roads[r.Id] = r;
                o += 48;
            }

            // Profile
            o = offs[1];
            for (int i = 0; i < nProf && Tag(m, o, "FORP"); i++)
            {
                int n = Bin.I32(m, o + 24);
                var p = new IaProfile
                {
                    Id = Bin.I32(m, o + 4),
                    LanesBack = Bin.I32(m, o + 28),
                    LanesFwd = Bin.I32(m, o + 32),
                    Width = Bin.F32(m, o + 36),
                    TotalWidth = Bin.F32(m, o + 40),
                    Offset = new float[n],
                    Height = new float[n],
                    Kind = new int[n],
                };
                for (int k = 0; k < n; k++)
                {
                    int q = o + 44 + 12 * k;
                    p.Offset[k] = Bin.F32(m, q); p.Height[k] = Bin.F32(m, q + 4); p.Kind[k] = Bin.I32(m, q + 8);
                }
                net.Profiles[p.Id] = p;
                o += 44 + 12 * n;
            }

            o = offs[2];
            if (Tag(m, o, "EMAN") && offs[3] > o)
            {
                // "EMAN" + Daten bis ENTA; Offsets der Abschnitte zählen ab hier (+4)
                int len = System.Math.Min(offs[3], m.Length) - (o + 4);
                net._names = new byte[System.Math.Max(0, len)];
                Buffer.BlockCopy(m, o + 4, net._names, 0, net._names.Length);
            }

            // Verlauf (ROAD_DETAIL)
            int nDet = Bin.I32(d, 8);
            o = 12;
            for (int i = 0; i < nDet && Tag(d, o, "ATED"); i++)
            {
                int rid = Bin.I32(d, o + 4), np = Bin.I32(d, o + 8);
                int end = o + 12 + 4 * np;
                net.Roads.TryGetValue(rid, out var road);
                for (int k = 0; k < np; k++)
                {
                    int p = o + Bin.I32(d, o + 12 + 4 * k);
                    int pts = Bin.U16(d, p);
                    var pc = new IaPiece
                    {
                        SpeedClass = Bin.U16(d, p + 2),
                        ProfileId = Bin.I32(d, p + 4),
                        LanesBack = d[p + 8],
                        LanesFwd = d[p + 9],
                        NameOffset = Bin.U16(d, p + 10),
                        Length = Bin.U16(d, p + 18),
                        Type = d[p + 17],
                        Points = new Vector3[pts],
                        Bank = new float[pts / 3 + 1],
                    };

                    int q = p + 20;
                    for (int j = 0; j < pts; j++)
                    {
                        pc.Points[j] = new Vector3(Bin.F32(d, q), Bin.F32(d, q + 4), Bin.F32(d, q + 8));
                        if (j % 3 == 0) { pc.Bank[j / 3] = Bin.F32(d, q + 12); q += 16; }
                        else q += 12;
                    }
                    end = System.Math.Max(end, q);
                    road?.Pieces.Add(pc);
                }
                o = end;
            }
            // Straßen ohne Verlauf entfernen
            var empty = new List<int>();
            foreach (var kv in net.Roads) if (kv.Value.Pieces.Count == 0) empty.Add(kv.Key);
            foreach (var k in empty) net.Roads.Remove(k);

            // Kreuzungsflächen (JDET) am Ende von ROAD_DETAIL: u32 Anzahl, dann je Eintrag
            // "JDET"-Tag, u32 Node-Id, u8[3] Punktzahlen, u8 0, u8[16] 0, je Umriss (f32 x, f32 z)-Punkte.
            try
            {
                int nJ = Bin.I32(d, o);
                int j = o + 4;
                for (int i = 0; i < nJ && Tag(d, j, "TEDJ") && j + 28 <= d.Length; i++)
                {
                    int id = Bin.I32(d, j + 4);
                    int c0 = d[j + 8], c1 = d[j + 9], c2 = d[j + 10];
                    int q = j + 28;
                    var jn = new IaJunction
                    {
                        NodeId = id,
                        RingRadius = Bin.F32(d, j + 12),
                        RingWidth = Bin.F32(d, j + 16),
                        RingCenter = new Vector2(Bin.F32(d, j + 20), Bin.F32(d, j + 24)),
                    };
                    foreach (int cnt in new[] { c0, c1, c2 })
                    {
                        if (cnt <= 0 || q + 8 * cnt > d.Length) { q += 8 * System.Math.Max(0, cnt); continue; }
                        var pts = new Vector2[cnt];
                        for (int k = 0; k < cnt; k++)
                        {
                            pts[k] = new Vector2(Bin.F32(d, q), Bin.F32(d, q + 4));
                            q += 8;
                        }
                        jn.Outlines.Add(pts);
                    }
                    net.Junctions[id] = jn;
                    j = q;
                }
            }
            catch { /* JDET optional: ohne bleiben die Kreuzungsflächen aus dem Hüllen-Ersatz */ }
            return net;
        }

        public override string ToString() => $"Roadnet: {Nodes.Count} intersections, {Roads.Count} roads, {Profiles.Count} profiles";
    }
}