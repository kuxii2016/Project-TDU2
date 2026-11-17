using BepuPhysics;
using BepuPhysics.Collidables;
using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Physics;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using Paradise.Game.World;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Paradise.Game.Gameplay;

/// <summary>
/// Verkehr wie im Spiel (tdu2-specs §8.2, §12.5) – Port von Unity TDU2.Traffic.TDU2Traffic auf die Engine:
///   * Straßennetz &lt;Insel&gt;_ROAD_MAP/_ROAD_DETAIL.IA (<see cref="TrafficLanes"/>, Rechtsverkehr, Tempoklassen, Kreisverkehre),
///   * Autos = DBCarPhysicsData Marke 40, Modelle islands/&lt;insel&gt;/vehicules/traffic/*.bnk, Mischung und Dichte aus
///     level/aiconfig.cpr (VEHICLE_REPARTITION), Lackfarben TR_01..TR_11 aus vehicules/colors.bnk,
///   * Fahren: Spur folgen, Abstand halten (IDM) mit begrenztem Ruck (kein abruptes Anhalten), Kurventempo, Kreuzungen und
///     Kreisverkehre nacheinander befahren (im Kreisel Vorfahrt für Autos auf dem Ring), Ampeln, Blinker/Bremslicht,
///   * Spieler: eigenes Auto ist Hindernis (auch quer fahrend, vorausberechnet), Vorfahrt an Kreuzungen,
///   * Physik: nahe Autos sind kinematische Körper (der Spieler stößt an), bei einem Aufprall fallen sie aus der KI (loser Körper
///     mit Schwerkraft, Warnblinker), danach fädeln sie wieder ein.
/// Spielraum = Unity-Inselkoordinaten (MapRoot); gezeichnet wie das eigene Auto (CarModel, Lage im Spielraum).
/// Nicht portiert (Engine ohne Mehrspieler): Abgleich der Autos zwischen Spielern (Unity TDU2Multiplayer-Pakete).
/// </summary>
public sealed class Traffic : IDisposable
{
    sealed class TypeInfo
    {
        public int DbId; public CarPhysics Car = null!; public string Name = "", Bank = ""; public string? Sound; public float Weight;
        public Task<CarAssets>? Load; public CarAssets? Assets; public bool Failed;
    }

    sealed class Car
    {
        public uint Id;
        public TypeInfo Type = null!; public int Color;
        public LaneTrack? Lane, Next; public JunctionTrack? Junction; public int Phase;   // 0 Spur, 1 Kreuzung/Kreisel
        public float S, V, Accel, SpeedFactor = 1f, WaitT, StuckT;
        public int Mode;            // 0 fährt, 1 Unfall (Physik), 2 steht (nach Unfall / Sackgasse)
        public float ModeT, RestT;
        public bool DeadEnd, Committed;
        public Vector3 Pos, Fwd = Vector3.UnitZ, Up = Vector3.UnitY, Vel;
        public float Steer, GroundDy; public Vector3 GroundUp = Vector3.UnitY;
        public Vector3 VisOffset;
        // Lage vor dem letzten Physikschritt (Darstellung zwischen den Schritten wie das eigene Auto, Unity Interpolate)
        public Vector3 PrevPos, PrevFwd = Vector3.UnitZ, PrevUp = Vector3.UnitY; public bool HasPrev;
        public bool Brake, IndL, IndR, Hazard;
        // Darstellung
        public CarModel? Model; public VehicleLights? Lights; public VehicleAudio? Audio;
        public (CarNode? fork, Quaternion fBase, float steerSign, CarNode? disc, Quaternion dBase, float spinSign, bool front)[]? Wheels;
        public float Radius = 0.33f, RideOffset = 0.3f, HalfLen = 2.2f, HalfWidth = 0.9f, Spin;
        public Vector3 BoxCenter = new(0f, 0.8f, 0f), BoxSize = new(1.8f, 1.3f, 4.4f);
        public bool Visible = true;
        // Physik
        public BodyHandle? Kin, Loose;
        public bool Removed;
    }

    readonly IRenderer _r;
    readonly TerrainModule _terrain;
    readonly Func<VehicleManager?> _vm;
    readonly Dictionary<uint, Car> _cars = new();
    readonly Dictionary<int, TypeInfo> _types = new();
    readonly List<TypeInfo> _spawnable = new();
    readonly List<Vector4> _paints = new();
    float[] _density = new float[13];
    TrafficLanes? _lanes;
    Task<LoadResult>? _load;
    string? _loadedIsland;
    PhysicsWorld? _world;
    readonly Random _rng = new();
    uint _idCounter;
    float _clock, _nextPopulation, _fillUntil;
    string _status = "";

    sealed class LoadResult { public TrafficLanes Lanes = null!; public List<TypeInfo> Types = new(); public float[] Density = new float[13]; public List<Vector4> Paints = new(); public string Log = ""; }

    public string Status => _status;
    /// <summary>Anteil des laufenden Physikschritts (GameTime.FixedAlpha) für die Darstellung zwischen zwei Schritten.</summary>
    public float Alpha = 1f;
    public int CarCount => _cars.Count;
    public TrafficLanes? Lanes => _lanes;

    public Traffic(IRenderer r, TerrainModule terrain, Func<VehicleManager?> vm) { _r = r; _terrain = terrain; _vm = vm; }

    // ------------------------------------------------------------------ Laden
    void EnsureLoaded(VehicleManager vm)
    {
        var st = _terrain.Streamer;
        if (st?.Index == null || vm.Database == null) { _status = "warte auf Welt/Datenbank"; return; }
        string island = st.Index.IslandName;
        if (_loadedIsland == island && ReferenceEquals(_world, _terrain.Physics) && (_load != null || _lanes != null)) return;
        ClearAll();
        _loadedIsland = island;
        _world = _terrain.Physics;
        var idx = st.Index; var db = vm.Database; string bnk = vm.BnkDir;
        _status = "lade Verkehrsnetz …";
        _load = Task.Run(() => LoadData(idx, db, bnk));
    }

