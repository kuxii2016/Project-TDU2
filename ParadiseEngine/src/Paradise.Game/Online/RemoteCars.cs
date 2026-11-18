using BepuPhysics;
using BepuPhysics.Collidables;
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Math;
using Paradise.Core.Online;
using Paradise.Core.Physics;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using System.Numerics;

namespace Paradise.Game.Online;

/// <summary>
/// Mitspieler-Autos – Darstellungsteil von Unity TDU2Multiplayer (UpdateRemotes/Sample/StartLoad/Build), gezeichnet wie der
/// Verkehr (Gameplay/Traffic: CarModel im Spielraum, VehicleLights/VehicleAudio extern):
///   * Zeitbasis des Senders (Sendezeit im Paket); Uhrversatz = kleinster (Ankunft − Sendezeit) der letzten Pakete,
///   * Puffer passt sich an (Grundverzögerung + 2,5 × Jitter, 50…400 ms), Hermite-Interpolation mit den Geschwindigkeiten,
///     kurze Extrapolation bei Paketverlust, Korrekturen weich (Teleport > 25 m springt),
///   * Original-Modell + Lack/Felge/Innenraum, Räder lenken/drehen, Lichter, Motor-/Hupen-Sound, Fahrer-Avatar mit der Kleidung
///     des Mitspielers, Lackierung/Beklebung (UDP-Karussell), Verschmutzung, Bremsscheiben-Wärme, Türen/Fenster/Verdeck,
///   * Kollision: kinematischer Körper (das eigene Auto prallt ab), Werte Game/Config/OnlineConfig.cs.
/// Namen über den Autos: <see cref="NameTags"/> (Original general.bnk statt Unity-OnGUI).
/// </summary>
public static class RemoteCars
{
    public struct Snap { public double T; public uint Seq; public Vector3 P, F, U, V; public float Steer, Kmh, Rpm, Torque; public byte Flags, Head, Flags2; }

    public sealed class Remote
    {
        public int Id;
        public int CarId = -1, Color = -1;
        // Darstellung
        public CarModel? Model;
        public CarAssets? Assets;
        public VehicleLights? Lights;
        public VehicleAudio? Audio;
        public CarAvatar? Driver;
        /// <summary>Ich als Beifahrer in diesem Auto (Party: Mitfahrt).</summary>
        public CarAvatar? Seat;
        public BodyHandle? Body;
        public Vector3 BoxCenter, BoxSize;
        public Task<CarAssets>? Load;
        public int LoadCarId, LoadColor, LoadRim, LoadInterior;
        // Ausstattung/Zustand (Erweiterung 0xE1)
        public int Rim, Interior, AccBits; public readonly float[] Heat = new float[4]; public bool HasExtra;
        public BrakeGlow? Glow; public bool GlowTried;
        // Ausstellungsauto im Raum (Erweiterung 0xA2) – für die Interieur-Spielfiguren (folgt)
        public int RoomCar, RoomCarBits, RoomCarFlags;
        public readonly List<Snap> Snaps = new();
        public uint LastSeq;
        public float LastPacket;
        public float Spin;
        public (CarNode? fork, Quaternion fBase, float steerSign, CarNode? disc, Quaternion dBase, float spinSign, float radius, bool front)[]? Wheels;
        // Zeitbasis
        public readonly Queue<double> Lag = new();
        public double Offset, Jitter; public bool HasOffset;
        // Darstellung (Spielraum)
        public bool HasVis; public Vector3 VisPos, VisVel; public Quaternion VisRot = Quaternion.Identity;
        public Snap Cur;
        public bool Visible;
        public float CamDistance;
        // Lackierung/Beklebung (UDP-Karussell)
        public uint LiveryHash; public CarLivery? Livery;
        public uint BufHash; public byte[]? Buf; public bool[]? Got; public int GotCount;
        public uint AppliedHash = uint.MaxValue;
        // Verschmutzung
        public float Dirt = -1f, Scratch; public bool Wet;
        public CarDirt? DirtView;
        // Spielfigur im Interieur (empfangen, Darstellung folgt)
        public uint Room; public Vector3 RoomPos; public float RoomYaw; public bool RoomWalk; public float RoomPacket;
    }

