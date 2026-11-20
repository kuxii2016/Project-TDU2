using Paradise.Core.Diagnostics;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using System.Numerics;
using DL = Paradise.Core.Render.DebugLines;

namespace Paradise.Game.Screens;

/// <summary>
/// Entwicklermenü (F1), Seiten für Auto-Physik, Physik-Debug mit Strahlen am Auto, Verkehr (Unity TrafficMenu /
/// TrafficDebugMenu, Debug-Linien wie TDU2DebugLines) und Bloom/AO (Rendering). Weltlinien: Core/Render/DebugLines
/// (gezeichnet in VehicleModule.Render), Beschriftungen und Verkehrstafel zeichnet <see cref="RenderWorldDebug"/>.
/// Physikwerte wirken sofort; Grundwerte stehen in Game/Config/VehicleConfig.cs bzw. TrafficConfig.cs / PostFxConfig.cs.
/// </summary>
public sealed partial class DevMenu
{
    // Physik-Debug am eigenen Auto (nur zur Laufzeit)
    bool _dbgRays, _dbgForces, _dbgVelocity, _dbgWheelLabels;

    /// <summary>Master-Schalter für alle vier Auto-Debug-Linien (Debug-Ansicht und Car-Debugmenü teilen die Werte).</summary>
    bool AnyCarDebug => _dbgRays || _dbgForces || _dbgVelocity || _dbgWheelLabels;
    void SetAllCarDebug(bool on) => _dbgRays = _dbgForces = _dbgVelocity = _dbgWheelLabels = on;

    static readonly Vector4 LGreen = new(0.3f, 1f, 0.3f, 1f), LRed = new(1f, 0.25f, 0.2f, 1f), LCyan = new(0.3f, 0.95f, 1f, 1f),
        LYellow = new(1f, 0.92f, 0.2f, 1f), LOrange = new(1f, 0.6f, 0.1f, 1f), LMagenta = new(1f, 0.3f, 1f, 1f), LWhite = new(1f, 1f, 1f, 1f);

