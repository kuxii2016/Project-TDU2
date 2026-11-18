// Portiert aus dem Unity-Projekt (Runtime/Racing/TDU2RaceManager.cs).
using Paradise.Core.Diagnostics;
using Paradise.Core.Math;
using Paradise.Core.Racing;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Config;
using System.Numerics;

namespace Paradise.Game.Racing;

public enum RaceState { Idle, Preparing, Countdown, Running, Finished }

/// <summary>Ergebnis einer Prüfung.</summary>
public sealed class RaceResult
{
    public RaceDefinition? Race;
    public bool Passed;
    public bool TimedOut;
    public bool Aborted;
    public int RawTimeMs;        // gefahrene Zeit
    public int PenaltyMs;        // Zeitstrafen
    public int FinalTimeMs => RawTimeMs + PenaltyMs;
    public int TargetMs;         // Referenzzeit (timG bzw. Zeitlimit)
    public readonly List<int> Splits = new();
}

/// <summary>
/// Führt ein Rennen/eine Fahrprüfung aus – Logik nach GMCompDrivingSchool/Zeitfahren der EXE:
///  - Vorbereitung: wartet, bis das Prüfungsauto geladen ist und die Strecke gestreamt wurde, baut Tore/Props.
///  - Countdown (3-2-1-GO, Auto gehalten), danach läuft die Zeit.
///  - Checkpoints in Reihenfolge (<see cref="RaceDefinition.TargetSequence"/>), Rundkurse über die Startlinie,
///    Bremszone (CHK2-Art 3): im Feld anhalten.
///  - Zeitlimit (ChCD/CtDw): Ablauf = durchgefallen ("/:IsTimeOut"). Ohne Limit: bestanden, wenn Zeit + Strafen &lt;= timG.
///  - Zeitstrafen: DBEnvironnement.PenaltyTimeLevel01..03 (10 s) je Treffer, wenn die Prüfung PeLv &gt; 0 hat (vermutet).
/// Alle Berechnungen in Inselkoordinaten (Spielraum), damit der verschobene Ursprung nichts verschiebt.
/// Engine: kein MonoBehaviour – <see cref="Update"/> ruft der Spielmodus jedes Bild (pausiert mit dem Pausemenü).
/// </summary>
public sealed class RaceManager
{
    public MapStreamer? Streamer;
    public VehicleManager? Vehicles;

    public RaceDefinition? Race { get; private set; }
    public RaceState State { get; private set; }
    public bool Running => State == RaceState.Running;
    public bool Finished => State == RaceState.Finished;
    public float ElapsedSeconds { get; private set; }
    public float CountdownLeft { get; private set; }
    public int PassedCount { get; private set; }          // erledigte Ziele (ohne Startlinie)
    public int PenaltyMs { get; private set; }
    public int Lap => Race == null ? 1 : Mathf.Min(Race.Laps, 1 + (Race.TargetsPerLap > 0 ? PassedCount / Race.TargetsPerLap : 0));
    public bool InBrakingZone { get; private set; }
    public RaceResult? Result { get; private set; }
    public RaceProps? Props { get; private set; }
    /// <summary>Fahrzeuge der Prüfung (ig2 VEHI) bzw. Meisterschafts-Gegner.</summary>
    public RaceOpponents? Opponents => _opponents;
    /// <summary>Tore der laufenden Prüfung (Original-Modelle aus pack_cp.bnk).</summary>
    public IReadOnlyList<RaceGate> Gates => _gates;

    public event Action<RaceManager>? Prepared;          // Strecke steht, Countdown beginnt
    public event Action<RaceManager>? Started;           // GO
    public event Action<RaceManager, int>? CheckpointPassed;   // Index in der Zielfolge
    public event Action<RaceManager>? LapCompleted;
    public event Action<RaceManager, int>? Penalty;      // ms
    public event Action<RaceManager>? Completed;         // Ziel erreicht (Result gesetzt)
    public event Action<RaceManager>? Failed;            // Zeit abgelaufen (Result gesetzt)
    public event Action<RaceManager>? Aborted;

    /// <summary>Aktuell anzuvisierendes Tor (null, wenn nicht aktiv).</summary>
    public RaceCheckpoint? Next => Race != null && _seq != null && _target < _seq.Count ? Race.Checkpoints[_seq[_target]] : null;
    public int TargetCount => _seq != null ? _seq.Count : 0;
    /// <summary>Index des nächsten Tors in <see cref="RaceDefinition.Checkpoints"/> (für die Minimap).</summary>
    public int NextCheckpointIndex => Next != null ? Next.Index : -1;

    /// <summary>Bestanden (für den laufenden/fertigen Lauf) – Zielzeit eingehalten.</summary>
    public bool WithinTarget => Race != null && (Race.TargetTimeMs <= 0 || (int)(ElapsedSeconds * 1000f) + PenaltyMs <= Race.TargetTimeMs);