    static readonly Dictionary<int, Remote> _remotes = new();
    static IGameModeContext? _ctx;
    static PhysicsWorld? _world;

    public static Remote? Get(int id) => _remotes.TryGetValue(id, out var r) ? r : null;
    public static IEnumerable<Remote> All => _remotes.Values;

    internal static Remote GetOrAdd(int id)
    {
        if (!_remotes.TryGetValue(id, out var r)) { r = new Remote { Id = id }; _remotes[id] = r; }
        return r;
    }

    /// <summary>Lenkung eines Mitspielers −1 … +1 (Unity RemoteSteer).</summary>
    public static float RemoteSteer(int id) => _remotes.TryGetValue(id, out var r) ? System.Math.Clamp(r.Cur.Steer / 25f, -1f, 1f) : 0f;
    public static float RemoteKmh(int id) => _remotes.TryGetValue(id, out var r) ? r.Cur.Kmh : 0f;
    public static bool HasCar(int id) => _remotes.TryGetValue(id, out var r) && r.Model != null;

    /// <summary>Mitspieler-Autos (Darstellung) in Spielkoordinaten: Id, Position, Geschwindigkeit, Richtung, halbe Länge – für die Verkehrs-KI.</summary>
    public static void RemoteVehicles(List<(int id, Vector3 pos, Vector3 vel, Vector3 fwd, float halfLength)> outList)
    {
        outList.Clear();
        foreach (var r in _remotes.Values)
        {
            if (r.Model == null || !r.HasVis || r.CarId <= 0) continue;
            float hl = MathF.Max(1.5f, (r.Model.BodyMax.Z - r.Model.BodyMin.Z) * 0.5f);
            outList.Add((r.Id, r.VisPos, r.VisVel, r.Cur.F, hl));
        }
    }