    // ------------------------------------------------------------------ Auto: Physik-Einstellungen
    List<Item> CarPhysicsMenu()
    {
        var m = Mgr;
        var l = new List<Item> { Head("TIRES (current car + new cars)") };
        if (m == null) { l.Add(Info("no vehicle manager")); return l; }
        l.Add(Float("Grip scale", () => m.Current?.GripScale ?? m.GripScale, v => { m.GripScale = v; if (m.Current != null) m.Current.GripScale = v; }, 0.02f, 0.3f, 1.2f, new VehicleConfig().GripScale, "0.00"));
        l.Add(Float("Grip on dirt", () => m.Current?.DirtGrip ?? 0.82f, v => { if (m.Current != null) m.Current.DirtGrip = v; }, 0.02f, 0.2f, 1.5f, 0.82f, "0.00"));
        l.Add(Float("Grip on grass", () => m.Current?.GrassGrip ?? 0.72f, v => { if (m.Current != null) m.Current.GrassGrip = v; }, 0.02f, 0.2f, 1.5f, 0.72f, "0.00"));
        l.Add(Float("Tire load max (x static, 0 = Unity)", () => Vehicle.TireLoadMax, v => Vehicle.TireLoadMax = v, 0.1f, 0f, 10f, new VehicleConfig().TireLoadMax, "0.0", " x"));
        l.Add(Float("Grip vs. rollover (0 = Unity)", () => Vehicle.GripVsRollover, v => Vehicle.GripVsRollover = v, 0.05f, 0f, 2f, new VehicleConfig().GripVsRollover, "0.00"));

        l.Add(Head("SUSPENSION"));
        l.Add(Float("Damper knee", () => Vehicle.DamperKnee, v => Vehicle.DamperKnee = v, 0.05f, 0.05f, 5f, new VehicleConfig().DamperKnee, "0.00", " m/s"));
        l.Add(Float("Damper ratio above knee", () => Vehicle.DamperHighRatio, v => Vehicle.DamperHighRatio = v, 0.05f, 0.05f, 1f, new VehicleConfig().DamperHighRatio, "0.00"));
        l.Add(Float("Wheel max rise (0 = unlimited)", () => Vehicle.WheelMaxRise, v => Vehicle.WheelMaxRise = v, 0.25f, 0f, 20f, new VehicleConfig().WheelMaxRise, "0.00", " m/s"));
        l.Add(Float("Bump rebound", () => Vehicle.BumpRebound, v => Vehicle.BumpRebound = v, 0.05f, 0f, 1f, new VehicleConfig().BumpRebound, "0.00"));
        l.Add(Float("Min. rollover (0 = DB centre of mass)", () => Vehicle.MinRolloverG, v => Vehicle.MinRolloverG = v, 0.05f, 0f, 3f, new VehicleConfig().MinRolloverG, "0.00", " g"));

        l.Add(Head("DRIVETRAIN / UPGRADES (current car)"));
        l.Add(Float("Torque factor", () => m.Current?.UpgradeTorque ?? 1f, v => { if (m.Current != null) m.Current.UpgradeTorque = v; }, 0.05f, 0.2f, 5f, 1f, "0.00", " x"));
        l.Add(Float("Top speed factor", () => m.Current?.UpgradeTopSpeed ?? 1f, v => { if (m.Current != null) m.Current.UpgradeTopSpeed = v; }, 0.05f, 0.2f, 3f, 1f, "0.00", " x"));
        l.Add(Float("Braking factor", () => m.Current?.UpgradeBraking ?? 1f, v => { if (m.Current != null) m.Current.UpgradeBraking = v; }, 0.05f, 0.2f, 3f, 1f, "0.00", " x"));
        l.Add(Bool("Auto hold (stand still on brake)", () => m.Current?.AutoHold ?? true, v => { if (m.Current != null) m.Current.AutoHold = v; }, true));
        l.Add(Float("Physics step (new cars)", () => m.FixedTimeStep * 1000f, v => m.FixedTimeStep = v / 1000f, 1f, 5f, 20f, new VehicleConfig().FixedTimeStep * 1000f, "0", " ms"));

        l.Add(Head("BODY (current car)"));
        l.Add(Info("Mass", () => m.Current != null ? Num(m.Current.Rigidbody.mass) + " kg" : "-"));
        l.Add(Info("Centre of mass (car)", () => m.Current is { } c ? V3(c.Rigidbody.centerOfMass) : "-"));
        l.Add(Info("Inertia", () => m.Current is { } c ? V3(c.Rigidbody.inertiaTensor, "0") : "-"));
        l.Add(Info("Physics config", () => m.Current?.Config?.Source ?? "-"));
        l.Add(Head(""));
        l.Add(Info("Defaults: Game/Config/VehicleConfig.cs (Delete = default)"));
        return l;
    }

