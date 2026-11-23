namespace Paradise.Core.Render;

/// <summary>
/// Zusatz-Nachbearbeitung der Engine (NICHT im Original: TDU2 hat keinen eigenen Bloom- und keinen AO-Durchgang, postfx_spec §0 –
/// "Bloom" steckt dort nur in der Photoshop-Farbtabelle, siehe <see cref="PhotoshopFx"/>). Auf Wunsch eingebaut; Stärken und
/// Schalter kommen aus Game/Config/PostFxConfig.cs, das Entwicklermenü (F1 > Rendering) ändert sie zur Laufzeit.
/// Umsetzung: Render/Vulkan/VulkanRendererPostFx.cs + PostFxShaders.cs, im selben Ablauf wie Photoshop vor dem HUD:
///   AO   – Bildschirmraum-Verdeckung aus der Tiefe (halbe Auflösung, 12 Abtastungen, danach Gauß), Bild × lerp(1, ao, Stärke),
///          vor der Farbtabelle (wirkt also wie im Bild "eingebacken");
///   Bloom – helle Stellen (über der Schwelle) auf ein Viertel verkleinert, zweimal Gauß in wachsender Weite, per "Negativ
///          multiplizieren" (Screen) nach der Farbtabelle aufs Bild gelegt.
/// Gerechnet wird wie Photoshop auf den gespeicherten 8-Bit-Bildwerten (UNORM-Sicht).
/// </summary>
public static class PostFx
{
    public static bool BloomEnabled;
    /// <summary>Stärke des Leuchtens (0 = aus, 1 = volle Helligkeit der hellen Stellen dazu).</summary>
    public static float BloomIntensity = 0.35f;
    /// <summary>Ab dieser Helligkeit (0–1, größter Farbkanal) leuchtet ein Bildpunkt.</summary>
    public static float BloomThreshold = 0.75f;
    /// <summary>Weicher Übergang unter der Schwelle (0 = harte Kante).</summary>
    public static float BloomKnee = 0.15f;
    /// <summary>Weite der Unschärfe (Schrittweite der Gauß-Durchgänge, 1 = Grundwert).</summary>
    public static float BloomRadius = 1f;
    /// <summary>Leuchten der selbstleuchtenden Materialien (Bloom-Maske im Alpha des Weltbilds: Autolampen, Flares, Glow-Sprites,
    /// Neon/Fenster, Spot-Hologramme) unabhängig von der Schwelle. 0 = nur Schwelle.</summary>
    public static float BloomEmissive = 1.5f;
    /// <summary>Selbstleuchtende Materialien leuchten erst ab dieser Helligkeit (0–1, größter Farbkanal im Bild). Die Maske der
    /// additiven Teile (Lichtkegel/-scheine der Laternen, Glow-Sprites) deckt deren ganze Fläche ab – auch die schwachen Ränder und
    /// den Hintergrund dahinter. Ohne diese Schwelle leuchtete die ganze Fläche mit. 0 = wie vorher (alles mit Maske leuchtet).</summary>
    public static float BloomEmissiveThreshold = 0.55f;

    public static bool AoEnabled;
    /// <summary>Stärke der Verdunkelung (0 = aus, 1 = volle Verdeckung).</summary>
    public static float AoIntensity = 0.6f;
    /// <summary>Suchradius in Metern (wie weit Nachbarflächen verdecken).</summary>
    public static float AoRadius = 1.2f;
    /// <summary>Kleine Neigung, damit ebene Flächen nicht sich selbst verdecken (0–0,5).</summary>
    public static float AoBias = 0.05f;
    /// <summary>Ab dieser Entfernung (m) blendet die Verdeckung aus (Gelände in der Ferne).</summary>
    public static float AoMaxDistance = 150f;

    /// <summary>Diagnose (F1 > Rendering): 0 aus, 1 nur das AO-Bild zeigen (weiß = frei, dunkel = verdeckt), 2 nur das Bloom-Bild,
    /// 3 nur die Bloom-Maske (weiß = leuchtet).</summary>
    public static int DebugView;
}