    static LoadResult LoadData(Paradise.Core.World.IslandIndex idx, CarDatabase db, string bnkDir)
    {
        var res = new LoadResult();
        var net = IaNetwork.LoadFromBank(idx.IslandBank!);
        res.Lanes = new TrafficLanes(net);
        int rings = 0; foreach (var j in net.Junctions.Values) if (res.Lanes.IsRoundabout(j.NodeId, out _)) rings++;
        string log = net + $", {rings} Kreisverkehre";

        // Mischung aus aiconfig.cpr
        var weights = new Dictionary<int, float>();
        int police = -1;
        try
        {
            var cfg = CarDatabase.FindFile(idx.LevelDir, "aiconfig.cpr");
            if (cfg != null)
            {
                var plain = CprCrypto.Decrypt(Paradise.Core.Files.GameFiles.ReadAllBytes(cfg));
                string xml = Bin.Latin1(plain, 0, plain.Length);
                var pm = Regex.Match(xml, @"<POLICE_CAR_ID>\s*(\d+)\s*</POLICE_CAR_ID>");
                if (pm.Success) police = int.Parse(pm.Groups[1].Value);
                int zones = 0;
                foreach (Match z in Regex.Matches(xml, @"<ZONE>(.*?)</ZONE>", RegexOptions.Singleline))
                {
                    var dens = Regex.Matches(z.Groups[1].Value, @"<NB_TRAFFIC\s+hour=""([\d.]+)""\s+frequency=""([\d.]+)""");
                    float sum = 0f;
                    var d = new float[13];
                    for (int i = 0; i < dens.Count && i < 13; i++) { d[i] = ParseF(dens[i].Groups[2].Value); sum += d[i]; }
                    if (sum <= 0f) continue;
                    zones++;
                    for (int i = 0; i < 13; i++) res.Density[i] += d[i];
                    foreach (Match v in Regex.Matches(z.Groups[1].Value, @"<VEHICLE\s+database_id=""(\d+)""\s+frequency_day=""([\d.]+)""\s+frequency_night=""([\d.]+)"""))
                    {
                        int id = int.Parse(v.Groups[1].Value);
                        weights.TryGetValue(id, out var w);
                        weights[id] = w + ParseF(v.Groups[2].Value);
                    }
                }
                if (zones > 0) for (int i = 0; i < 13; i++) res.Density[i] /= zones;
                log += $", aiconfig: {zones} Zonen, {weights.Count} Autos";
            }
        }
        catch (Exception ex) { log += ", aiconfig: " + ex.Message; }

        // Modelle
        string trafficDir = Path.Combine(idx.IslandDir, "vehicules", "traffic");
        string soundDir = Path.Combine(bnkDir, "sound", "vehicles");
        string? generic = CarDatabase.FindFile(Path.Combine(bnkDir, "vehicules", "med"), "generic_traffic_low.bnk")
                          ?? CarDatabase.FindFile(soundDir, "generic_high_high.bnk");
        foreach (var c in db.Traffic)
        {
            string model = c.Raw != null ? c.Raw.S("_car_model", "") ?? "" : "";
            string version = c.Raw != null ? c.Raw.S("_car_version", "") ?? "" : "";
            string name = (model + version).ToLowerInvariant();
            if (name.Length == 0) continue;
            var bank = CarDatabase.FindFile(trafficDir, name + ".bnk");
            if (bank == null) continue;
            float w = weights.Count == 0 ? 1f : (weights.TryGetValue(c.Id, out var ww) ? ww : 0f);
            if (c.Id == police) w *= 0.15f;
            var snd = CarDatabase.FindFile(soundDir, name + "_high.bnk") ?? generic;
            res.Types.Add(new TypeInfo { DbId = c.Id, Car = c, Name = name, Bank = bank, Sound = snd, Weight = w });
        }
        log += $", {res.Types.Count} Modelle";

        // Lackfarben TR_01..TR_11
        try
        {
            var cb = CarDatabase.FindFile(Path.Combine(bnkDir, "vehicules"), "colors.bnk");
            if (cb != null)
            {
                var bank = BnkArchive.Open(cb);
                BnkEntry? e = null;
                foreach (var x in bank.All(".2dm")) { e = x; break; }
                if (e != null)
                {
                    var lib = Material2DM.Parse(bank.Read(e));
                    for (int i = 1; i <= 11; i++)
                        if (lib.Materials.TryGetValue(MaterialDef.KeyFromName($"TR_{i:00}"), out var m) && m.TryGetParam("RAMPCLR1", out var c1))
                            res.Paints.Add(new Vector4(c1.X, c1.Y, c1.Z, 1f));
                }
            }
        }
        catch (Exception ex) { log += ", Farben: " + ex.Message; }
        res.Log = log;
        return res;
    }