    // ------------------------------------------------------------------ Auto: Physik-Debug (live + Strahlen)
    List<Item> CarDebugMenu()
    {
        var m = Mgr;
        var l = new List<Item> { Head("DEBUG LINES IN THE WORLD") };
        l.Add(Bool("Suspension rays (green ground / red air)", () => _dbgRays, v => _dbgRays = v, false));
        l.Add(Bool("Tire forces (yellow load, orange long., cyan lat.)", () => _dbgForces, v => _dbgForces = v, false));
        l.Add(Bool("Velocity / forward arrows", () => _dbgVelocity, v => _dbgVelocity = v, false));
        l.Add(Bool("Wheel labels (load / slip)", () => _dbgWheelLabels, v => _dbgWheelLabels = v, false));
        if (m == null) { l.Add(Info("no vehicle manager")); return l; }
        l.Add(Head("ENGINE / GEARBOX"));
        l.Add(Info("Speed / forward", () => m.Current is { } c ? Num(c.SpeedKmh) + " km/h  /  " + Num(c.ForwardSpeed * 3.6f) + " km/h" : "-"));
        l.Add(Info("Rpm / gear", () => m.Current is { } c ? Num(c.Rpm) + "  /  " + (c.Gear < 0 ? "R" : c.Gear == 0 ? "N" : c.Gear.ToString(Inv)) + (c.Shifting ? "  (shifting)" : "") : "-"));
        l.Add(Info("Throttle / brake / hold", () => m.Current is { } c ? c.Throttle.ToString("0.00", Inv) + " / " + c.Brake.ToString("0.00", Inv) + " / " + c.HoldAmount.ToString("0.00", Inv) : "-"));
        l.Add(Info("Engine torque", () => m.Current is { } c ? Num(c.EngineTorque) + " Nm" : "-"));
        l.Add(Info("Steering angle", () => m.Current is { } c ? c.SteeringAngle.ToString("+0.0;-0.0", Inv) + "°" : "-"));
        l.Add(Info("Angular velocity", () => m.Current is { } c ? V3(c.Rigidbody.angularVelocity, "0.00") : "-"));
        var cur = m.Current;
        if (cur != null)
        {
            l.Add(Head("WHEELS  (comp. m · load N · slip ratio · slip angle · surface)"));
            foreach (var wh in cur.Wheels)
            {
                var w = wh;
                l.Add(Info(w.Name + (w.Front ? " F" : " R") + (w.Left ? "L" : "R") + (w.Driven ? " *" : ""), () =>
                    (w.Grounded ? "" : "AIR  ") + w.Compression.ToString("0.000", Inv) + "  " + Num(w.Fz) + " N  " +
                    w.SlipRatio.ToString("+0.00;-0.00", Inv) + "  " + (w.SlipAngle * (180f / MathF.PI)).ToString("+0.0;-0.0", Inv) + "°  s" + w.Surface));
            }
        }
        l.Add(Head(""));
        l.Add(Info("Slip angle shown in degrees (assumes radians internally)"));
        return l;
    }

    static string V3(Vector3 v, string fmt = "0.00") => v.X.ToString(fmt, Inv) + " / " + v.Y.ToString(fmt, Inv) + " / " + v.Z.ToString(fmt, Inv);

    /// <summary>Strahlen am eigenen Auto sammeln (jedes Bild, auch bei geschlossenem Menü, wenn eingeschaltet).</summary>
    void CollectCarDebug()
    {
        if (!(_dbgRays || _dbgForces || _dbgVelocity || _dbgWheelLabels)) return;
        var m = Mgr; var v = m?.Current;
        if (m == null || v == null || _terrain.MapView) return;
        var tr = v.transform;
        var up = tr.up;
        foreach (var w in v.Wheels)
        {
            var anchor = tr.TransformPoint(w.Anchor);
            var origin = anchor + up * w.TravelUp;
            var end = origin - up * (w.TravelUp + w.Droop + w.Radius);
            var hit = w.Grounded ? w.ContactPoint : end;
            if (_dbgRays)
            {
                DL.Line(m.ToGame(origin), m.ToGame(hit), w.Grounded ? LGreen : LRed);
                if (w.Grounded) DL.Ray(m.ToGame(w.ContactPoint), U.Mirror(w.ContactNormal), 0.5f, LWhite);
            }
            if (_dbgForces && w.Grounded)
            {
                var n = w.ContactNormal;
                var fwd = U.ProjectOnPlane(U.Rot(U.AngleAxis(w.SteerAngle, up), tr.forward), n);
                fwd = U.Normalized(fwd);
                var right = Vector3.Cross(n, fwd);
                float k = 1f / MathF.Max(1000f, w.StaticLoad);   // 1 m = statische Radlast
                var cp = m.ToGame(w.ContactPoint + n * 0.05f);
                DL.Ray(cp, U.Mirror(n), w.Fz * k, LYellow);
                if (MathF.Abs(w.Fx) > 1f) DL.Ray(cp, U.Mirror(fwd * MathF.Sign(w.Fx)), MathF.Abs(w.Fx) * k, LOrange);
                if (MathF.Abs(w.Fy) > 1f) DL.Ray(cp, U.Mirror(right * MathF.Sign(w.Fy)), MathF.Abs(w.Fy) * k, LCyan);
            }
            if (_dbgWheelLabels)
                DL.Label(m.ToGame(anchor + up * 0.6f), w.Name + "\n" + Num(w.Fz) + " N\n" +
                    w.SlipRatio.ToString("+0.00;-0.00", Inv) + " / " + (w.SlipAngle * (180f / MathF.PI)).ToString("+0.0;-0.0", Inv) + "°",
                    w.Grounded ? LGreen : LRed);
        }
        if (_dbgVelocity)
        {
            var com = m.ToGame(v.Rigidbody.worldCenterOfMass);
            var vel = U.Mirror(v.Rigidbody.linearVelocity);
            float sp = vel.Length();
            if (sp > 0.2f) DL.Arrow(com, vel, MathF.Min(sp * 0.25f, 15f), LMagenta, 0.8f);
            DL.Arrow(com + Vector3.UnitY * 0.8f, U.Mirror(tr.forward), 3f, LWhite, 0.6f);
            DL.Circle(com, 0.3f, LMagenta, 12);
        }
    }