    // ------------------------------------------------------------------ jedes Bild
    internal static void Update(IGameModeContext ctx, float dt)
    {
        _ctx = ctx;
        var vm = ctx.Vehicles?.Manager;
        _world = ctx.Terrain?.Physics;
        var cam = ctx.Terrain?.Camera; var st = ctx.Terrain?.Streamer;
        Vector3? camGame = cam != null && st != null ? st.RenderToGame(cam.Position) : null;
        var dead = new List<int>();
        dt = MathF.Max(1e-4f, dt);
        foreach (var r in _remotes.Values)
        {
            bool gone = Multiplayer.FrameClock - r.LastPacket > OnlineConfig.Timeout || !TDU2Session.LoggedIn || !TDU2GameData.Online;
            if (gone) { dead.Add(r.Id); continue; }
            if (vm == null) continue;

            bool want = r.CarId > 0 && !Multiplayer.Hidden && !Multiplayer.HideRemotes && !InteriorUtils.Inside;
            if (!want) { if (r.Model != null) DestroyModel(r); r.HasVis = false; r.Visible = false; continue; }
            bool modelOk = r.Model != null && r.Assets != null && r.Assets.Car.Id == r.CarId && r.LoadColor == r.Color
                           && r.LoadRim == r.Rim && r.LoadInterior == r.Interior;
            if (!modelOk && r.Load == null) StartLoad(vm, r);
            PollLoad(ctx, r);
            if (r.Model == null) continue;

            // Lackierung/Beklebung des Mitspielers (sobald vollständig empfangen)
            if (r.AppliedHash != r.LiveryHash && OnlineConfig.SyncLiveries)
            {
                r.AppliedHash = r.LiveryHash;
                LiveryView.For(r.Model, r.Assets, ctx.Vehicles!.Renderer)?.Show(r.Livery);
            }
            // Verschmutzung des Mitspielers auf seine Lack-Materialien
            if (r.Dirt >= 0f && OnlineConfig.SyncDirt)
            {
                if (r.DirtView == null) { r.DirtView = new CarDirt(); r.DirtView.Setup(null!, r.Model); }
                r.DirtView.SetExternal(r.Dirt, r.Scratch, r.Wet);
            }

            if (!Sample(r, out var s)) continue;
            r.Cur = s;

            // weiche Korrektur: Vorhersage aus der letzten Darstellung, Abweichung klingt ab
            if (!r.HasVis || (s.P - r.VisPos).LengthSquared() > 25f * 25f) { r.VisPos = s.P; r.VisVel = s.V; r.HasVis = true; r.VisRot = RotGame(s.F, s.U); }
            else
            {
                var predicted = r.VisPos + r.VisVel * dt;
                var err = predicted - s.P;
                var np = s.P + err * MathF.Exp(-dt * 12f);
                r.VisVel = (np - r.VisPos) / dt;
                r.VisPos = np;
            }
            var rot = RotGame(s.F, s.U);
            r.VisRot = Quaternion.Normalize(Quaternion.Slerp(r.VisRot, rot, 1f - MathF.Exp(-dt * 20f)));
            if (AngleDeg(r.VisRot, rot) > 60f) r.VisRot = rot;

            r.CamDistance = camGame.HasValue ? Vector3.Distance(camGame.Value, r.VisPos) : 0f;
            bool far = r.CamDistance > OnlineConfig.MaxDistance;
            r.Visible = !far;
            if (far) { if (r.Body != null) RemoveBody(r); }
            else if (OnlineConfig.PlayerCollisions && r.Body == null) AddBody(r);
            else if (!OnlineConfig.PlayerCollisions && r.Body != null) RemoveBody(r);

            // Räder (Unity: Lenkwinkel in Grad, Drehung über die gefahrene Strecke)
            float speed = s.Kmh / 3.6f;
            if (Vector3.Dot(s.V, s.F) < 0f) speed = -speed;
            if (r.Wheels != null)
                foreach (var w in r.Wheels)
                {
                    if (w.fork != null && w.front) w.fork.LocalRotation = w.fBase * U.AngleAxis(s.Steer * w.steerSign, Vec.Up);
                    if (w.disc != null) w.disc.LocalRotation = w.dBase * U.AngleAxis(r.Spin / MathF.Max(0.2f, w.radius) * (180f / MathF.PI) * w.spinSign, Vec.Right);
                }
            r.Spin += speed * dt;

            // Lichter
            if (r.Lights != null)
            {
                r.Lights.Headlights = s.Head;
                r.Lights.ExtBrake = (s.Flags & 2) != 0;
                r.Lights.ExtReverse = (s.Flags & 4) != 0;
                r.Lights.IndicatorLeft = (s.Flags & 8) != 0;
                r.Lights.IndicatorRight = (s.Flags & 16) != 0;
                r.Lights.Hazard = (s.Flags & 32) != 0;
                r.Lights.Flash = (s.Flags2 & 2) != 0;
                var mine = vm.Lights;
                if (mine != null) { r.Lights.Brightness = mine.Brightness; r.Lights.LensBrightness = mine.LensBrightness; }
                r.Lights.Update(dt);
            }
            // Ton (Engine-Mixer 2D: Lautstärke nach Abstand wie Traffic, Unity 3D-Quelle)
            bool audible = OnlineConfig.PlayerSounds && !far && r.CamDistance < OnlineConfig.SoundRange && !ctx.Paused;
            if (audible && r.Audio == null && r.Assets?.Sound != null)
            {
                r.Audio = new VehicleAudio { External = true };
                r.Audio.Setup(r.Assets);
            }
            else if (!audible && r.Audio != null) { r.Audio.SetHorn(false); r.Audio.Dispose(); r.Audio = null; }
            if (r.Audio != null)
            {
                float d = r.CamDistance;
                float att = d <= 4f ? 1f : d >= OnlineConfig.SoundRange ? 0f : 1f - (d - 4f) / (OnlineConfig.SoundRange - 4f);
                r.Audio.ExtRpm = s.Rpm; r.Audio.ExtKmh = MathF.Abs(s.Kmh); r.Audio.ExtTorque = s.Torque;
                r.Audio.MasterVolume = vm.Volume * att;
                r.Audio.SetHorn((s.Flags2 & 1) != 0);
                r.Audio.Update(dt);
            }
            // Bremsscheiben-Wärme und Türen/Fenster/Verdeck des Mitspielers
            if (r.HasExtra)
            {
                if (!r.GlowTried) { r.GlowTried = true; r.Glow = BrakeGlow.Create(r.Model, ctx.Vehicles!.Renderer); }
                if (r.Glow != null) { for (int i = 0; i < 4; i++) r.Glow.SetHeat(i, r.Heat[i], dt); r.Glow.Apply(); }
                var acc = CarAccessories.For(r.Model, r.Lights);
                if (acc != null)
                {
                    acc.Audio = r.Audio;
                    if (acc.Bits != r.AccBits) acc.ApplyBits(r.AccBits);
                    acc.Update(dt);
                }
            }
        }
        foreach (var id in dead) { DestroyRemote(_remotes[id]); _remotes.Remove(id); }
    }

