// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceOpponents.cs).
using BepuPhysics;
using BepuPhysics.Collidables;
using Paradise.Core.Diagnostics;
using Paradise.Core.Math;
using Paradise.Core.Physics;
using Paradise.Core.Racing;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using System.Numerics;

namespace Paradise.Game.Racing;

/// <summary>
/// Fahrzeuge einer Prüfung (ig2 VEHI), z.B. die zu überholenden Autos der Überhol-Tests, und Meisterschafts-Gegner.
/// Vereinfachte KI wie Unity: das Modell (DBCarPhysicsData-ID "VSph") fährt kinematisch mit seinem Höchsttempo ("Spmx") die
/// Checkpoint-Linie der Prüfung ab dem nächsten vor ihm liegenden Tor entlang (Straßenroute über das Verkehrsnetz, sonst geglättet
/// durch die Tore). Es fährt los, sobald der Spieler den Checkpoint "Stch" − 1 passiert hat (Stch = 1: beim Start) – vermutet,
/// im Original steuert das AiVehicleOppRace.
/// Engine: alles in Spielkoordinaten (Inselkoordinaten), gezeichnet wie Gameplay/Traffic bzw. Online/RemoteCars (CarModel,
/// kinematischer Körper). Namen über den Autos: Online/NameTags (general.bnk av_name_XX, Label "npc") statt Unity-OnGUI.
/// </summary>
public sealed class RaceOpponents
{
    sealed class Opp
    {
        public RaceOpponentDef Def = null!;
        public Task<CarAssets>? Load;
        public CarModel? Model;
        public CarAssets? Assets;
        public readonly List<Vector3> Path = new();   // Spielkoordinaten
        public int Seg;
        public float SegT, Speed;
        public bool Active, Done;
        public Vector3 Island, Fwd = Vec.Forward;   // aktuelle Lage (Spielkoordinaten); Fwd waagerecht
        public Quaternion Rot = Quaternion.Identity;
        public Vector3 Pos;                          // Ursprung des Modells (mit Bodenabstand)
        public float Lift;                           // Abstand Ursprung -> Unterkante (Räder) in m
        public float Y; public bool HasY;
        public float Pitch; public bool HasPitch;    // Grad, positiv = Nase hoch
        // Räder wie Gameplay/Traffic: STIRRUP_xx/FORK_xx (Lenkung), DISC_xx (Drehung)
        public (CarNode? fork, Quaternion fBase, float steerSign, CarNode? disc, Quaternion dBase, float spinSign, float radius, bool front)[]? Wheels;
        public float Spin, Steer;
        public float[]? VLim;          // Kurventempo je Routenpunkt (m/s), null = keine Begrenzung
        public float Traveled;         // gefahrener Weg ab der Startlinie (m)
        public bool Out;               // Eliminator: ausgeschieden
        public float FinishTime = -1f; // Rennzeit beim Überqueren der Ziellinie (s), -1 = noch nicht
        public float Shift, ShiftTarget, ClearSince;   // seitliches Ausweichen/Überholen (m, zusätzlich zur Spur)
        public float HalfLen = 1.8f;
        // Bodenschatten (wie VehicleEffects des Spielers / stehende Autos im Interieur)
        public IGpuMesh? ShadowMesh;
        public IGpuMaterial? ShadowMat;
        public IGpuTexture? ShadowBlob;
        // kinematischer Körper (Unity: Rigidbody isKinematic + BoxCollider)
        public BodyHandle? Body;
        public Vector3 BoxCenter, BoxSize;
    }

    /// <summary>Gegner weichen einander und dem Spieler aus (bremsen hinter langsameren Autos, überholen seitlich).</summary>
    public bool Avoidance = true;
    /// <summary>Namen der Gegner über den Autos anzeigen (Online/NameTags).</summary>
    public bool NameTags = true;

    /// <summary>Stand eines Gegners (für Platzierung/Ergebnis).</summary>
    public struct Standing
    {
        public RaceOpponentDef Def;
        public string Name, Car;
        public float Traveled, FinishTime, Speed;
        /// <summary>Eliminator: ausgeschieden (<see cref="Eliminate"/>).</summary>
        public bool Out;
        public Vector3 Position;   // Inselkoordinaten des Autos (für SpeedTrap-Portale)
        public bool Finished => FinishTime >= 0f;
    }

    /// <summary>Weg der Ziellinie ab Start (m); erreicht ein Gegner ihn, wird seine Zielzeit gemerkt. 0 = aus.</summary>
    public float FinishDistance;
    /// <summary>Rennzeit (s) für die Zielzeiten der Gegner (vom Renn-Manager).</summary>
    public Func<float>? Clock;

    public int Count => _opps.Count;
    /// <summary>Nach dem Zieleinlauf des Spielers weiterfahren (Rennen), statt stehen zu bleiben.</summary>
    public bool KeepDrivingAfterFinish;

