using Paradise.Core.Flash;
using Paradise.Core.Racing;
using System.Globalization;

namespace Paradise.Game.Racing;

/// <summary>
/// Tempo-Prüfung (gmth 10) wie die Komponente GMCompSpeed der exe (Klasse VTable 0x14f5878, Konstruktor 0xb60db0):
///  - Init (0xb60e00): Punkte 0, Mindesttempo = ig2 "tras" / 1000 (m/s), Referenz = ig2 "SPPG" / 1000.
///  - Start (0xb61280): /HUD/speed_gauge öffnen (nur bei Mindesttempo > 0), /:speed_min = Mindesttempo in der Anzeigeeinheit
///    (ganzzahlig), /:m_SpeedUnit, /HUD/position und /HUD/ref_points öffnen.
///  - Jedes Bild (0xb60a20): v = Fahrzeugtempo (m/s). Balken /HUD/speed_gauge/gauge/completion (301 Bilder) =
///    Abbildung (0x5ab960, geklemmt) v ∈ [0, vmin] -> 1..100, v ∈ [vmin, 113,889 m/s (410 km/h)] -> 101..300.
///    Punkte += (v − vmin) · dt, nur wenn v ≥ vmin (also Meter über dem Mindesttempo). /:speed_max = v, /:SpeedScore = Punkte (ganzzahlig).
///  - Das Ende kommt mit dem Zeitlimit (ChCD/CtDw, Komponente COUNTDOWN).
/// Text der Spielart (exe 0x88e): „Erreichen Sie zum Punkten das minimale Tempo. Wer die meisten Punkte holt, gewinnt.“
/// </summary>
public sealed class SpeedChallenge
{
    /// <summary>Obere Grenze des Balkens (exe 0x14f57b0: 113,889 m/s = 410 km/h).</summary>
    const float GaugeTop = 113.889f;

    public float MinSpeed { get; private set; }     // m/s
    public float RefPoints { get; private set; }
    public float Score { get; private set; }        // Meter über dem Mindesttempo
    int _lastFrame = -1, _lastScore = -1, _lastSpeed = -1;

    /// <summary>Init + Start der Komponente (HUD-Teile öffnen). Vor dem Countdown aufrufen.</summary>
    public void Start(VmfPlayer? h, RaceDefinition def)
    {
        MinSpeed = def.SpeedTrapMin;
        RefPoints = def.SpeedPointsRef;
        Score = 0f; _lastFrame = -1; _lastScore = -1; _lastSpeed = -1;
        if (h == null) return;
        if (MinSpeed > 0f)
        {
            ChallengeModes.ShowClip(h, "/HUD/speed_gauge", true);
            h.SetVariable("/:speed_min", ChallengeModes.UnitInt(MinSpeed));
        }
        h.SetVariable("/:m_SpeedUnit", ChallengeModes.UnitName);
        h.SetVariable("/:SpeedScore", "0");
        h.SetVariable("/:speed_max", "0");
        ChallengeModes.ShowClip(h, "/HUD/position", true);
        ChallengeModes.ShowClip(h, "/HUD/ref_points", true);
        // Referenz der Prüfung in der Punkte-Karte (cart "points", /:SpeedRef). Die exe füllt /:SpeedRef nur in der
        // Mehrspieler-Variante (0xb608cb); solo bleibt die Karte wie vom Film geöffnet. [Referenz solo vermutet]
        h.GotoLabel("/HUD/ref_points/cart", "points", false);
        h.SetVariable("/:SpeedRef", ((int)RefPoints).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Ein Bild während des Laufs (v = Fahrzeugtempo in m/s).</summary>
    public void Update(VmfPlayer? h, float v, float dt)
    {
        v = MathF.Abs(v);
        if (v >= MinSpeed) Score += (v - MinSpeed) * dt;
        if (h == null) return;
        float comp = v < MinSpeed ? Remap(v, 0f, MinSpeed, 1f, 100f) : Remap(v, MinSpeed, GaugeTop, 101f, 300f);
        int frame = (int)comp;
        if (frame != _lastFrame) { _lastFrame = frame; h.GotoFrameNumber("/HUD/speed_gauge/gauge/completion", frame); }
        int unit = (int)ChallengeModes.ToUnit(v);
        if (unit != _lastSpeed) { _lastSpeed = unit; h.SetVariable("/:speed_max", unit.ToString(CultureInfo.InvariantCulture)); }
        int s = (int)Score;
        if (s != _lastScore) { _lastScore = s; h.SetVariable("/:SpeedScore", s.ToString(CultureInfo.InvariantCulture)); }
    }

    /// <summary>Ende: Anzeige schließen (exe [19] 0xb5f140: HUD/speed_gauge "close").</summary>
    public void Close(VmfPlayer? h)
    {
        if (h == null) return;
        h.GotoLabel("/HUD/speed_gauge", "close", true);
        h.SetVisible("/HUD/ref_points", false);
        h.SetVisible("/HUD/position", false);
    }

    /// <summary>exe 0x5ab960: lineare Abbildung, auf den Zielbereich geklemmt.</summary>
    static float Remap(float x, float inLo, float inHi, float outLo, float outHi)
    {
        float t = inHi - inLo != 0f ? (x - inLo) / (inHi - inLo) : 0f;
        float r = outLo + t * (outHi - outLo);
        return System.Math.Clamp(r, MathF.Min(outLo, outHi), MathF.Max(outLo, outHi));
    }
}