    // ------------------------------------------------------------------ Verkehr (Unity TrafficMenu / TrafficDebugMenu)
    Gameplay.Traffic? Traffic => _vehicles.Traffic;

    List<Item> TrafficMenu()
    {
        var tr = Traffic;
        var l = new List<Item> { Head("STATUS") };
        if (tr == null) { l.Add(Info("no traffic (Gameplay/Traffic) loaded")); return l; }
        l.Add(Info("Traffic cars", () => tr.CarCount + " / " + TrafficConfig.MaxCars));
        l.Add(Info("State", () => tr.Status.Replace('\n', ' ')));
        l.Add(Head("TRAFFIC"));
        l.Add(Bool("Traffic", () => TrafficConfig.Enabled, v => TrafficConfig.Enabled = v, true));
        l.Add(Int("Max cars", () => TrafficConfig.MaxCars, v => TrafficConfig.MaxCars = v, 0, 40, 12));
        l.Add(Bool("Density by time of day", () => TrafficConfig.DensityByTime, v => TrafficConfig.DensityByTime = v, false));
        l.Add(Float("Speed factor", () => TrafficConfig.SpeedFactor, v => TrafficConfig.SpeedFactor = v, 0.02f, 0.5f, 1.3f, 0.92f, "0.00"));
        l.Add(Float("Spawn distance min", () => TrafficConfig.SpawnMin, v => TrafficConfig.SpawnMin = MathF.Min(v, TrafficConfig.SpawnMax - 20f), 10f, 40f, 1000f, 180f, "0", " m"));
        l.Add(Float("Spawn distance max", () => TrafficConfig.SpawnMax, v => TrafficConfig.SpawnMax = MathF.Max(v, TrafficConfig.SpawnMin + 20f), 10f, 80f, 1500f, 400f, "0", " m"));
        l.Add(Float("Despawn beyond", () => TrafficConfig.DespawnDistance, v => TrafficConfig.DespawnDistance = MathF.Max(v, TrafficConfig.SpawnMax + 20f), 10f, 100f, 2000f, 520f, "0", " m"));
        l.Add(Action_("Remove all traffic cars", () => { tr.ClearCars(); Toast("Traffic cleared"); }));
        l.Add(Head("DRIVING (IDM)"));
        l.Add(Float("Max acceleration", () => TrafficConfig.AccelMax, v => TrafficConfig.AccelMax = v, 0.1f, 0.3f, 6f, 1.6f, "0.0", " m/s²"));
        l.Add(Float("Comfortable braking", () => TrafficConfig.ComfortBrake, v => TrafficConfig.ComfortBrake = v, 0.1f, 0.5f, 8f, 2.4f, "0.0", " m/s²"));
        l.Add(Float("Emergency braking", () => TrafficConfig.EmergencyBrake, v => TrafficConfig.EmergencyBrake = v, 0.5f, 2f, 15f, 8f, "0.0", " m/s²"));
        l.Add(Float("Minimum gap", () => TrafficConfig.MinGap, v => TrafficConfig.MinGap = v, 0.5f, 1f, 15f, 3.5f, "0.0", " m"));
        l.Add(Float("Time gap", () => TrafficConfig.TimeGap, v => TrafficConfig.TimeGap = v, 0.1f, 0.5f, 4f, 1.4f, "0.0", " s"));
        l.Add(Float("Jerk (normal)", () => TrafficConfig.Jerk, v => TrafficConfig.Jerk = v, 0.5f, 1f, 40f, 5f, "0.0", " m/s³"));
        l.Add(Float("Jerk (emergency)", () => TrafficConfig.EmergencyJerk, v => TrafficConfig.EmergencyJerk = v, 1f, 5f, 100f, 30f, "0", " m/s³"));
        l.Add(Float("Yield only if stopping below", () => TrafficConfig.YieldBrake, v => TrafficConfig.YieldBrake = v, 0.1f, 1f, 10f, 3.5f, "0.0", " m/s²"));
        l.Add(Float("Player prediction", () => TrafficConfig.PlayerPredict, v => TrafficConfig.PlayerPredict = v, 0.1f, 0f, 4f, 1.5f, "0.0", " s"));
        l.Add(Head("PHYSICS & SOUND"));
        l.Add(Int("Cars with physics (max)", () => TrafficConfig.PhysicsMax, v => TrafficConfig.PhysicsMax = v, 0, 30, 6));
        l.Add(Float("Physics radius", () => TrafficConfig.PhysicsRadius, v => TrafficConfig.PhysicsRadius = v, 10f, 20f, 500f, 100f, "0", " m"));
        l.Add(Float("Crash above", () => TrafficConfig.CrashSpeed, v => TrafficConfig.CrashSpeed = v, 0.5f, 0.5f, 20f, 3.5f, "0.0", " m/s"));
        l.Add(Bool("Traffic sounds", () => TrafficConfig.Sounds, v => TrafficConfig.Sounds = v, true));
        l.Add(Int("Max sounds", () => TrafficConfig.SoundMax, v => TrafficConfig.SoundMax = v, 0, 16, 3));
        l.Add(Float("Sound radius", () => TrafficConfig.SoundRadius, v => TrafficConfig.SoundRadius = v, 5f, 10f, 300f, 70f, "0", " m"));
        l.Add(Head("DEBUG"));
        AddTrafficDebugToggles(l);
        AddAiPathToggles(l);
        l.Add(Menu("AI path system & colors", AiPathMenu));
        l.Add(Menu("Traffic Debug Details", TrafficDebugMenu));
        l.Add(Menu("Car list", TrafficCarListMenu));
        return l;
    }

