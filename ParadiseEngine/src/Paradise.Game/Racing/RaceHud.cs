// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceHud.cs).
using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Math;
using Paradise.Core.Racing;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using System.Globalization;

namespace Paradise.Game.Racing;

/// <summary>
/// Renn-/Prüfungsanzeige über das Original-HUD (frontend/&lt;res&gt;/hud.bnk → hud.vmf), Pfade und Variablen wie in der EXE
/// (Zeitfahr-Komponente bei 0xb4cc80 / 0xb4ee00 / 0xb4f0c0):
///   /_3_2_1_go          Labels 3_go / 2_go / 1_go / go (Start-Countdown)
///   /HUD/time           laufende Zeit  /:Min /:Sec /:Cent              (ohne Zeitlimit)
///   /HUD/ref_score      Referenzzeit   /:MinRef /:SecRef /:CentRef, Unterclip cart Label "time"
///   /HUD/time_cd        Restzeit       /:MinCd /:SecCd /:CentCd       (Zeitlimit "ChCD"; time und ref_score aus)
///   /HUD/time_out       letzte 5 s des Limits: Label "on", /:time_out = Sekunden
///   /HUD/checkpoints    /:NbCheckpoints /:TotalCheckpoints /:CheckTime /:Gap /:IsCheckGreen /:IsLapOver, Label "chekpoint"
///   /HUD/laps           /:NbTurns /:TotalTurns (/:LastLap)  – nur Rundkurse
///   /:IsPenalty         Zeitstrafe erhalten (Original-Anzeige des HUD-Films; Unitys zusätzliche IMGUI-Einblendung entfällt – nur Original-Oberflächen)
/// Rennlogik bleibt in <see cref="RaceManager"/>.
///
/// Welche Teile öffnen, entscheidet wie in der exe die Komponentenliste der Spielart (CGameMech 0xb3e790, Start in der
/// Reihenfolge der Komponenten-Felder 0x84..0xa0, exe 0xb40143):
///   CHECKPOINTS (alle außer Radar/Tempo, 0xb4f500): /HUD/time, /HUD/checkpoints, /HUD/laps (Rundkurs mit nblp != 1),
///                /HUD/position (nur Rennen), /:CheckTime = "", /:Gap = "", /:IsCheckGreen = "2".
///   TA (nur Zeitfahren solo, 0xb602e0): /HUD/ref_score mit der Referenzzeit.
///   TRAPS (Radar, 0xb647b0 / 0xb63d10): /HUD/radars, Zeilen 1..8 (nur so viele wie Radare), /HUD/position, /:NbRadars, /:Score.
///   TIME (0xb4d490): /HUD/time.   COUNTDOWN (0xb4cc80): bei Zeitlimit /HUD/time_cd auf, /HUD/time und /HUD/ref_score zu.
/// Vorher setzt die exe /:cur_gm und /:whatcha (<see cref="ChallengeModes.ApplyHud"/>), danach richtet sich der Film aus.
/// Schließen = nur unsichtbar (exe 0xb3c6b0 ShowClip(false)), nicht "default".
/// </summary>
public sealed class RaceHud
{
    public RaceManager? Race;
    /// <summary>Minimap des Fahrzeug-Managers: besitzt hud.bnk, zeichnet die Rennstrecke.</summary>
    public Minimap? Minimap;

    const string CountdownClip = "/_3_2_1_go", TimeClip = "/HUD/time", TimeCdClip = "/HUD/time_cd", RefClip = "/HUD/ref_score",
                 TimeOutClip = "/HUD/time_out", CheckClip = "/HUD/checkpoints", LapsClip = "/HUD/laps";

    bool _shown;
    bool _minimapRoute;   // Straßenband aus => Route auf der Minimap
    int _lastTimeOut = -1;
    RaceManager? _bound;

    VmfPlayer? H => Minimap?.Hud;

    public void Bind()
    {
        if (Race == null || _bound == Race) return;
        Unbind();
        _bound = Race;
        Race.Prepared += OnPrepared;
        Race.Started += OnStarted;
        Race.CheckpointPassed += OnCheckpoint;
        Race.Penalty += OnPenalty;
        Race.RadarPassed += OnRadar;
        Race.RadarRecordByOpponent += OnRadarRecordByOpponent;
        Race.Completed += OnEnd;
        Race.Failed += OnEnd;
        Race.Aborted += OnEnd;
    }