    /// <summary>Die Gegner der laufenden Prüfung (Zeichnen/Physik über VehicleModule, Namen über NameTags).</summary>
    public static RaceOpponents? Active { get; private set; }
    /// <summary>Renderer und Physikwelt (VehicleModule.Load).</summary>
    public static IRenderer? Renderer;
    public static PhysicsWorld? World;

    /// <summary>Spielpositionen der Gegnerautos (Minimap).</summary>
    public void WorldPositions(List<Vector3> into)
    {
        foreach (var o in _opps) if (o.Model != null && !o.Done) into.Add(o.Island);
    }

    /// <summary>Karosserie-Boxen der Gegner in Spielkoordinaten (Mitte, Drehung, Größe) für die Debug-Ansicht.</summary>
    public IEnumerable<(Vector3 center, Quaternion rot, Vector3 size)> DebugBoxes()
    {
        foreach (var o in _opps)
        {
            if (o.Model == null || !o.Active || o.Done) continue;
            // Gleiche Lage wie beim Zeichnen des Modells (RenderActive); die Körperpose hinkt im FixedUpdate hinterher.
            yield return (o.Pos + Vector3.Transform(o.BoxCenter, o.Rot), o.Rot, o.BoxSize);
        }
    }

    public List<Standing> Standings()
    {
        var l = new List<Standing>(_opps.Count);
        foreach (var o in _opps)
            l.Add(new Standing { Def = o.Def, Name = o.Def.Name ?? "", Car = o.Assets?.Car?.ModelName ?? "", Traveled = o.Traveled, FinishTime = o.FinishTime, Speed = o.Speed, Position = o.Island, Out = o.Out });
        return l;
    }

    readonly List<Opp> _opps = new();
    RaceManager? _race;
    MapStreamer? _streamer;
    VehicleManager? _vehicles;
    RaceDefinition? _def;
    float _time;

    public void Setup(RaceManager race, MapStreamer? streamer, VehicleManager? vehicles, RaceDefinition def)
    {
        Clear();
        _race = race; _streamer = streamer; _vehicles = vehicles; _def = def;
        if (streamer == null || vehicles == null || vehicles.Database == null) return;
        Active = this;
        string bnk = vehicles.BnkDir; var db = vehicles.Database;
        foreach (var d in def.Opponents)
        {
            if (!db.ById.TryGetValue(d.CarId, out var car) || string.IsNullOrEmpty(bnk))
            {
                Log.Warn("Prüfungsfahrzeug " + d.CarId + " nicht gefunden");
                continue;
            }
            var o = new Opp { Def = d };
            o.Load = Task.Run(() => CarAssets.Load(bnk, db, car, 0, false));
            BuildPath(o);
            _opps.Add(o);
        }
    }