    /// <summary>Live-Liste der KI-Autos – auch aus Network > Traffic &amp; Remote Cars erreichbar.</summary>
    List<Item> TrafficCarListMenu()
    {
        var tr = Traffic;
        var cl = new List<Item> { Head("TRAFFIC CARS  (id · model · state · speed · distance · lane)") };
        if (tr == null) { cl.Add(Info("no traffic")); return cl; }
        foreach (var c in tr.Info())
        {
            var cc = c;
            cl.Add(Info($"{cc.id & 0xFFFF:X4} {cc.model}", () => $"{cc.mode}  {cc.kmh:0} km/h  {cc.dist:0} m  {cc.lane}"));
        }
        if (cl.Count == 1) cl.Add(Info("no cars"));
        return cl;
    }

    void AddTrafficDebugToggles(List<Item> l)
    {
        l.Add(Bool("Traffic debug (3D + overlay)", () => TrafficConfig.ShowDebug, v => TrafficConfig.ShowDebug = v, false));
        l.Add(Bool("Debug: planned routes", () => TrafficConfig.DebugRoutes, v => TrafficConfig.DebugRoutes = v, true));
        l.Add(Bool("Debug: forward arrows", () => TrafficConfig.DebugArrows, v => TrafficConfig.DebugArrows = v, true));
        l.Add(Bool("Debug: speed/heading/target labels", () => TrafficConfig.DebugLabels, v => TrafficConfig.DebugLabels = v, true));
        l.Add(Float("Debug range", () => TrafficConfig.DebugRange, v => TrafficConfig.DebugRange = v, 10f, 20f, 600f, 140f, "0", " m"));
    }

