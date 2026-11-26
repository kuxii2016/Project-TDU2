namespace Paradise.Core.Engine;

/// <summary>Zeitwerte eines Frames.</summary>
public struct GameTime
{
    public float DeltaTime;      // Sekunden seit letztem Frame (gekappt)
    public float FixedDeltaTime; // fester Physik-Schritt
    public double TotalTime;     // Sekunden seit Start
    public long FrameCount;
    /// <summary>Vollständig gerenderte Bilder, seit das Fenster sichtbar ist (0, solange es versteckt ist).</summary>
    public int VisibleFrames;
    /// <summary>Anteil des angefangenen Physikschritts (0..1) für die Interpolation.</summary>
    public float FixedAlpha;
}