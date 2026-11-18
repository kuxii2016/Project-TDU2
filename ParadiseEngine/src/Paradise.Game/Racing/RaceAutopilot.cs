// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceAutopilot.cs).
using Paradise.Core.Diagnostics;
using Paradise.Core.Math;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using Paradise.Game.Gameplay;
using System.Numerics;

namespace Paradise.Game.Racing;

/// <summary>
/// Autopilot nach dem Ziel (wie im Spiel: das Auto bremst ab und folgt der Straße, während Kino/Ergebnis laufen).
/// Folgt den Spuren des Verkehrsnetzes (<see cref="TrafficLanes"/>) mit <see cref="RaceConfig.AutopilotCruiseKmh"/>; ohne Straße in
/// der Nähe bremst er bis zum Stillstand. Fahreingaben des Spielers sind solange gesperrt (VehicleManager.ExternalControl).
/// </summary>
public sealed class RaceAutopilot
{
    VehicleManager? _mgr;
    Vehicle? _veh;
    TrafficLanes? _lanes;
    readonly List<TrafficTrack> _chain = new();
    float _s;              // Position auf _chain[0]
    bool _onRoad;
    float _steer;
    float _t;
    readonly List<(int road, bool forward)> _succ = new();

    public bool Active { get; private set; }

    public void Begin(VehicleManager? manager)
    {
        _mgr = manager;
        _veh = manager?.Current;
        if (manager == null || _veh == null) return;
        Active = true;
        _t = 0f;
        manager.ExternalControl = true;
        _lanes = RaceRoadPath.FindLanes();
        _chain.Clear(); _onRoad = false;
        if (_lanes != null)
        {
            var p = manager.PositionGame;
            var d = manager.ForwardGame; d.Y = 0f;
            if (d.LengthSquared() > 1e-6f && _lanes.Nearest(p, Vector3.Normalize(d), RaceConfig.AutopilotSnapDistance, out var lane, out var s) && lane != null)
            {
                _chain.Add(lane); _s = s; _onRoad = true;
                Extend(400f);
            }
        }
        Log.Info("Autopilot nach dem Ziel: " + (_onRoad ? "folgt der Straße (Straße " + ((LaneTrack)_chain[0]).RoadId + ")" : "keine Straße in der Nähe – bremst bis zum Stillstand"));
    }

    public void End()
    {
        if (!Active) return;
        Active = false;
        if (_mgr != null) _mgr.ExternalControl = false;
        if (_veh != null) { _veh.AccelPedal = 0f; _veh.BrakePedal = 0f; _veh.Steer = 0f; _veh.HandBrake = 0f; }
        _veh = null;
        _chain.Clear();
    }

    void Extend(float meters)
    {
        if (_lanes == null || _chain.Count == 0) return;
        float have = -_s;
        foreach (var t in _chain) have += t.Length;
        int guard = 0;
        while (have < meters && guard++ < 40)
        {
            if (_chain[^1] is not LaneTrack last)
            {
                if (_chain[^1] is not JunctionTrack j || j.To == null) return;
                _chain.Add(j.To); have += j.To.Length; continue;
            }
            _lanes.Successors(last, _succ);
            LaneTrack? best = null; float bestAng = float.MaxValue;
            foreach (var (road, fwd) in _succ)
            {
                var nl = _lanes.NextLane(last, road, fwd);
                if (nl == null) continue;
                float ang = Angle2D(new Vector2(last.EndDir.X, last.EndDir.Z), new Vector2(nl.StartDir.X, nl.StartDir.Z));
                if (ang < bestAng) { bestAng = ang; best = nl; }
            }
            if (best == null) return;
            var jt = _lanes.Junction(last, best);
            if (jt != null && jt.P != null && jt.P.Length > 1) { _chain.Add(jt); have += jt.Length; }
            _chain.Add(best); have += best.Length;
        }
    }

