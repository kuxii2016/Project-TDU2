using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using Paradise.Game.World;
using System.Numerics;
using System.Text;

namespace Paradise.Game.Gameplay;

public sealed class AreaRow
{
    public int Index;
    /// <summary>Area-Nummer (Alias zu Index).</summary>
    public int Area => Index;
    /// <summary>Fortschritt in Prozent (Done/Total).</summary>
    public int Percent;
    public string Goal = "";
    public string Island = "";
    /// <summary>Punkte „Von/Total“ als Text (aufgedeckte/alle Straßenstücke).</summary>
    public string Points = "";
    public int Found;
    /// <summary>Anzahl erledigter Straßenstücke (Alias zu Found).</summary>
    public int Done => Found;
    public int Total;
    public bool Complete => Total > 0 && Found >= Total;
}

/// <summary>Gebietszeilen beider Inseln, getrennt nach Hawaii und Ibiza.</summary>
public sealed class AreaIndex
{
    public AreaRow[] Hawaii = System.Array.Empty<AreaRow>();
    public AreaRow[] Ibiza = System.Array.Empty<AreaRow>();
}


internal sealed class Discovery : GameModule
{
    public static Discovery? Current { get; private set; }
    readonly TerrainModule _terrain;
    readonly VehicleModule _vehicles;

    sealed class Segment
    {
        public string Key = "";
        public string Id = "";
        public int Area;
        public float Length;
        public Vector3[] Pts = Array.Empty<Vector3>();
    }

    /// <summary>Polygon aus level/cruisingzones.bin (nur für die zweite, nicht geladene Insel).</summary>
    sealed class Zone
    {
        public float[] X = System.Array.Empty<float>();
        public float[] Z = System.Array.Empty<float>();
    }

    sealed class Dealer
    {
        public string Code = "";
        public string Label = "";
        public Vector3 Pos;
        public string RequiredKey = "";
    }