    /// <summary>Restzeit des Zeitlimits (s), nur bei <see cref="RaceDefinition.HasTimeLimit"/>.</summary>
    public float TimeLeft => Race != null && Race.HasTimeLimit ? Mathf.Max(0f, Race.TimeLimitMs / 1000f - ElapsedSeconds - PenaltyMs / 1000f) : 0f;

    List<int>? _seq;
    int _target;
    float _prevSigned;
    float _prepareT, _stopT;
    bool _haveGround;
    readonly List<RaceGate> _gates = new();
    float _clock, _lastPenaltyT = -10f;
    Vehicle? _locked;   // Auto mit Startsperre (Countdown)
    VehicleManager? _hooked;
    readonly List<int> _splits = new();
    RaceOpponents? _opponents;

    // ------------------------------------------------------------------ Steuerung

    /// <summary>Prüfung vorbereiten. Das Prüfungsauto muss bereits gespawnt sein bzw. gerade geladen werden.</summary>
    public bool StartRace(RaceDefinition? race)
    {
        Abort(false);
        if (race == null || race.Checkpoints.Count < 2) return false;
        if (Streamer == null) { Log.Warn("RaceManager: kein Streamer gesetzt"); return false; }
        Race = race;
        _seq = race.TargetSequence();
        _target = 0;
        PassedCount = 0; PenaltyMs = 0; ElapsedSeconds = 0f; CountdownLeft = RaceConfig.CountdownSeconds;
        Result = null; InBrakingZone = false; _stopT = 0f; _prepareT = 0f; _haveGround = false;
        State = RaceState.Preparing;
        Hook();
        return true;
    }

    public void Abort() => Abort(true);
    /// <summary>Ohne Ereignis beenden (neu starten, Modus verlassen).</summary>
    public void Stop() => Abort(false);

    void Abort(bool notify)
    {
        bool was = State == RaceState.Running || State == RaceState.Countdown || State == RaceState.Preparing;
        if (notify && was)
            Result = new RaceResult { Race = Race, Aborted = true, RawTimeMs = (int)(ElapsedSeconds * 1000f), PenaltyMs = PenaltyMs };
        State = RaceState.Idle;
        Release();
        Cleanup();
        if (notify && was) Aborted?.Invoke(this);
    }

    /// <summary>Tore, Props und Gegner entfernen (nach dem Ergebnis).</summary>
    public void Cleanup()
    {
        _gates.Clear();
        if (Props != null) { Props.Hit -= OnPropHit; Props.Dispose(); Props = null; }
        _opponents?.Clear();
    }

    /// <summary>Startsperre (Unity LateUpdate) und Kollisionsmeldungen am Fahrzeug-Manager anmelden.</summary>
    void Hook()
    {
        if (Vehicles == null || ReferenceEquals(_hooked, Vehicles)) return;
        Unhook();
        _hooked = Vehicles;
        _hooked.AfterInput += HoldCar;
        _hooked.BodyImpact += OnCollisionReport;
    }

    void Unhook()
    {
        if (_hooked == null) return;
        _hooked.AfterInput -= HoldCar;
        _hooked.BodyImpact -= OnCollisionReport;
        _hooked = null;
    }

    void Release()
    {
        if (_locked != null) { _locked.StartLock = false; _locked = null; }
    }

    /// <summary>Jedes Bild (Unity Update).</summary>
    public void Update(float dt)
    {
        _clock += dt;
        switch (State)
        {
            case RaceState.Preparing: TickPrepare(dt); break;
            case RaceState.Countdown:
                CountdownLeft -= dt;
                HoldCar(Vehicles?.Current);
                if (CountdownLeft <= 0f) Go();
                break;
            case RaceState.Running: Tick(dt); break;
        }
        _opponents?.Update(dt);
        if (State == RaceState.Preparing) HoldCar(Vehicles?.Current);
    }

    /// <summary>Während Vorbereitung/Countdown das Auto festhalten (nach dem Eingabe-Update des Fahrzeug-Managers).</summary>
    void HoldCar(Vehicle? v)
    {
        bool hold = State == RaceState.Preparing || State == RaceState.Countdown;
        if (v != null && v != _locked) { if (_locked != null) _locked.StartLock = false; _locked = null; }
        if (v != null) { v.StartLock = hold; _locked = hold ? v : null; }
        if (!hold || v == null) return;
        if (State == RaceState.Preparing) v.AccelPedal = 0f;   // im Countdown darf Gas gegeben werden (Drehzahl), die Bremse hält
        v.BrakePedal = 1f;
        v.HandBrake = 1f;
    }