    static float Angle2D(Vector2 a, Vector2 b)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-6f || lb < 1e-6f) return 0f;
        return MathF.Acos(System.Math.Clamp(Vector2.Dot(a, b) / (la * lb), -1f, 1f)) * Mathf.Rad2Deg;
    }

    Vector3 Ahead(float d)
    {
        float s = _s + d;
        for (int i = 0; i < _chain.Count; i++)
        {
            var t = _chain[i];
            if (s <= t.Length || i == _chain.Count - 1) { t.Sample(Mathf.Min(s, t.Length + 30f), out var p, out _); return p; }
            s -= t.Length;
        }
        return Vector3.Zero;
    }

    void Project(Vector3 island)
    {
        for (int guard = 0; guard < 4 && _chain.Count > 0; guard++)
        {
            var t = _chain[0];
            float best = float.MaxValue, bs = _s;
            for (int i = 0; i < t.P.Length; i++)
            {
                if (t.S[i] < _s - 20f || t.S[i] > _s + 40f) continue;
                var d = t.P[i] - island; d.Y = 0f;
                float dd = d.LengthSquared();
                if (dd < best) { best = dd; bs = t.S[i]; }
            }
            _s = Mathf.Max(_s, bs);
            if (_s < t.Length - 0.5f || _chain.Count < 2) break;
            _s -= t.Length;
            _chain.RemoveAt(0);
        }
    }

    /// <summary>Jedes Bild (Unity FixedUpdate): Pedale/Lenkung setzen.</summary>
    public void Update(float dt)
    {
        if (!Active) return;
        if (_veh == null || _mgr == null || _mgr.Current != _veh) { End(); return; }
        if (dt <= 0f) return;
        _t += dt;
        float kmh = MathF.Abs(_veh.SpeedKmh);
        float targetKmh = RaceConfig.AutopilotCruiseKmh;
        float steerCmd = 0f;
        if (_onRoad && _chain.Count > 0)
        {
            var island = _mgr.PositionGame;
            Project(island);
            Extend(300f);
            float look = Mathf.Clamp(kmh / 3.6f * 0.9f, 7f, 30f);
            // Ziel im Fahrzeugraum (Unity: _veh.transform.InverseTransformPoint) – Unity-Raum des Fahrzeugs (an X gespiegelt)
            var pose = _veh.transform;
            var to = _mgr.ToUnity(Ahead(look)) - pose.position;
            var right = Vector3.Transform(Vector3.UnitX, pose.rotation);
            var fwd = Vector3.Transform(Vector3.UnitZ, pose.rotation);
            float lx = Vector3.Dot(to, right), lz = Vector3.Dot(to, fwd);
            float ang = Mathf.Atan2(lx, Mathf.Max(0.5f, lz)) * Mathf.Rad2Deg;
            steerCmd = Mathf.Clamp(ang / 22f, -1f, 1f);
            var t0 = _chain[0];
            float lim = t0.MinLimit(_s, 40f) * 3.6f;
            if (lim > 1f) targetKmh = Mathf.Min(targetKmh, lim);
            t0.Sample(_s, out var lp, out _);
            var off = lp - island; off.Y = 0f;
            if (off.Length() > RaceConfig.AutopilotSnapDistance * 1.6f) { _onRoad = false; Log.Info("Autopilot: Spur verloren – bremst"); }
        }
        else targetKmh = 0f;

        _steer = Mathf.MoveTowards(_steer, steerCmd, 3f * dt);
        float accel = 0f, brake = 0f, hand = 0f;
        if (targetKmh <= 0.5f)
        {
            if (kmh > 3f) brake = Mathf.Clamp(kmh / 60f, 0.25f, 0.7f); else hand = 1f;
        }
        else if (kmh > targetKmh + 3f) brake = Mathf.Clamp((kmh - targetKmh) / 40f, 0.15f, 0.65f);
        else if (kmh < targetKmh - 2f) accel = Mathf.Clamp((targetKmh - kmh) / 25f, 0.15f, 0.45f);
        _veh.AccelPedal = accel;
        _veh.BrakePedal = brake;
        _veh.Steer = _steer;
        _veh.HandBrake = hand;
    }
}
