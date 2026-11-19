// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceHud.cs).
using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Math;
using Paradise.Core.Vehicles;
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
/// </summary>
public sealed class RaceHud
{
    public RaceManager? Race;
    /// <summary>Minimap des Fahrzeug-Managers: besitzt hud.bnk, zeichnet die Rennstrecke.</summary>
    public Minimap? Minimap;

    const string CountdownClip = "/_3_2_1_go", TimeClip = "/HUD/time", TimeCdClip = "/HUD/time_cd", RefClip = "/HUD/ref_score",
                 TimeOutClip = "/HUD/time_out", CheckClip = "/HUD/checkpoints", LapsClip = "/HUD/laps";

    bool _shown;
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
        _bound.Completed -= OnEnd;
        _bound.Failed -= OnEnd;
        _bound.Aborted -= OnEnd;
        _bound = null;
    }

    /// <summary>Anzeigen und Strecke ausblenden, angemeldet bleiben (Prüfung von außen beendet).</summary>
    public void Hide() { HideAll(); Minimap?.SetRaceRoute(null); }

    /// <summary>Unity OnDisable.</summary>
    public void Disable() { Unbind(); HideAll(); Minimap?.SetRaceRoute(null); }

    void OnPrepared(RaceManager r)
    {
        var race = r.Race!;
        if (Minimap != null)
        {
            // Straßenroute sicherstellen, BEVOR die Minimap sie übernimmt: ohne RoadPath
            // fällt RaceRoute.Build auf die gerade Linie durch die Tore zurück.
            if ((race.RoadPath == null || race.RoadPath.Count < 2) && RaceRoadPath.FindLanes() != null)
            {
                try { RaceRoadPath.Compute(race); }
                catch (Exception ex) { Log.Warn("Straßenroute (Renn-HUD): " + ex.Message); }
            }
            Minimap.SetRaceRoute(race);
            Minimap.SetRaceProgress(r.NextCheckpointIndex);
        }
        FrontendSounds.Preload("three", "two", "one", "go", "chptpass", "chptrmv", "time", "time2", "wrong");
        var h = H; if (h == null) return;
        FrontendSounds.Attach(h);   // HUD meldet sndp:three/two/one/go, sndp:chptpass, sndp:time ...
        _shown = true; _lastTimeOut = -1;
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
        if (race.HasTimeLimit)
        {
            Show(h, TimeCdClip, true); Show(h, TimeClip, false); Show(h, RefClip, false);
        }
        else
        {
            Show(h, TimeClip, true); Show(h, TimeCdClip, false);
            if (race.TargetTimeMs > 0) { Show(h, RefClip, true); h.GotoLabel(RefClip + "/cart", "time", false); }
        }
        Show(h, CheckClip, true);
        if (race.IsCircuit && race.Laps > 1) Show(h, LapsClip, true);
    }

    void OnCheckpoint(RaceManager r, int seqIndex)
    {
        Minimap?.SetRaceProgress(r.NextCheckpointIndex);
        var h = H; if (h == null) return;
        var race = r.Race!;
        h.SetVariable("/:NbCheckpoints", r.PassedCount.ToString(CultureInfo.InvariantCulture));
        h.SetVariable("/:CheckTime", FormatRaceTime(r.ElapsedSeconds + r.PenaltyMs / 1000f));
        // Abstand zur gleichmäßig verteilten Referenzzeit (Original: Abstand zur Referenz-Splitzeit "CpTs")
        if (race.TargetTimeMs > 0 && r.TargetCount > 0)
        {
            float refT = race.TargetTimeMs / 1000f * r.PassedCount / r.TargetCount;
            float gap = r.ElapsedSeconds + r.PenaltyMs / 1000f - refT;
            h.SetVariable("/:Gap", (gap <= 0f ? "-" : "+") + FormatRaceTime(MathF.Abs(gap)));
            h.SetVariable("/:IsCheckGreen", gap <= 0f ? "1" : "0");
        }
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
        Minimap?.SetRaceRoute(null);
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