    public void Unbind()
    {
        if (_bound == null) return;
        _bound.Prepared -= OnPrepared;
        _bound.Started -= OnStarted;
        _bound.CheckpointPassed -= OnCheckpoint;
        _bound.Penalty -= OnPenalty;
        _bound.RadarPassed -= OnRadar;
        _bound.RadarRecordByOpponent -= OnRadarRecordByOpponent;
        _bound.Completed -= OnEnd;
        _bound.Failed -= OnEnd;
        _bound.Aborted -= OnEnd;
        _bound = null;
    }

    /// <summary>Anzeigen und Strecke ausblenden, angemeldet bleiben (Prüfung von außen beendet).</summary>
    public void Hide() { HideAll(); ClearRoute(); }

    /// <summary>Unity OnDisable.</summary>
    public void Disable() { Unbind(); HideAll(); ClearRoute(); }

    void ClearRoute()
    {
        _minimapRoute = false;
        Minimap?.SetRaceRoute(null);
        Minimap?.SetRacePortals(null);
        Minimap.ClearObjectives();   // exe 0xb639b0 / Ende der Komponenten: Ziel-Symbole leeren
    }

    void OnPrepared(RaceManager r)
    {
        var race = r.Race!;
        if (Minimap != null)
        {
            // Radar (gmth = 1): kein Band, keine Route – die Radare zeigt die Minimap als Ziel-Symbole (radar_on/off, StartRadars).
            Minimap.SetRacePortals(null);
            Minimap.ClearObjectives();
            // Straßenroute wie bisher auch für das Band sicherstellen; Radar braucht sie nicht (nur Dots).
            if (race.Kind != RaceMode.SpeedTrap && (race.RoadPath == null || race.RoadPath.Count < 2) && RaceRoadPath.FindLanes() != null)
            {
                try { RaceRoadPath.Compute(race); }
                catch (Exception ex) { Log.Warn("Straßenroute (Renn-HUD): " + ex.Message); }
            }
            _minimapRoute = RaceConfig.MinimapRoute(race);
            if (_minimapRoute)
            {
                Minimap.SetRaceRoute(race);
                Minimap.SetRaceProgress(r.NextCheckpointIndex);
            }
            else Minimap.SetRaceRoute(null);
        }
        FrontendSounds.Preload("three", "two", "one", "go", "chptpass", "chptrmv", "time", "time2", "wrong");
        var h = H; if (h == null) return;
        FrontendSounds.Attach(h);   // HUD meldet sndp:three/two/one/go, sndp:chptpass, sndp:time ...
        _shown = true; _lastTimeOut = -1;
        ChallengeModes.ApplyHud(h, race);
        h.SetVisible("/HUD/score", false);   // exe 0xb400e3
        h.SetVisible(CountdownClip, true);
        h.GotoLabel(CountdownClip, "3_go", true);
        // Anzeigen vorbereiten (noch geschlossen)
        SetTimeVars(h, "", 0f);
        h.SetVariable("/:NbCheckpoints", "0");
        h.SetVariable("/:TotalCheckpoints", r.TargetCount.ToString(CultureInfo.InvariantCulture));
        h.SetVariable("/:CheckTime", "");
        h.SetVariable("/:Gap", "");
        h.SetVariable("/:NbTurns", "1");
        h.SetVariable("/:TotalTurns", race.Laps.ToString(CultureInfo.InvariantCulture));
        h.SetValue("/:LastLap", race.IsCircuit && race.Laps <= 1);
        h.SetValue("/:IsPenalty", false);
        if (race.TargetTimeMs > 0)
        {
            Split(race.TargetTimeMs / 1000f, out var m, out var s, out var c);
            h.SetVariable("/:MinRef", m); h.SetVariable("/:SecRef", s); h.SetVariable("/:CentRef", c);
        }
    }