    void TickPrepare(float dt)
    {
        _prepareT += dt;
        var v = Vehicles?.Current;
        bool carReady = v != null && Vehicles != null && !Vehicles.IsLoadingCar;
        if (!carReady && _prepareT < RaceConfig.MaxPrepareSeconds + 15f) return;
        // Boden unter dem Start geladen?
        if (!_haveGround && Streamer != null && Race != null)
        {
            _haveGround = Streamer.RaycastDown(Race.StartPosition, 50f, 200f, out _);
            if (!_haveGround && _prepareT < RaceConfig.MaxPrepareSeconds) return;
        }
        // Props-Modelle (worldactors.bnk, pack_cp.bnk) fertig geladen?
        string? bank = RaceProps.WorldActorsBank(Vehicles?.BnkDir, Streamer?.Index?.IslandName);
        if ((RaceConfig.SpawnProps || RaceConfig.ShowGates) && Streamer != null && !RaceProps.Preload(bank, Streamer) && _prepareT < RaceConfig.MaxPrepareSeconds) return;

        BuildTrack();
        State = RaceState.Countdown;
        CountdownLeft = RaceConfig.CountdownSeconds;
        Prepared?.Invoke(this);
    }

    void Go()
    {
        State = RaceState.Running;
        if (_locked != null) { _locked.StartLock = false; _locked.BrakePedal = 0f; _locked.HandBrake = 0f; _locked = null; }
        ElapsedSeconds = 0f;
        var pos = VehicleIsland();
        var n = Next;
        _prevSigned = pos.HasValue && n != null ? Signed(n, pos.Value) : 0f;
        _opponents?.OnRaceStarted();
        Started?.Invoke(this);
    }

    /// <summary>Ein Simulationsschritt (von Update aufgerufen; öffentlich für Tests).</summary>
    public void Tick(float deltaSeconds)
    {
        if (State != RaceState.Running || Race == null || _seq == null) return;
        ElapsedSeconds += deltaSeconds;
        int elapsedMs = (int)(ElapsedSeconds * 1000f);

        // Zeitlimit
        if (Race.HasTimeLimit && Race.TimeLimitMs > 0 && elapsedMs + PenaltyMs >= Race.TimeLimitMs) { Fail(true); return; }
        if (!Race.HasTimeLimit && Race.TimeLimitMs > 0 && elapsedMs >= Race.TimeLimitMs) { Fail(true); return; }

        var p = VehicleIsland();
        var cp = Next;
        if (p == null || cp == null) return;
        var pos = p.Value;

        if (cp.IsBrakingZone && _target == _seq.Count - 1)
        {
            // Bremszone: im Feld zum Stehen kommen
            Vector3 d = pos - cp.Position; d.Y = 0f;
            float lat = Vector3.Dot(d, cp.Right), lon = Vector3.Dot(d, cp.Forward);
            InBrakingZone = MathF.Abs(lat) <= cp.Width * 0.5f + 0.5f && MathF.Abs(lon) <= cp.ZoneDepth * 0.5f + 0.5f;
            float kmh = Vehicles?.Current != null ? MathF.Abs(Vehicles.Current.SpeedKmh) : 99f;
            if (InBrakingZone && kmh < 1.5f) { _stopT += deltaSeconds; if (_stopT > 0.3f) Advance(pos); }
            else _stopT = 0f;
            return;
        }

        float signed = Signed(cp, pos);
        Vector3 dd = pos - cp.Position; dd.Y = 0f;
        float lateral = MathF.Abs(Vector3.Dot(dd, cp.Right));
        float half = cp.HalfAcceptWidth(Race.School == 'B');
        // Durchfahrt in beliebiger Richtung (Kegel-Tore sind in den Daten teils umgekehrt ausgerichtet)
        bool crossed = (_prevSigned < 0f) != (signed < 0f) && MathF.Abs(signed - _prevSigned) < 30f && lateral <= half;
        bool close = MathF.Abs(signed) < 1.0f && lateral <= half;
        if (crossed || close) Advance(pos);
        else _prevSigned = signed;
    }

    void Advance(Vector3 pos)
    {
        if (Race == null || _seq == null) return;
        int ms = (int)(ElapsedSeconds * 1000f);
        PassedCount++;
        int idx = _target;
        _target++;
        InBrakingZone = false; _stopT = 0f;
        HighlightNext();
        _opponents?.OnCheckpoint(_seq[idx]);
        CheckpointPassed?.Invoke(this, idx);
        if (Race.IsCircuit && Race.TargetsPerLap > 0 && PassedCount % Race.TargetsPerLap == 0) LapCompleted?.Invoke(this);
        _splits.Add(ms);
        if (_target >= _seq.Count) { Finish(); return; }
        _prevSigned = Signed(Next!, pos);
    }

