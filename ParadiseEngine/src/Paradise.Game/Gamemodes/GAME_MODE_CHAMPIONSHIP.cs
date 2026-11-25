// Portiert aus dem Unity-Projekt (Runtime/Racing/TDU2Championships.cs) – Ablauf eines Meisterschaftsrennens.
using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Core.Racing;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using Paradise.Game.Input;
using Paradise.Game.Online;
using Paradise.Game.Racing;
using Paradise.Game.Screens;
using System.Globalization;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Meisterschaftsrennen (Daten/Treffpunkte/Karte: <see cref="Championships"/>). Im Original Teil des Wettbewerbs-Managers,
/// hier eigene Klasse (wie GAME_MODE_TUNER). Ablauf wie Unity TDU2Championships:
///  - Rennen (gmth 3): Gegner aus der Lizenz (_bot_n = DBPNJ, Auto der Klasse aus DBPNJCar, Stärke _difficulty_bot_n) in
///    Zweierreihen vor dem Spieler (<see cref="RaceOpponents"/>), Platzierung nach Weg/Zieleinlauf. HUD wie die exe-Komponente
///    CHECKPOINTS: /HUD/position nur mit /:Pos, /:Exp, /:NbPlayers (0xb4f380). /HUD/list_versus gehört im Original nur zum
///    Club-Duell (GMCompClub 0xb6aad0) und /HUD/ref_score nur zu Zeitfahren/Countdown – beide hier nicht mehr benutzt.
///  - Zeitfahren (0): Gegnerzeiten aus der Zielzeit timG. Radar (1, GMCompTraps): Tor 1..n−1 sind Radare ohne Reihenfolge, je
///    Radar zählt die höchste Durchfahrt ab dem Mindesttempo "tras"; Wertung = Durchschnitt aller Radare (Text 0xc7), Ende mit
///    dem Zeitlimit CtDw. Tempo (10, GMCompSpeed, <see cref="SpeedChallenge"/>): Punkte = ∫ (v − vmin) dt über v ≥ vmin
///    bis zum Zeitlimit (Text 0x88e).
///  - Ergebnis: results.bnk (<see cref="RaceResults.ShowChampionship"/>), Punkte 10-8-6-5-4-3-2-1, Preisgeld
///    (DBLicense._racemoneyreward nach Platz, bei Wiederholung nur die Verbesserung; Gesamtsieg _champmoneyreward).
///  - "Weiter": Abschluss-Kino (falls die Meisterschaft fertig ist), zurück zum Treffpunkt und Wettbewerbsansicht der Karte.
/// Unitys eigenes Ergebnisfenster (OnGUI, nur ohne results.bnk) entfällt: Original-Film oder direkt zurück.
/// </summary>
public sealed class GAME_MODE_CHAMPIONSHIP : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_CHAMPIONSHIP;
    static readonly ScreenDef[] _screens = { ScreenDefs.Hud };
    public override IReadOnlyList<ScreenDef> Screens => _screens;

    public static GAME_MODE_CHAMPIONSHIP Instance => (GAME_MODE_CHAMPIONSHIP)GameModes.Get(Gamemode.GAME_MODE_CHAMPIONSHIP);
    /// <summary>Rennen, Ergebnis oder Rückweg zum Treffpunkt laufen.</summary>
    public static bool Busy => GameModes.Get(Gamemode.GAME_MODE_CHAMPIONSHIP) is GAME_MODE_CHAMPIONSHIP c && c._phase != Phase.None;
    /// <summary>Zuletzt bekannter Kontext (für Starts außerhalb eines Bildes, z.B. nach einem Kino).</summary>
    public static IGameModeContext? Context { get; private set; }

    enum Phase { None, Racing, Speed, Results, BackToHub }
    Phase _phase;
    IGameModeContext? _ctx;
    readonly RaceManager _race = new();
    readonly RaceHud _hud = new();
    readonly RaceAutopilot _autopilot = new();
    readonly RaceResults _results = new();
    bool _wired;
    Championships.Cup? _cup;
    RaceDefinition? _def;
    readonly Dictionary<string, (int target, int limit, bool hasLimit)> _orig = new();
    float[] _cum = Array.Empty<float>();   // Weglänge entlang RoadPath
    int _pIdx;
    float _pProgress;
    int _lastPos = -1, _lastTotal = -1;
    float _finishLen;
    float _resultAt = -1f;
    List<Row>? _rows;
    bool _filmShown;
    bool _privateOn;
    float _clock;
    // Tempo-Modus
    float _speedT, _speedCountdown;
    readonly SpeedChallenge _speed = new();
    // Eliminator (exe LapElimination): Teilnehmer 0 = Spieler, 1.. = Gegner
    readonly LapElimination _elim = new();
    bool _elimOn;
    int[] _elimLaps = Array.Empty<int>();
    List<float>[] _elimRoundT = Array.Empty<List<float>>();
    // Rückweg zum Treffpunkt
    Championships.Cup? _hubCup; float _hubUntil, _hubSettleAt; bool _hubTeleported;

    sealed class Row
    {
        public string Name = "", Car = "";
        public bool Player, Finished;
        public float Value;      // Zeit (s) bzw. Wert (km/h); Eliminator: Platz
        public string? Text;     // Anzeige statt Value (Eliminator: Zeit des Siegers bzw. "--")
        public int Points, Place, BotIndex = -1;
    }

    VehicleManager? Vm => _ctx?.Vehicles?.Manager;
    VmfPlayer? Hud => Vm?.Minimap?.Hud;

    // ------------------------------------------------------------------ Start (aus Championships)
    internal static void Begin(IGameModeContext ctx, Championships.Cup cup, RaceDefinition def) => Instance.BeginRace(ctx, cup, def);

    /// <summary>
    /// Testfahrt des Challenge-Editors (exe FSCommand "test" 0xad7c68 -> 0xad27c0: die Challenge des Editors läuft über denselben
    /// Challenge-Ablauf wie die Rennen, danach zurück in den Editor). Ohne Meisterschaft: keine Gegner, keine Punkte/Preise;
    /// Ziel, Zeitablauf oder Abbruch rufen <paramref name="onEnd"/> (Editor: BackFromTest).
    /// </summary>
    internal static void BeginTest(IGameModeContext ctx, RaceDefinition def, bool showRoute, Action<bool> onEnd)
    {
        Instance._testEnd = onEnd;
        Instance.BeginRace(ctx, null, def);
        Minimap.RaceParticipants = 1;   // nur der Spieler (sonst bleibt die Teilnehmerzahl des letzten Rennens im HUD)
        Instance._lastPos = -1;
        if (!showRoute) { RaceRoute.RoadPath = null; RaceRoute.RoadPathSeg = null; }   // ohne Pflichtweg keine Fahrlinie
    }

    /// <summary>Ergebnis der letzten Testfahrt (Zeit, Strafen) und höchste Radar-Geschwindigkeit – für die Spielhalle.</summary>
    internal static RaceResult? LastTestResult;
    internal static float LastTestTopKmh;

    /// <summary>Laufende Testfahrt des Editors: Rückruf nach dem Ende (true = Ziel erreicht).</summary>
    Action<bool>? _testEnd;

    /// <summary>Testfahrt beenden und in den Editor zurück.</summary>
    void EndTest(bool completed)
    {
        var te = _testEnd; _testEnd = null;
        // Ergebnis für die Spielhalle (Community Racing Center: Wertung der gefahrenen Challenge)
        LastTestResult = _race.Result;
        LastTestTopKmh = _race.BestTrapSpeedKmh;
        EndRace();
        te?.Invoke(completed);
    }

    void Wire(IGameModeContext ctx)
    {
        _ctx = ctx; Context = ctx;
        var vm = ctx.Vehicles?.Manager;
        _race.Streamer = ctx.Terrain?.Streamer;
        _race.Vehicles = vm;
        _hud.Race = _race;
        _hud.Minimap = vm?.Minimap;
        _results.Manager = vm;
        RaceRoadPath.Lanes = ctx.Vehicles?.Traffic?.Lanes;
        if (_wired) return;
        _wired = true;
        _hud.Bind();
        _results.Retry += () => AfterResults(1);
        _results.Next += () => AfterResults(2);
        _results.Quit += () => AfterResults(0);
        _race.Prepared += r =>
        {
            var opp = r.Opponents;
            if (opp != null) { opp.FinishDistance = _finishLen; opp.Clock = () => _race.ElapsedSeconds; opp.KeepDrivingAfterFinish = true; }
            Minimap.RacePortalBlips.Clear();   // Radare zeigt die Minimap als Ziel-Symbole (RaceHud, exe 0xb63920)
        };
        _race.Started += r =>
        {
            ShowPosition(true);
            if (_elimOn && _def != null) StartElimination();
        };
        _race.Completed += r => OnFinished(false);
        _race.Failed += r => OnFinished(true);
        _race.Aborted += r => { Log.Info("Rennen abgebrochen"); if (_testEnd != null) EndTest(false); else EndRace(); };
        // ESC-Menü im Rennen (main.vmf WhatGameMode 2): "Neu starten" / "Beenden" = Rennen abbrechen
        PauseMenu.RegisterRace(this,
            () => _phase == Phase.Racing || _phase == Phase.Speed,
            () =>
            {
                var c = _cup; var d = _def; var te = _testEnd; _testEnd = null;
                EndRace();
                _testEnd = te;   // Testfahrt neu starten bleibt eine Testfahrt
                if (d != null && _ctx != null && (c != null || te != null)) BeginRace(_ctx, c, d);
            },
            () => { Log.Info("Rennen abgebrochen"); if (_testEnd != null) EndTest(false); else EndRace(); });
    }

    void BeginRace(IGameModeContext ctx, Championships.Cup? cup, RaceDefinition def)
    {
        Wire(ctx);
        var vm = Vm; var veh = vm?.Current;
        if (vm == null || veh == null || ctx.Terrain?.Streamer == null) return;
        _cup = cup; _def = def;
        _autopilot.End();
        _race.Stop();
        _race.Cleanup();
        _results.Close();
        SetPrivate(true);

        // Original-Werte merken (werden je Spielart angepasst)
        if (!_orig.ContainsKey(def.Name)) _orig[def.Name] = (def.TargetTimeMs, def.TimeLimitMs, def.HasTimeLimit);
        var o = _orig[def.Name];
        def.TargetTimeMs = o.target; def.TimeLimitMs = o.limit; def.HasTimeLimit = o.hasLimit;
        // Rennen haben kein Zeitlimit. SpeedTrap behält ChCD/CtDw aus der ig2: Der Spieler braucht die Zeit,
        // um schlechte Portdurchfahrten innerhalb des Limits noch zu verbessern.
        if (def.Kind == RaceMode.Race) { def.TargetTimeMs = 0; def.TimeLimitMs = 0; def.HasTimeLimit = false; }
        // Radar zeigt keine GPS-Route: ein vor dem Start gesetztes Ziel würde sonst über der Minimap liegen.
        if (def.Kind == RaceMode.SpeedTrap) Gps.Instance?.ClearTarget();

        // Eliminator: Runden = Teilnehmer − 1 (exe 0xb5a0b8 setzt sie an der Checkpoint-Komponente), Straßenroute neu
        _elimOn = def.IsEliminator && cup != null;
        if (_elimOn)
        {
            int laps = System.Math.Max(1, System.Math.Min(def.BotCount > 0 ? def.BotCount : 7, cup!.Lic.Bots.Count));
            if (def.Laps != laps) { def.Laps = laps; def.RoadPath = null; def.RoadPathSeg = null; }
        }
        if (def.RoadPath == null) { try { RaceRoadPath.Compute(def); } catch (Exception ex) { Log.Warn("Straßenroute: " + ex.Message); } }
        if (def.RoadPath == null || def.RoadPath.Count < 2) def.RoadPath = new List<Vector3> { def.StartPosition, def.Checkpoints[^1].Position };
        // Radar: keine Routenanzeige auf der Minimap/großen Karte, nur blaue Portal-Dots.
        bool showRoute = def.Kind != RaceMode.Speed && def.Kind != RaceMode.SpeedTrap;
        RaceRoute.RoadPath = showRoute ? def.RoadPath : null;
        RaceRoute.RoadPathSeg = showRoute ? def.RoadPathSeg : null;
        BuildCum(def.RoadPath);

        Championships.StartFrame(def, out var start, out var fwd);
        var right = new Vector3(fwd.Z, 0f, -fwd.X);
        def.Opponents.Clear();
        int playerSlot = 0;
        if (cup != null && (def.Kind == RaceMode.Race || def.Kind == RaceMode.SpeedTrap)) playerSlot = BuildGrid(cup, def, start, fwd, right);
        // Spielerauto in die Startaufstellung (hinterste Reihe) bzw. an die Startlinie (hält fest, bis der Boden geladen ist)
        int row = playerSlot / 2; float side = playerSlot == 0 ? 0f : (playerSlot % 2 == 0 ? -2.2f : 2.2f);
        var pIsl = start - fwd * (row * ChampionshipConfig.GridRowSpacing) + right * side;
        vm.TeleportSafe(pIsl, fwd);
        _pIdx = 0; _pProgress = -row * ChampionshipConfig.GridRowSpacing; _lastPos = -1;
        _rows = null; _resultAt = -1f; _filmShown = false;


        if (def.Kind == RaceMode.Speed)
        {
            _phase = Phase.Speed;
            _speedT = 0f; _speedCountdown = RaceConfig.CountdownSeconds;
            Minimap.RaceParticipants = 1;   // Tempo: nur der Spieler fährt
            vm.ExternalControl = true;   // im Countdown halten (Pedale setzt dieser Modus)
            var h = Hud;
            if (h != null)
            {
                FrontendSounds.Attach(h);
                ChallengeModes.ApplyHud(h, def);       // exe 0xb40039: /:cur_gm = /:whatcha = "speed"
                h.SetVisible("/HUD/score", false);
                h.SetVisible("/_3_2_1_go", true); h.GotoLabel("/_3_2_1_go", "3_go", true);
            }
        }
        else
        {
            if (!_race.StartRace(def)) { Log.Warn("Rennen " + def.Name + " konnte nicht gestartet werden"); if (_testEnd != null) EndTest(false); else EndRace(); return; }
            _phase = Phase.Racing;
        }
        // Phase vor dem Wechsel setzen (Enter ohne Rennen kehrt sonst in die Freifahrt zurück)
        if (GameMode.Current != Gamemode.GAME_MODE_CHAMPIONSHIP) GameMode.Set(Gamemode.GAME_MODE_CHAMPIONSHIP);
    }

    /// <summary>Gegner der Lizenz in Zweierreihen vor dem Spieler aufstellen. Liefert den Startplatz des Spielers.</summary>
    int BuildGrid(Championships.Cup cup, RaceDefinition def, Vector3 start, Vector3 fwd, Vector3 right)
    {
        var vm = Vm!; var db = vm.Database;
        int n = System.Math.Min(def.BotCount > 0 ? def.BotCount : 7, cup.Lic.Bots.Count);
        var extended = new List<Vector3>(def.RoadPath!);
        var a = extended[^1]; var b = extended[System.Math.Max(0, extended.Count - 3)];
        var dir = a - b; dir.Y = 0f;
        if (dir.LengthSquared() > 1e-3f) extended.Add(a + Vector3.Normalize(dir) * 250f);
        for (int i = 0; i < n; i++)
        {
            var (pnj, diff, aggr) = cup.Lic.Bots[i];
            var car = Championships.BotCar(pnj, cup.Lic, db, vm.BnkDir);
            if (car == null) continue;
            int row = i / 2; float side = i % 2 == 0 ? -2.2f : 2.2f;
            var pos = start - fwd * (row * ChampionshipConfig.GridRowSpacing) + right * side;
            var path = new List<Vector3> { pos };
            // auf die Ideallinie: ab dem Punkt der Route, der vor dem Startplatz liegt
            int k = 0; while (k < extended.Count - 1 && Vector3.Dot(extended[k] - pos, fwd) < 2f) k++;
            for (; k < extended.Count; k++) path.Add(extended[k]);
            float top = MathF.Max(30f, car.MaxTheoricalSpeed) / 3.6f;
            // Fahrergesetz (opponents_spec.md): vmax * (0.82 + 0.25 d), a_lat = g (0.75 + 0.4 d),
            // a = (27.8 / t0-100) * (0.85 + 0.4 d) * 1.3, Bremse = 7.5 + 3 d.
            // Die alten Werte (0.74..0.94 / grip*0.40..0.58 / 27.8*0.8..1.05) waren deutlich zu
            // niedrig – deshalb wirkten die Gegner träge und bogen zu früh ab.
            def.Opponents.Add(new RaceOpponentDef
            {
                CarId = car.Id,
                Name = Championships.BotName(pnj, i),
                Position = pos,
                Forward = fwd,
                MaxSpeed = top * (0.82f + 0.25f * diff) * ChampionshipConfig.BotSpeedScale,
                LatAccel = 9.81f * (0.75f + 0.4f * diff),
                Accel = (27.8f / MathF.Max(3f, car.Accel0To100)) * (0.85f + 0.4f * diff) * 1.3f,
                Brake = 7.5f + 3f * diff,
                Difficulty = diff,
                Aggressiveness = aggr,
                LateralOffset = side * 0.6f,
                PathOverride = path,
                StartDistance = -row * ChampionshipConfig.GridRowSpacing,
                StartCheckpoint = 1,
            });
        }
        return def.Opponents.Count;
    }

    void BuildCum(List<Vector3> path)
    {
        _cum = new float[path.Count];
        for (int i = 1; i < path.Count; i++) { var d = path[i] - path[i - 1]; d.Y = 0f; _cum[i] = _cum[i - 1] + d.Length(); }
        _finishLen = _cum.Length > 0 ? _cum[^1] : 0f;
    }

    // ------------------------------------------------------------------ Spielmodus
    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx; Context = ctx;
        if (from == Gamemode.GAME_MODE_PAUSE) return;
        if (_phase == Phase.None) GameMode.Set(Gamemode.GAME_MODE_FREERIDE);   // ohne Rennen (Entwicklertaste): zurück
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to == Gamemode.GAME_MODE_PAUSE) return;   // Pausemenü: Rennen bleibt stehen
        if (_phase == Phase.BackToHub && to == Gamemode.GAME_MODE_MAP) { _phase = Phase.None; return; }
        if (_phase != Phase.None)
        {
            if (_testEnd != null && to != Gamemode.GAME_MODE_EDITOR) { _testEnd = null; }
            EndRace();
        }
    }

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx; Context = ctx;
        float dt = time.DeltaTime;
        _clock += dt;
        if (_phase == Phase.None) { GameMode.Set(Gamemode.GAME_MODE_FREERIDE); return; }
        Wire(ctx);
        _hud.Minimap = Vm?.Minimap;
        if (_phase == Phase.BackToHub) { UpdateBackToHub(ctx); return; }
        if (_phase == Phase.Results) { UpdateResults(ctx, dt); return; }

        // Fahren wie in der Freifahrt
        HandlePauseKey(ctx);
        if (ctx.Paused) return;
        var veh = ctx.Vehicles; var vm = veh?.Manager;
        if (TDU2Controls.Pressed("minimap") && vm?.Minimap != null && vm.Current != null && !veh!.Suspended && !veh.Frozen)
            vm.Minimap.ShowMinimap = !vm.Minimap.ShowMinimap;
        if (_phase == Phase.Speed) { UpdateSpeed(ctx, dt); return; }
        // Unity AbortKey (Backspace) während Countdown/Lauf
        if ((_race.State == RaceState.Running || _race.State == RaceState.Countdown) && ctx.Pressed(0x08)) { _race.Abort(); return; }
        _race.Update(dt);
        _hud.Update();
        _autopilot.Update(dt);
        UpdateRacing(dt);
    }

    // ------------------------------------------------------------------ Laufendes Rennen
    void UpdateRacing(float dt)
    {
        if (_def == null || !_race.Running || Vm == null) return;
        var carIsland = Vm.PositionGame;
        // Fortschritt des Spielers entlang der Route (Fenster um den letzten Punkt, Rundkurse wiederholen die Strecke)
        var path = _def.RoadPath!;
        int lo = System.Math.Max(0, _pIdx - 15), hi = System.Math.Min(path.Count - 1, _pIdx + 80);
        float best = float.MaxValue; int bi = _pIdx;
        for (int i = lo; i <= hi; i++) { var d = path[i] - carIsland; d.Y = 0f; float m = d.LengthSquared(); if (m < best) { best = m; bi = i; } }
        _pIdx = bi;
        if (_pIdx == 0)
        {
            var d0 = carIsland - path[0]; d0.Y = 0f;
            Championships.StartFrame(_def, out _, out var f0);
            _pProgress = Vector3.Dot(d0, f0);
        }
        else _pProgress = _cum[_pIdx];

        var opp = _race.Opponents;
        if (opp == null)
        {
            // ohne Gegner: Platz 1 von 1 (sonst bliebe /:Pos//:NbPlayers vom letzten Lauf stehen)
            if ((_def.Kind == RaceMode.SpeedTrap || _def.Kind == RaceMode.Race) && (_lastPos != 1 || _lastTotal != 1)) { _lastPos = 1; _lastTotal = 1; SetPosition(1, 1); }
            return;
        }
        var st = opp.Standings();
        if (_def.Kind == RaceMode.SpeedTrap)
        {
            // Radar (exe 0xb63bd0): /:NbPlayers und Platz nach dem Durchschnitt der Radare
            float mine = _race.TrapAverageKmh;
            int tp = 1; for (int i = 0; i < st.Count; i++) if (_race.OpponentTrapAverageKmh(i) > mine) tp++;
            if (tp != _lastPos || st.Count + 1 != _lastTotal) { _lastPos = tp; _lastTotal = st.Count + 1; SetPosition(tp, st.Count + 1); }
            return;
        }
        if (_def.Kind != RaceMode.Race) return;
        int pos = 1, total = 1;
        foreach (var s in st) { if (s.Out) continue; total++; if (s.Finished || s.Traveled > _pProgress) pos++; }
        if (pos != _lastPos || total != _lastTotal) { _lastPos = pos; _lastTotal = total; SetPosition(pos, total); }
        Minimap.RaceCount = total;
        Minimap.RaceParticipants = total;
        Minimap.RaceBlips.Clear(); opp.WorldPositions(Minimap.RaceBlips);
        if (_elimOn) UpdateElimination(dt, st);
    }

    // ------------------------------------------------------------------ Eliminator (exe LapElimination, Game/Racing/LapElimination)
    void StartElimination()
    {
        int n = 1 + (_race.Opponents?.Count ?? 0);
        _elim.Start(Hud, n);
        _elimLaps = new int[n];
        _elimRoundT = new List<float>[n];
        for (int i = 0; i < n; i++) _elimRoundT[i] = new List<float>();
    }

    void UpdateElimination(float dt, List<RaceOpponents.Standing> st)
    {
        var def = _def!; var h = Hud;
        int n = _elimLaps.Length;
        if (n == 0 || _elim.Remaining <= 1) return;
        float t = _race.ElapsedSeconds;
        float lapLen = def.Laps > 0 ? _finishLen / def.Laps : _finishLen;
        // beendete Runden: Spieler über die Checkpoints, Gegner über den Weg entlang der Strecke
        for (int i = 0; i < n; i++)
        {
            int laps;
            if (i == 0) laps = def.TargetsPerLap > 0 ? _race.PassedCount / def.TargetsPerLap : 0;
            else
            {
                var s = st[i - 1];
                laps = s.Finished ? def.Laps : lapLen > 1f ? (int)(MathF.Max(0f, s.Traveled) / lapLen) : 0;
            }
            while (_elimRoundT[i].Count < laps) _elimRoundT[i].Add(t);
            _elimLaps[i] = laps;
        }
        int who = _elim.Update(dt, _elimLaps, (p, k) => k - 1 < _elimRoundT[p].Count ? _elimRoundT[p][k - 1] : float.MaxValue);
        // Liste nach Platzierung: Aktive nach Weg, Ausgeschiedene nach Endplatz
        var order = new List<int>(n);
        for (int i = 0; i < n; i++) if (!_elim.IsOut(i)) order.Add(i);
        order.Sort((a, b) => Progress(b, st).CompareTo(Progress(a, st)));
        var outs = new List<int>(); for (int i = 0; i < n; i++) if (_elim.IsOut(i)) outs.Add(i);
        outs.Sort((a, b) => _elim.FinalPlace[a].CompareTo(_elim.FinalPlace[b]));
        order.AddRange(outs);
        _elim.UpdateRows(h, order, i => ParticipantName(i, st));
        if (who < 0) return;
        string name = ParticipantName(who, st);
        LapElimination.ShowEliminated(h, name);
        if (who == 0) { _race.ForceFinish(false); return; }   // Spieler raus: verloren (exe Zustand 2)
        _race.Opponents?.Eliminate(who - 1);
        Minimap.RaceParticipants = _elim.Remaining;
        if (_elim.Remaining <= 1) { _elim.CloseWinner(); _race.ForceFinish(true); }   // nur der Spieler übrig (exe Zustand 1)
    }

    float Progress(int p, List<RaceOpponents.Standing> st)
    {
        if (p == 0) return _pProgress;
        var s = st[p - 1];
        return s.Finished ? 1e9f - s.FinishTime : s.Traveled;
    }

    static string ParticipantName(int p, List<RaceOpponents.Standing> st) => p == 0 ? Championships.PlayerName : (st[p - 1].Name ?? "?");

    /// <summary>
    /// Platzierung (exe CHECKPOINTS 0xb4f380 bzw. TRAPS 0xb63bd0): /HUD/position öffnet RaceHud beim Start der Komponenten (Rennen,
    /// Radar); hier nur Zustand zurücksetzen bzw. am Ende Minimap-Punkte löschen. Keine Namen vor/hinter mir, keine Versus-Liste.
    /// </summary>
    void ShowPosition(bool on)
    {
        if (_def == null || !on)
        {
            Minimap.RaceCount = 0; Minimap.RaceBlips.Clear(); Minimap.RacePortalBlips.Clear();
            Minimap.RaceParticipants = 0;   // Minimap zeigt wieder die Spieler der Sitzung
            Hud?.SetVisible("/HUD/eliminator", false);
            return;
        }
        _lastPos = -1; _lastTotal = -1;
        // Spielerzahl der Minimap = Teilnehmer dieses Rennens (die exe zählt die Spieler der Sitzung – im Rennen die Rennsitzung)
        Minimap.RaceParticipants = 1 + (_race.Opponents?.Count ?? 0);
        if (_def.Kind == RaceMode.SpeedTrap)
        {
            // Radar: keine Gegner-Punkte; die Radare zeigt die Minimap als Ziel-Symbole (RaceHud)
            Minimap.RaceCount = 0; Minimap.RaceBlips.Clear(); Minimap.RacePortalBlips.Clear();
        }
        else Minimap.RacePortalBlips.Clear();
    }

    void SetPosition(int pos, int total)
    {
        var h = Hud;
        if (h == null) return;
        h.SetVariable("/:NbPlayers", total.ToString(CultureInfo.InvariantCulture));
        ChallengeModes.SetRank(h, pos);   // exe 0xb3c710: "%d." + Exponent " " (deutsch)
    }

    // ------------------------------------------------------------------ Tempo (exe GMCompSpeed, SpeedChallenge)
    void UpdateSpeed(IGameModeContext ctx, float dt)
    {
        var vm = Vm; var veh = vm?.Current;
        if (vm == null || veh == null || _def == null) { if (_testEnd != null) EndTest(false); else EndRace(); return; }
        var h = Hud;
        float limit = MathF.Max(1f, _orig[_def.Name].limit / 1000f);
        if (_speedCountdown > 0f)
        {
            _speedCountdown -= dt;
            veh.BrakePedal = 1f; veh.HandBrake = 1f; veh.StartLock = true;
            if (_speedCountdown <= 0f)
            {
                vm.ExternalControl = false;
                veh.BrakePedal = 0f; veh.HandBrake = 0f; veh.StartLock = false;
                // Start der Komponenten in exe-Reihenfolge: SPEED (0x88), TIME (0x9c), COUNTDOWN (0xa0)
                _speed.Start(h, _def);
                ChallengeModes.ShowClip(h, "/HUD/time", true);
                ChallengeModes.ShowClip(h, "/HUD/time_cd", true);
                ChallengeModes.ShowClip(h, "/HUD/time", false);
                ChallengeModes.ShowClip(h, "/HUD/ref_score", false);
            }
            return;
        }
        if (ctx.Pressed(0x08)) { Log.Info("Rennen abgebrochen"); if (_testEnd != null) EndTest(false); else EndRace(); return; }
        _speedT += dt;
        _speed.Update(h, veh.SpeedKmh / 3.6f, dt);
        if (h != null)
        {
            float left = MathF.Max(0f, limit - _speedT);
            int ms = (int)(left * 1000f);
            h.SetVariable("/:MinCd", (ms / 60000).ToString(CultureInfo.InvariantCulture));
            h.SetVariable("/:SecCd", ((ms / 1000) % 60).ToString("00", CultureInfo.InvariantCulture));
            h.SetVariable("/:CentCd", ((ms / 10) % 100).ToString("00", CultureInfo.InvariantCulture));
        }
        if (_speedT >= limit)
        {
            _speed.Close(h);
            if (h != null) { ChallengeModes.ShowClip(h, "/HUD/time_cd", false); h.GotoLabel("/_3_2_1_go", "default", false); h.SetVisible("/_3_2_1_go", false); }
            OnFinished(false);
        }
    }

    // ------------------------------------------------------------------ Ergebnis
    void OnFinished(bool failed)
    {
        if (_testEnd != null) { EndTest(!failed); return; }   // Editor-Testfahrt: ohne Wertung zurück in den Editor
        if (_cup == null || _def == null) return;
        ShowPosition(false);
        var vm = Vm;
        if (vm?.Current != null) _autopilot.Begin(vm);
        _rows = BuildRows(failed);
        _filmShown = false;
        int[] pts = new int[1 + _cup.Lic.Bots.Count];
        var table = ChampionshipConfig.PointsTable;
        foreach (var r in _rows)
        {
            r.Points = r.Place - 1 < table.Length ? table[r.Place - 1] : 0;
            if (r.Player) pts[0] = r.Points; else if (r.BotIndex >= 0 && r.BotIndex + 1 < pts.Length) pts[r.BotIndex + 1] = r.Points;
        }
        RaceProgress.SetCupRacePoints(_cup.Lic.Hash, _def.Name, pts);
        var me = _rows.Find(r => r.Player)!;
        string info = failed ? "Zeit abgelaufen" : "Platz " + me.Place + " von " + _rows.Count + " – " + me.Points + " Punkte";
        // Preisgeld des Rennens (DBLicense._racemoneyreward) nach Platz; bei Wiederholung nur die Verbesserung
        if (!failed)
        {
            long prize = PrizeFor(me.Place, _cup.Lic.RaceMoneyReward);
            long paid = RaceProgress.CupPaid(_cup.Lic.Hash, _def.Name);
            if (prize > paid && AddMoney(prize - paid)) { RaceProgress.SetCupPaid(_cup.Lic.Hash, _def.Name, prize); info += "   +" + Money(prize - paid); }
        }
        string cupInfo = CupStanding(out bool done, out int place);
        if (done && !RaceProgress.IsCupDone(_cup.Lic.Hash))
        {
            RaceProgress.MarkCupDone(_cup.Lic.Hash, place);
            bool hasElite = false;
            foreach (var c in Championships.Cups) if (c.Lic.VehicleCategory1 == _cup.Lic.VehicleCategory1 && c.Lic.Order == _cup.Lic.Order + 1) hasElite = true;
            cupInfo += " | Meisterschaft abgeschlossen – Gesamtplatz " + place + (hasElite ? ". Die Elite-Meisterschaft ist freigeschaltet!" : ".");
            // Meisterschafts-Preisgeld (DBLicense._champmoneyreward) für den Gesamtsieg
            if (place == 1 && _cup.Lic.ChampMoneyReward > 0 && AddMoney(_cup.Lic.ChampMoneyReward))
                cupInfo += " Preisgeld Meisterschaft: +" + Money(_cup.Lic.ChampMoneyReward);
        }
        _phase = Phase.Results;
        _resultAt = _clock + 1.2f;
        Championships.Refresh();
    }

    static readonly NumberFormatInfo De = Paradise.Core.Util.GermanFormat.Number;

    static long PrizeFor(int place, int reward)
    {
        var share = ChampionshipConfig.PrizeShare;
        return place >= 1 && place <= share.Length ? (long)MathF.Round(reward * share[place - 1]) : 0;
    }

    static string Money(long v) => v.ToString("N0", De) + " $";

    /// <summary>Geld auf den Spielstand (Server) buchen. Ohne Anmeldung kein Geld.</summary>
    static bool AddMoney(long amount)
    {
        if (amount <= 0 || !TDU2Session.LoggedIn || TDU2Session.Save == null) return false;
        TDU2Session.Save.money += amount;
        _ = TDU2Session.SaveAsync();
        Log.Info("Preisgeld +" + amount + " $ -> " + TDU2Session.Save.money + " $");
        return true;
    }

    List<Row> BuildRows(bool failed)
    {
        var rows = new List<Row>();
        var opp = _race.Opponents;
        var st = opp != null ? opp.Standings() : new List<RaceOpponents.Standing>();
        var me = new Row { Name = Championships.PlayerName, Car = Vm?.CurrentCar?.ModelName ?? "", Player = true, Finished = !failed };
        rows.Add(me);
        var def = _def!;
        switch (def.Kind)
        {
            case RaceMode.Race when _elimOn && _elim.FinalPlace.Length == st.Count + 1:
                {
                    // Eliminator: Platz = Reihenfolge des Ausscheidens; noch Fahrende nach Weg vor den Ausgeschiedenen
                    var act = new List<int>();
                    for (int i = 0; i < st.Count + 1; i++) if (!_elim.IsOut(i)) act.Add(i);
                    act.Sort((a, b) => Progress(b, st).CompareTo(Progress(a, st)));
                    var place = (int[])_elim.FinalPlace.Clone();
                    for (int k = 0; k < act.Count; k++) place[act[k]] = k + 1;
                    me.Value = place[0];
                    me.Text = place[0] == 1 && _race.Result != null && !failed ? RaceHud.FormatRaceTimeMs(_race.Result.FinalTimeMs) : "--";
                    for (int i = 0; i < st.Count; i++)
                        rows.Add(new Row { Name = st[i].Name, Car = st[i].Car, Value = place[i + 1], Text = "--", Finished = !st[i].Out, BotIndex = i });
                    rows.Sort((a, b) => a.Value.CompareTo(b.Value));
                    for (int i = 0; i < rows.Count; i++) rows[i].Place = i + 1;
                    return rows;
                }
            case RaceMode.Race:
                {
                    float now = _race.ElapsedSeconds;
                    me.Value = failed ? float.MaxValue : _race.Result != null ? _race.Result.FinalTimeMs / 1000f : now;
                    for (int i = 0; i < st.Count; i++)
                    {
                        var s = st[i];
                        float t = s.Finished ? s.FinishTime : now + MathF.Max(0f, _finishLen - s.Traveled) / MathF.Max(8f, s.Def.MaxSpeed * 0.7f);
                        rows.Add(new Row { Name = s.Name, Car = s.Car, Value = t, Finished = s.Finished, BotIndex = i });
                    }
                    rows.Sort((a, b) => a.Value.CompareTo(b.Value));
                    break;
                }
            case RaceMode.SpeedTrap:
                {
                    // Wertung = Durchschnitt aller Radare (exe 0xb640f0, Text 0xc7)
                    float avg = failed ? 0f : _race.TrapAverageKmh;
                    me.Value = avg;
                    if (st.Count > 0)
                    {
                        for (int i = 0; i < st.Count; i++)
                            rows.Add(new Row { Name = st[i].Name, Car = st[i].Car, Value = _race.OpponentTrapAverageKmh(i), Finished = true, BotIndex = i });
                    }
                    else AddGeneratedBots(rows, avg > 0f ? avg : 200f, true);
                    rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                    break;
                }
            case RaceMode.Speed:
                {
                    // Punkte = Meter über dem Mindesttempo (GMCompSpeed); Gegner um die Referenz SPPG gestreut
                    me.Value = _speed.Score;
                    AddGeneratedBots(rows, _speed.RefPoints > 0f ? _speed.RefPoints : MathF.Max(1f, _speed.Score), true);
                    rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                    break;
                }
            default:
                {
                    me.Value = failed || _race.Result == null ? float.MaxValue : _race.Result.FinalTimeMs / 1000f;
                    float refT = _orig[def.Name].target > 0 ? _orig[def.Name].target / 1000f : (me.Value < float.MaxValue ? me.Value : 120f);
                    AddGeneratedBots(rows, refT, false);
                    rows.Sort((a, b) => a.Value.CompareTo(b.Value));
                    break;
                }
        }
        if (failed) { rows.Remove(me); rows.Add(me); }
        for (int i = 0; i < rows.Count; i++) rows[i].Place = i + 1;
        return rows;
    }

    /// <summary>Gegnerwerte ohne gefahrene Gegner: um den Referenzwert gestreut nach Stärke (Zeit: kleiner = besser).</summary>
    void AddGeneratedBots(List<Row> rows, float reference, bool higherIsBetter)
    {
        var bots = _cup!.Lic.Bots; var vm = Vm;
        for (int i = 0; i < bots.Count; i++)
        {
            var (pnj, diff, _) = bots[i];
            float k = 1f + (0.55f - diff) * 0.12f + i * 0.012f;
            float v = higherIsBetter ? reference / k : reference * k;
            var car = vm != null ? Championships.BotCar(pnj, _cup.Lic, vm.Database, vm.BnkDir) : null;
            rows.Add(new Row { Name = Championships.BotName(pnj, i), Car = car != null ? car.ModelName : "", Value = v, Finished = true, BotIndex = i });
        }
    }

    string CupStanding(out bool done, out int place)
    {
        var total = Championships.CupTotals(_cup!, out int raced);
        done = raced >= _cup!.Races.Count;
        place = 1;
        for (int i = 1; i < total.Length; i++) if (total[i] > total[0]) place++;
        return _cup.Name + ": " + raced + "/" + _cup.Races.Count + " Rennen, " + total[0] + " Punkte (Gesamtplatz " + place + ")";
    }

    void UpdateResults(IGameModeContext ctx, float dt)
    {
        _autopilot.Update(dt);
        if (_filmShown) { _results.Update(ctx, dt); return; }   // Eingaben gehören dem Film (RaceResults)
        if (_rows == null || _clock < _resultAt) return;
        _filmShown = ShowResultsFilm();
        if (!_filmShown) AfterResults(2);   // ohne results.bnk: weiter wie "Weiter"
    }

    /// <summary>Ergebnis im Original-Film (results.vmf): Ergebniskarte, Rennliste, Meisterschaftswertung, Menü.</summary>
    bool ShowResultsFilm()
    {
        if (_rows == null || _cup == null || _def == null) return false;
        bool time = _def.Kind == RaceMode.Race || _def.Kind == RaceMode.TimeAttack;
        string Val(Row r) => r.Text != null ? r.Text : time ? (r.Value >= float.MaxValue * 0.5f ? "--'--\"--" : RaceHud.FormatRaceTime(r.Value))
                           : _def.Kind == RaceMode.Speed ? ((int)r.Value).ToString(CultureInfo.InvariantCulture) + " " + RaceTexts.Get(0x2fd, "Punkte").Trim()
                           : r.Value.ToString("0.00", CultureInfo.InvariantCulture) + " " + ChallengeModes.UnitName;
        var race = new List<RaceResults.Row>();
        foreach (var r in _rows)
            race.Add(new RaceResults.Row { Place = r.Place, Name = r.Name, Car = r.Car, Player = r.Player, Score = Val(r) + " (" + r.Points + " Pkt.)" });
        var cupRows = new List<RaceResults.Row>();
        var st = Championships.MapStandings(Championships.IndexOf(_cup));
        string myCar = Vm?.CurrentCar?.ModelName ?? "";
        for (int i = 0; i < st.Count; i++)
            cupRows.Add(new RaceResults.Row { Place = i + 1, Name = st[i].name, Car = st[i].player ? myCar : "", Player = st[i].player, Score = st[i].points + " Pkt." });
        var me = _rows.Find(r => r.Player);
        long prize = me != null ? PrizeFor(me.Place, _cup.Lic.RaceMoneyReward) : 0;
        return _results.ShowChampionship(Championships.RaceName(_def), _cup.Name, Championships.ModeLabel(_def), Championships.HubLabel(_cup),
                                         me != null ? me.Place : _rows.Count, me != null ? Val(me) : "", prize, race, cupRows);
    }

    /// <summary>Nach dem Ergebnis: 0 freie Fahrt, 1 wiederholen, 2 weiter = zurück in den Wettbewerb der Meisterschaft (Karte).</summary>
    void AfterResults(int mode)
    {
        var cup = _cup; var def = _def; var ctx = _ctx;
        _filmShown = false;
        EndRace();
        if (mode == 1 && cup != null && def != null && ctx != null) { BeginRace(ctx, cup, def); return; }
        if (mode == 2 && cup != null)
        {
            // Meisterschaft gerade abgeschlossen: Sieg-/Niederlage-Kino (Podium), dann zurück zum Treffpunkt
            string endKey = "end." + cup.Lic.Hash.ToString("X");
            if (RaceProgress.IsCupDone(cup.Lic.Hash) && !RaceCinematics.Seen(endKey))
            {
                var tot = Championships.CupTotals(cup, out _);
                bool won = true; for (int k = 1; k < tot.Length; k++) if (tot[k] > tot[0]) won = false;
                var end = RaceCinematics.ChampionshipEnd(Championships.BnkDir, cup.Lic, won);
                if (end != null) { RaceCinematics.MarkSeen(endKey); RaceCinematics.Play(end, () => BeginBackToHub(cup)); return; }
            }
            BeginBackToHub(cup);
            return;
        }
        GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    // ------------------------------------------------------------------ "Weiter": zurück zum Treffpunkt, Wettbewerb der Karte
    /// <summary>
    /// Das Auto steht nach dem Rennen irgendwo auf der Strecke – die Karte zeigte sonst Gegenden, die nicht geladen sind: an den
    /// Treffpunkt setzen (TeleportSafe hält es fest, bis der Boden geladen ist), warten bis die Umgebung steht, dann Karte öffnen.
    /// </summary>
    void BeginBackToHub(Championships.Cup cup)
    {
        var vm = Vm;
        if (vm?.Current == null) { GameMode.Set(Gamemode.GAME_MODE_FREERIDE); return; }
        var hub = Championships.HubPosition(cup, out var f);
        vm.TeleportSafe(hub, f);
        _hubCup = cup; _hubUntil = _clock + 15f; _hubSettleAt = -1f; _hubTeleported = true;
        _phase = Phase.BackToHub;
        if (GameMode.Current != Gamemode.GAME_MODE_CHAMPIONSHIP) GameMode.Set(Gamemode.GAME_MODE_CHAMPIONSHIP);
    }

    void UpdateBackToHub(IGameModeContext ctx)
    {
        var vm = Vm; var st = ctx.Terrain?.Streamer;
        if (!_hubTeleported || _hubCup == null) { _phase = Phase.None; GameMode.Set(Gamemode.GAME_MODE_FREERIDE); return; }
        var hub = Championships.HubPosition(_hubCup, out _);
        if (_hubSettleAt < 0f)
        {
            bool ready = st == null || st.ReadyAround(st.GameToRender(hub), 300f);
            if ((ready && (vm == null || !vm.Settling)) || _clock >= _hubUntil) _hubSettleAt = _clock + 0.4f;   // kurz ausrollen lassen (Federung)
            return;
        }
        if (_clock < _hubSettleAt) return;
        int idx = Championships.IndexOf(_hubCup);
        _hubCup = null; _hubTeleported = false;
        if (idx >= 0 && GAME_MODE_MAP.OpenContest(idx, true)) return;   // Exit (-> MAP) setzt die Phase zurück
        _phase = Phase.None;
        GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    void EndRace()
    {
        ShowPosition(false);
        var h = Hud;
        if (h != null) { h.SetVisible("/HUD/speed_gauge", false); h.SetVisible("/HUD/ref_points", false); h.SetVisible("/HUD/time_cd", false); }
        _autopilot.End();
        _results.Close();
        var vm = Vm;
        if (vm != null) vm.ExternalControl = false;
        if (vm?.Current != null) vm.Current.StartLock = false;
        if (_race.Running || _race.State == RaceState.Countdown || _race.State == RaceState.Preparing) _race.Stop();
        _race.Cleanup();
        _hud.Hide();
        RaceRoute.RoadPath = null; RaceRoute.RoadPathSeg = null;
        SetPrivate(false);
        _phase = Phase.None;
        _rows = null;
        Championships.Refresh();
    }

    /// <summary>Private Sitzung: Verkehr weg, Mitspieler aus (Unity SetPrivate; Verkehr entsteht nur in der Freifahrt neu).</summary>
    void SetPrivate(bool on)
    {
        if (on == _privateOn) return;
        _privateOn = on;
        if (on) _ctx?.Vehicles?.Traffic?.ClearCars();
        Multiplayer.Hidden = on && ChampionshipConfig.HidePlayersInCups;
    }
}