    List<Item> TrafficDebugMenu()
    {
        var tr = Traffic;
        var l = new List<Item> { Head("TRAFFIC DEBUG DETAILS") };
        if (tr == null) { l.Add(Info("no traffic")); return l; }
        AddTrafficDebugToggles(l);
        AddAiPathToggles(l);
        l.Add(Menu("AI path system & colors", AiPathMenu));
        l.Add(Info("Cars / physics / crash / stopped", () => { var (p, c, s) = tr.Counts(); return tr.CarCount + " / " + p + " / " + c + " / " + s; }));
        l.Add(Info("Physics / Sound", () => "p:" + TrafficConfig.PhysicsMax + "@" + Num(TrafficConfig.PhysicsRadius) + "m  s:" + TrafficConfig.SoundMax + "@" + Num(TrafficConfig.SoundRadius) + "m"));
        l.Add(Info("Road network", () => tr.Lanes != null ? "loaded" : "-"));
        l.Add(Head("NEARBY CARS (live)"));
        int shown = 0;
        foreach (var c in tr.Info())
        {
            if (shown++ > 25) break;
            var cc = c;
            string distStr = cc.dist < 1000 ? cc.dist.ToString("0", Inv) + "m" : (cc.dist / 1000f).ToString("0.0", Inv) + "km";
            l.Add(Info($"{cc.model} [{cc.mode}]", () => $"{cc.kmh:0} km/h  {distStr}  Lane:{cc.lane}"));
        }
        if (shown == 0) l.Add(Info("No traffic cars"));
        return l;
    }

    // ------------------------------------------------------------------ Bloom / AO (Zusatz, Game/Config/PostFxConfig.cs)
    void AddPostFxItems(List<Item> l)
    {
        var d = new PostFxConfig();
        l.Add(Head("BLOOM (engine extra, not in the original - Game/Config/PostFxConfig.cs)"));
        l.Add(Bool("Bloom", () => PostFx.BloomEnabled, v => PostFx.BloomEnabled = v, d.Bloom));
        l.Add(Float("Bloom intensity", () => PostFx.BloomIntensity, v => PostFx.BloomIntensity = v, 0.05f, 0f, 2f, d.BloomIntensity, "0.00"));
        l.Add(Float("Bloom threshold", () => PostFx.BloomThreshold, v => PostFx.BloomThreshold = v, 0.02f, 0f, 1f, d.BloomThreshold, "0.00"));
        l.Add(Float("Bloom knee", () => PostFx.BloomKnee, v => PostFx.BloomKnee = v, 0.01f, 0f, 0.5f, d.BloomKnee, "0.00"));
        l.Add(Float("Bloom emissive (lights, neon, holograms)", () => PostFx.BloomEmissive, v => PostFx.BloomEmissive = v, 0.1f, 0f, 4f, d.BloomEmissive, "0.0"));
        l.Add(Float("Bloom radius", () => PostFx.BloomRadius, v => PostFx.BloomRadius = v, 0.25f, 0.25f, 8f, d.BloomRadius, "0.00", " x"));
        l.Add(Head("AMBIENT OCCLUSION (engine extra, not in the original)"));
        l.Add(Bool("Ambient occlusion", () => PostFx.AoEnabled, v => PostFx.AoEnabled = v, d.AmbientOcclusion));
        l.Add(Float("AO intensity", () => PostFx.AoIntensity, v => PostFx.AoIntensity = v, 0.05f, 0f, 1f, d.AoIntensity, "0.00"));
        l.Add(Float("AO radius", () => PostFx.AoRadius, v => PostFx.AoRadius = v, 0.1f, 0.1f, 5f, d.AoRadius, "0.0", " m"));
        l.Add(Float("AO bias", () => PostFx.AoBias, v => PostFx.AoBias = v, 0.01f, 0f, 0.5f, d.AoBias, "0.00"));
        l.Add(Info("AO active", () => PostFx.AoEnabled && !_terrain.MapView ? "yes (not on the big map)" : "no"));
        l.Add(Float("AO fade distance", () => PostFx.AoMaxDistance, v => PostFx.AoMaxDistance = v, 10f, 5f, 2000f, d.AoMaxDistance, "0", " m"));
        l.Add(Bool("Photoshop (original colour LUT)", () => PhotoshopFx.Enabled, v => PhotoshopFx.Enabled = v, true));
        l.Add(Table("Debug view (show buffer only)", new[] { "Off", "AO buffer", "Bloom buffer", "Bloom mask (glowing)" }, () => PostFx.DebugView, v => PostFx.DebugView = v, 0));
        l.Add(Info("Bloom threshold 1.0 = nothing glows (lower = more glow)"));
        l.Add(Info("AO bias high (0.3-0.5) = almost no AO (lower = more AO)"));
    }