    static float ParseF(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;

    void PollLoad()
    {
        if (_load == null || !_load.IsCompleted) return;
        var t = _load; _load = null;
        if (t.IsFaulted)
        {
            _status = "Fehler: " + t.Exception?.GetBaseException().Message;
            Log.Warn("Verkehr: " + t.Exception?.GetBaseException().Message);
            return;
        }
        var r = t.Result;
        _lanes = r.Lanes;
        _density = r.Density;
        _paints.Clear(); _paints.AddRange(r.Paints);
        _types.Clear(); _spawnable.Clear();
        foreach (var ti in r.Types) { _types[ti.DbId] = ti; if (ti.Weight > 0f) _spawnable.Add(ti); }
        if (_spawnable.Count == 0) _spawnable.AddRange(r.Types);
        foreach (var ti in _spawnable) StartTypeLoad(ti);   // alle Modelle im Hintergrund vorladen
        _fillUntil = _clock + 4f;
        _status = r.Log;
        Log.Info("Verkehr: " + r.Log);
    }

    void StartTypeLoad(TypeInfo ti)
    {
        if (ti.Load != null || ti.Assets != null || ti.Failed) return;
        bool snd = TrafficConfig.Sounds;
        ti.Load = Task.Run(() => CarAssets.LoadTraffic(ti.Bank, snd ? ti.Sound : null, ti.Car));
    }

    void PollTypes()
    {
        foreach (var ti in _types.Values)
        {
            if (ti.Load == null || !ti.Load.IsCompleted) continue;
            var t = ti.Load; ti.Load = null;
            if (t.IsFaulted) { ti.Failed = true; Log.Warn("Verkehrsauto " + ti.Name + ": " + t.Exception?.GetBaseException().Message); continue; }
            ti.Assets = t.Result;
        }
    }

    /// <summary>Alle Autos entfernen (Insel gewechselt, Verkehr aus).</summary>
    public void ClearCars()
    {
        foreach (var c in _cars.Values) DestroyVisual(c);
        _cars.Clear();
    }

    void ClearAll()
    {
        ClearCars();
        foreach (var ti in _types.Values) ti.Assets?.DestroyTextures();
        _types.Clear(); _spawnable.Clear();
        _lanes = null; _load = null;
    }

    public void Dispose() => ClearAll();

    // ------------------------------------------------------------------ Hauptschleife
    Vector3 _me, _meFwd = Vector3.UnitZ, _meVel, _mePrev; bool _hasMe, _meDriving, _mePrevOk;
    float _meHalfLen = 2.3f, _meHalfWidth = 0.95f;
    Vector3 _cam, _camFwd = Vector3.UnitZ;

    /// <summary>Jedes Bild. spawn = neue Autos erlaubt (Freifahrt, Spieler fährt).</summary>
    public void Update(float dt, bool spawn)
    {
        _clock += dt;
        var vm = _vm();
        if (vm == null) return;
        if (!TrafficConfig.Enabled) { if (_cars.Count > 0) ClearCars(); return; }
        EnsureLoaded(vm);
        PollLoad();
        PollTypes();
        if (_lanes == null) return;
        CollectMe(vm);
        if (_clock >= _nextPopulation)
        {
            _nextPopulation = _clock + 0.25f;
            Population(spawn && _meDriving);
        }
        BuildVisuals();
        UpdateVisuals(vm, dt);
    }

    void CollectMe(VehicleManager vm)
    {
        var s = _terrain.Streamer; var cam = _terrain.Camera;
        if (s != null && cam != null)
        {
            _cam = s.RenderToGame(cam.Position);
            var f = cam.Override?.forward ?? cam.Forward;
            _camFwd = f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vector3.UnitZ;
        }
        _meDriving = vm.Current != null;
        if (vm.Current != null)
        {
            _me = vm.PositionGame; _meFwd = vm.ForwardGame; _hasMe = true;
            var m = vm.Model;
            if (m != null) { _meHalfLen = MathF.Max(1.8f, (m.BodyMax.Z - m.BodyMin.Z) * 0.5f); _meHalfWidth = MathF.Max(0.7f, (m.BodyMax.X - m.BodyMin.X) * 0.5f); }
        }
        else if (s != null && cam != null && !_terrain.MapView) { _me = _cam; _hasMe = true; }
        else _hasMe = false;
    }

    static float Flat(Vector3 a, Vector3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return MathF.Sqrt(dx * dx + dz * dz); }

    /// <summary>Im Blickfeld der Kamera (grob: vor der Kamera, ± 70°, unter 600 m)?</summary>
    bool Visible(Vector3 p)
    {
        var d = p + Vector3.UnitY - _cam;
        float len = d.Length();
        if (len < 1f) return true;
        if (len > 600f) return false;
        return Vector3.Dot(d / len, _camFwd) > 0.34f;
    }

    // ------------------------------------------------------------------ Bevölkerung: Spawnen, Entfernen
    readonly List<(int, int)> _cells = new();
    readonly List<uint> _remove = new();

    void Population(bool spawn)
    {
        _remove.Clear();
        foreach (var c in _cars.Values)
        {
            float dMe = _hasMe ? Flat(c.Pos, _me) : 0f;
            bool parkedLong = c.Mode == 2 && c.ModeT > 60f && dMe > 120f && !Visible(c.Pos);
            bool deadEnd = c.DeadEnd && c.V < 0.2f && !Visible(c.Pos) && dMe > 60f;
            bool stuck = c.StuckT > 45f && !Visible(c.Pos) && dMe > 60f;
            if (dMe > TrafficConfig.DespawnDistance || parkedLong || deadEnd || stuck) _remove.Add(c.Id);
        }
        foreach (var id in _remove) if (_cars.TryGetValue(id, out var c)) { DestroyVisual(c); _cars.Remove(id); }

        // neue Autos nur, wenn ich fahre (nicht im Login, Interieur oder Freiflug)
        if (!spawn || !_hasMe || _spawnable.Count == 0 || _lanes == null) return;
        int target = TrafficConfig.MaxCars;
        if (TrafficConfig.DensityByTime && _density != null)
        {
            float h = DateTime.Now.Hour + DateTime.Now.Minute / 60f;
            int i = System.Math.Clamp((int)MathF.Floor(h / 2f), 0, 11);
            float d = _density[i] + (_density[i + 1] - _density[i]) * ((h - i * 2f) / 2f);
            float max = 0f; foreach (var x in _density) max = MathF.Max(max, x);
            if (max > 0f) target = (int)MathF.Round(TrafficConfig.MaxCars * d / max);
        }
        int count = 0;
        foreach (var c in _cars.Values) if (!c.Removed && Flat(c.Pos, _me) < TrafficConfig.SpawnMax + 30f) count++;
        bool fill = _clock < _fillUntil;
        int attempts = fill ? 24 : 6;
        float rMin = fill ? 60f : TrafficConfig.SpawnMin;
        _lanes.CellsInRing(_me, rMin, TrafficConfig.SpawnMax, _cells);
        if (_cells.Count == 0) return;
        for (int a = 0; a < attempts && count < target; a++)
            if (TrySpawn(rMin, fill)) count++;
    }

    bool TrySpawn(float rMin, bool fill)
    {
        var lanes = _lanes!;
        var cell = _cells[_rng.Next(_cells.Count)];
        var roads = lanes.RoadsInCell(cell);
        if (roads == null || roads.Count == 0) return false;
        int rid = roads[_rng.Next(roads.Count)];
        var road = lanes.Net.Roads[rid];
        bool fwd = _rng.NextDouble() < 0.5;
        if (lanes.LaneCount(road, fwd) == 0) fwd = !fwd;
        int cnt = lanes.LaneCount(road, fwd);
        if (cnt == 0) return false;
        var lane = lanes.Lane(rid, fwd, _rng.Next(cnt));
        if (lane == null || lane.Length < 20f) return false;
        float s = (float)(_rng.NextDouble() * (lane.Length - 16f) + 8f);
        lane.Sample(s, out var p, out var t);
        float d = Flat(p, _me);
        if (d < rMin || d > TrafficConfig.SpawnMax) return false;
        if (!fill || d < 120f) { if (d < 320f && Visible(p)) return false; }
        foreach (var c in _cars.Values) if (!c.Removed && (c.Pos - p).LengthSquared() < 28f * 28f) return false;
        var ti = PickType();
        if (ti == null) return false;
        var car = new Car
        {
            Id = ++_idCounter,
            Type = ti,
            Color = _paints.Count > 0 ? _rng.Next(_paints.Count) : 0,
            Lane = lane,
            S = s,
            SpeedFactor = TrafficConfig.SpeedFactor * (0.85f + 0.2f * (float)_rng.NextDouble()),
        };
        car.V = lane.LimitAt(s) * car.SpeedFactor * 0.85f;
        car.Pos = p + Vector3.UnitY * car.RideOffset; car.Fwd = t;
        ChooseNext(car);
        _cars[car.Id] = car;
        return true;
    }

    TypeInfo? PickType()
    {
        float sum = 0f;
        foreach (var t in _spawnable) if (!t.Failed) sum += MathF.Max(0.001f, t.Weight);
        if (sum <= 0f) return null;
        float r = (float)_rng.NextDouble() * sum;
        foreach (var t in _spawnable)
        {
            if (t.Failed) continue;
            r -= MathF.Max(0.001f, t.Weight);
            if (r <= 0f) return t;
        }
        return _spawnable[^1];
    }

    // ------------------------------------------------------------------ KI
    readonly List<(int road, bool forward)> _succ = new();

    void ChooseNext(Car c)
    {
        c.Next = null; c.Junction = null; c.DeadEnd = false; c.Committed = false;
        if (c.Lane == null || _lanes == null) return;
        _lanes.Successors(c.Lane, _succ);
        if (_succ.Count == 0) { c.DeadEnd = true; return; }
        // geradeaus bevorzugt
        float sum = 0f;
        var w = new float[_succ.Count];
        for (int i = 0; i < _succ.Count; i++)
        {
            var nl = _lanes.NextLane(c.Lane, _succ[i].road, _succ[i].forward);
            if (nl == null) continue;
            var a = new Vector3(c.Lane.EndDir.X, 0, c.Lane.EndDir.Z); var b = new Vector3(nl.StartDir.X, 0, nl.StartDir.Z);
            float dot = a.LengthSquared() > 1e-6f && b.LengthSquared() > 1e-6f ? Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)) : 0f;
            w[i] = dot > 0.85f ? 3f : dot > -0.2f ? 1f : 0.25f;
            if (_succ[i].road == c.Lane.RoadId) w[i] = 0.05f;   // wenden nur notfalls
            sum += w[i];
        }
        if (sum <= 0f) { c.DeadEnd = true; return; }
        float r = (float)_rng.NextDouble() * sum;
        int pick = 0;
        for (int i = 0; i < w.Length; i++) { r -= w[i]; if (r <= 0f) { pick = i; break; } }
        var next = _lanes.NextLane(c.Lane, _succ[pick].road, _succ[pick].forward);
        c.Next = next;
        c.Junction = _lanes.Junction(c.Lane, next);
    }

    static TrafficTrack? Cur(Car c) => c.Phase == 1 && c.Junction != null ? c.Junction : c.Lane;

    /// <summary>Punkt auf dem geplanten Weg <paramref name="ahead"/> Meter voraus.</summary>
    static Vector3 PointAhead(Car c, float ahead)
    {
        float s = c.S + ahead;
        if (c.Phase == 0)
        {
            if (s <= c.Lane!.Length || c.Junction == null) return c.Lane.PointAt(s);
            s -= c.Lane.Length;
            if (s <= c.Junction.Length || c.Next == null) return c.Junction.PointAt(s);
            return c.Next.PointAt(s - c.Junction.Length);
        }
        if (s <= c.Junction!.Length || c.Next == null) return c.Junction.PointAt(s);
        return c.Next.PointAt(s - c.Junction.Length);
    }

    struct Obstacle { public Vector3 P, V, F; public float HalfLen, HalfWidth; public Car? Car; public bool Player; }
    readonly List<Obstacle> _obs = new();

    void CollectObstacles()
    {
        _obs.Clear();
        foreach (var c in _cars.Values)
            if (!c.Removed) _obs.Add(new Obstacle { P = c.Pos, V = c.Vel, F = c.Fwd, HalfLen = c.HalfLen, HalfWidth = c.HalfWidth, Car = c });
        if (_meDriving)
            _obs.Add(new Obstacle { P = _me, V = _meVel, F = _meFwd, HalfLen = _meHalfLen, HalfWidth = _meHalfWidth, Player = true });
    }

    /// <summary>Muss das Auto vor dem Knoten warten? Kreisverkehr: Autos auf dem Ring, die auf die Einfahrt zukommen, haben Vorfahrt.</summary>
    bool NeedYield(Car c, float dEnd)
    {
        var lanes = _lanes!;
        int nodeId = c.Lane!.ToNode;
        if (!lanes.Net.Nodes.TryGetValue(nodeId, out var node) || node.Roads.Length < 3 && !(c.Junction?.Ring ?? false)) return false;
        if (c.Junction != null && c.Junction.Ring)
        {
            var j = c.Junction;
            var center = new Vector3(j.RingCenter.X, 0f, j.RingCenter.Y);
            foreach (var o in _obs)
            {
                if (o.Car == c) continue;
                float rc = Flat(o.P, center);
                if (MathF.Abs(rc - j.RingLaneRadius) > 6f) continue;   // nicht auf der Ringfahrbahn
                var oc = o.Car;
                // eigene Verkehrsautos: nur wer wirklich im Kreisel fährt (Phase 1 desselben Knotens)
                if (oc != null && !(oc.Phase == 1 && oc.Junction != null && oc.Junction.Node == nodeId)) continue;
                float up = TrafficLanes.RingDistanceTo(j, o.P);   // Weg bis zu meiner Einfahrt
                float sp = new Vector3(o.V.X, 0f, o.V.Z).Length();
                if (up < 6f) return true;                            // steht/fährt gerade an der Einfahrt
                if (up < 10f + sp * 2.5f && sp > 0.5f) return true;  // kommt in den nächsten Sekunden an
            }
            return false;
        }
        lanes.NodeRadius.TryGetValue(nodeId, out var rad);
        float R = MathF.Max(8f, rad + 3f);
        var ctr = node.Pos;
        foreach (var o in _obs)
        {
            if (o.Car == c) continue;
            float dc = Flat(o.P, ctr);
            if (dc > R + 8f) continue;
            var oc = o.Car;
            if (oc != null)
            {
                if (oc.Phase == 1 && oc.Junction != null && oc.Junction.Node == nodeId && oc.Junction.From != c.Lane) return true;
                if (oc.Phase == 0 && oc.Lane != null && oc.Lane.ToNode == nodeId && oc.Lane != c.Lane && oc.Junction != null)
                {
                    float od = oc.Lane.Length - oc.S;
                    if (od < 12f && (od < dEnd - 0.01f || (MathF.Abs(od - dEnd) <= 0.01f && oc.Id < c.Id))) return true;
                }
                continue;
            }
            // Spieler: wer sich in der Kreuzung bewegt (quer zu mir) oder darin steht
            float sp = new Vector3(o.V.X, 0f, o.V.Z).Length();
            if (dc < R && sp > 0.5f && Vector3.Dot(o.F, c.Fwd) < 0.8f) return true;
            if (dc < R * 0.8f && sp <= 0.5f) return true;
        }
        return false;
    }

    void Step(Car c, float dt)
    {
        var cur = Cur(c);
        if (cur == null) { c.Mode = 2; return; }
        float v = c.V;
        var fwdH = new Vector3(c.Fwd.X, 0f, c.Fwd.Z);
        fwdH = fwdH.LengthSquared() > 1e-6f ? Vector3.Normalize(fwdH) : Vector3.UnitZ;

        // Hindernis voraus (Verkehr und Spieler): Abstand entlang des geplanten Wegs
        float look = MathF.Max(30f, v * 3.5f + 12f);
        float gap = float.MaxValue, leadV = 0f;
        foreach (var o in _obs)
        {
            if (o.Car == c) continue;
            for (int pass = 0; pass < (o.Player ? 2 : 1); pass++)
            {
                // Spieler zusätzlich vorausberechnet (quer fahrend, einscherend)
                var op = pass == 0 ? o.P : o.P + new Vector3(o.V.X, 0f, o.V.Z) * TrafficConfig.PlayerPredict;
                var d = op - c.Pos; d.Y = 0f;
                if (d.LengthSquared() > (look + 10f) * (look + 10f)) continue;
                float f = Vector3.Dot(d, fwdH);
                if (f < 0.5f || f > look) continue;
                var p = PointAhead(c, f);
                float lat = Flat(op, p);
                float ov = Vector3.Dot(o.V, fwdH);
                float latMax = c.HalfWidth + o.HalfWidth * 0.8f + (o.Player ? 0.6f : 0.2f);
                if (lat > MathF.Max(2.0f, latMax) || (ov < -2f && lat > 1.2f && !o.Player)) continue;
                float g = f - c.HalfLen - o.HalfLen;
                if (pass == 1) g = MathF.Max(g, 0.5f);   // Vorhersage: rechtzeitig, aber nicht als stehende Wand
                if (g < gap) { gap = g; leadV = pass == 0 ? ov : MathF.Min(ov, 0f); }
            }
        }

        // Ampel voraus (Rot / Gelb mit genug Bremsweg): vor der Ampel anhalten
        if (Paradise.Core.World.TrafficLights.MustStop(c.Pos, fwdH, look, v, out float dLight))
        {
            float g = dLight - c.HalfLen - 2.5f;
            if (g < gap) { gap = g; leadV = 0f; }
        }

        float dEnd = c.Phase == 0 ? c.Lane!.Length - c.S : 0f;
        bool waiting = false;
        if (c.Phase == 0)
        {
            if (c.Junction == null)
            {
                float g = dEnd - 2f;   // Sackgasse
                if (g < gap) { gap = g; leadV = 0f; }
            }
            else if (dEnd < 40f && c.WaitT < 7f && !c.Committed)
            {
                // Vorfahrt: nur warten, wenn das Anhalten vor der Linie noch weich geht – sonst einfahren (kein Vollbremsen)
                float stopDist = v * v / (2f * TrafficConfig.YieldBrake);
                if (NeedYield(c, dEnd))
                {
                    if (stopDist < dEnd - 1.5f || v < 3f)
                    {
                        float g = dEnd - 1.5f;
                        if (g < gap) { gap = g; leadV = 0f; }
                        waiting = dEnd < 6f;
                    }
                    else if (dEnd < stopDist) c.Committed = true;
                }
                else if (dEnd < 4f) c.Committed = true;   // an der Linie und frei: einfahren
            }
        }
        if (waiting) c.WaitT += dt; else if (c.Phase == 1 || dEnd > 12f) c.WaitT = 0f;

        // Wunschtempo: Tempoprofil + rechtzeitig für die Kreuzung bremsen
        float vDes = cur.LimitAt(c.S);
        if (c.Phase == 0 && c.Junction != null)
        {
            float vj = c.Junction.MinLimit(0f, c.Junction.Length);
            vDes = MathF.Min(vDes, MathF.Sqrt(vj * vj + 2f * TrafficTrack.Brake * MathF.Max(0f, dEnd)));
        }
        else if (c.Phase == 1 && c.Next != null)
        {
            float vn = c.Next.LimitAt(0f);
            vDes = MathF.Min(vDes, MathF.Sqrt(vn * vn + 2f * TrafficTrack.Brake * MathF.Max(0f, c.Junction!.Length - c.S)));
        }
        vDes *= c.SpeedFactor;

        // IDM
        float aMax = TrafficConfig.AccelMax, b = TrafficConfig.ComfortBrake, s0 = TrafficConfig.MinGap, T = TrafficConfig.TimeGap;
        float acc = aMax * (1f - MathF.Pow(v / MathF.Max(0.5f, vDes), 4f));
        bool emergency = false;
        if (gap < float.MaxValue)
        {
            float sStar = s0 + MathF.Max(0f, v * T + v * (v - leadV) / (2f * MathF.Sqrt(aMax * b)));
            float gg = MathF.Max(0.2f, gap);
            acc -= aMax * (sStar / gg) * (sStar / gg);
            // nötige Verzögerung zum Stehen vor dem Hindernis: liegt sie über der komfortablen, ist es ein Notfall
            float closing = MathF.Max(0f, v - MathF.Max(0f, leadV));
            float need = closing * closing / (2f * MathF.Max(0.3f, gap - 0.5f));
            emergency = need > b * 1.6f || gap < 1.0f;
        }
        acc = System.Math.Clamp(acc, emergency ? -TrafficConfig.EmergencyBrake : -b * 1.6f, aMax);
        // Ruck begrenzen: Bremsen baut sich auf statt schlagartig (Notfall schneller)
        float jerk = (emergency ? TrafficConfig.EmergencyJerk : TrafficConfig.Jerk) * dt;
        acc = System.Math.Clamp(acc, c.Accel - jerk, c.Accel + jerk);
        v = MathF.Max(0f, v + acc * dt);
        if (gap < 0.3f) v = MathF.Min(v, MathF.Max(0f, leadV));
        if (v <= 0f && acc < 0f) acc = 0f;
        c.Accel = acc;
        c.V = v;
        c.Brake = acc < -1.2f || (v < 0.3f && vDes > 1f);
        c.StuckT = v < 0.1f ? c.StuckT + dt : 0f;

        // weiterfahren
        c.S += v * dt;
        for (int guard = 0; guard < 4; guard++)
        {
            if (c.Phase == 0)
            {
                if (c.S <= c.Lane!.Length) break;
                if (c.Junction == null) { c.S = c.Lane.Length; c.V = 0f; break; }
                c.S -= c.Lane.Length; c.Phase = 1;
            }
            else
            {
                if (c.S <= c.Junction!.Length) break;
                c.S -= c.Junction.Length;
                c.Lane = c.Next; c.Phase = 0;
                if (c.Lane == null) { c.Mode = 2; break; }
                ChooseNext(c);
            }
        }

        // Blinker
        int turn = 0;
        if (c.Phase == 1 && c.Junction != null) turn = c.Junction.Turn;
        else if (c.Phase == 0 && c.Junction != null && c.Lane != null && c.Lane.Length - c.S < 45f) turn = c.Junction.Turn;
        c.IndL = turn < 0; c.IndR = turn > 0;
        c.Hazard = false;
    }

    static void ComputePose(Car c, float dt)
    {
        var cur = Cur(c);
        if (cur == null) return;
        cur.Sample(c.S, out var p, out var t);
        if (t.LengthSquared() < 1e-6f) t = c.Fwd;
        c.Fwd = Vector3.Normalize(t);
        float k = dt > 0f ? 1f - MathF.Exp(-dt * 6f) : 1f;
        c.Up = Vector3.Normalize(Vector3.Lerp(c.Up, c.GroundUp, k));
        // nur Steigung neigen, nicht seitlich kippen (Straßenquerneigung)
        var rightH = Vector3.Cross(Vector3.UnitY, c.Fwd);
        if (rightH.LengthSquared() > 1e-6f)
        {
            rightH = Vector3.Normalize(rightH);
            var up = c.Up - rightH * Vector3.Dot(c.Up, rightH);
            c.Up = up.LengthSquared() > 1e-6f ? Vector3.Normalize(up) : Vector3.UnitY;
        }
        c.Vel = c.Fwd * c.V;
        c.Pos = p + Vector3.UnitY * (c.RideOffset + c.GroundDy);
        // Lenkwinkel aus der Krümmung (Radstand ~2,7 m)
        var dir = PointAhead(c, 2.7f) - p; dir.Y = 0f;
        float a1 = MathF.Atan2(c.Fwd.Z, c.Fwd.X), a2 = MathF.Atan2(dir.Z, dir.X);
        float ang = (a2 - a1) * 180f / MathF.PI;
        while (ang > 180f) ang -= 360f;
        while (ang < -180f) ang += 360f;
        float ks = dt > 0f ? 1f - MathF.Exp(-dt * 8f) : 1f;
        c.Steer += (System.Math.Clamp(ang, -35f, 35f) - c.Steer) * ks;
    }

    // ------------------------------------------------------------------ Physikschritt (vor dem Schritt der Physikwelt)
    public void FixedUpdate(float dt)
    {
        if (_lanes == null || !TrafficConfig.Enabled || dt <= 0f) return;
        if (_hasMe && _meDriving)
        {
            _meVel = _mePrevOk ? (_me - _mePrev) / dt : Vector3.Zero;
            if (_meVel.Length() > 120f) _meVel = Vector3.Zero;   // Teleport
            _mePrev = _me; _mePrevOk = true;
        }
        else { _meVel = Vector3.Zero; _mePrevOk = false; }
        CollectObstacles();
        int frame = (int)(_clock * 60f);
        foreach (var c in _cars.Values)
        {
            if (c.Removed) continue;
            c.PrevPos = c.Pos + c.VisOffset; c.PrevFwd = c.Fwd; c.PrevUp = c.Up; c.HasPrev = true;
            c.ModeT += dt;
            if (c.Mode == 0)
            {
                Step(c, dt);
                if (c.Mode != 0) continue;
                if (((frame + (int)c.Id) & 3) == 0) GroundProbe(c);
                ComputePose(c, dt);
                c.VisOffset *= MathF.Exp(-dt * 3f);
                DriveBody(c, dt);
                CheckHit(c);
            }
            else if (c.Mode == 1)
            {
                // Unfall: Physik bestimmt die Lage
                if (c.Loose is BodyHandle h && _world != null && _world.Simulation.Bodies.BodyExists(h))
                {
                    var b = _world.Body(h);
                    var rot = b.Pose.Orientation;
                    c.Fwd = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rot));
                    c.Up = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, rot));
                    c.Pos = _world.ToGame(b.Pose.Position) - Vector3.Transform(c.BoxCenter, rot);
                    c.Vel = b.Velocity.Linear;
                    bool rest = c.Vel.Length() < 0.4f && b.Velocity.Angular.Length() < 0.3f;
                    c.RestT = rest ? c.RestT + dt : 0f;
                    if (c.RestT > 1.5f || c.ModeT > 25f) { c.Mode = 2; c.ModeT = 0f; RemoveLoose(c); }
                }
                else { c.Mode = 2; c.ModeT = 0f; }
                c.V = 0f; c.Hazard = c.ModeT > 0.8f; c.Brake = false; c.IndL = c.IndR = false;
            }
            else
            {
                // steht: nach einer Weile wieder einfädeln, wenn die Spur frei ist
                c.Vel = Vector3.Zero; c.V = 0f; c.Hazard = true; c.Brake = false; c.IndL = c.IndR = false;
                if (c.ModeT > 6f) { c.RestT += dt; if (c.RestT > 2f) { c.RestT = 0f; TryRejoin(c); } }
            }
        }
    }

    void GroundProbe(Car c)
    {
        var s = _terrain.Streamer;
        if (s == null || !_hasMe || Flat(c.Pos, _me) > 160f) { c.GroundDy *= 0.9f; c.GroundUp = Vector3.UnitY; return; }
        var cur = Cur(c);
        if (cur == null) return;
        var surf = cur.PointAt(c.S);
        // wie Unity (Physics.Raycast 3 m über der Spur, 7 m nach unten): Straßen-/Brückenoberfläche aus den Kollidern. Die Höhenkarte
        // (TryGetGroundHeight) liegt unter den Straßenmodellen (Gelände abgesenkt) -> die Autos sanken damit in die Fahrbahn.
        if (_world != null && _world.Raycast(surf + Vector3.UnitY * 3f, -Vector3.UnitY, 7f, out var hit))
        {
            float dy = System.Math.Clamp(hit.Point.Y - surf.Y, -1.5f, 1.5f);
            c.GroundDy += (dy - c.GroundDy) * 0.35f;
            if (hit.Normal.Y > 0.7f) c.GroundUp = Vector3.Normalize(hit.Normal);
            return;
        }
        if (s.TryGetGroundHeight(surf, out float y) && y > surf.Y)
        {
            // ohne Kollider (noch nicht geladen): Höhenkarte nur, wenn sie über der Spur liegt (nie absenken)
            float dy = System.Math.Clamp(y - surf.Y, 0f, 1.5f);
            c.GroundDy += (dy - c.GroundDy) * 0.35f;
            // Steigung aus zwei Bodenpunkten längs der Fahrtrichtung
            var ahead = surf + c.Fwd * 1.5f;
            if (s.TryGetGroundHeight(ahead, out float y2))
            {
                var t = new Vector3(c.Fwd.X * 1.5f, y2 - y, c.Fwd.Z * 1.5f);
                var right = Vector3.Cross(Vector3.UnitY, c.Fwd);
                var n = Vector3.Cross(t, right);
                if (n.Y < 0f) n = -n;
                if (n.LengthSquared() > 1e-6f) { n = Vector3.Normalize(n); if (n.Y > 0.7f) c.GroundUp = n; }
            }
        }
    }

    void TryRejoin(Car c)
    {
        foreach (var o in _obs) if (o.Car != c && Flat(o.P, c.Pos) < 7f) return;
        if (_lanes == null || !_lanes.Nearest(c.Pos, c.Fwd, 4.5f, out var lane, out var s) || lane == null) return;
        var old = c.Pos;
        c.Lane = lane; c.S = s; c.Phase = 0; c.V = 0f; c.Accel = 0f; c.Mode = 0; c.ModeT = 0f; c.WaitT = 0f; c.StuckT = 0f;
        ChooseNext(c);
        ComputePose(c, 0f);
        c.VisOffset = old - c.Pos;
        if (c.VisOffset.LengthSquared() > 25f) c.VisOffset = Vector3.Zero;
    }

    static Quaternion RotGame(Vector3 fwd, Vector3 up)
    {
        // wie das eigene Auto: Unity-LookRotation im gespiegelten Raum, zurückgespiegelt
        var f = U.Mirror(fwd); var u = U.Mirror(up);
        return U.Mirror(U.LookRotation(f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vec.Forward, u.LengthSquared() > 1e-6f ? Vector3.Normalize(u) : Vec.Up));
    }

    /// <summary>Kinematischer Körper folgt der KI über die Geschwindigkeit (der Spieler stößt sich daran).</summary>
    void DriveBody(Car c, float dt)
    {
        if (_world == null || c.Kin is not BodyHandle h || !_world.Simulation.Bodies.BodyExists(h)) return;
        var b = _world.Body(h);
        var rot = RotGame(c.Fwd, c.Up);
        var target = _world.ToLocal(c.Pos + c.VisOffset + Vector3.Transform(c.BoxCenter, rot));
        var err = target - b.Pose.Position;
        if (err.Length() > 3f) { b.Pose.Position = target; b.Pose.Orientation = rot; b.Velocity.Linear = c.Vel; b.Velocity.Angular = Vector3.Zero; }
        else
        {
            b.Velocity.Linear = err / dt;
            var dq = rot * Quaternion.Inverse(b.Pose.Orientation);
            if (dq.W < 0f) dq = -dq;
            float sin = MathF.Sqrt(dq.X * dq.X + dq.Y * dq.Y + dq.Z * dq.Z);
            float angle = 2f * MathF.Atan2(sin, dq.W);
            b.Velocity.Angular = sin > 1e-5f ? new Vector3(dq.X, dq.Y, dq.Z) / sin * (angle / dt) : Vector3.Zero;
        }
        b.Awake = true;
    }

    /// <summary>Aufprall des Spielers (Quader-Überschneidung in der Waagerechten) mit genug Relativtempo: Unfall.</summary>
    void CheckHit(Car c)
    {
        if (!_meDriving || c.Kin == null) return;
        var d = c.Pos - _me; d.Y = 0f;
        float reach = c.HalfLen + _meHalfLen + 0.4f;
        if (d.LengthSquared() > reach * reach) return;
        if (!Overlap(c.Pos, c.Fwd, c.HalfLen + 0.15f, c.HalfWidth + 0.15f, _me, _meFwd, _meHalfLen, _meHalfWidth)) return;
        var rel = _meVel - c.Vel; rel.Y = 0f;
        if (rel.Length() < TrafficConfig.CrashSpeed) return;
        float m1 = 1400f, m2 = MathF.Max(300f, c.Type.Car.Mass);
        Crash(c, c.Vel + (_meVel - c.Vel) * (m1 / (m1 + m2)) * 1.3f);
    }

    static bool Overlap(Vector3 pa, Vector3 fa, float la, float wa, Vector3 pb, Vector3 fb, float lb, float wb)
    {
        var ax = Vector2.Normalize(new Vector2(fa.X, fa.Z) + new Vector2(1e-6f, 0f)); var ay = new Vector2(-ax.Y, ax.X);
        var bx = Vector2.Normalize(new Vector2(fb.X, fb.Z) + new Vector2(1e-6f, 0f)); var by = new Vector2(-bx.Y, bx.X);
        var t = new Vector2(pb.X - pa.X, pb.Z - pa.Z);
        foreach (var axis in new[] { ax, ay, bx, by })
        {
            float ra = la * MathF.Abs(Vector2.Dot(ax, axis)) + wa * MathF.Abs(Vector2.Dot(ay, axis));
            float rb = lb * MathF.Abs(Vector2.Dot(bx, axis)) + wb * MathF.Abs(Vector2.Dot(by, axis));
            if (MathF.Abs(Vector2.Dot(t, axis)) > ra + rb) return false;
        }
        return true;
    }

    /// <summary>Unfall: KI aus, loser Körper mit Schwerkraft.</summary>
    void Crash(Car c, Vector3 vel)
    {
        RemoveKin(c);
        c.Mode = 1; c.ModeT = 0f; c.RestT = 0f; c.V = 0f; c.Accel = 0f;
        if (_world == null) { c.Mode = 2; return; }
        var rot = RotGame(c.Fwd, c.Up);
        var h = _world.AddLooseBox(c.Pos + Vector3.Transform(c.BoxCenter, rot), rot, c.BoxSize, MathF.Max(600f, c.Type.Car.Mass));
        var b = _world.Body(h);
        b.Velocity.Linear = vel;
        b.Awake = true;
        c.Loose = h;
    }

    void SetPhysical(Car c, bool on)
    {
        if (_world == null) return;
        if (!on) { RemoveKin(c); return; }
        if (c.Kin != null || c.Mode != 0) return;
        var box = new Box(MathF.Max(0.2f, c.BoxSize.X), MathF.Max(0.2f, c.BoxSize.Y), MathF.Max(0.2f, c.BoxSize.Z));
        var shape = _world.Simulation.Shapes.Add(box);
        var rot = RotGame(c.Fwd, c.Up);
        var pose = new RigidPose(_world.ToLocal(c.Pos + Vector3.Transform(c.BoxCenter, rot)), rot);
        var desc = BodyDescription.CreateKinematic(pose, new BodyVelocity(), new CollidableDescription(shape, 0.1f, ContinuousDetection.Continuous()), new BodyActivityDescription(-1f));
        var h = _world.Simulation.Bodies.Add(desc);
        _world.RayIgnoreBodies.Add(h.Value);   // Radstrahlen des Spielers treffen es nicht (Unity Layer 2)
        c.Kin = h;
    }

    void RemoveKin(Car c)
    {
        if (c.Kin is BodyHandle h && _world != null) _world.RemoveBody(h);
        c.Kin = null;
    }

    void RemoveLoose(Car c)
    {
        if (c.Loose is BodyHandle h && _world != null) _world.RemoveBody(h);
        c.Loose = null;
    }

    // ------------------------------------------------------------------ Darstellung
    void BuildVisuals()
    {
        int built = 0;
        foreach (var c in _cars.Values)
        {
            if (c.Model != null || c.Removed || built >= 2) continue;
            var ti = c.Type;
            if (ti.Assets == null) { StartTypeLoad(ti); continue; }
            try { BuildCar(c); built++; }
            catch (Exception ex) { Log.Warn("Verkehrsauto bauen: " + ex.Message); ti.Failed = true; }
        }
    }

    void BuildCar(Car c)
    {
        var a = c.Type.Assets!;
        var model = CarModel.Build(a, _r);
        c.Model = model;
        // Lack (TR_xx: RampClr1)
        if (_paints.Count > 0)
        {
            var col = _paints[System.Math.Clamp(c.Color, 0, _paints.Count - 1)];
            foreach (var m in model.PaintMaterials) { m.Desc.Color = new Vector4(col.X, col.Y, col.Z, m.Desc.Color.W); m.Apply(_r); }
        }
        // Räder: FORK_xx lenkt, DISC_xx dreht; Bodenabstand und Radius aus den Rädern
        var wheels = new List<(CarNode?, Quaternion, float, CarNode?, Quaternion, float, bool)>();
        float minY = float.MaxValue, radius = 0f;
        foreach (var n in new[] { "FL", "FR", "RL", "RR" })
        {
            var fork = model.Find("FORK_" + n); var disc = model.Find("DISC_" + n);
            if (disc != null)
                foreach (var nd in disc.SelfAndDescendants())
                {
                    if (!nd.HasMesh) continue;
                    var m = model.LocalToVisualParent(nd);
                    for (int i = 0; i < 8; i++)
                    {
                        var p = Vector3.Transform(new Vector3((i & 1) != 0 ? nd.MeshMax.X : nd.MeshMin.X, (i & 2) != 0 ? nd.MeshMax.Y : nd.MeshMin.Y, (i & 4) != 0 ? nd.MeshMax.Z : nd.MeshMin.Z), m);
                        minY = MathF.Min(minY, p.Y);
                    }
                    radius = MathF.Max(radius, (nd.MeshMax.Y - nd.MeshMin.Y) * 0.5f);
                }
            // Vorzeichen wie Unity (TDU2Traffic): am Modell ausprobieren statt annehmen (Achsen der Knoten sind je Modell verschieden).
            // Modellraum = Spielraum bei Fwd +Z / Up +Y (RotGame = Identität). Lenkung: c.Steer > 0 dreht nach -X (atan2 steigt).
            float steerSign = 1f, spinSign = 1f;
            if (fork != null && n[0] == 'F')
            {
                var b0 = fork.LocalRotation;
                var m0 = model.LocalToVisualParent(fork);
                fork.LocalRotation = b0 * U.AngleAxis(10f, Vec.Up);
                var m1 = model.LocalToVisualParent(fork);
                fork.LocalRotation = b0;
                if (Matrix4x4.Invert(m0, out var i0))
                {
                    var v = Vector3.TransformNormal(Vector3.TransformNormal(Vector3.UnitZ, i0), m1);
                    steerSign = v.X < 0f ? 1f : -1f;
                }
            }
            if (disc != null)
            {
                // Rollen vorwärts: der obere Punkt des Rads bewegt sich nach vorne (+Z)
                var b0 = disc.LocalRotation;
                var m0 = model.LocalToVisualParent(disc);
                var top = m0.Translation + Vector3.UnitY * 0.3f;
                if (Matrix4x4.Invert(m0, out var i0))
                {
                    var local = Vector3.Transform(top, i0);
                    disc.LocalRotation = b0 * U.AngleAxis(10f, Vec.Right);
                    var moved = Vector3.Transform(local, model.LocalToVisualParent(disc));
                    disc.LocalRotation = b0;
                    spinSign = moved.Z - top.Z > 0f ? 1f : -1f;
                }
            }
            wheels.Add((fork, fork?.LocalRotation ?? Quaternion.Identity, steerSign, disc, disc?.LocalRotation ?? Quaternion.Identity, spinSign, n[0] == 'F'));
        }
        c.Wheels = wheels.ToArray();
        var bmin = model.BodyMin; var bmax = model.BodyMax;
        c.RideOffset = minY < float.MaxValue ? -minY : MathF.Max(0.2f, -bmin.Y);
        if (radius > 0.15f) c.Radius = radius;
        c.HalfLen = MathF.Max(1.5f, (bmax.Z - bmin.Z) * 0.5f);
        c.HalfWidth = MathF.Max(0.7f, (bmax.X - bmin.X) * 0.5f);
        // Körper: Box ab etwas über dem Boden (fährt ohne Bodenkontakt)
        float bottom = bmin.Y + 0.25f;
        c.BoxCenter = new Vector3((bmin.X + bmax.X) * 0.5f, (bottom + bmax.Y) * 0.5f, (bmin.Z + bmax.Z) * 0.5f);
        c.BoxSize = new Vector3((bmax.X - bmin.X) * 0.95f, MathF.Max(0.4f, bmax.Y - bottom), (bmax.Z - bmin.Z) * 0.97f);
        if (c.Mode == 0) ComputePose(c, 0f);

        var lights = new VehicleLights { External = true, UnityLights = false };
        lights.Setup(null!, model);
        c.Lights = lights;
    }

    void DestroyVisual(Car c)
    {
        RemoveKin(c); RemoveLoose(c);
        c.Audio?.Dispose(); c.Audio = null;
        c.Lights?.Dispose(); c.Lights = null;
        c.Model?.Destroy(); c.Model = null;
        c.Wheels = null;
    }

    readonly List<(Car c, float d)> _near = new();

    void UpdateVisuals(VehicleManager vm, float dt)
    {
        _near.Clear();
        foreach (var c in _cars.Values) if (c.Model != null) _near.Add((c, Vector3.Distance(c.Pos, _cam)));
        _near.Sort((x, y) => x.d.CompareTo(y.d));
        int phys = 0, snd = 0;
        var mine = vm.Lights;
        bool headlights = Paradise.Core.World.CarLighting.HeadlightsIntensity > 0.01f || Paradise.Core.World.WorldWeather.Night > 0.5f || (mine != null && mine.Headlights > 0);
        foreach (var (c, d) in _near)
        {
            // Physik nur für die nächsten Autos im Umkreis, solange der Spieler fährt
            if (c.Mode == 0)
            {
                bool want = _meDriving && d < TrafficConfig.PhysicsRadius && phys < TrafficConfig.PhysicsMax;
                if (want) phys++;
                if (want != (c.Kin != null)) SetPhysical(c, want);
            }
            c.Visible = d < 900f;
            if (!c.Visible) continue;

            float speed = c.V;
            c.Spin += speed * dt / MathF.Max(0.2f, c.Radius);
            if (c.Wheels != null && d < 250f)
                foreach (var w in c.Wheels)
                {
                    if (w.fork != null && w.front) w.fork.LocalRotation = w.fBase * U.AngleAxis(c.Steer * w.steerSign, Vec.Up);
                    if (w.disc != null) w.disc.LocalRotation = w.dBase * U.AngleAxis(c.Spin * 180f / MathF.PI * w.spinSign, Vec.Right);
                }
            if (c.Lights != null)
            {
                c.Lights.Headlights = headlights ? 1 : 0;
                c.Lights.ExtBrake = c.Brake;
                c.Lights.IndicatorLeft = c.IndL; c.Lights.IndicatorRight = c.IndR; c.Lights.Hazard = c.Hazard;
                if (mine != null) { c.Lights.Brightness = mine.Brightness; c.Lights.LensBrightness = mine.LensBrightness; }
                c.Lights.Brightness = MathF.Max(c.Lights.Brightness, 0.3f + 0.8f * Paradise.Core.World.WorldWeather.Night);
                c.Lights.Update(dt);
            }
            // Ton für die nächsten Autos (Mixer 2D: Lautstärke nach Abstand, Unity Min 4 m / Max 90 m)
            bool wantSnd = TrafficConfig.Sounds && d < TrafficConfig.SoundRadius && snd < TrafficConfig.SoundMax && c.Type.Assets?.Sound != null && _meDriving;
            if (wantSnd) snd++;
            if (wantSnd && c.Audio == null)
            {
                c.Audio = new VehicleAudio { External = true };
                c.Audio.Setup(c.Type.Assets);
            }
            else if (!wantSnd && c.Audio != null && (d > TrafficConfig.SoundRadius * 1.3f || !_meDriving)) { c.Audio.Dispose(); c.Audio = null; }
            if (c.Audio != null)
            {
                float v = MathF.Abs(speed);
                int gear = System.Math.Clamp((int)MathF.Floor(v / 7.5f), 0, 4);
                float inGear = (v - gear * 7.5f) / 7.5f;
                float att = d <= 4f ? 1f : d >= 90f ? 0f : 1f - (d - 4f) / 86f;
                c.Audio.ExtKmh = v * 3.6f;
                c.Audio.ExtRpm = 850f + gear * 350f + System.Math.Clamp(inGear, 0f, 1f) * 2300f;
                c.Audio.ExtTorque = System.Math.Clamp(c.Accel * 120f + 60f, 0f, 250f);
                c.Audio.MasterVolume = vm.Volume * 0.7f * att;
                c.Audio.Update(dt);
            }
        }
    }

    /// <summary>Scheinwerferkegel/Bremslicht der nächsten Autos (wie das eigene Auto über MapStreamer.ExtraLamps).</summary>
    public void CollectLamps(VehicleManager vm, List<LampLight> output, Matrix4x4 gameToRender)
    {
        int n = 0;
        var u2r = vm.UnityToRender(gameToRender);
        foreach (var (c, d) in _near)
        {
            if (n >= 4 || d > 120f) break;
            if (c.Lights == null || c.Model == null || !c.Visible) continue;
            c.Lights.CollectLamps(output, PoseUnity(vm, c), u2r);
            n++;
        }
    }

    /// <summary>Dargestellte Lage: zwischen Lage vor und nach dem letzten Physikschritt (Alpha) – sonst ruckelt das Auto gegenüber
    /// dem eigenen (interpolierten) Auto, weil die KI im festen Takt rechnet.</summary>
    Vector3 VisPos(Car c)
    {
        var cur = c.Pos + c.VisOffset;
        if (!c.HasPrev || (cur - c.PrevPos).LengthSquared() > 100f) return cur;
        return Vector3.Lerp(c.PrevPos, cur, System.Math.Clamp(Alpha, 0f, 1f));
    }

    Quaternion VisRot(Car c)
    {
        var cur = RotGame(c.Fwd, c.Up);
        if (!c.HasPrev) return cur;
        return Quaternion.Normalize(Quaternion.Slerp(RotGame(c.PrevFwd, c.PrevUp), cur, System.Math.Clamp(Alpha, 0f, 1f)));
    }

    VehiclePose PoseUnity(VehicleManager vm, Car c)
    {
        var rot = VisRot(c);
        return new VehiclePose { position = vm.ToUnity(VisPos(c)), rotation = U.Mirror(rot) };
    }

    /// <summary>Zeichnen (Render-Raum über die Spiel->Render-Matrix des Streamers).</summary>
    public void Render(VehicleManager vm, Matrix4x4 gameToRender)
    {
        if (!TrafficConfig.Enabled) return;
        var camU = vm.ToUnity(_cam);
        var u2r = vm.UnityToRender(gameToRender);
        foreach (var c in _cars.Values)
        {
            if (c.Model == null || !c.Visible || c.Removed) continue;
            var rot = VisRot(c);
            var root = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(VisPos(c)) * gameToRender;
            c.Model.UpdateTransforms(root);
            c.Model.Draw(_r);
            c.Lights?.RenderFlares(_r, PoseUnity(vm, c), camU, Vec.Up, u2r);
        }
    }

    // ------------------------------------------------------------------ Debug-Ansicht (Unity TDU2Traffic.OnGUI / OnDrawGizmos)

    /// <summary>Farbe wie Unity OnGUI: Unfall magenta, steht grau, wartet gelb, > 60 km/h rot, > 40 km/h cyan, sonst grün.</summary>
    static Vector4 DebugColor(Car c)
    {
        float kmh = c.V * 3.6f;
        if (c.Mode == 1) return new(1f, 0f, 1f, 1f);
        if (c.Mode == 2) return new(0.5f, 0.5f, 0.5f, 1f);
        if (c.WaitT > 0.1f) return new(1f, 0.92f, 0.016f, 1f);
        if (kmh > 60f) return new(1f, 0f, 0f, 1f);
        if (kmh > 40f) return new(0.3f, 0.95f, 1f, 1f);
        return new(0.45f, 1f, 0.45f, 1f);
    }

    static string ModeText(Car c)
    {
        if (c.Removed) return "removed";
        if (c.Mode == 1) return "crash";
        if (c.Mode == 2) return "stopped";
        if (c.Phase == 1) return "junction";
        if (c.WaitT > 0.1f) return "waiting";
        return "driving";
    }

    /// <summary>Himmelsrichtung (Unity-Welt: +Z Nord, +X Ost; Spielraum ist an X gespiegelt).</summary>
    static string Compass(Vector3 fwdGame)
    {
        var f = new Vector3(-fwdGame.X, 0f, fwdGame.Z);
        if (f.LengthSquared() < 1e-6f) return "-";
        float deg = MathF.Atan2(f.X, f.Z) * (180f / MathF.PI);
        if (deg < 0f) deg += 360f;
        int i = (int)MathF.Round(deg / 45f) % 8;
        string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        return names[i] + " " + deg.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "°";
    }

    static string TargetText(Car c)
    {
        if (c.Phase == 1 && c.Junction != null) return "junction #" + c.Junction.Node + (c.Junction.Ring ? " (roundabout)" : "");
        if (c.Next != null) return "next lane L" + c.Next.Lane + " (road " + c.Next.RoadId + ")";
        if (c.Lane != null) return "end of lane L" + c.Lane.Lane;
        return "-";
    }

    /// <summary>
    /// Debug-Linien und Beschriftungen wie Unity TDU2Traffic.OnGUI (ShowDebug): Pfeil in Fahrtrichtung, geplanter Weg 56 m voraus
    /// (ausblendend), Beschriftung Modell / Tempo / Zustand / Richtung / Ziel. Schalter in Game/Config/TrafficConfig (Debug*).
    /// Aufruf je Bild nach <see cref="Update"/> (VehicleModule), gezeichnet über Core/Render/DebugLines.
    /// </summary>
    public void DrawDebug()
    {
        if (!TrafficConfig.ShowDebug || _lanes == null) return;
        foreach (var c in _cars.Values)
        {
            if (c.Removed || (c.Lane == null && c.Junction == null)) continue;
            var wpos = VisPos(c);
            float dcam = Vector3.Distance(_cam, wpos);
            if (dcam > TrafficConfig.DebugRange) continue;
            var col = DebugColor(c);
            var fwd = new Vector3(c.Fwd.X, 0f, c.Fwd.Z);
            if (fwd.LengthSquared() < 1e-6f) fwd = Vector3.UnitZ; else fwd = Vector3.Normalize(fwd);

            if (TrafficConfig.DebugArrows)
                Paradise.Core.Render.DebugLines.Arrow(wpos + Vector3.UnitY * 0.4f, fwd, 6f, col, 1.3f);

            if (TrafficConfig.DebugRoutes && dcam < 160f && c.Mode == 0)
            {
                var prev = wpos;
                const float maxS = 56f;
                for (float s = 4f; s < maxS; s += 4f)
                {
                    var p = PointAhead(c, s);
                    Paradise.Core.Render.DebugLines.Line(prev, p, new Vector4(col.X, col.Y, col.Z, 1f - s / maxS));
                    prev = p;
                }
            }

            if (TrafficConfig.DebugLabels)
                Paradise.Core.Render.DebugLines.Label(wpos + Vector3.UnitY * 2.4f,
                    c.Type.Name + "\n" + (c.V * 3.6f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " km/h · " + ModeText(c) +
                    "\n" + Compass(fwd) + " · " + TargetText(c), col);
        }
    }

    /// <summary>Tafel unten links wie Unity OnGUI (TRAFFIC DEBUG …).</summary>
    public IEnumerable<string> DebugPanel()
    {
        yield return "TRAFFIC DEBUG   ·   " + _cars.Count + " cars" + (TrafficConfig.Enabled ? "" : "   ·   TRAFFIC OFF");
        if (string.IsNullOrEmpty(_status)) yield return "loading...";
        else foreach (var s in _status.Split('\n')) yield return s.TrimEnd('\r');
        yield return "green driving · cyan fast · red > 60 km/h · yellow waiting · magenta crash · gray stopped";
    }

    /// <summary>Autoliste fürs Entwicklermenü (Unity TDU2Traffic.Info): Kennung, Modell, Zustand, Tempo, Abstand, Spur.</summary>
    public List<(uint id, string model, string mode, float kmh, float dist, string lane)> Info()
    {
        var l = new List<(uint, string, string, float, float, string)>(_cars.Count);
        foreach (var c in _cars.Values)
            l.Add((c.Id, c.Type.Name, ModeText(c), c.V * 3.6f, Vector3.Distance(_cam, c.Pos),
                c.Phase == 1 && c.Junction != null ? "J" + c.Junction.Node : c.Lane != null ? "L" + c.Lane.Lane + " r" + c.Lane.RoadId : "-"));
        l.Sort((a, b) => a.Item5.CompareTo(b.Item5));
        return l;
    }

    /// <summary>Autos mit Physikkörper / im Unfall / stehend.</summary>
    public (int phys, int crash, int park) Counts()
    {
        int phys = 0, crash = 0, park = 0;
        foreach (var c in _cars.Values) { if (c.Kin != null) phys++; if (c.Mode == 1) crash++; if (c.Mode == 2) park++; }
        return (phys, crash, park);
    }

    /// <summary>Zeilen für das Entwicklermenü.</summary>
    public IEnumerable<string> DebugLines()
    {
        yield return "Status: " + _status;
        int phys = 0, crash = 0, park = 0;
        foreach (var c in _cars.Values) { if (c.Kin != null) phys++; if (c.Mode == 1) crash++; if (c.Mode == 2) park++; }
        yield return $"Autos {_cars.Count} (Physik {phys}, Unfall {crash}, stehen {park}), Modelle {_types.Count}";
    }
}
