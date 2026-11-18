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
using Vector2 = System.Numerics.Vector2;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Meisterschaftsrennen (Daten/Treffpunkte/Karte: <see cref="Championships"/>). Im Original Teil des Wettbewerbs-Managers,
/// hier eigene Klasse (wie GAME_MODE_TUNER). Ablauf wie Unity TDU2Championships:
///  - Rennen (gmth 3): Gegner aus der Lizenz (_bot_n = DBPNJ, Auto der Klasse aus DBPNJCar, Stärke _difficulty_bot_n) in
///    Zweierreihen vor dem Spieler (<see cref="RaceOpponents"/>), Platzierung nach Weg/Zieleinlauf, HUD /HUD/position,
///    /HUD/list_versus und Abstand /HUD/ref_score (Zeitmessung alle 25 m).
///  - Zeitfahren (0): Gegnerzeiten aus der Zielzeit timG. Radar (1): Summe der Tempi an den Toren. Tempo (10): Durchschnitts-
///    tempo über die Zeit CtDw mit /HUD/speed_gauge und /HUD/time_cd. [Wertung vermutet]
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
    readonly List<float> _cpDist = new();       // Weg der Tore (Radar)
    readonly List<float> _playerRadar = new();
    float[][]? _botRadar;
    int[]? _botRadarNext;
    float _resultAt = -1f;
    List<Row>? _rows;
    bool _filmShown;
    bool _privateOn;
    float _clock;
    // Tempo-Modus
    float _speedT, _speedDist, _speedCountdown, _gaugeBase = -1f;
    // Rückweg zum Treffpunkt
    Championships.Cup? _hubCup; float _hubUntil, _hubSettleAt; bool _hubTeleported;

    sealed class Row
    {
        public string Name = "", Car = "";
        public bool Player, Finished;
        public float Value;      // Zeit (s) bzw. Wert (km/h)
        public int Points, Place, BotIndex = -1;
    }

    VehicleManager? Vm => _ctx?.Vehicles?.Manager;
    VmfPlayer? Hud => Vm?.Minimap?.Hud;

    // ------------------------------------------------------------------ Start (aus Championships)
    internal static void Begin(IGameModeContext ctx, Championships.Cup cup, RaceDefinition def) => Instance.BeginRace(ctx, cup, def);

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
            _botRadar = null;
        };
        _race.Started += r => ShowPosition(true);
        _race.CheckpointPassed += (r, i) => { if (_def != null && _def.Kind == RaceMode.SpeedTrap && Vm?.Current != null) _playerRadar.Add(MathF.Abs(Vm.Current.SpeedKmh)); };
        _race.Completed += r => OnFinished(false);
        _race.Failed += r => OnFinished(true);
        _race.Aborted += r => { Log.Info("Rennen abgebrochen"); EndRace(); };
        // ESC-Menü im Rennen (main.vmf WhatGameMode 2): "Neu starten" / "Beenden" = Rennen abbrechen
        PauseMenu.RegisterRace(this,
            () => _phase == Phase.Racing || _phase == Phase.Speed,
            () => { var c = _cup; var d = _def; EndRace(); if (c != null && d != null && _ctx != null) BeginRace(_ctx, c, d); },
            () => { Log.Info("Rennen abgebrochen"); EndRace(); });
    }

    void BeginRace(IGameModeContext ctx, Championships.Cup cup, RaceDefinition def)
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
        if (def.Kind == RaceMode.Race || def.Kind == RaceMode.SpeedTrap) { def.TargetTimeMs = 0; def.TimeLimitMs = 0; def.HasTimeLimit = false; }

        if (def.RoadPath == null) { try { RaceRoadPath.Compute(def); } catch (Exception ex) { Log.Warn("Straßenroute: " + ex.Message); } }
        if (def.RoadPath == null || def.RoadPath.Count < 2) def.RoadPath = new List<Vector3> { def.StartPosition, def.Checkpoints[^1].Position };
        RaceRoute.RoadPath = def.Kind == RaceMode.Speed ? null : def.RoadPath;
        RaceRoute.RoadPathSeg = def.Kind == RaceMode.Speed ? null : def.RoadPathSeg;
        BuildCum(def.RoadPath);

        Championships.StartFrame(def, out var start, out var fwd);
        var right = new Vector3(fwd.Z, 0f, -fwd.X);
        def.Opponents.Clear();
        int playerSlot = 0;
        if (def.Kind == RaceMode.Race || def.Kind == RaceMode.SpeedTrap) playerSlot = BuildGrid(cup, def, start, fwd, right);
        // Spielerauto in die Startaufstellung (hinterste Reihe) bzw. an die Startlinie (hält fest, bis der Boden geladen ist)
        int row = playerSlot / 2; float side = playerSlot == 0 ? 0f : (playerSlot % 2 == 0 ? -2.2f : 2.2f);
        var pIsl = start - fwd * (row * ChampionshipConfig.GridRowSpacing) + right * side;
        vm.TeleportSafe(pIsl, fwd);
        _pIdx = 0; _pProgress = -row * ChampionshipConfig.GridRowSpacing; _lastPos = -1;
        _playerRadar.Clear(); _cpDist.Clear();
        foreach (int k in def.TargetSequence()) _cpDist.Add(DistanceAlong(def.Checkpoints[k].Position));
        _rows = null; _resultAt = -1f; _filmShown = false;

        Log.Info("Meisterschaft " + cup.Name + " – " + Championships.ModeName(def) + " " + def.Name + " (" + Championships.RaceName(def) + "): " +
                 def.Opponents.Count + " Gegner, Strecke " + (_finishLen / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km, " + (def.IsCircuit ? def.Laps + " Runden" : "Punkt-zu-Punkt"));

        if (def.Kind == RaceMode.Speed)
        {
            _phase = Phase.Speed;
            _speedT = 0f; _speedDist = 0f; _speedCountdown = RaceConfig.CountdownSeconds;
            vm.ExternalControl = true;   // im Countdown halten (Pedale setzt dieser Modus)
            var h = Hud;
            if (h != null) { FrontendSounds.Attach(h); h.SetVisible("/_3_2_1_go", true); h.GotoLabel("/_3_2_1_go", "3_go", true); }
        }
        else
        {
            if (!_race.StartRace(def)) { Log.Warn("Rennen " + def.Name + " konnte nicht gestartet werden"); EndRace(); return; }
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
            var (pnj, diff, _) = cup.Lic.Bots[i];
            var car = Championships.BotCar(pnj, cup.Lic, db, vm.BnkDir);
            if (car == null) continue;
            int row = i / 2; float side = i % 2 == 0 ? -2.2f : 2.2f;
            var pos = start - fwd * (row * ChampionshipConfig.GridRowSpacing) + right * side;
            var path = new List<Vector3> { pos };
            // auf die Ideallinie: ab dem Punkt der Route, der vor dem Startplatz liegt
            int k = 0; while (k < extended.Count - 1 && Vector3.Dot(extended[k] - pos, fwd) < 2f) k++;
            for (; k < extended.Count; k++) path.Add(extended[k]);
            float top = MathF.Max(30f, car.MaxTheoricalSpeed) / 3.6f;
            float grip = System.Math.Clamp(MathF.Min(car.MaxLatFrictionFront, car.MaxLatFrictionRear), 1.1f, 2f);
            def.Opponents.Add(new RaceOpponentDef
            {
                CarId = car.Id,
                Name = Championships.BotName(pnj, i),
                Position = pos,
                Forward = fwd,
                MaxSpeed = top * Lerp(0.74f, 0.94f, diff) * ChampionshipConfig.BotSpeedScale,
                LatAccel = 9.81f * grip * Lerp(0.40f, 0.58f, diff),
                Accel = (100f / 3.6f) / MathF.Max(3f, car.Accel0To100) * Lerp(0.8f, 1.05f, diff),
                LateralOffset = side * 0.6f,
                PathOverride = path,
                StartDistance = -row * ChampionshipConfig.GridRowSpacing,
                StartCheckpoint = 1,
            });
        }
        return def.Opponents.Count;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * System.Math.Clamp(t, 0f, 1f);

    void BuildCum(List<Vector3> path)
    {
        _cum = new float[path.Count];
        for (int i = 1; i < path.Count; i++) { var d = path[i] - path[i - 1]; d.Y = 0f; _cum[i] = _cum[i - 1] + d.Length(); }
        _finishLen = _cum.Length > 0 ? _cum[^1] : 0f;
    }

    float DistanceAlong(Vector3 p)
    {
        var path = _def!.RoadPath!; float best = float.MaxValue; int bi = 0;
        for (int i = 0; i < path.Count; i++) { var d = path[i] - p; d.Y = 0f; float m = d.LengthSquared(); if (m < best) { best = m; bi = i; } }
        return _cum[bi];
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
            // Modus von außen gewechselt (Entwicklertaste, Inselwechsel …): Rennen beenden wie Unity OnDisable
            Log.Info("Meisterschaft: Modus " + to + " – laufendes Rennen beendet");
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
        UpdateRacing();
    }

    // ------------------------------------------------------------------ Laufendes Rennen
    void UpdateRacing()
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
        if (opp == null) return;
        var st = opp.Standings();
        if (_def.Kind == RaceMode.SpeedTrap) TrackBotRadar(st);
        if (_def.Kind != RaceMode.Race) return;
        int pos = 1;
        foreach (var s in st) if (s.Finished || s.Traveled > _pProgress) pos++;
        if (pos != _lastPos || st.Count + 1 != _lastTotal) { _lastPos = pos; _lastTotal = st.Count + 1; SetPosition(pos, st.Count + 1); }
        Minimap.RaceCount = st.Count + 1;
        Minimap.RaceBlips.Clear(); opp.WorldPositions(Minimap.RaceBlips);
        UpdateRaceHud(st, pos);
    }

    // ------------------------------------------------------------------ Renn-HUD: Namen vor/hinter mir, Abstand zum Führenden
    const float GapBin = 25f;                      // Zeitmessung alle 25 m Strecke
    readonly List<float> _binBot = new();          // früheste Gegnerzeit je Abschnitt
    readonly List<float> _binMe = new();           // meine Zeit je Abschnitt
    int[]? _botNextBin;
    float[]? _botLastBinT;
    List<float>[]? _botBins;
    bool _versusOpen;
    float _hudNext;
    int _gapSign;
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    void ResetRaceHud()
    {
        _binBot.Clear(); _binMe.Clear(); _botNextBin = null; _botLastBinT = null; _botBins = null; _versusOpen = false; _gapSign = 0; _hudNext = 0f;
    }

    void UpdateRaceHud(List<RaceOpponents.Standing> st, int pos)
    {
        var h = Hud;
        if (h == null) return;
        float t = _race.ElapsedSeconds;
        // Abschnittszeiten
        if (_botNextBin == null || _botNextBin.Length != st.Count)
        {
            _botNextBin = new int[st.Count]; _botLastBinT = new float[st.Count];
            _botBins = new List<float>[st.Count]; for (int i = 0; i < st.Count; i++) _botBins[i] = new List<float>();
        }
        float? gap = null;
        for (int i = 0; i < st.Count; i++)
            while ((_botNextBin[i] + 1) * GapBin <= st[i].Traveled)
            {
                int k = _botNextBin[i]++;
                _botLastBinT![i] = t; _botBins![i].Add(t);
                while (_binBot.Count <= k) _binBot.Add(-1f);
                if (_binBot[k] < 0f)
                {
                    _binBot[k] = t;
                    // ich war vorne: Vorsprung auf den Zweiten, sobald der erste Gegner den Abschnitt erreicht
                    if (k < _binMe.Count && k == _binMe.Count - 1) gap = _binMe[k] - t;
                }
            }
        while ((_binMe.Count + 1) * GapBin <= _pProgress)
        {
            int k = _binMe.Count;
            _binMe.Add(t);
            if (k < _binBot.Count && _binBot[k] >= 0f) gap = t - _binBot[k];   // Rückstand auf den Führenden
        }
        if (gap.HasValue) ShowGap(h, gap.Value);
        if (_clock < _hudNext) return;
        _hudNext = _clock + 0.2f;
        // Reihenfolge (Ziel zuerst, dann nach Weg) für die Namen vor/hinter mir (-1 = ich)
        var order = new List<(string name, float key, int bot)>();
        for (int i = 0; i < st.Count; i++) order.Add((st[i].Name ?? "?", st[i].Finished ? 1e9f - st[i].FinishTime : st[i].Traveled, i));
        order.Add((Championships.PlayerName, _pProgress, -1));
        order.Sort((a, b) => b.key.CompareTo(a.key));
        int me = order.FindIndex(o => o.bot < 0);
        // Abstand zu Vorder-/Hintermann (Sekunden) über die 25-m-Abschnitte
        string GapText(int bot, bool ahead)
        {
            if (bot < 0 || _botBins == null || bot >= _botBins.Length) return "";
            var bb = _botBins[bot];
            int k = ahead ? _binMe.Count - 1 : bb.Count - 1;
            if (k < 0 || k >= bb.Count || k >= _binMe.Count) return "";
            float g = ahead ? _binMe[k] - bb[k] : bb[k] - _binMe[k];
            if (g < 0.05f) return "";
            return "  " + (ahead ? "-" : "+") + g.ToString("0.0", De) + "s";
        }
        h.SetVariable("/HUD/position/player:PlayerName", Championships.PlayerName);
        h.SetVariable("/:PosPlayer", pos.ToString(CultureInfo.InvariantCulture)); h.SetVariable("/:ExpPlayer", ".");
        string? before = me > 0 ? order[me - 1].name + GapText(order[me - 1].bot, true) : null;
        string? after = me >= 0 && me + 1 < order.Count ? order[me + 1].name + GapText(order[me + 1].bot, false) : null;
        h.SetVariable("/HUD/position/player_before:PlyrNameBefore", before ?? "");
        h.SetVariable("/:PosBefore", (pos - 1).ToString(CultureInfo.InvariantCulture)); h.SetVariable("/:ExpBefore", ".");
        h.SetVariable("/HUD/position/player_after:PlyrNameAfter", after ?? "");
        h.SetVariable("/:PosAfter", (pos + 1).ToString(CultureInfo.InvariantCulture)); h.SetVariable("/:ExpAfter", ".");
        // Platzliste rechts unter der Zeit (Original-Liste /HUD/list_versus, neutrale Zeilen, eigene Zeile hervorgehoben)
        UpdateVersusList(h, st);
    }

    /// <summary>Liste der Fahrer rechts (Original /HUD/list_versus: line0..7, Name + Rückstand auf den Führenden).</summary>
    void UpdateVersusList(VmfPlayer h, List<RaceOpponents.Standing> st)
    {
        float LeaderAt(int bin)
        {
            float lt = float.MaxValue;
            if (bin >= 0 && bin < _binBot.Count && _binBot[bin] >= 0f) lt = _binBot[bin];
            if (bin >= 0 && bin < _binMe.Count) lt = MathF.Min(lt, _binMe[bin]);
            return lt;
        }
        var rows = new List<(string name, float key, float gap, bool me)>();
        for (int i = 0; i < st.Count; i++)
        {
            int bin = _botNextBin != null && i < _botNextBin.Length ? _botNextBin[i] - 1 : -1;
            float lt = LeaderAt(bin);
            float gap = bin >= 0 && lt < float.MaxValue && _botLastBinT != null ? _botLastBinT[i] - lt : 0f;
            rows.Add((st[i].Name ?? "?", st[i].Finished ? 1e9f - st[i].FinishTime : st[i].Traveled, gap, false));
        }
        {
            int bin = _binMe.Count - 1;
            float lt = LeaderAt(bin);
            float gap = bin >= 0 && lt < float.MaxValue ? _binMe[bin] - lt : 0f;
            rows.Add((Championships.PlayerName, _pProgress, gap, true));
        }
        rows.Sort((x, y) => y.key.CompareTo(x.key));
        if (!_versusOpen)
        {
            _versusOpen = true;
            h.SetVisible("/HUD/list_versus", true);
            h.GotoLabel("/HUD/list_versus", "open", true);
        }
        for (int i = 0; i < 8; i++)
        {
            string line = "/HUD/list_versus/line" + i;
            if (i >= rows.Count) { h.GotoLabel(line + "/content", "empty", false); continue; }
            var r = rows[i];
            string txt = r.name + (i > 0 && r.gap > 0.05f ? "  +" + r.gap.ToString("0.0", De) : "");
            bool changed = h.GetVariable(line + ":player_name") != txt;
            h.SetVariable(line + ":player_name", txt);
            h.SetVariable(line + ":pos", (i + 1).ToString(CultureInfo.InvariantCulture));
            string lab = r.me ? "blue_on" : "default";
            if (h.Label(line + "/content") != lab) h.GotoLabel(line + "/content", lab, false);
            var cur = h.Label(line);
            if (cur == "default" || cur == null) h.GotoLabel(line, "open", true);
            if (changed) h.CallClip(line + "/content/p", "SetTxtScroll");
        }
    }

    /// <summary>Abstand (s): positiv = Rückstand auf den Führenden, negativ = Vorsprung auf den Zweiten (ref_score, Karte "time").</summary>
    void ShowGap(VmfPlayer h, float gap)
    {
        int ms = (int)MathF.Round(MathF.Abs(gap) * 1000f);
        h.SetVariable("/:MinRef", (gap < 0f ? "-" : "+") + (ms / 60000).ToString(CultureInfo.InvariantCulture));
        h.SetVariable("/:SecRef", ((ms / 1000) % 60).ToString("00", CultureInfo.InvariantCulture));
        h.SetVariable("/:CentRef", ((ms / 10) % 100).ToString("00", CultureInfo.InvariantCulture));
        int sign = gap < 0f ? -1 : 1;
        if (sign != _gapSign)
        {
            if (_gapSign == 0) { h.SetVisible("/HUD/ref_score", true); h.GotoLabel("/HUD/ref_score", "open", true); }
            else h.GotoLabel("/HUD/ref_score", sign < 0 ? "up" : "down", true);
            h.GotoLabel("/HUD/ref_score/cart", "time", false);
            _gapSign = sign;
        }
    }

    void TrackBotRadar(List<RaceOpponents.Standing> st)
    {
        if (_botRadar == null || _botRadar.Length != st.Count)
        {
            _botRadar = new float[st.Count][]; _botRadarNext = new int[st.Count];
            for (int i = 0; i < st.Count; i++) _botRadar[i] = new float[_cpDist.Count];
        }
        for (int i = 0; i < st.Count; i++)
            while (_botRadarNext![i] < _cpDist.Count && st[i].Traveled >= _cpDist[_botRadarNext[i]])
                _botRadar[i][_botRadarNext[i]++] = st[i].Speed * 3.6f;
    }

    void ShowPosition(bool on)
    {
        var h = Hud;
        if (h == null || _def == null) { Minimap.RaceCount = 0; Minimap.RaceBlips.Clear(); return; }
        if (on && _def.Kind == RaceMode.Race)
        {
            h.SetVariable("/:cur_gm", "race");
            h.SetVisible("/HUD/position", true);
            h.GotoLabel("/HUD/position", "open", true);
            h.GotoLabel("/HUD/position/player", "default", false);
            h.GotoLabel("/HUD/position/player_before", "default", false);
            h.GotoLabel("/HUD/position/player_after", "default", false);
            _lastPos = -1;
            ResetRaceHud();
        }
        else
        {
            h.GotoLabel("/HUD/position", "default", false);
            h.GotoLabel("/HUD/ref_score", "default", false);
            h.GotoLabel("/HUD/list_versus", "default", false);
            Minimap.RaceCount = 0; Minimap.RaceBlips.Clear();
            ResetRaceHud();
        }
    }

    void SetPosition(int pos, int total)
    {
        var h = Hud;
        if (h == null) return;
        h.SetVariable("/:Pos", pos.ToString(CultureInfo.InvariantCulture));
        h.SetVariable("/:Exp", ".");
        h.SetVariable("/:NbPlayers", total.ToString(CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ Tempo (Durchschnitt über die Zeit)
    void UpdateSpeed(IGameModeContext ctx, float dt)
    {
        var vm = Vm; var veh = vm?.Current;
        if (vm == null || veh == null || _def == null) { EndRace(); return; }
        var h = Hud;
        float limit = MathF.Max(10f, _orig[_def.Name].limit / 1000f);
        if (_speedCountdown > 0f)
        {
            _speedCountdown -= dt;
            veh.BrakePedal = 1f; veh.HandBrake = 1f; veh.StartLock = true;
            if (_speedCountdown <= 0f)
            {
                vm.ExternalControl = false;
                veh.BrakePedal = 0f; veh.HandBrake = 0f; veh.StartLock = false;
                if (h != null)
                {
                    h.SetVisible("/HUD/time_cd", true); h.GotoLabel("/HUD/time_cd", "open", true);
                    // Tempo-Prüfung: Original-Anzeige speed_gauge (Wert + Mindest-/aktuelles Tempo, Balken = Zeit)
                    h.SetVisible("/HUD/speed_gauge", true); h.GotoLabel("/HUD/speed_gauge", "open", true);
                    h.SetVariable("/:m_Pts", "km/h Ø"); h.SetVariable("/:m_SpeedUnit", "km/h");
                    _gaugeBase = -1f;
                }
            }
            return;
        }
        if (ctx.Pressed(0x08)) { Log.Info("Rennen abgebrochen"); EndRace(); return; }
        _speedT += dt;
        _speedDist += MathF.Abs(veh.SpeedKmh) / 3.6f * dt;
        if (h != null)
        {
            float avgK = _speedT > 0.1f ? _speedDist / _speedT * 3.6f : 0f;
            h.SetVariable("/:SpeedScore", avgK.ToString("0", CultureInfo.InvariantCulture));
            h.SetVariable("/:speed_min", MathF.Abs(veh.SpeedKmh).ToString("0", CultureInfo.InvariantCulture));
            var comp = h.Find("/HUD/speed_gauge/gauge/completion");
            if (comp != null)
            {
                if (_gaugeBase < 0f) _gaugeBase = MathF.Abs(comp.Matrix.A) > 1e-4f ? comp.Matrix.A : 1f;
                comp.Matrix.A = _gaugeBase * System.Math.Clamp(_speedT / limit, 0f, 1f); comp.ScriptMatrix = true;
            }
            float left = MathF.Max(0f, limit - _speedT);
            int ms = (int)(left * 1000f);
            h.SetVariable("/:MinCd", (ms / 60000).ToString(CultureInfo.InvariantCulture));
            h.SetVariable("/:SecCd", ((ms / 1000) % 60).ToString("00", CultureInfo.InvariantCulture));
            h.SetVariable("/:CentCd", ((ms / 10) % 100).ToString("00", CultureInfo.InvariantCulture));
        }
        if (_speedT >= limit)
        {
            if (h != null) { h.GotoLabel("/HUD/time_cd", "default", false); h.GotoLabel("/HUD/speed_gauge", "default", false); h.GotoLabel("/_3_2_1_go", "default", false); h.SetVisible("/_3_2_1_go", false); }
            OnFinished(false);
        }
    }

    // ------------------------------------------------------------------ Ergebnis
    void OnFinished(bool failed)
    {
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
        Log.Info("Meisterschaftsrennen " + _def.Name + ": " + info + " | " + cupInfo);
        _phase = Phase.Results;
        _resultAt = _clock + 1.2f;
        Championships.Refresh();
    }

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
                    float sum = 0f; foreach (var v in _playerRadar) sum += v;
                    me.Value = failed ? 0f : sum;
                    if (st.Count > 0)
                    {
                        if (_botRadar == null) TrackBotRadar(st);
                        for (int i = 0; i < st.Count; i++)
                        {
                            float s2 = 0f;
                            for (int k = 0; k < _cpDist.Count; k++) s2 += _botRadar![i][k] > 0f ? _botRadar[i][k] : st[i].Def.MaxSpeed * 3.6f * 0.75f;
                            rows.Add(new Row { Name = st[i].Name, Car = st[i].Car, Value = s2, Finished = true, BotIndex = i });
                        }
                    }
                    else AddGeneratedBots(rows, sum > 0f ? sum : 600f, true);
                    rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                    break;
                }
            case RaceMode.Speed:
                {
                    me.Value = _speedT > 0.1f ? _speedDist / _speedT * 3.6f : 0f;
                    AddGeneratedBots(rows, 105f, true);
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
        string Val(Row r) => time ? (r.Value >= float.MaxValue * 0.5f ? "--'--\"--" : RaceHud.FormatRaceTime(r.Value))
                                  : r.Value.ToString("0.0", De) + " km/h";
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
        Log.Info("Meisterschaft: zurück zum Treffpunkt " + cup.Name);
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
        if (h != null) { h.GotoLabel("/HUD/speed_gauge", "default", false); h.GotoLabel("/HUD/time_cd", "default", false); }
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