    /// <summary>Spot-Typen, die erst nach Aufdecken ihrer Straße erscheinen (Häuser, Händler, Läden, Werkstätten …).</summary>
    static readonly HashSet<string> GatedSpotLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "home", "club", "estates", "clothes", "hair", "surgery", "casino", "gamblingHall", "airport", "watches", "forsale",
        "carDealer", "bikeDealer", "tuning", "carWash", "carPaint", "carRental", "auctionHall", "photograph", "stolenCar"
    };

    readonly List<Segment> _segments = new();
    readonly Dictionary<string, Segment> _byKey = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _unlocked = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _unlockedDealers = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Dealer> _dealers = new();
    readonly List<Zone> _zones = new();   // cruisingzones.bin der aktiven Insel (logische Areas)
    string _island = "";
    float _trackTimer;
    bool _saveImported, _colorsDirty;
    int _points, _area = -1;
    string _lastRoadId = "";

    public Discovery(TerrainModule terrain, VehicleModule vehicles)
    {
        _terrain = terrain;
        _vehicles = vehicles;
    }

    public override void Load()
    {
        Current = this;
        DiscoveryConfig.Apply(_terrain.Streamer?.Index == null ? null : GamePC.Load(_terrain.Streamer.Index.BnkDir));
        Minimap.RoadsRebuilt = h => { _colorsDirty = true; ApplyColors(h); };
    }

    public override void Unload()
    {
        if (Minimap.RoadsRebuilt != null)
            Minimap.RoadsRebuilt = null;
        _segments.Clear();
        _byKey.Clear();
        _unlocked.Clear();
        _unlockedDealers.Clear();
        _dealers.Clear();
        _zones.Clear();
        _island = "";
        if (ReferenceEquals(Current, this))
            Current = null;
    }

    public override void Update(in GameTime time)
    {
        var streamer = _terrain.Streamer;
        string? island = streamer?.Index?.IslandName;
        if (streamer == null || string.IsNullOrEmpty(island)) return;
        if (!string.Equals(island, _island, StringComparison.OrdinalIgnoreCase))
        {
            if (!LoadIsland(island, streamer.Index!)) return;
            _colorsDirty = true;
        }
        if (!_saveImported && TDU2Session.Save != null) ImportSave();
        if (_colorsDirty) { _colorsDirty = false; ApplyColors(); }

        if (_segments.Count == 0) return;
        var mm = _vehicles.Manager?.Minimap;
        if (mm == null || mm.Roads == null) return;

        int area = Wrecks.Current?.AreaAt(_vehicles.Manager!.PositionGame) ?? _area;
        _area = area;
        _trackTimer -= time.DeltaTime;
        if (_trackTimer > 0f) return;
        _trackTimer = Math.Max(0.05f, DiscoveryConfig.TrackInterval);

        var car = _vehicles.Manager!.PositionGame;
        if (_vehicles.Manager.Current == null) return;
        Segment? near = null;
        float best = DiscoveryConfig.DetectRadius * DiscoveryConfig.DetectRadius;
        foreach (var s in _segments)
        {
            float d2 = DistanceSquaredXZ(car, s.Pts);
            if (d2 < best) { best = d2; near = s; }
        }
        if (near != null) Unlock(near);
    }

    #region Load/Create Road Discovery Network
    bool LoadIsland(string island, IslandIndex index)
    {
        _island = island;
        _saveImported = false;
        _segments.Clear(); _byKey.Clear(); _unlocked.Clear(); _unlockedDealers.Clear(); _dealers.Clear();
        _zones.Clear();
        _zones.AddRange(ReadCruisingZones(index.LevelDir));
        _area = -1;
        if (string.IsNullOrEmpty(index.IslandBank))
        {
            Log.Warn("Island: " + island + " not valid Island bnk");
            return false;
        }
        IaNetwork net;
        try { net = IaNetwork.LoadFromBank(index.IslandBank); }
        catch (Exception ex)
        {
            Log.Error("Failed read IA-Network from bank: " + ex.Message);
            return false;
        }

        foreach (var road in net.Roads.Values)
            for (int pi = 0; pi < road.Pieces.Count; pi++)
            {
                var pc = road.Pieces[pi];
                var pts = SamplePiece(pc);
                if (pts.Length < 2) continue;
                var s = new Segment
                {
                    Key = HudMapRoads.RoadKey(road.Id, pi),
                    Pts = pts,
                    Length = PolylineLength(pts),
                    Area = AreaFor(Mid(pts)),
                };
                s.Id = MakeId(island, s.Area, s.Key);
                _segments.Add(s); _byKey[s.Key] = s;
            }
        foreach (var kv in net.Junctions)
        {
            var j = kv.Value;
            if (j.RingWidth <= 0f || j.RingRadius <= 0f) continue;
            var c = new Vector3(j.RingCenter.X, 0f, j.RingCenter.Y);
            var pts = new Vector3[13];
            for (int i = 0; i < pts.Length; i++)
            {
                float a = i * MathF.PI * 2f / (pts.Length - 1);
                float r = j.RingRadius + j.RingWidth * 0.5f;
                pts[i] = new Vector3(c.X + MathF.Cos(a) * r, 0f, c.Z + MathF.Sin(a) * r);
            }
            var s = new Segment
            {
                Key = HudMapRoads.RingKey(kv.Key),
                Pts = pts,
                Length = 2f * MathF.PI * j.RingRadius,
                Area = AreaFor(c),
            };
            s.Id = MakeId(island, s.Area, s.Key);
            _segments.Add(s); _byKey[s.Key] = s;
        }

        _dealers.Clear();
        var spots = _terrain.Streamer?.Spots;
        if (spots != null)
            foreach (var sp in spots.Spots)
            {
                string code = sp.Code ?? "";
                string label = Minimap.SpotLabel(code) ?? "";
                if (!IsGatedSpotLabel(label)) continue;
                Segment? req = Nearest(sp.Position, float.MaxValue);
                _dealers.Add(new Dealer { Code = code, Label = label, Pos = sp.Position, RequiredKey = req?.Key ?? "" });
            }
        Log.Debug($"{island}: {_segments.Count} Road parts, {_dealers.Count} gated Spots");
        return true;
    }

    static Vector3[] SamplePiece(IaPiece pc)
    {
        var src = pc.Points;
        var pts = new List<Vector3>();
        for (int i = 0; i + 2 < src.Length; i += 3)
        {
            pts.Add(src[i]);
            // Bézier-Mitte (Gewichte 0,125/0,375/0,375/0,125) – grob, reicht für die Näheprüfung.
            if (i + 3 < src.Length)
                pts.Add(src[i] * 0.125f + src[i + 1] * 0.375f + src[i + 2] * 0.375f + src[i + 3] * 0.125f);
        }
        if (src.Length > 0 && pts.Count > 0 && pts[^1] != src[^1]) pts.Add(src[^1]);
        return pts.ToArray();
    }

    int AreaFor(Vector3 p, List<Zone>? zones = null, IslandIndex? index = null)
    {
        // Logische Areas kommen ausschließlich aus cruisingzones.bin: Hawaii 4, Ibiza 2.
        var zs = zones ?? _zones;
        if (zs.Count > 0)
        {
            int a = AreaIn(zs, p.X, p.Z);
            return (a >= 0 ? a : NearestZone(zs, p.X, p.Z)) + 1;
        }
        var idx = index ?? _terrain.Streamer?.Index;
        if (idx == null || idx.AreasX <= 0 || idx.AreasZ <= 0) return 1;
        int ax = idx.AreasX, az = idx.AreasZ;
        float sx = ax * 6144f, sz = az * 6144f;
        int x = Math.Clamp((int)MathF.Floor((p.X + sx * 0.5f) / sx * ax), 0, ax - 1);
        int z = Math.Clamp((int)MathF.Floor((p.Z + sz * 0.5f) / sz * az), 0, az - 1);
        return z * ax + x + 1;
    }

    // ---- Hilfe für die zweite (nicht aktive) Insel: Zonen + Straßennetz nur lesen ----------

    static List<Zone> ReadCruisingZones(string? levelDir)
    {
        var zones = new List<Zone>();
        if (string.IsNullOrEmpty(levelDir)) return zones;
        string path = Path.Combine(levelDir, "cruisingzones.bin");
        if (!GameFiles.Exists(path)) return zones;
        try
        {
            byte[] d = GameFiles.ReadAllBytes(path);
            if (d.Length < 48 || Encoding.ASCII.GetString(d, 12, 8) != "CRZODATA") return zones;
            int count = (int)Bin.U32(d, 32);
            int o = 48;
            for (int z = 0; z < count && o + 16 <= d.Length; z++)
            {
                // Zwischen den Zonen können 8 Füll-Bytes stehen; der letzte Datensatz hat
                // keinen Abschluss-Block. Deshalb Header mit plausibler Punktzahl suchen.
                while (o + 16 <= d.Length)
                {
                    int check = (int)Bin.U32(d, o + 4);
                    if (check >= 3 && check <= 4096 && o + 16 + check * 8 <= d.Length) break;
                    o += 8;
                }
                if (o + 16 > d.Length) break;
                int n = (int)Bin.U32(d, o + 4);
                int po = o + 16;
                var xs = new float[n]; var zs = new float[n];
                for (int i = 0; i < n; i++) { xs[i] = Bin.F32(d, po + i * 8); zs[i] = Bin.F32(d, po + i * 8 + 4); }
                zones.Add(new Zone { X = xs, Z = zs });
                o = po + n * 8;
            }
        }
        catch (Exception ex) { Log.Warn($"Discovery: cruisingzones.bin ({levelDir}): {ex.Message}"); }
        return zones;
    }

    static int AreaIn(List<Zone> zones, float x, float z)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            var p = zones[i];
            bool inside = false;
            for (int a = 0, b = p.X.Length - 1; a < p.X.Length; b = a++)
                if ((p.Z[a] > z) != (p.Z[b] > z) &&
                    x < (p.X[b] - p.X[a]) * (z - p.Z[a]) / (p.Z[b] - p.Z[a]) + p.X[a])
                    inside = !inside;
            if (inside) return i;
        }
        return -1;
    }

    static int NearestZone(List<Zone> zones, float x, float z)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < zones.Count; i++)
        {
            var p = zones[i];
            for (int j = 0; j < p.X.Length; j++)
            {
                float dx = p.X[j] - x, dz = p.Z[j] - z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
        }
        return best;
    }

    /// <summary>Straßenstücke einer Insel mit stabilen IDs – gleiche Regeln wie LoadIsland().</summary>
    List<Segment> BuildRoadSegments(string island, IslandIndex index, List<Zone> zones)
    {
        var net = IaNetwork.LoadFromBank(index.IslandBank);
        var list = new List<Segment>();
        foreach (var road in net.Roads.Values)
            for (int pi = 0; pi < road.Pieces.Count; pi++)
            {
                var pts = SamplePiece(road.Pieces[pi]);
                if (pts.Length < 2) continue;
                var s = new Segment
                {
                    Key = HudMapRoads.RoadKey(road.Id, pi),
                    Pts = pts,
                    Area = AreaFor(Mid(pts), zones, index),
                };
                s.Id = MakeId(island, s.Area, s.Key);
                list.Add(s);
            }
        foreach (var kv in net.Junctions)
        {
            var j = kv.Value;
            if (j.RingWidth <= 0f || j.RingRadius <= 0f) continue;
            var c = new Vector3(j.RingCenter.X, 0f, j.RingCenter.Y);
            var pts = new Vector3[13];
            for (int i = 0; i < pts.Length; i++)
            {
                float a = i * MathF.PI * 2f / (pts.Length - 1);
                float r = j.RingRadius + j.RingWidth * 0.5f;
                pts[i] = new Vector3(c.X + MathF.Cos(a) * r, 0f, c.Z + MathF.Sin(a) * r);
            }
            var s = new Segment
            {
                Key = HudMapRoads.RingKey(kv.Key),
                Pts = pts,
                Area = AreaFor(c, zones, index),
            };
            s.Id = MakeId(island, s.Area, s.Key);
            list.Add(s);
        }
        return list;
    }

    static HashSet<string> UnlockedIdsFor(string island)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var e = TDU2Session.Save?.exploredRoads?.Find(x => x != null
            && string.Equals(x.island, island, StringComparison.OrdinalIgnoreCase));
        if (e?.ids == null) return ids;
        foreach (var id in e.ids) if (!string.IsNullOrEmpty(id)) ids.Add(id);
        return ids;
    }

    static Vector3 Mid(Vector3[] p) => p.Length == 0 ? Vector3.Zero : p[p.Length / 2];

    static float PolylineLength(Vector3[] p)
    {
        float l = 0f;
        for (int i = 1; i < p.Length; i++) l += Vector3.Distance(p[i - 1], p[i]);
        return l;
    }

    static float DistanceSquaredXZ(Vector3 p, Vector3[] line)
    {
        float best = float.MaxValue;
        for (int i = 1; i < line.Length; i++)
        {
            float ax = line[i - 1].X, az = line[i - 1].Z;
            float dx = line[i].X - ax, dz = line[i].Z - az;
            float l2 = dx * dx + dz * dz;
            float t = l2 > 1e-8f ? Math.Clamp(((p.X - ax) * dx + (p.Z - az) * dz) / l2, 0f, 1f) : 0f;
            float ex = ax + dx * t - p.X, ez = az + dz * t - p.Z;
            best = Math.Min(best, ex * ex + ez * ez);
        }
        return best;
    }

    static string MakeId(string island, int area, string key)
    {
        string isl = island.ToUpperInvariant();
        if (isl.StartsWith("HAWAI", StringComparison.Ordinal)) isl = "HAWAI";
        else if (isl.StartsWith("IBIZA", StringComparison.Ordinal)) isl = "IBIZA";
        return "CRUISING_" + isl + "_ROADS_AREA" + area.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + key.Replace(':', '_');
    }

    Segment? Nearest(Vector3 p, float maxDist)
    {
        Segment? best = null;
        float bd = maxDist * maxDist;
        foreach (var s in _segments)
        {
            float d = DistanceSquaredXZ(p, s.Pts);
            if (d < bd) { bd = d; best = s; }
        }
        return best;
    }
    #endregion

    void ImportSave()
    {
        var save = TDU2Session.Save;
        if (save == null) return;
        _saveImported = true;
        var entry = save.exploredRoads?.Find(e => string.Equals(e.island, _island, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            _unlocked.Clear();
            foreach (var id in entry.ids) if (!string.IsNullOrEmpty(id)) _unlocked.Add(id);
            _lastRoadId = entry.ids.Count > 0 ? entry.ids[^1] : "";
        }
        foreach (var c in save.unlockedDealers ?? new List<string>()) if (!string.IsNullOrEmpty(c)) _unlockedDealers.Add(c);
        _points = Math.Max(0, save.cruisingRoadPoints);
        _colorsDirty = true;
    }

    void WriteSave()
    {
        var save = TDU2Session.Save;
        if (save == null) return;
        save.exploredRoads ??= new List<RoadDiscoveryEntry>();
        var entry = save.exploredRoads.Find(e => string.Equals(e.island, _island, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            entry = new RoadDiscoveryEntry { island = _island };
            save.exploredRoads.Add(entry);
        }
        entry.ids = _unlocked.ToList();
        entry.areas.Clear();
        foreach (var g in _segments.GroupBy(s => s.Area).OrderBy(g => g.Key))
        {
            int n = g.Count(s => _unlocked.Contains(s.Id));
            entry.areas.Add(new HouseEntry { k = g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), v = n.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        save.unlockedDealers = _unlockedDealers.ToList();
        save.cruisingRoadPoints = _points;
        save.cruisingRoadEvents ??= new List<HouseEntry>();
        save.roadDiscoveryVersion = 1;
    }

    void Unlock(Segment s)
    {
        if (_unlocked.Contains(s.Id)) return;
        _unlocked.Add(s.Id);
        _lastRoadId = s.Id;
        int gained = Math.Max(1, (int)MathF.Floor(s.Length / 50f)) * Math.Max(0, DiscoveryConfig.PointsPer50Meters);
        _points += gained;
        CheckMilestones(s.Area);

        foreach (var d in _dealers)
            if (string.Equals(d.RequiredKey, s.Key, StringComparison.OrdinalIgnoreCase) && !_unlockedDealers.Contains(d.Code))
                _unlockedDealers.Add(d.Code);

        Changed();
    }

    void CheckMilestones(int area)
    {
        int total = _segments.Count;
        int done = _unlocked.Count;
        if (total <= 0) return;
        for (int i = 0; i < DiscoveryConfig.MilestonePercent.Length && i < DiscoveryConfig.MilestonePoints.Length; i++)
        {
            int pct = DiscoveryConfig.MilestonePercent[i];
            if (done * 100 / total < pct) continue;
            string ev = EventKey(area, pct);
            var save = TDU2Session.Save;
            if (save != null)
            {
                save.cruisingRoadEvents ??= new List<HouseEntry>();
                if (save.cruisingRoadEvents.Any(e => e.k == ev)) continue;
                save.cruisingRoadEvents.Add(new HouseEntry { k = ev, v = "1" });
            }
            _points += DiscoveryConfig.MilestonePoints[i];
        }
    }

    string EventKey(int area, int percent)
    {
        string isl = _island.StartsWith("h", StringComparison.OrdinalIgnoreCase)
            ? "HAWAI" : _island.StartsWith("i", StringComparison.OrdinalIgnoreCase) ? "IBIZA" : _island.ToUpperInvariant();
        return "CRUISING_" + isl + "_ROADS_AREA" + area.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" +
               percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    void ApplyColors(HudMapRoads? roads = null)
    {
        var h = roads ?? _vehicles.Manager?.Minimap?.Roads;
        if (h == null) return;
        h.ApplyDiscovery(key =>
        {
            if (!_byKey.TryGetValue(key, out var s)) return false;
            return _unlocked.Contains(s.Id);
        });
    }

    string AreaLabel(int area, string? island = null)
    {
        if (area < 1) return "";
        bool hawaii = (island ?? _island).StartsWith("h", StringComparison.OrdinalIgnoreCase);
        // db_data.cpr / db_<lang>.cpr: m_HawaiArea1..4, m_IbizaArea1..2 (z.B. "Hawaii-Bereich 1").
        string key = (hawaii ? "m_HawaiArea" : "m_IbizaArea") + area.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var texts = FlashLocalization.Load(GameFiles.BnkDir, FlashHost.Language);
        if (texts.TryGetValue(key, out var txt) && !string.IsNullOrEmpty(txt)) return txt;
        return (hawaii ? "Hawaii" : "Ibiza") + " - Bereich " + area.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public int TotalPoints
    {
        get
        {
            int wrecks = 0;
            var save = TDU2Session.Save;
            if (save?.foundWrecks != null) wrecks = save.foundWrecks.Sum(e => e.indices?.Count ?? 0) * 25;
            return Math.Max(0, _points) + Math.Max(0, wrecks);
        }
    }

    // ------------------------------------------------------------------ DevMenu-API

    public string Island => _island;
    public int RoadCount => _segments.Count;
    public int UnlockedRoadCount => _unlocked.Count;
    public int DealerCount => _dealers.Count;
    public int UnlockedDealerCount => _unlockedDealers.Count;
    public int RoadPoints => _points;
    public int Level => DiscoveryConfig.LevelForPoints(TotalPoints);
    public int NextThreshold => DiscoveryConfig.NextThreshold(TotalPoints);
    public int Area => _area;
    public string AreaName => AreaLabel(_area);

    // ------------------------------------------------------------------ Gebietslevel (Tuner-Stufen, DBAreaLevel)
    /// <summary>
    /// Entdeckungspunkte eines Gebiets der geladenen Insel (Original: Profil +0x30 = Spielstand PlayerLevels/CruisingLevels/Area_n;
    /// exe FUN_00a0cc43 Kategorie 3 addiert jede Cruising-Belohnung auf das Gebiet, in dem sie liegt). Hier aus denselben Quellen
    /// wie die Gesamtpunkte: aufgedeckte Straßen des Gebiets, gefundene Wracks (25), Gebiets-Events (DBRewardXP _cruisingpoints).
    /// Aussichtspunkte sind nur je Insel geführt (CRUISING_PHOTOS_*) und lassen sich keinem Gebiet zuordnen.
    /// </summary>
    public int AreaPoints(int area)
    {
        if (area < 1) return 0;
        int pts = 0;
        foreach (var s in _segments)
            if (s.Area == area && _unlocked.Contains(s.Id)) pts += Math.Max(1, (int)MathF.Floor(s.Length / 50f));
        var w = Wrecks.Current;
        if (w != null && string.Equals(w.Island, _island, StringComparison.OrdinalIgnoreCase)) pts += w.CountsInArea(area).found * Wrecks.XpPerWreck;
        bool hawaii = _island.StartsWith("h", StringComparison.OrdinalIgnoreCase);
        foreach (var rw in CruiseRewards().Values)
            if (rw.Area == area && rw.PointsEach > 0 && rw.Island.StartsWith(hawaii ? "H" : "I", StringComparison.OrdinalIgnoreCase))
                pts += Math.Clamp(CruiseEventCount(rw.Event), 0, rw.Times) * rw.PointsEach;
        return pts;
    }

    /// <summary>
    /// Gebietslevel 1..6 (exe 0xa05b40): DBCruisingZones liefert die Insel des Gebiets (_island: Ibiza 0, Hawaii 1), dann die erste
    /// DBAreaLevel-Zeile dieser Insel mit _alpneeded &gt; Punkte -&gt; _level - 1; ab der letzten Schwelle (Gebiet voll) deren _level.
    /// Gebiet -1 (außerhalb) = 1. Der Tuner schaltet Stufe n frei, wenn das Gebietslevel &gt;= n ist (exe 0xa2f720).
    /// </summary>
    public int AreaLevel(int area) => AreaLevelFor(_island, area, AreaPoints(area));

    public static int AreaLevelFor(string island, int area, int points)
    {
        if (area < 1) return 1;
        int code = island.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? 1 : 0;   // DBCruisingZones._island (i1/i2 = 0, h1..h4 = 1)
        var rows = AreaLevelRows();
        int last = 1, lastNeed = -1;
        foreach (var (isl, need, level) in rows)
        {
            if (isl != code) continue;
            if (need > points) return Math.Max(1, level - 1);
            if (need >= lastNeed) { lastNeed = need; last = level; }
        }
        return lastNeed >= 0 ? last : 1;
    }

    static List<(int island, int need, int level)>? _areaLevels;

    /// <summary>DBAreaLevel (db_data.cpr) in Dateireihenfolge: _island, _alpneeded, _level.</summary>
    static List<(int island, int need, int level)> AreaLevelRows()
    {
        if (_areaLevels != null) return _areaLevels;
        var list = new List<(int, int, int)>();
        try
        {
            var root = XmbFile.Parse(CprCrypto.Decrypt(GameFiles.ReadAllBytes(Path.Combine(FlashHost.BnkDir, "database", "db_data.cpr"))));
            foreach (var e in root.Get("DBAreaLevel")?.Items ?? new List<XmbNode>())
                list.Add((e.I("_island", -1), e.I("_alpneeded"), e.I("_level", 1)));
        }
        catch (Exception ex) { Log.Warn("Discovery: DBAreaLevel nicht lesbar: " + ex.Message); }
        _areaLevels = list;
        return list;
    }
    public string LastRoadId => string.IsNullOrEmpty(_lastRoadId) ? "-" : _lastRoadId;

    /// <summary>
    /// Gebietszeilen beider Inseln für die sichtbare gameprogress_fl (Cruising/Roads).
    /// Nur Straßendaten – keine Wracks. Jede Zeile: Area, Percent, Points (done/total), Done, Goal.
    /// Die aktive Insel kommt aus dem geladenen Netz, die andere aus Inselbank + Savegame-IDs.
    /// </summary>
    public AreaIndex BuildAreaRows()
    {
        return new AreaIndex
        {
            Hawaii = BuildIslandAreaRows("hawai"),
            Ibiza = BuildIslandAreaRows("ibiza"),
        };
    }

    AreaRow[] BuildIslandAreaRows(string island)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool isHawaii = island.StartsWith("h", StringComparison.OrdinalIgnoreCase);
        string name = isHawaii ? "Hawaii" : "Ibiza";
        bool active = _segments.Count > 0 && isHawaii == _island.StartsWith("h", StringComparison.OrdinalIgnoreCase);
        List<Segment> segments;
        HashSet<string> unlocked;
        if (active)
        {
            segments = _segments;
            unlocked = _unlocked;
        }
        else
        {
            try
            {
                var idx = IslandIndex.Scan(Paradise.Core.Files.GameFiles.Root, island);
                if (string.IsNullOrEmpty(idx.IslandBank)) return System.Array.Empty<AreaRow>();
                segments = BuildRoadSegments(island, idx, ReadCruisingZones(idx.LevelDir));
                unlocked = UnlockedIdsFor(island);
            }
            catch (Exception ex)
            {
                Log.Warn($"Discovery: Gebietszeilen für {name} nicht verfügbar: {ex.Message}");
                return System.Array.Empty<AreaRow>();
            }
        }

        var rows = new List<AreaRow>();
        foreach (var g in segments.GroupBy(s => s.Area).OrderBy(x => x.Key))
        {
            int total = g.Count();
            int done = g.Count(s => unlocked.Contains(s.Id));
            string label = AreaLabel(g.Key, island);
            rows.Add(new AreaRow
            {
                Index = g.Key,
                Goal = label.Length > 0 ? label : "Area " + (g.Key + 1).ToString(inv),
                Island = name,
                Found = done,
                Total = total,
                Percent = total > 0 ? done * 100 / total : 0,
                Points = done.ToString(inv) + "/" + total.ToString(inv),
            });
        }
        return rows.ToArray();
    }

    // ------------------------------------------------ Cruising: Events / Aussichtspunkte (DBRewardXP)
    // Die Werte kommen ausschließlich aus DBRewardXP; gefundene Events/Viewpoints aus den
    // vorhandenen Event-Zählern des Spielstands (fehlt der Eintrag, steht dort 0).

    sealed class CruiseReward
    {
        public string Event = "";
        public string Island = "";
        public int Area;
        public int GoalHash;
        public int Times;
        public int PointsEach;
    }

    static Dictionary<string, CruiseReward>? _cruiseRewards;

    // Ibiza zuerst (2 Areas + DLC), dann Hawaii (4 Areas + DLC) – strikt nach Insel getrennt.
    static readonly string[] CruiseEventKeys =
    {
        "CRUISING_IBIZA_EVENT_AREA1", "CRUISING_IBIZA_EVENT_AREA2",
        "CRUISING_DLC_EVENT_IBIZA_AREA1", "CRUISING_DLC_EVENT_IBIZA_AREA2",
        "CRUISING_HAWAI_EVENT_AREA1", "CRUISING_HAWAI_EVENT_AREA2",
        "CRUISING_HAWAI_EVENT_AREA3", "CRUISING_HAWAI_EVENT_AREA4",
        "CRUISING_DLC_EVENT_HAWAI_AREA1", "CRUISING_DLC_EVENT_HAWAI_AREA2",
        "CRUISING_DLC_EVENT_HAWAI_AREA3", "CRUISING_DLC_EVENT_HAWAI_AREA4",
    };
    static readonly string[] CruisePhotoKeys = { "CRUISING_PHOTOS_IBIZA", "CRUISING_PHOTOS_HAWAI" };

    /// <summary>Zeilen für gapr:flli:crui:even – 20/15 Events je Area plus DLC-Events.</summary>
    public IReadOnlyList<AreaRow> BuildEventRows() => BuildCruiseRows(CruiseEventKeys);

    /// <summary>Zeilen für gapr:flli:crui:phot – je 20 Aussichtspunkte pro Insel.</summary>
    public IReadOnlyList<AreaRow> BuildPhotoRows() => BuildCruiseRows(CruisePhotoKeys);

    static IReadOnlyList<AreaRow> BuildCruiseRows(string[] keys)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rewards = CruiseRewards();
        var rows = new List<AreaRow>();
        foreach (string key in keys)
        {
            if (!rewards.TryGetValue(key, out var rw)) continue;
            int found = Math.Clamp(CruiseEventCount(key), 0, rw.Times);
            string goal = (FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, rw.GoalHash) ?? "")
                .Replace("§NUM_AREA§", rw.Area.ToString(inv))
                .Replace("§NB_EVENTS_DONE§", found.ToString(inv))
                .Replace("§NB_DLC_EVENTS_DONE§", found.ToString(inv))
                .Replace("§NB_VIEWPOINTS_FOUND§", found.ToString(inv));
            rows.Add(new AreaRow
            {
                Index = rw.Area,
                Goal = goal,
                Island = rw.Island,
                Found = found,
                Total = rw.Times,
                Percent = rw.Times > 0 ? found * 100 / rw.Times : 0,
                Points = (found * rw.PointsEach).ToString(inv) + "/" + (rw.Times * rw.PointsEach).ToString(inv),
            });
        }
        return rows;
    }

    static Dictionary<string, CruiseReward> CruiseRewards()
    {
        if (_cruiseRewards != null) return _cruiseRewards;
        var map = new Dictionary<string, CruiseReward>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = XmbFile.Parse(CprCrypto.Decrypt(GameFiles.ReadAllBytes(
                Path.Combine(FlashHost.BnkDir, "database", "db_data.cpr"))));
            foreach (var e in root.Get("DBRewardXP")?.Items ?? new List<XmbNode>())
            {
                string ev = e.S("_event", "") ?? "";
                if (!ev.StartsWith("CRUISING_", StringComparison.OrdinalIgnoreCase)) continue;
                int sub = e.I("_subtype", -1);
                if (sub != 2 && sub != 3) continue;   // 2 = Event, 3 = Aussichtspunkt/Foto
                string island = ev.IndexOf("IBIZA", StringComparison.OrdinalIgnoreCase) >= 0 ? "Ibiza" : "Hawaii";
                int area = 0;
                var m = System.Text.RegularExpressions.Regex.Match(ev, "AREA(\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) int.TryParse(m.Groups[1].Value, out area);
                map[ev] = new CruiseReward
                {
                    Event = ev,
                    Island = island,
                    Area = area,
                    GoalHash = e.I("_goaltext_hc"),
                    Times = Math.Max(0, e.I("_nbtimesallowed")),
                    PointsEach = Math.Max(0, e.I("_cruisingpoints")),
                };
            }
        }
        catch (Exception ex) { Log.Warn($"Discovery: DBRewardXP Events/Fotos nicht lesbar: {ex.Message}"); }
        _cruiseRewards = map;
        return map;
    }

    /// <summary>Eventstand aus den vorhandenen Cruising-/Wreck-Event-Zählern des Spielstands.</summary>
    static int CruiseEventCount(string key)
    {
        var sv = TDU2Session.Save;
        if (sv == null) return 0;
        int n = 0;
        void Add(List<HouseEntry>? list)
        {
            if (list == null) return;
            foreach (var e in list)
                if (e != null && string.Equals(e.k, key, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(e.v, out int v)) n += Math.Max(0, v);
        }
        Add(sv.cruisingRoadEvents);
        Add(sv.wreckEvents);
        return n;
    }

    public string[] RoadIds => _segments.Select(s => s.Id).ToArray();
    public string[] DealerCodes => _dealers.Select(d => d.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool IsRoadUnlocked(string id) => !string.IsNullOrEmpty(id) && _unlocked.Contains(id);

    public bool IsDealerUnlocked(string code) => !string.IsNullOrEmpty(code) && _unlockedDealers.Contains(code);

    public void SetDealerUnlocked(string code, bool unlocked)
    {
        if (string.IsNullOrEmpty(code)) return;
        if (unlocked) _unlockedDealers.Add(code); else _unlockedDealers.Remove(code);
        Changed();
    }

    /// <summary>Spot-Typ, der an eine aufgedeckte Straße gebunden ist (Haus, Händler, Laden, Werkstatt …).</summary>
    public static bool IsGatedSpotLabel(string? label)
        => !string.IsNullOrEmpty(label) && GatedSpotLabels.Contains(label);

    /// <summary>Bigmap: Spot erst zeigen, wenn die zugehörige Straße aufgedeckt ist (oder explizit freigeschaltet).</summary>
    public bool IsDealerVisible(string code, Vector3 position)
    {
        // Kein Straßennetz geladen (Modul noch nicht aktiv): nicht künstlich die Karte leeren.
        if (_segments.Count == 0) return true;
        if (IsDealerUnlocked(code)) return true;
        var d = _dealers.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        string? key = d != null && !string.IsNullOrEmpty(d.RequiredKey) ? d.RequiredKey : Nearest(position, 150f)?.Key;
        if (string.IsNullOrEmpty(key) || !_byKey.TryGetValue(key, out var seg)) return false;
        return _unlocked.Contains(seg.Id);
    }

    /// <summary>Teleport nur auf bereits aufgedeckte Straßen. Kein Netz geladen = alter Fallback (erlaubt).</summary>
    public bool IsPositionOnUnlockedRoad(Vector3 position, float radius)
    {
        if (_segments.Count == 0) return true;
        var s = Nearest(position, Math.Max(radius, 30f));
        return s != null && _unlocked.Contains(s.Id);
    }

    public void SetRoadUnlocked(string id, bool unlocked)
    {
        var s = _segments.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (s == null) return;
        if (unlocked) { if (_unlocked.Add(s.Id)) { _points += Math.Max(1, (int)MathF.Floor(s.Length / 50f)); _lastRoadId = s.Id; } }
        else _unlocked.Remove(s.Id);
        Changed();
    }

    public void UnlockAll(bool currentAreaOnly)
    {
        foreach (var s in _segments)
        {
            if (currentAreaOnly && s.Area != _area) continue;
            if (_unlocked.Add(s.Id)) _points += Math.Max(1, (int)MathF.Floor(s.Length / 50f));
        }
        _lastRoadId = "ALL" + (currentAreaOnly ? "_AREA" : "");
        Changed();
        Log.Info($"[Discovery] Unlock all ({(currentAreaOnly ? "Area " + _area : "alle Areas")}): {_unlocked.Count}/{_segments.Count}");
    }

    public void UnlockNext()
    {
        var s = _segments.FirstOrDefault(x => !_unlocked.Contains(x.Id));
        if (s == null) return;
        _unlocked.Add(s.Id); _lastRoadId = s.Id;
        _points += Math.Max(1, (int)MathF.Floor(s.Length / 50f));
        Changed();
    }

    public void UnlockAllDealers()
    {
        foreach (var d in _dealers) _unlockedDealers.Add(d.Code);
        Changed();
    }

    public void AddPoints(int amount)
    {
        _points = Math.Max(0, _points + amount);
        Changed();
    }

    public void Clear()
    {
        _unlocked.Clear(); _unlockedDealers.Clear(); _points = 0; _lastRoadId = "";
        var save = TDU2Session.Save;
        if (save != null)
        {
            save.exploredRoads?.Clear(); save.unlockedDealers?.Clear();
            save.cruisingRoadEvents?.Clear(); save.cruisingRoadPoints = 0; save.roadDiscoveryVersion = 1;
        }
        Changed();
        Log.Info("[Discovery] Fortschritt entfernt – alle Straßen wieder grau");
    }

    public void Refresh() { _colorsDirty = true; ApplyColors(); }

    public void SaveNow()
    {
        WriteSave();
        //TDU2Session.SaveAsync();
        Log.Info("[Discovery] Spielstand geschrieben");
    }

    /// <summary>DevMenu: gespeicherten Straßenstand erneut aus dem Spielstand lesen.</summary>
    public void Reload()
    {
        _saveImported = false;
        ImportSave();
        _colorsDirty = true;
        ApplyColors();
        GAME_MODE_MAP.RefreshDiscoverySpots();
        Log.Info("[Discovery] Spielstand neu geladen");
    }

    void Changed()
    {
        WriteSave();
        _colorsDirty = true;
        ApplyColors();
        GAME_MODE_MAP.RefreshDiscoverySpots();
        //SessionAutosave.Schedule(2f);
    }
}