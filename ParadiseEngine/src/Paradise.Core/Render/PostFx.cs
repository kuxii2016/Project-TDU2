namespace Paradise.Core.Render;

/// <summary>
/// Zusatz-Nachbearbeitung der Engine (NICHT im Original: TDU2 hat keinen eigenen Bloom-, AO-, God-Ray- oder Höhennebel-Durchgang,
/// postfx_spec §0 – "Bloom" steckt dort nur in der Photoshop-Farbtabelle, siehe <see cref="PhotoshopFx"/>). Auf Wunsch eingebaut;
/// Stärken und Schalter kommen aus Game/Config/PostFxConfig.cs, das Entwicklermenü (F1 > Rendering) ändert sie zur Laufzeit.
/// Umsetzung: Render/Vulkan/VulkanRendererPostFx.cs + PostFxShaders.cs, im selben Ablauf wie Photoshop vor dem HUD:
///   AO        – Bildschirmraum-Verdeckung aus der Tiefe (halbe Auflösung, 12 Abtastungen, danach Gauß), Bild × lerp(1, ao, Stärke),
///               vor der Farbtabelle (wirkt also wie im Bild "eingebacken");
///   God Rays  – Himmel-/Sonnenmaske aus Helligkeit + großer Tiefe, radialer Blur von der projizierten Sonne weg (Viertelauflösung),
///               vor der Farbtabelle addiert;
///   Höhenfog  – aus der Tiefe rekonstruierte Sichtposition + Kameraachsen => Welt-Y, Entfernung × Höhenabfall, vor der Farbtabelle
///               mit der Nebelfarbe gemischt;
///   Bloom     – helle Stellen (über der Schwelle) auf ein Viertel verkleinert, zweimal Gauß in wachsender Weite, per "Negativ
///               multiplizieren" (Screen) nach der Farbtabelle aufs Bild gelegt.
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

    public static bool GodRaysEnabled;
    /// <summary>Stärke der Lichtstrahlen (0 = aus, 2 = kräftig). Wird vor der Farbtabelle addiert.</summary>
    public static float GodIntensity = 0.55f;
    /// <summary>Helligkeit ab der ein Himmelspixel Strahlen erzeugt (0–1, größter Farbkanal). Die Tiefe verhindert, dass weiße Wände strahlen.</summary>
    public static float GodThreshold = 0.55f;
    /// <summary>Abfall der Abtastungen entlang der Strahlen (0,5–0,995). Höher = längere, weichere Strahlen.</summary>
    public static float GodDecay = 0.96f;
    /// <summary>Anzahl der Radial-Abtastungen (4–24, Viertelauflösung).</summary>
    public static int GodSamples = 20;

    public static bool FogEnabled;
    /// <summary>Stärke des Höhen-/Entfernungsnebels (0 = aus, 1 = volle Nebelfarbe).</summary>
    public static float FogIntensity = 0.35f;
    /// <summary>Entfernung (m), ab der Nebel einsetzt.</summary>
    public static float FogStart = 60f;
    /// <summary>Entfernung (m), ab der der Nebel voll wirkt.</summary>
    public static float FogEnd = 900f;
    /// <summary>Höhenabfall (1/m): wie schnell der Nebel über dem Nullniveau dünner wird.</summary>
    public static float FogHeightDensity = 0.02f;
    /// <summary>Welt-Y (m), ab dem der Höhenabfall gerechnet wird.</summary>
    public static float FogHeightBase = 0f;

    /// <summary>Volumetrisches Licht des Spieler-Scheinwerfers (nur wenn dieser an ist).</summary>
    public static bool VolumetricEnabled;
    /// <summary>Stärke des Streulichts im Kegel (0 = aus, 2 = kräftig). Wird vor der Farbtabelle addiert.</summary>
    public static float VolumetricIntensity = 0.35f;
    /// <summary>Anzahl der Raymarch-Schritte entlang des Kamerastrahls (4–16, Viertelauflösung).</summary>
    public static int VolumetricSteps = 8;
    /// <summary>Diagnose (F1 > Rendering): PostFX-Projektor des Spielers im letzten Bild vorhanden.</summary>
    public static bool VolumetricProjectorAvailable;
    /// <summary>Diagnose (F1 > Rendering): Stärke dieses Projektors im letzten Bild.</summary>
    public static float VolumetricProjectorStrength;

    /// <summary>Volumetrisches Sonnenlicht im Höhennebel. Ersetzt die klassischen God Rays – beide gleichzeitig würden die Sonne doppelt addieren.</summary>
    public static bool SunVolumetricEnabled;
    /// <summary>Stärke der Sonnenstreuung (0 = aus, 2 = kräftig). Wird vor der Farbtabelle addiert.</summary>
    public static float SunVolumetricIntensity = 0.35f;
    /// <summary>Anzahl der Raymarch-Schritte entlang des Kamerastrahls (4–24, Viertelauflösung).</summary>
    public static int SunVolumetricSteps = 12;
    /// <summary>Diagnose (F1 > Rendering): Sonnen-Volumetrie im letzten Bild aktiv.</summary>
    public static bool SunVolumetricActive;

    /// <summary>Diagnose (F1 > Rendering): 0 aus, 1 AO, 2 Bloom, 3 Bloom-Maske, 4 God-/Sonnenpuffer, 5 Nebelanteil, 6 Volumen-Puffer.</summary>
    public static int DebugView;
}