    void Finish()
    {
        State = RaceState.Finished;
        int ms = (int)(ElapsedSeconds * 1000f);
        var race = Race!;
        Result = new RaceResult { Race = race, RawTimeMs = ms, PenaltyMs = PenaltyMs, TargetMs = race.HasTimeLimit ? race.TimeLimitMs : race.TargetTimeMs };
        Result.Splits.AddRange(_splits);
        Result.Passed = race.HasTimeLimit ? Result.FinalTimeMs < race.TimeLimitMs : (race.TargetTimeMs <= 0 || Result.FinalTimeMs <= race.TargetTimeMs);
        _splits.Clear();
        Completed?.Invoke(this);
    }

    void Fail(bool timeout)
    {
        State = RaceState.Finished;
        var race = Race!;
        Result = new RaceResult { Race = race, TimedOut = timeout, Passed = false, RawTimeMs = (int)(ElapsedSeconds * 1000f), PenaltyMs = PenaltyMs, TargetMs = race.HasTimeLimit ? race.TimeLimitMs : race.TargetTimeMs };
        _splits.Clear();
        Failed?.Invoke(this);
    }

    // ------------------------------------------------------------------ Strafen

    void OnPropHit(string prop, float speed) => AddPenalty(speed, prop);

    void OnCollisionReport(float impact) => AddPenalty(impact, "Wand/Objekt");

    void AddPenalty(float impact, string what = "")
    {
        if (State != RaceState.Running || Race == null) return;
        string? why = null;
        if (!RaceConfig.PenaltiesAlways && Race.PenaltyLevel <= 0) why = "Prüfung ohne Strafstufe (PeLv 0)";
        else if (impact < RaceConfig.PenaltyMinImpact) why = "zu leicht (< " + RaceConfig.PenaltyMinImpact.ToString("0.0") + " m/s)";
        else if (_clock - _lastPenaltyT < 1.5f) why = "gleicher Treffer (< 1,5 s)";
        Log.Info("Treffer " + what + " mit " + impact.ToString("0.0") + " m/s – " + (why ?? "Strafe +" + RaceConfig.PenaltySeconds.ToString("0") + " s"));
        if (why != null) return;
        _lastPenaltyT = _clock;
        int ms = Mathf.RoundToInt(RaceConfig.PenaltySeconds * 1000f);
        PenaltyMs += ms;
        Penalty?.Invoke(this, ms);
    }

    // ------------------------------------------------------------------ Hilfen

    static float Signed(RaceCheckpoint cp, Vector3 islandPos)
    {
        Vector3 d = islandPos - cp.Position; d.Y = 0f;
        return Vector3.Dot(d, cp.Forward);
    }

    Vector3? VehicleIsland() => Vehicles?.Current != null ? Vehicles.PositionGame : null;

    // ------------------------------------------------------------------ Tore, Props

    void BuildTrack()
    {
        Cleanup();
        if (Race == null || Streamer == null) return;
        if (RaceConfig.SpawnProps || RaceConfig.ShowGates)
        {
            Props = new RaceProps();
            Props.Hit += OnPropHit;
            Props.Build(Streamer, Race, RaceConfig.ShowGates ? _gates : null, RaceConfig.SpawnProps);
            Log.Info("Prüfung " + Race.Name + ": " + Props.Count + " Props (" + Props.Source + "), " + _gates.Count + " Tore, "
                     + (Race.IsCircuit ? Race.Laps + " Runden" : "Punkt-zu-Punkt") + (Race.EndsInBrakingZone ? ", Bremszone im Ziel" : ""));
        }
        HighlightNext();
        // Gegner (ig2 VEHI / Meisterschaft)
        if (Race.Opponents.Count > 0 && Vehicles != null)
        {
            _opponents ??= new RaceOpponents();
            _opponents.Setup(this, Streamer, Vehicles, Race);
        }
    }

    void HighlightNext()
    {
        int next = NextCheckpointIndex;
        foreach (var g in _gates)
        {
            if (g.Index == next) g.SetState(RaceGateState.Next);
            else if (IsPassed(g.Index)) g.SetState(RaceGateState.Passed);
            else g.SetState(RaceGateState.Pending);
        }
    }

    bool IsPassed(int cpIndex)
    {
        if (_seq == null || Race == null) return false;
        // im aktuellen Durchgang schon passiert?
        int lapStart = Race.TargetsPerLap > 0 ? (_target / Race.TargetsPerLap) * Race.TargetsPerLap : 0;
        for (int i = lapStart; i < _target && i < _seq.Count; i++) if (_seq[i] == cpIndex) return true;
        return cpIndex == 0 && !Race.IsCircuit;
    }

    /// <summary>Unity OnDisable: alles lösen (Spielmodus verlassen).</summary>
    public void Shutdown()
    {
        if (State != RaceState.Idle) Abort(false);
        Release();
        Unhook();
    }
}