    /// <summary>Zustand zur Darstellungszeit (Senderuhr − adaptive Verzögerung): Hermite zwischen zwei Paketen, sonst kurz extrapolieren.</summary>
    static bool Sample(Remote r, out Snap s)
    {
        s = default;
        if (r.Snaps.Count == 0 || !r.HasOffset) return false;
        double delay = System.Math.Clamp(OnlineConfig.InterpDelay * 0.5 + r.Jitter * 2.5 + 1.0 / MathF.Max(2f, OnlineConfig.SendRate), 0.05, 0.4);
        double t = Multiplayer.Clock - r.Offset - delay;   // in Senderzeit
        var snaps = r.Snaps;
        if (t <= snaps[0].T) { s = snaps[0]; return true; }
        for (int i = snaps.Count - 1; i > 0; i--)
        {
            var a = snaps[i - 1]; var b = snaps[i];
            if (t < a.T || t > b.T) continue;
            double span = System.Math.Max(1e-4, b.T - a.T);
            float k = (float)((t - a.T) / span);
            s = b;
            if ((b.P - a.P).LengthSquared() > 25f * 25f) { s.P = b.P; return true; }   // Teleport
            float h = (float)span;
            float k2 = k * k, k3 = k2 * k;
            // kubische Hermite-Kurve mit den Geschwindigkeiten
            s.P = (2 * k3 - 3 * k2 + 1) * a.P + (k3 - 2 * k2 + k) * h * a.V + (-2 * k3 + 3 * k2) * b.P + (k3 - k2) * h * b.V;
            s.V = Vector3.Lerp(a.V, b.V, k);
            s.F = Slerp(a.F, b.F, k);
            s.U = Slerp(a.U, b.U, k);
            s.Steer = a.Steer + (b.Steer - a.Steer) * k;
            s.Kmh = a.Kmh + (b.Kmh - a.Kmh) * k;
            s.Rpm = a.Rpm + (b.Rpm - a.Rpm) * k;
            s.Torque = a.Torque + (b.Torque - a.Torque) * k;
            return true;
        }
        // neuer als das letzte Paket: höchstens 0,5 s weiterrechnen
        s = snaps[^1];
        float ex = (float)System.Math.Clamp(t - s.T, 0.0, 0.5);
        s.P += s.V * ex;
        return true;
    }