    // ------------------------------------------------------------------ Beschriftungen in der Welt + Verkehrstafel
    /// <summary>Unity GUI.Label der Debug-Ansichten: Text am Bildschirmpunkt des Weltpunkts, Tafel des Verkehrs unten links.</summary>
    void RenderWorldDebug(FontAtlas f, float cw, float ch, float lh, float k)
    {
        var s = Streamer;
        if (s != null && DL.Labels.Count > 0 && !_terrain.MapView)
        {
            var root = s.RootMatrix;
            foreach (var t in DL.Labels)
            {
                if (!_terrain.WorldToScreen(Vector3.Transform(t.P, root), out var px)) continue;
                var lines = t.S.Split('\n');
                int maxLen = 0;
                foreach (var ln in lines) maxLen = Math.Max(maxLen, ln.Length);
                float w = maxLen * cw + 8f, h = lines.Length * lh + 2f;
                float x0 = px.X - w * 0.5f, y0 = px.Y - h;
                if (x0 > _r.Width || y0 > _r.Height || x0 + w < 0f || y0 + h < 0f) continue;
                Quad(x0, y0, x0 + w, y0 + h, 0f, 0f, 0f, 0f, CPanel);
                uint col = ObjectVertex.PackColor((byte)(t.C.X * 255f), (byte)(t.C.Y * 255f), (byte)(t.C.Z * 255f), 255);
                for (int i = 0; i < lines.Length; i++)
                {
                    float lx = px.X - lines[i].Length * cw * 0.5f;
                    Text(f, lx, y0 + 1f + i * lh, lines[i], col, lx + lines[i].Length * cw + 1f, cw, ch);
                }
            }
        }
        var tr = Traffic;
        if (TrafficConfig.ShowDebug && tr != null)
        {
            var lines = new List<string>(tr.DebugPanel());
            int maxLen = 0;
            foreach (var ln in lines) maxLen = Math.Max(maxLen, ln.Length);
            float pw = MathF.Min(_r.Width - 16f * k, maxLen * cw + 16f), ph = lines.Count * lh + 10f;
            float x0 = 8f * k, y0 = _r.Height - ph - 8f * k;
            Quad(x0, y0, x0 + pw, y0 + ph, 0f, 0f, 0f, 0f, CPanel);
            for (int i = 0; i < lines.Count; i++) Text(f, x0 + 6f, y0 + 5f + i * lh, lines[i], CWhite, x0 + pw - 6f, cw, ch);
        }
    }
}