    void BuildPath(Opp o)
    {
        o.Traveled = o.Def.StartDistance;
        if (o.Def.PathOverride != null && o.Def.PathOverride.Count > 1)
        {
            o.Path.AddRange(o.Def.PathOverride);
            Densify(o.Path);
            if (MathF.Abs(o.Def.LateralOffset) > 0.01f) Offset(o.Path, o.Def.LateralOffset);
            if (o.Def.LatAccel > 0f) o.VLim = CurveSpeeds(o.Path, o.Def.LatAccel, o.Def.Brake, o.Def.MaxSpeed);
            return;
        }
        o.Path.Add(o.Def.Position);
        var cps = _def!.Checkpoints;
        // nächstes Tor vor dem Auto (in Fahrtrichtung, am nächsten)
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < cps.Count; i++)
        {
            var d = cps[i].Position - o.Def.Position; d.Y = 0f;
            if (Vector3.Dot(d, o.Def.Forward) <= 0f) continue;
            float m = d.LengthSquared();
            if (m < bestD) { bestD = m; best = i; }
        }
        if (best < 0) { o.Path.Add(o.Def.Position + o.Def.Forward * 300f); return; }
        int laps = _def.IsCircuit ? _def.Laps : 1;
        for (int l = 0; l < laps; l++)
        {
            for (int i = (l == 0 ? best : 0); i < cps.Count; i++) o.Path.Add(cps[i].Position);
            if (_def.IsCircuit) o.Path.Add(cps[0].Position);
        }
        // etwas über das Ziel hinaus weiterfahren
        var last = o.Path[^1]; var prev = o.Path[System.Math.Max(0, o.Path.Count - 2)];
        var dir = last - prev; dir.Y = 0f;
        if (dir.LengthSquared() > 1e-3f) o.Path.Add(last + Vector3.Normalize(dir) * 200f);
        // entlang der Straßen fahren (Verkehrsnetz), sonst geglättet durch die Tore
        var lanes = RaceRoadPath.FindLanes();
        int legs = 0;
        var road = lanes != null ? RaceRoadPath.Route(lanes, o.Path, out _, out legs) : null;
        if (road != null && legs > 0) { o.Path.Clear(); o.Path.AddRange(road); Log.Info($"Gegner #{o.Def.CarId}: Straßenroute {legs} Abschnitte, {road.Count} Punkte"); }
        else { Smooth(o.Path); Log.Info($"Gegner #{o.Def.CarId}: keine Straßenroute (Netz {(lanes != null ? "da" : "fehlt")}), Linie durch die Tore"); }
        Densify(o.Path);
        if (o.Def.LatAccel > 0f) o.VLim = CurveSpeeds(o.Path, o.Def.LatAccel, o.Def.Brake, o.Def.MaxSpeed);
    }

    /// <summary>Rechte Seite einer waagerechten Richtung im Spielraum (wie Unity im gespiegelten Inselraum: (z, 0, −x)).</summary>
    static Vector3 RightOf(Vector3 d) => new(d.Z, 0f, -d.X);

    /// <summary>Seitlich versetzen (rechts = positiv), am Anfang über 40 m von der Startposition auf den Versatz einblenden.</summary>
    static void Offset(List<Vector3> path, float off)
    {
        var src = new List<Vector3>(path);
        float s = 0f;
        for (int i = 0; i < src.Count; i++)
        {
            if (i > 0) s += Vector3.Distance(src[i - 1], src[i]);
            var d = src[System.Math.Min(src.Count - 1, i + 2)] - src[System.Math.Max(0, i - 2)]; d.Y = 0f;
            if (d.LengthSquared() < 1e-6f) continue;
            var right = Vector3.Normalize(RightOf(d));
            // 0 am Start (kein Sprung aus dem Startgitter) -> voller Linienversatz nach 40 m.
            path[i] = src[i] + right * off * Mathf.Clamp01(s / 40f);
        }
    }

    /// <summary>
    /// Höchsttempo je Punkt aus der Krümmung (v = sqrt(aQuer / k), Krümmung über ±6 m gemessen), danach rückwärts
    /// mit Bremsverzögerung <paramref name="brake"/> ausgeglichen, damit vor Kurven rechtzeitig gebremst wird.
    /// Wichtig: die Geraden auf <paramref name="top"/> begrenzen – mit 999 m/s wäre die Rückwärtsbremsung
    /// wirkungslos und die Gegner würden erst IN der Kurve abbremsen (statt davor).
    /// </summary>
    static float[] CurveSpeeds(List<Vector3> p, float latAcc, float brake, float top)
    {
        int n = p.Count;
        var v = new float[n];
        float cap = top > 1f ? top : 999f;
        for (int i = 0; i < n; i++)
        {
            int a = System.Math.Max(0, i - 6), b = System.Math.Min(n - 1, i + 6);
            var d1 = p[i] - p[a]; var d2 = p[b] - p[i]; d1.Y = 0f; d2.Y = 0f;
            float len = d1.Length() + d2.Length();
            float ang = d1.LengthSquared() > 1e-4f && d2.LengthSquared() > 1e-4f ? Angle(d1, d2) * (MathF.PI / 180f) : 0f;
            float k = len > 0.5f ? ang / len : 0f;   // 1/m
            v[i] = MathF.Min(cap, k > 1e-4f ? MathF.Sqrt(latAcc / k) : cap);
        }
        for (int i = n - 2; i >= 0; i--)
        {
            float ds = Vector3.Distance(p[i], p[i + 1]);
            v[i] = MathF.Min(v[i], MathF.Sqrt(v[i + 1] * v[i + 1] + 2f * brake * ds));
        }
        return v;
    }

    /// <summary>Unity Vector3.Angle (Grad).</summary>
    static float Angle(Vector3 a, Vector3 b)
    {
        float d = MathF.Sqrt(a.LengthSquared() * b.LengthSquared());
        return d < 1e-15f ? 0f : MathF.Acos(System.Math.Clamp(Vector3.Dot(a, b) / d, -1f, 1f)) * (180f / MathF.PI);
    }

    /// <summary>Punkte höchstens ~1 m auseinander (gleichmäßige Fahrt, Blick voraus).</summary>
    static void Densify(List<Vector3> path)
    {
        if (path.Count < 2) return;
        var src = new List<Vector3>(path); path.Clear(); path.Add(src[0]);
        for (int i = 1; i < src.Count; i++)
        {
            float d = Vector3.Distance(src[i - 1], src[i]);
            int n = System.Math.Clamp(Mathf.CeilToInt(d), 1, 2000);
            for (int k = 1; k <= n; k++) path.Add(Vector3.Lerp(src[i - 1], src[i], k / (float)n));
        }
    }

    /// <summary>Route glätten: Catmull-Rom durch die Punkte, alle ~1 m ein Punkt (keine Knicke an den Toren).</summary>
    static void Smooth(List<Vector3> path)
    {
        if (path.Count < 3) return;
        var src = new List<Vector3>(path);
        path.Clear();
        for (int i = 0; i + 1 < src.Count; i++)
        {
            Vector3 P(int k) => src[System.Math.Clamp(k, 0, src.Count - 1)];
            Vector3 p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
            int sub = System.Math.Clamp(Mathf.CeilToInt(Vector3.Distance(p1, p2)), 1, 600);
            for (int k = 0; k < sub; k++)
            {
                float u = k / (float)sub, u2 = u * u, u3 = u2 * u;
                path.Add(0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (-p0 + 3f * p1 - 3f * p2 + p3) * u3));
            }
        }
        path.Add(src[^1]);
    }

    public void OnRaceStarted() { foreach (var o in _opps) if (o.Def.StartCheckpoint <= 1) o.Active = true; }

    public void OnCheckpoint(int cpIndex) { foreach (var o in _opps) if (!o.Active && cpIndex + 1 >= o.Def.StartCheckpoint) o.Active = true; }

    /// <summary>Jedes Bild (RaceManager.Update; steht mit dem Pausemenü).</summary>
    public void Update(float dt)
    {
        _time += dt;
        foreach (var o in _opps)
        {
            if (o.Model == null && o.Load != null && o.Load.IsCompleted)
            {
                var t = o.Load; o.Load = null;
                if (t.IsFaulted) { Log.Warn("Prüfungsfahrzeug: " + t.Exception?.GetBaseException().Message); continue; }
                try { Spawn(o, t.Result); }
                catch (Exception ex) { Log.Warn("Prüfungsfahrzeug aufbauen: " + ex.Message); t.Result?.DestroyTextures(); DestroyModel(o); }
            }
            if (o.Model == null || !o.Active || o.Done || dt <= 0f || _race == null) continue;
            if (!_race.Running && !(KeepDrivingAfterFinish && _race.Finished)) continue;
            Drive(o, dt);
        }
    }

    void Spawn(Opp o, CarAssets a)
    {
        var rend = Renderer ?? throw new InvalidOperationException("kein Renderer (VehicleModule)");
        o.Assets = a;
        // Bodenschatten unter dem KI-Auto: gleiche Textur/Größe wie beim Spieler (VehicleEffects), nur statisch.
        var (sm, smat, sblob) = VehicleEffects.CreateStaticShadow(rend, a, 0.7f);
        o.ShadowMesh = sm; o.ShadowMat = smat; o.ShadowBlob = sblob;
        var model = CarModel.Build(a, rend);
        o.Model = model;
        // Unterkante (Reifen) bestimmen, damit das Auto auf dem Boden steht und nicht darin; Räder (Vorzeichen am Modell ausprobiert)
        var wheels = new List<(CarNode?, Quaternion, float, CarNode?, Quaternion, float, float, bool)>();
        float minY = float.MaxValue;
        foreach (var n in new[] { "FL", "FR", "RL", "RR" })
        {
            var fork = model.Find("STIRRUP_" + n) ?? model.Find("FORK_" + n); var disc = model.Find("DISC_" + n);
            bool front = n[0] == 'F';
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
                }
            float steerSign = 1f, spinSign = 1f;
            if (fork != null && front)
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
            wheels.Add((fork, fork?.LocalRotation ?? Quaternion.Identity, steerSign, disc, disc?.LocalRotation ?? Quaternion.Identity, spinSign, model.TireRadiusFx(front), front));
        }
        o.Wheels = wheels.ToArray();
        var bmin = model.BodyMin; var bmax = model.BodyMax;
        o.Lift = minY < float.MaxValue && minY < 0.5f ? -minY : MathF.Max(0f, -bmin.Y);
        o.HalfLen = MathF.Max(1f, (bmax.Z - bmin.Z) * 0.5f * 0.8f);
        // Kollision: Karosserie-Box (Unity BoxCollider 95 % / 90 % / 97 %)
        o.BoxCenter = (bmin + bmax) * 0.5f;
        o.BoxSize = new Vector3((bmax.X - bmin.X) * 0.95f, MathF.Max(0.5f, (bmax.Y - bmin.Y) * 0.9f), (bmax.Z - bmin.Z) * 0.97f);
        Place(o, o.Def.Position, o.Def.Forward, 0f);
        AddBody(o);
    }

    void Drive(Opp o, float dt)
    {
        float target = MathF.Max(3f, o.Def.MaxSpeed);
        if (o.VLim != null && o.Seg < o.VLim.Length) target = MathF.Min(target, MathF.Max(6f, o.VLim[System.Math.Min(o.VLim.Length - 1, o.Seg + 2)]));
        if (Avoidance) target = Avoid(o, target);
        // Fahrergesetz: Beschleunigung linear auf 0 am Höchstempo, Bremse aus der Schwierigkeit
        // (statt pauschal 9 m/s² – das ließ die Gegner in Kurven viel zu spät langsam werden).
        float top = MathF.Max(10f, o.Def.MaxSpeed);
        float accel = MathF.Max(0.5f, o.Def.Accel) * Mathf.Clamp01(1f - o.Speed / top);
        float brake = MathF.Max(2f, o.Def.Brake);
        o.Speed = Mathf.MoveTowards(o.Speed, target, (o.Speed < target ? accel : brake) * dt);
        float dist = o.Speed * dt;
        o.Traveled += dist;
        if (o.FinishTime < 0f && FinishDistance > 0f && o.Traveled >= FinishDistance) o.FinishTime = Clock != null ? Clock() : _time;
        while (dist > 0f && o.Seg < o.Path.Count - 1)
        {
            var a = o.Path[o.Seg]; var b = o.Path[o.Seg + 1];
            float len = Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
            if (len < 0.01f) { o.Seg++; o.SegT = 0f; continue; }
            float left = len * (1f - o.SegT);
            if (dist < left) { o.SegT += dist / len; dist = 0f; }
            else { dist -= left; o.Seg++; o.SegT = 0f; }
        }
        if (o.Seg >= o.Path.Count - 1) { o.Done = true; return; }
        var pos = Vector3.Lerp(o.Path[o.Seg], o.Path[o.Seg + 1], o.SegT);
        // Spurwechsel: Querbewegung nach Tempo (ca. 2 s für eine Spur, langsam fahrend nicht seitlich "schieben")
        float shiftRate = System.Math.Clamp(o.Speed * 0.12f, 0.4f, 1.8f);
        float shiftVel = System.Math.Clamp((o.ShiftTarget - o.Shift) * 2f, -shiftRate, shiftRate);
        o.Shift = Mathf.MoveTowards(o.Shift, o.ShiftTarget, MathF.Abs(shiftVel) * dt);
        var sd = o.Path[o.Seg + 1] - o.Path[o.Seg]; sd.Y = 0f;
        var sRight = sd.LengthSquared() > 1e-4f ? RightOf(Vector3.Normalize(sd)) : Vector3.Zero;
        pos += sRight * o.Shift;
        // Blickrichtung: Punkt auf der (gleich versetzten) Route in tempoabhängiger Entfernung, plus Querbewegung
        var aheadPos = PointAhead(o, System.Math.Clamp(o.Speed * 0.45f, 5f, 14f));
        var fwd = aheadPos - pos; fwd.Y = 0f;
        if (fwd.LengthSquared() > 1e-4f && MathF.Abs(shiftVel) > 0.01f && o.Speed > 1f)
            fwd = Vector3.Normalize(fwd) + sRight * (shiftVel / o.Speed);
        if (fwd.LengthSquared() < 1e-4f) fwd = o.Fwd;
        var cur = o.Fwd;
        if (cur.LengthSquared() > 1e-4f) fwd = SlerpDir(Vector3.Normalize(cur), Vector3.Normalize(fwd), Mathf.Clamp01(6f * dt));
        // Lenkwinkel aus der Kurve voraus (wie Gameplay/Traffic: Steer > 0 = atan2(z, x) steigt)
        var look = PointAhead(o, System.Math.Clamp(o.Speed * 0.9f, 10f, 28f)) - pos; look.Y = 0f;
        float steer = 0f;
        if (look.LengthSquared() > 1f && fwd.LengthSquared() > 1e-4f)
        {
            float ang = (MathF.Atan2(look.Z, look.X) - MathF.Atan2(fwd.Z, fwd.X)) * (180f / MathF.PI);
            while (ang > 180f) ang -= 360f;
            while (ang < -180f) ang += 360f;
            steer = System.Math.Clamp(ang * 1.5f, -30f, 30f);
        }
        o.Steer = Mathf.Lerp(o.Steer, steer, Mathf.Clamp01(6f * dt));
        Place(o, pos, fwd, dt);
        if (o.Wheels != null)
        {
            o.Spin += o.Speed * dt;
            foreach (var w in o.Wheels)
            {
                if (w.fork != null && w.front) w.fork.LocalRotation = w.fBase * U.AngleAxis(o.Steer * w.steerSign, Vec.Up);
                if (w.disc != null) w.disc.LocalRotation = w.dBase * U.AngleAxis(o.Spin / MathF.Max(0.2f, w.radius) * (180f / MathF.PI) * w.spinSign, Vec.Right);
            }
        }
    }

    /// <summary>Unity Vector3.Slerp für Einheitsrichtungen.</summary>
    static Vector3 SlerpDir(Vector3 a, Vector3 b, float t)
    {
        float dot = System.Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        float th = MathF.Acos(dot) * t;
        var rel = b - a * dot;
        if (rel.LengthSquared() < 1e-12f) return dot > 0f ? a : Vector3.Normalize(Vector3.Lerp(a, b, t) + new Vector3(1e-4f, 0f, 0f));
        rel = Vector3.Normalize(rel);
        return a * MathF.Cos(th) + rel * MathF.Sin(th);
    }

    /// <summary>Punkt d Meter voraus auf der Route (ab der aktuellen Lage), um den aktuellen Spurversatz verschoben.</summary>
    static Vector3 PointAhead(Opp o, float d)
    {
        int seg = o.Seg; float t = o.SegT;
        while (seg < o.Path.Count - 1)
        {
            var a = o.Path[seg]; var b = o.Path[seg + 1];
            var ab = b - a; ab.Y = 0f;
            float len = ab.Length();
            float left = len * (1f - t);
            if (len > 0.01f && d <= left)
            {
                var p = Vector3.Lerp(a, b, t + d / len);
                return p + RightOf(ab / len) * o.Shift;
            }
            d -= left; seg++; t = 0f;
        }
        return o.Path[^1];
    }

    /// <summary>
    /// Andere Autos (Gegner und Spieler) im Fahrschlauch voraus: Tempo auf deren Tempo begrenzen und seitlich zur freien
    /// Seite ausweichen (Überholen); ist der Weg wieder frei, langsam zurück auf die eigene Spur. Ohne das schieben sich die
    /// (kinematischen) Gegner ineinander bzw. in den Spieler.
    /// </summary>
    float Avoid(Opp o, float target)
    {
        if (o.Model == null) return target;
        var fwd = o.Fwd; fwd.Y = 0f;
        if (fwd.LengthSquared() < 1e-4f) return target;
        fwd = Vector3.Normalize(fwd);
        var right = RightOf(fwd);
        float look = 12f + 0.6f * o.Speed;   // opponents_spec: 12 m + 0.6 s Vorsprung
        bool blocked = false; float blockSpeed = float.MaxValue, blockLat = 0f, blockAlong = float.MaxValue;
        bool leftBusy = false, rightBusy = false;
        void Check(Vector3 p, float speed)
        {
            var rel = p - o.Island; rel.Y = 0f;
            float along = Vector3.Dot(rel, fwd), lat = Vector3.Dot(rel, right);
            if (along > -12f && along < look * 1.3f)
            {
                if (lat > 1.0f && lat < 5.8f) rightBusy = true;
                if (lat < -1.0f && lat > -5.8f) leftBusy = true;
            }
            if (along <= 0.5f || along > look || MathF.Abs(lat) > 2.4f) return;
            if (along < blockAlong) { blockAlong = along; blockSpeed = speed; blockLat = lat; }
            blocked = true;
        }
        foreach (var x in _opps) if (x != o && x.Model != null && !x.Done) Check(x.Island, x.Speed);
        var me = _vehicles?.Current;
        if (me != null && _vehicles != null) Check(_vehicles.PositionGame, MathF.Abs(me.SpeedKmh) / 3.6f);
        if (blocked)
        {
            // Abstand halten: je näher, desto langsamer als der Vordermann
            float gap = MathF.Max(0f, blockAlong - 6f);
            float limit = blockSpeed + gap * 0.5f - (blockAlong < 7f ? 4f : 0f);
            float desired = target;
            target = MathF.Min(target, MathF.Max(0f, limit));
            // Überholen nur, wenn der Vordermann deutlich langsamer ist: zur freien Seite, höchstens ~1,5 Spuren daneben
            bool curve = o.VLim != null && o.Seg + 6 < o.VLim.Length && o.VLim[o.Seg + 6] < o.Def.MaxSpeed * 0.8f;
            // opponents_spec: überholt wird, wenn der Vordermann langsamer ist als (3 - 3 * Aggressiveness) m/s.
            // vorher fest +2 m/s -> aggressive Fahrer warteten trotz freier Seite.
            float passMargin = 3f - 3f * Mathf.Clamp01(o.Def.Aggressiveness);
            float side = desired > blockSpeed + passMargin && !curve ? (blockLat >= 0f ? -1f : 1f) : 0f;
            if (side > 0f && rightBusy) side = leftBusy ? 0f : -1f;
            else if (side < 0f && leftBusy) side = rightBusy ? 0f : 1f;
            // Querrichtung: "right" entspricht +Shift; höchstens 2,8 m neben der eigenen Linie
            if (side != 0f)
            {
                float want = Mathf.Clamp(o.ShiftTarget + side * 2.8f, -2.8f, 2.8f);
                if (MathF.Abs(want - o.ShiftTarget) > 0.5f && MathF.Abs(o.Shift - o.ShiftTarget) < 0.3f) o.ShiftTarget = want;
            }
            o.ClearSince = _time;
        }
        else if (MathF.Abs(o.ShiftTarget) > 0.01f && _time - o.ClearSince > 2.5f)
        {
            // frei: zurück auf die eigene Spur, wenn dort niemand fährt
            bool busy = o.ShiftTarget > 0f ? leftBusy : rightBusy;
            if (!busy) o.ShiftTarget = 0f;
        }
        return target;
    }

    /// <summary>Namen über den Gegnern (Online/NameTags): Lage (Spielkoordinaten) über dem Dach und Name.</summary>
    public static IEnumerable<(Vector3 anchor, string name)> Tags()
    {
        var a = Active;
        if (a == null || !a.NameTags || a._race == null || !a._race.Running) yield break;
        foreach (var o in a._opps)
        {
            if (o.Model == null || o.Done || string.IsNullOrEmpty(o.Def.Name)) continue;
            var up = Vector3.Transform(Vector3.UnitY, o.Rot);
            yield return (o.Pos + up * (o.Model.BodyMax.Y + 0.6f), o.Def.Name!);
        }
    }

    /// <summary>Auf den Boden setzen: Höhe und Neigung aus zwei Strahlen (vorne/hinten), Höhe leicht gefiltert.</summary>
    void Place(Opp o, Vector3 island, Vector3 fwd, float dt)
    {
        fwd.Y = 0f;
        if (fwd.LengthSquared() < 1e-6f) fwd = Vec.Forward;
        o.Fwd = Vector3.Normalize(fwd);
        var wf = o.Fwd;
        float half = o.Model != null ? o.HalfLen : 1.8f;
        // Erwartete Höhe: letzte Höhe bzw. Höhe der Route – Boden nur nahe dieser Höhe suchen (sonst springt das Auto unter
        // Brücken auf die Überführung bzw. fällt auf die Straße darunter)
        float refY = o.HasY ? o.Y : island.Y;
        if (!o.HasY && World != null && World.Raycast(new Vector3(island.X, island.Y + 8f, island.Z), -Vector3.UnitY, 40f, out var first)) refY = first.Point.Y;
        bool hc = Ground(island, refY, out var gc), hf = Ground(island + wf * half, refY, out var gf), hb = Ground(island - wf * half, refY, out var gb);
        float y;
        if (hf && hb) y = (gf.Y + gb.Y) * 0.5f;          // Mitte zwischen Vorder- und Hinterachse: am Hang nicht im Boden
        else if (hc) y = gc.Y;
        else y = o.HasY ? o.Y : island.Y;
        if (o.HasY && dt > 0f) y = Mathf.Lerp(o.Y, y, Mathf.Clamp01(25f * dt));   // nur leicht glätten (keine Verzögerung am Berg)
        o.Y = y; o.HasY = true;
        float pitch = 0f;
        if (hf && hb && MathF.Abs(gf.Y - gb.Y) < half * 1.2f)
            pitch = System.Math.Clamp(MathF.Atan2(gf.Y - gb.Y, 2f * half) * (180f / MathF.PI), -25f, 25f);
        o.Pitch = o.HasPitch ? Mathf.Lerp(o.Pitch, pitch, Mathf.Clamp01(10f * MathF.Max(dt, 0.016f))) : pitch; o.HasPitch = true;
        // Lage: waagerechte Richtung um die Neigung gekippt (Unity LookRotation(wf) * Euler(pitch, 0, 0))
        float pr = o.Pitch * (MathF.PI / 180f);
        var f3 = wf * MathF.Cos(pr) + Vector3.UnitY * MathF.Sin(pr);
        var rightH = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, wf));
        var up = Vector3.Normalize(Vector3.Cross(f3, rightH));
        o.Rot = Paradise.Game.Online.RemoteCars.RotGame(f3, up);
        o.Island = new Vector3(island.X, y, island.Z);
        o.Pos = o.Island + Vector3.UnitY * o.Lift;
    }

    /// <summary>
    /// Boden (statische Welt) unter p nahe der erwarteten Höhe refY: Strahl 3 m über refY, 7 m nach unten (wie Gameplay/Traffic –
    /// die Engine-Physik liefert nur den nächsten Treffer, Unity wählte aus allen den nächsten an refY); Wände ignorieren.
    /// Ohne Kollider (noch nicht geladen): Höhenkarte, wenn sie nahe refY liegt.
    /// </summary>
    bool Ground(Vector3 p, float refY, out Vector3 point)
    {
        point = p;
        if (World != null && World.Raycast(new Vector3(p.X, refY + 3f, p.Z), -Vector3.UnitY, 7f, out var hit) && hit.Normal.Y >= 0.5f)
        {
            point = hit.Point;
            return true;
        }
        if (_streamer != null && _streamer.TryGetGroundHeight(p, out float gy) && MathF.Abs(gy - refY) < 4f)
        {
            point = new Vector3(p.X, gy, p.Z);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ Physik (kinematischer Körper, wie Online/RemoteCars)
    void AddBody(Opp o)
    {
        var w = World;
        if (w == null || o.Body != null || o.Model == null) return;
        var box = new Box(MathF.Max(0.2f, o.BoxSize.X), MathF.Max(0.2f, o.BoxSize.Y), MathF.Max(0.2f, o.BoxSize.Z));
        var shape = w.Simulation.Shapes.Add(box);
        var pose = new RigidPose(w.ToLocal(o.Pos + Vector3.Transform(o.BoxCenter, o.Rot)), o.Rot);
        var desc = BodyDescription.CreateKinematic(pose, new BodyVelocity(), new CollidableDescription(shape, 0.1f, ContinuousDetection.Continuous()), new BodyActivityDescription(-1f));
        var h = w.Simulation.Bodies.Add(desc);
        w.RayIgnoreBodies.Add(h.Value);   // Bodenstrahlen (Spieler und Gegner) treffen es nicht (Unity Layer 2)
        o.Body = h;
    }

    static void RemoveBody(Opp o)
    {
        if (o.Body is BodyHandle h && World != null) World.RemoveBody(h);
        o.Body = null;
    }

    /// <summary>Vor dem Physikschritt (VehicleModule.FixedUpdate): Körper folgt der Lage über die Geschwindigkeit.</summary>
    public static void FixedUpdateActive(float dt)
    {
        var a = Active; var w = World;
        if (a == null || w == null || dt <= 0f) return;
        foreach (var o in a._opps)
        {
            if (o.Body is not BodyHandle h || !w.Simulation.Bodies.BodyExists(h)) continue;
            var b = w.Body(h);
            var target = w.ToLocal(o.Pos + Vector3.Transform(o.BoxCenter, o.Rot));
            var err = target - b.Pose.Position;
            if (err.Length() > 3f) { b.Pose.Position = target; b.Pose.Orientation = o.Rot; b.Velocity.Linear = Vector3.Zero; b.Velocity.Angular = Vector3.Zero; }
            else
            {
                b.Velocity.Linear = err / dt;
                var dq = o.Rot * Quaternion.Inverse(b.Pose.Orientation);
                if (dq.W < 0f) dq = -dq;
                float sin = MathF.Sqrt(dq.X * dq.X + dq.Y * dq.Y + dq.Z * dq.Z);
                float angle = 2f * MathF.Atan2(sin, dq.W);
                b.Velocity.Angular = sin > 1e-5f ? new Vector3(dq.X, dq.Y, dq.Z) / sin * (angle / dt) : Vector3.Zero;
            }
            b.Awake = true;
        }
    }

    // ------------------------------------------------------------------ Zeichnen (VehicleModule.Render)
    public static void RenderActive(Matrix4x4 gameToRender)
    {
        var a = Active; var rend = Renderer;
        if (a == null || rend == null) return;
        foreach (var o in a._opps)
        {
            if (o.Model == null || !o.HasY) continue;
            var root = Matrix4x4.CreateFromQuaternion(o.Rot) * Matrix4x4.CreateTranslation(o.Pos) * gameToRender;
            o.Model.UpdateTransforms(root);
            o.Model.Draw(rend);
            // Schatten liegt auf dem Boden (Island-Y), 3 cm darüber, in Fahrtrichtung des Autos.
            if (o.ShadowMesh != null && o.ShadowMat != null)
            {
                var size = o.BoxSize;
                var pose = Matrix4x4.CreateScale(MathF.Max(1f, size.X * 1.25f), 1f, MathF.Max(1f, size.Z * 1.12f))
                         * Matrix4x4.CreateFromQuaternion(o.Rot)
                         * Matrix4x4.CreateTranslation(new Vector3(o.Island.X, o.Island.Y + 0.03f, o.Island.Z))
                         * gameToRender;
                rend.DrawMesh(o.ShadowMesh, o.ShadowMat, pose);
            }
        }
    }

    static void DestroyModel(Opp o)
    {
        RemoveBody(o);
        o.Model?.Destroy(); o.Model = null;
        o.Assets?.DestroyTextures(); o.Assets = null;
        o.ShadowMesh?.Dispose(); o.ShadowMesh = null;
        o.ShadowMat?.Dispose(); o.ShadowMat = null;
        o.ShadowBlob?.Dispose(); o.ShadowBlob = null;
        o.Wheels = null;
    }

    /// <summary>
    /// Eliminator: Gegner scheidet aus (exe 0xb595e0 ruft am Teilnehmer die Methode +0x18 auf). Er fährt nicht weiter und wird
    /// entfernt (Modell, Körper, Name). [Darstellung des Ausscheidens im Original nicht verfolgt – Entfernen angenommen]
    /// </summary>
    public void Eliminate(int index)
    {
        if (index < 0 || index >= _opps.Count) return;
        var o = _opps[index];
        o.Out = true; o.Done = true;
        DestroyModel(o);
    }

    public void Clear()
    {
        foreach (var o in _opps)
        {
            DestroyModel(o);
            if (o.Load != null) { var t = o.Load; o.Load = null; t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result?.DestroyTextures(); }); }
        }
        _opps.Clear();
        if (ReferenceEquals(Active, this)) Active = null;
    }
}