    void OnStarted(RaceManager r)
    {
        var h = H; if (h == null) return;
        var race = r.Race!;
        bool trap = race.Kind == RaceMode.SpeedTrap, speed = race.Kind == RaceMode.Speed;
        // CHECKPOINTS (exe 0xb4f500)
        if (!trap && !speed)
        {
            ChallengeModes.ShowClip(h, TimeClip, true);
            ChallengeModes.ShowClip(h, CheckClip, true);
            if (race.IsCircuit && race.LapsRaw != 1) ChallengeModes.ShowClip(h, LapsClip, true);
            if (race.Kind == RaceMode.Race) ChallengeModes.ShowClip(h, "/HUD/position", true);
            h.SetVariable("/:CheckTime", "");
            h.SetVariable("/:Gap", "");
            h.SetVariable("/:IsCheckGreen", "2");
            SetNextObjective(r);
        }
        // TA (exe 0xb602e0): nur Zeitfahren, nur solo – Referenzzeit
        if (race.Kind == RaceMode.TimeAttack && race.TargetTimeMs > 0)
        {
            ChallengeModes.ShowClip(h, RefClip, true);
            h.GotoLabel(RefClip + "/cart", "time", false);
        }
        // TRAPS (exe 0xb647b0)
        if (trap) StartRadars(h, r);
        // TIME (exe 0xb4d490)
        ChallengeModes.ShowClip(h, TimeClip, true);
        // COUNTDOWN (exe 0xb4cc80): Zeitlimit -> Restzeit statt Zeit/Referenz
        if (race.HasTimeLimit && race.TimeLimitMs > 0)
        {
            ChallengeModes.ShowClip(h, TimeCdClip, true);
            ChallengeModes.ShowClip(h, TimeClip, false);
            ChallengeModes.ShowClip(h, RefClip, false);
        }
    }

    // ------------------------------------------------------------------ Radar (exe GMCompTraps)

    /// <summary>exe 0xb647b0 + 0xb63d10: Radar-Anzeige öffnen, Zeilen 1..8 vorbereiten.</summary>
    void StartRadars(VmfPlayer h, RaceManager r)
    {
        int n = r.Race!.RadarCount;
        ChallengeModes.ShowClip(h, "/HUD/radars", true);
        h.SetVariable("/:m_SpeedUnit", ChallengeModes.UnitName);
        h.SetVariable("/:NbRadars", n.ToString(CultureInfo.InvariantCulture));
        h.SetVariable("/:Score", "0");
        h.SetVariable("/:ScoreUnit", ChallengeModes.UnitName);
        h.SetVariable("/:TimeRef_IG", "");
        h.SetVisible("/HUD/endRace", false);
        // /HUD/flash gehört zum HUD-Film (im Original immer sichtbar, Bild 0 leer); unser Minimap-HUD blendet ihn sonst aus.
        h.SetVisible("/HUD/flash", true);
        h.GotoFrameNumber("/HUD/flash", 0);
        UpdateRadarObjectives(r);
        for (int i = 1; i <= 8; i++)
        {
            string row = "/HUD/radars/" + i;
            bool on = i <= n;
            h.SetVisible(row, on);
            if (!on) continue;
            h.GotoLabel(row, "default", false);
            h.SetVariable(row + ":Radar", "---");
            h.SetVariable(row + ":PosRadar", "(---)");
        }
        ChallengeModes.ShowClip(h, "/HUD/position", true);
    }

    /// <summary>
    /// exe 0xb648e0: Durchfahrt eines Radars durch den Spieler. Ab dem Mindesttempo blitzt es (/HUD/flash "on", danach
    /// /:IsFlashOK = 0 – der Film spielt damit "fl_radar"). Ein neuer eigener Höchstwert wird gespeichert; ist er zugleich der
    /// Bestwert aller Teilnehmer, steht er in Klammern (/HUD/radars/n:PosRadar "(%d)", 0xb63ac0) und die Zeile wird "good"
    /// (0xb638d0). Danach der eigene Wert in der Zeile (:Radar "%d", 0xb639d0) mit "modif_average". Unter dem Mindesttempo ohne
    /// bisherigen Wert: Zeile "bad". /:Score = Durchschnitt "%.2f" (0xb64c06). Das Minimap-Symbol wird "radar_off" (0xb63920).
    /// </summary>
    void OnRadar(RaceManager r, int radar, float v, bool above, bool improved, bool record)
    {
        var h = H; if (h == null) return;
        string row = "/HUD/radars/" + (radar + 1);
        if (above) h.GotoLabel("/HUD/flash", "on", true);
        if (improved)
        {
            if (record)
            {
                h.SetVariable(row + ":PosRadar", "(" + ChallengeModes.UnitInt(v) + ")");
                h.GotoLabel(row, "good", true);
            }
            h.SetVariable(row + ":Radar", ChallengeModes.UnitInt(v));
            h.GotoLabel("/HUD/radars", "modif_average", true);
        }
        if (above) h.SetVariable("/:IsFlashOK", "0");
        else if (radar < r.RadarSpeeds.Length && r.RadarSpeeds[radar] <= 0f)
            h.GotoLabel(row, "bad", true);
        h.SetVariable("/:Score", r.TrapAverageKmh.ToString("0.00", CultureInfo.InvariantCulture));
        UpdateRadarObjectives(r);
    }