    /// <summary>Unity Vector3.Slerp für Richtungen.</summary>
    static Vector3 Slerp(Vector3 a, Vector3 b, float t)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-6f || lb < 1e-6f) return Vector3.Lerp(a, b, t);
        var na = a / la; var nb = b / lb;
        float dot = System.Math.Clamp(Vector3.Dot(na, nb), -1f, 1f);
        float th = MathF.Acos(dot) * t;
        var rel = nb - na * dot;
        rel = rel.LengthSquared() > 1e-12f ? Vector3.Normalize(rel) : Vector3.Zero;
        return (na * MathF.Cos(th) + rel * MathF.Sin(th)) * (la + (lb - la) * t);
    }

    static float AngleDeg(Quaternion a, Quaternion b)
    {
        float d = MathF.Min(MathF.Abs(Quaternion.Dot(a, b)), 1f);
        return 2f * MathF.Acos(d) * (180f / MathF.PI);
    }

    /// <summary>Lage im Spielraum aus Vorne/Oben (wie Gameplay/Traffic.RotGame: Unity-LookRotation im gespiegelten Raum, zurückgespiegelt).</summary>
    internal static Quaternion RotGame(Vector3 fwd, Vector3 up)
    {
        var f = U.Mirror(fwd); var u = U.Mirror(up);
        return U.Mirror(U.LookRotation(f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vec.Forward, u.LengthSquared() > 1e-6f ? Vector3.Normalize(u) : Vec.Up));
    }

    // ------------------------------------------------------------------ Laden / Aufbauen
    static void StartLoad(VehicleManager vm, Remote r)
    {
        var db = vm.Database;
        if (db == null || string.IsNullOrEmpty(vm.BnkDir) || !db.ById.TryGetValue(r.CarId, out var car)) return;
        car = vm.FindCar(car.FileName) ?? car;
        string bnk = vm.BnkDir; int color = r.Color; bool snd = OnlineConfig.PlayerSounds; int rim = r.Rim, intr = r.Interior;
        r.LoadCarId = r.CarId; r.LoadColor = color; r.LoadRim = rim; r.LoadInterior = intr;
        r.Load = Task.Run(() => CarAssets.Load(bnk, db, car, color, snd, rim, intr));
    }

    static void PollLoad(IGameModeContext ctx, Remote r)
    {
        if (r.Load == null || !r.Load.IsCompleted) return;
        var t = r.Load; r.Load = null;
        if (t.IsFaulted) { Log.Warn("Mitspieler-Auto: " + t.Exception?.GetBaseException().Message); return; }
        if (r.LoadCarId != r.CarId || r.LoadColor != r.Color || r.LoadRim != r.Rim || r.LoadInterior != r.Interior) { t.Result?.DestroyTextures(); return; }   // inzwischen gewechselt
        DestroyModel(r);
        try { Build(ctx, r, t.Result); }
        catch (Exception ex) { Log.Warn("Mitspieler-Auto aufbauen: " + ex.Message); t.Result?.DestroyTextures(); DestroyModel(r); }
    }

    static void Build(IGameModeContext ctx, Remote r, CarAssets a)
    {
        var rend = ctx.Vehicles!.Renderer;
        var model = CarModel.Build(a, rend);
        r.Model = model; r.Assets = a; r.HasVis = false;
        r.AppliedHash = uint.MaxValue;   // Lackierung neu auflegen

        // Räder: STIRRUP_xx (Unity) bzw. FORK_xx lenkt, DISC_xx dreht – Vorzeichen am Modell ausprobiert (wie Gameplay/Traffic)
        var wheels = new List<(CarNode?, Quaternion, float, CarNode?, Quaternion, float, float, bool)>();
        foreach (var n in new[] { "FL", "FR", "RL", "RR" })
        {
            var fork = model.Find("STIRRUP_" + n) ?? model.Find("FORK_" + n); var disc = model.Find("DISC_" + n);
            bool front = n[0] == 'F';
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
            float radius = model.TireRadiusFx(front);
            wheels.Add((fork, fork?.LocalRotation ?? Quaternion.Identity, steerSign, disc, disc?.LocalRotation ?? Quaternion.Identity, spinSign, radius, front));
        }
        r.Wheels = wheels.ToArray();

        // Kollision: Karosserie-Box (Unity BoxCollider 96 % / 90 % / 98 %)
        var bmin = model.BodyMin; var bmax = model.BodyMax;
        r.BoxCenter = (bmin + bmax) * 0.5f;
        r.BoxSize = new Vector3((bmax.X - bmin.X) * 0.96f, MathF.Max(0.4f, (bmax.Y - bmin.Y) * 0.9f), (bmax.Z - bmin.Z) * 0.98f);

        var lights = new VehicleLights { External = true, UnityLights = false };
        lights.Setup(null!, model);
        r.Lights = lights;

        // Fahrer: Avatar des Mitspielers mit seiner Kleidung (Server-Spielerliste), Lenkung aus dem Zustand
        int pid = r.Id;
        r.Driver = CarAvatar.Attach(rend, model, AvatarConfigs.ForPlayer(pid), false, () => System.Math.Clamp(r.Cur.Steer / 25f, -1f, 1f));
        if (r.Driver != null) r.Driver.ConfigCode = () => AvatarConfigs.RemoteCode(pid);
    }

    static void DestroyModel(Remote r)
    {
        RemoveBody(r);
        CarAccessories.Forget(r.Model);
        r.Audio?.SetHorn(false); r.Audio?.Dispose(); r.Audio = null;
        r.Driver?.Dispose(); r.Driver = null;
        r.Seat?.Dispose(); r.Seat = null;
        r.Lights?.Dispose(); r.Lights = null;
        r.Model?.Destroy(); r.Model = null; r.DirtView = null; r.Glow = null; r.GlowTried = false;
        r.Assets?.DestroyTextures(); r.Assets = null;
        r.Wheels = null; r.HasVis = false; r.Visible = false;
    }

    static void DestroyRemote(Remote r)
    {
        DestroyModel(r);
        if (r.Load != null) { var t = r.Load; r.Load = null; t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result?.DestroyTextures(); }); }
    }

    /// <summary>Alle Mitspieler-Autos entfernen (Abmelden, Beenden).</summary>
    public static void Clear()
    {
        foreach (var r in _remotes.Values) DestroyRemote(r);
        _remotes.Clear();
    }

    // ------------------------------------------------------------------ Physik (kinematischer Körper, wie Gameplay/Traffic)
    static void AddBody(Remote r)
    {
        if (_world == null || r.Body != null || r.Model == null || !r.HasVis) return;
        var box = new Box(MathF.Max(0.2f, r.BoxSize.X), MathF.Max(0.2f, r.BoxSize.Y), MathF.Max(0.2f, r.BoxSize.Z));
        var shape = _world.Simulation.Shapes.Add(box);
        var pose = new RigidPose(_world.ToLocal(r.VisPos + Vector3.Transform(r.BoxCenter, r.VisRot)), r.VisRot);
        var desc = BodyDescription.CreateKinematic(pose, new BodyVelocity(), new CollidableDescription(shape, 0.1f, ContinuousDetection.Continuous()), new BodyActivityDescription(-1f));
        var h = _world.Simulation.Bodies.Add(desc);
        _world.RayIgnoreBodies.Add(h.Value);   // Radstrahlen des eigenen Autos treffen es nicht (Unity Layer 2)
        r.Body = h;
    }

    static void RemoveBody(Remote r)
    {
        if (r.Body is BodyHandle h && _world != null) _world.RemoveBody(h);
        r.Body = null;
    }

    /// <summary>Vor dem Physikschritt (VehicleModule.FixedUpdate): Körper folgt der Darstellung über die Geschwindigkeit.</summary>
    public static void FixedUpdate(float dt)
    {
        if (_world == null || dt <= 0f) return;
        foreach (var r in _remotes.Values)
        {
            if (r.Body is not BodyHandle h || !_world.Simulation.Bodies.BodyExists(h)) continue;
            var b = _world.Body(h);
            var target = _world.ToLocal(r.VisPos + Vector3.Transform(r.BoxCenter, r.VisRot));
            var err = target - b.Pose.Position;
            if (err.Length() > 3f) { b.Pose.Position = target; b.Pose.Orientation = r.VisRot; b.Velocity.Linear = r.VisVel; b.Velocity.Angular = Vector3.Zero; }
            else
            {
                b.Velocity.Linear = err / dt;
                var dq = r.VisRot * Quaternion.Inverse(b.Pose.Orientation);
                if (dq.W < 0f) dq = -dq;
                float sin = MathF.Sqrt(dq.X * dq.X + dq.Y * dq.Y + dq.Z * dq.Z);
                float angle = 2f * MathF.Atan2(sin, dq.W);
                b.Velocity.Angular = sin > 1e-5f ? new Vector3(dq.X, dq.Y, dq.Z) / sin * (angle / dt) : Vector3.Zero;
            }
            b.Awake = true;
        }
    }

    // ------------------------------------------------------------------ Zeichnen (VehicleModule.Render / CollectLamps)
    static VehiclePose PoseUnity(VehicleManager vm, Remote r) => new() { position = vm.ToUnity(r.VisPos), rotation = U.Mirror(r.VisRot) };

    public static void Render(VehicleManager vm, Matrix4x4 gameToRender, Vector3 cameraRender, IRenderer rend)
    {
        if (_remotes.Count == 0) return;
        var u2r = vm.UnityToRender(gameToRender);
        var camU = Matrix4x4.Invert(u2r, out var r2u) ? Vector3.Transform(cameraRender, r2u) : Vector3.Zero;
        foreach (var r in _remotes.Values)
        {
            if (r.Model == null || !r.Visible || !r.HasVis) continue;
            var root = Matrix4x4.CreateFromQuaternion(r.VisRot) * Matrix4x4.CreateTranslation(r.VisPos) * gameToRender;
            r.Model.UpdateTransforms(root);
            r.Model.Draw(rend);
            r.Driver?.Render(cameraRender, Paradise.Core.World.WorldWeather.Inside);
            r.Seat?.Render(cameraRender, Paradise.Core.World.WorldWeather.Inside);
            r.Lights?.RenderFlares(rend, PoseUnity(vm, r), camU, Vec.Up, u2r);
        }
    }

    /// <summary>Scheinwerferkegel/Bremslicht der nächsten Mitspieler (wie Traffic über MapStreamer.ExtraLamps).</summary>
    public static void CollectLamps(VehicleManager vm, List<LampLight> output, Matrix4x4 gameToRender)
    {
        int n = 0;
        var u2r = vm.UnityToRender(gameToRender);
        foreach (var r in _remotes.Values)
        {
            if (n >= 4) break;
            if (r.Lights == null || r.Model == null || !r.Visible || r.CamDistance > 120f) continue;
            r.Lights.CollectLamps(output, PoseUnity(vm, r), u2r);
            n++;
        }
    }

    /// <summary>F1/Diagnose: Id, geladen, Auto, Alter des letzten Pakets (Unity RemoteInfo).</summary>
    public static IEnumerable<(int id, bool loaded, string car, float age)> Info()
    {
        foreach (var r in _remotes.Values)
            yield return (r.Id, r.Model != null, (r.Assets?.Car?.ModelName ?? (r.Load != null ? "lädt …" : "-")) + $"  Jitter {r.Jitter * 1000:0} ms", Multiplayer.FrameClock - r.LastPacket);
    }
}