    /// <summary>exe 0xb648e0 für einen Gegner: neuer Bestwert an einem Radar – "(%d)" in der Zeile, Zeile "bad" (0xb638d0).</summary>
    void OnRadarRecordByOpponent(RaceManager r, int radar, float v)
    {
        var h = H; if (h == null) return;
        string row = "/HUD/radars/" + (radar + 1);
        h.SetVariable(row + ":PosRadar", "(" + ChallengeModes.UnitInt(v) + ")");
        h.GotoLabel(row, "bad", true);
    }

    /// <summary>
    /// exe 0xb63920: je Radar (Entity CheckPointType 4, höchstens 8) ein Minimap-Symbol – "radar_on", solange der Spieler es noch
    /// nicht über dem Mindesttempo geblitzt hat, sonst "radar_off"; Nummer = Index des Tors (Radar 1 = Tor 1), Größe 1.
    /// </summary>
    void UpdateRadarObjectives(RaceManager r)
    {
        var race = r.Race; if (race == null) return;
        int slot = 0;
        for (int i = 1; i < race.Checkpoints.Count && slot < 8; i++, slot++)
        {
            bool flashed = i - 1 < r.RadarSpeeds.Length && r.RadarSpeeds[i - 1] > 0f;
            Minimap.SetObjective(slot, race.Checkpoints[i].Position, flashed ? "radar_off" : "radar_on", i);
        }
        for (; slot < 8; slot++) Minimap.ClearObjective(slot);
    }

    /// <summary>
    /// exe CHECKPOINTS 0xb50740: Platz 0 der Minimap-Symbole = nächstes Tor, Label nach dem Checkpoint-Typ (1 "start",
    /// 2 "finish", sonst "checkpoint"), ohne Nummer.
    /// </summary>
    void SetNextObjective(RaceManager r)
    {
        var race = r.Race; if (race == null) return;
        int next = r.NextCheckpointIndex;
        if (next < 0 || next >= race.Checkpoints.Count) { Minimap.ClearObjective(0); return; }
        var cp = race.Checkpoints[next];
        Minimap.SetObjective(0, cp.Position, cp.IsStart && !race.IsCircuit ? "start" : cp.IsFinish ? "finish" : "checkpoint");
    }

    // exe 0xb63e50 (nächstes Radar -> /HUD/radars/cur_speed + "new_speedtrap") hat im Original keine Wirkung: hud.vmf hat
    // unter /HUD/radars keinen Clip "cur_speed" (Kinder: 1..8, average), die Suche schlägt fehl und nichts wird gespielt.

    void OnCheckpoint(RaceManager r, int seqIndex)
    {
        if (_minimapRoute) Minimap?.SetRaceProgress(r.NextCheckpointIndex);
        var race = r.Race!;
        if (race.Kind == RaceMode.SpeedTrap) return;   // Radar hat keine CHECKPOINTS-Komponente (exe 0xb3e9f9), Anzeige über OnRadar
        SetNextObjective(r);
        var h = H; if (h == null) return;
        h.SetVariable("/:NbCheckpoints", r.PassedCount.ToString(CultureInfo.InvariantCulture));
        h.SetVariable("/:CheckTime", FormatRaceTime(r.ElapsedSeconds + r.PenaltyMs / 1000f));
        // exe CHECKPOINTS 0xb4ed00: Abstand zur Referenz-Zwischenzeit (ig2 "CpTs", Eintrag 0 = Startlinie); ohne Referenz
        // /:Gap = " " und /:IsCheckGreen = "2". Grün ("1") bei Abstand ≤ 0.
        int k = r.PassedCount;
        if (k > 0 && k < race.RefSplitsMs.Count)
        {
            float gap = r.ElapsedSeconds + r.PenaltyMs / 1000f - race.RefSplitsMs[k] / 1000f;
            h.SetVariable("/:Gap", (gap <= 0f ? "-" : "+") + FormatRaceTime(MathF.Abs(gap)));
            h.SetVariable("/:IsCheckGreen", gap <= 0f ? "1" : "0");
        }
        else { h.SetVariable("/:Gap", " "); h.SetVariable("/:IsCheckGreen", "2"); }
        bool lapOver = race.IsCircuit && race.TargetsPerLap > 0 && r.PassedCount % race.TargetsPerLap == 0;
        h.SetVariable("/:IsLapOver", lapOver ? "1" : "0");
        h.GotoLabel(CheckClip, "chekpoint", true);
        if (race.IsCircuit)
        {
            h.SetVariable("/:NbTurns", r.Lap.ToString(CultureInfo.InvariantCulture));
            h.SetValue("/:LastLap", r.Lap >= race.Laps);
        }
    }

    void OnPenalty(RaceManager r, int ms)
    {
        FrontendSounds.Play("wrong");
        H?.SetValue("/:IsPenalty", true);
        Log.Info("Zeitstrafe +" + FormatRaceTimeMs(ms) + " (gesamt " + FormatRaceTimeMs(r.PenaltyMs) + ")");
    }

    void OnEnd(RaceManager r)
    {
        ClearRoute();
        HideAll();
    }

    void HideAll()
    {
        var h = H;
        if (h == null || !_shown) return;
        _shown = false;
        foreach (var c in new[] { TimeClip, TimeCdClip, RefClip, CheckClip, LapsClip, TimeOutClip })
            if (!h.GotoLabel(c, "default", false)) h.GotoLabel(c, "off", false);
        h.GotoLabel(TimeOutClip, "off", false);
        h.GotoLabel(CountdownClip, "default", false);
        h.SetVisible(CountdownClip, false);
        // Radar/Platzierung (exe: Komponenten schließen nur unsichtbar)
        h.SetVisible("/HUD/radars", false);
        h.SetVisible("/HUD/flash", false);
        h.SetVisible("/HUD/position", false);
        h.SetVisible("/HUD/eliminator", false);
    }

    static void Show(VmfPlayer h, string clip, bool on)
    {
        if (on) { h.SetVisible(clip, true); h.GotoLabel(clip, "open", true); }
        else h.GotoLabel(clip, "default", false);
    }

    /// <summary>Jedes Bild (Unity Update).</summary>
    public void Update()
    {
        if (Race != null && _bound != Race) Bind();
        if (Race == null || !_shown) return;
        var h = H; if (h == null) return;
        if (Race.State == RaceState.Countdown) return;
        if (!Race.Running) return;
        var race = Race.Race!;
        if (race.HasTimeLimit)
        {
            float left = Race.TimeLeft;
            SetTimeVars(h, "Cd", left);
            int sec = Mathf.CeilToInt(left);
            if (sec <= 5 && sec >= 1 && sec != _lastTimeOut)
            {
                if (_lastTimeOut < 0) h.GotoLabel(TimeOutClip, "on", true);
                _lastTimeOut = sec;
                h.SetVariable("/:time_out", sec.ToString(CultureInfo.InvariantCulture));
            }
        }
        else SetTimeVars(h, "", Race.ElapsedSeconds + Race.PenaltyMs / 1000f);
    }

    static void SetTimeVars(VmfPlayer h, string suffix, float seconds)
    {
        Split(seconds, out var m, out var s, out var c);
        h.SetVariable("/:Min" + suffix, m);
        h.SetVariable("/:Sec" + suffix, s);
        h.SetVariable("/:Cent" + suffix, c);
    }

    static void Split(float seconds, out string min, out string sec, out string cent)
    {
        if (seconds < 0f) seconds = 0f;
        int totalMs = (int)(seconds * 1000f + 0.5f);
        min = (totalMs / 60000).ToString(CultureInfo.InvariantCulture);
        sec = ((totalMs / 1000) % 60).ToString("00", CultureInfo.InvariantCulture);
        cent = ((totalMs / 10) % 100).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Original-Zeitformat der Rennen: Minuten'sekunden"Hundertstel, z.B. 1'23"45.
    /// (Aus den Formatstrings "%d'%02d"%02d" bzw. "%3d'%02d"%02d" der ausführbaren Datei abgeleitet.)
    /// </summary>
    public static string FormatRaceTime(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int totalMs = (int)(seconds * 1000f + 0.5f);
        int cc = (totalMs / 10) % 100;
        int ss = (totalMs / 1000) % 60;
        int mm = totalMs / 60000;
        return string.Format(CultureInfo.InvariantCulture, "{0}'{1:00}\"{2:00}", mm, ss, cc);
    }

    public static string FormatRaceTimeMs(int ms) => FormatRaceTime(ms / 1000f);
}
