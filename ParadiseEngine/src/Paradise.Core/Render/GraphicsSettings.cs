namespace Paradise.Core.Render;

/// <summary>Upscaler für das Szenenbild vor Flash/HUD. 0 = aus, 1 = FSR 1 (räumlich), 3 = FSR 3 (temporal).</summary>
public enum UpscalerMode
{
    Off = 0,
    Fsr1 = 1,
    Fsr3 = 3,
}

/// <summary>FSR-Qualitätsstufe; bestimmt den internen Render-Scale.</summary>
public enum UpscalerQuality
{
    Quality,
    Balanced,
    Performance,
    UltraPerformance,
}

/// <summary>
/// Grafik-Einstellungen zur Laufzeit (Optionsmenü, Unity QualitySettings). Der Renderer übernimmt Änderungen am Ende des Bildes
/// (MSAA/VSync/Schattenauflösung: Swapchain + Pipelines neu; Texturstufe: für neu geladene Texturen).
/// </summary>
public static class GraphicsSettings
{
    /// <summary>Kantenglättung (Unity QualitySettings.antiAliasing): 1 = aus, 2, 4, 8 Abtastungen.</summary>
    public static int Msaa = 2;
    /// <summary>Aktiver Upscaler. Standard = aus; FSR 3 braucht später Jitter + Velocity + Depth-Kopie.</summary>
    public static UpscalerMode Upscaler = UpscalerMode.Off;
    /// <summary>Qualitätsstufe des Upscalers (nur wirksam, wenn <see cref="Upscaler"/> aktiv ist).</summary>
    public static UpscalerQuality UpscalerQuality = UpscalerQuality.Quality;
    /// <summary>Schärfe 0..1 für FSR 1 (RCAS) bzw. FSR 3 (Sharpening).</summary>
    public static float FsrSharpness = 0.5f;
    /// <summary>Interner Render-Scale für Dev-Anzeige. FSR-SDK überschreibt die Werte später ggf. leicht.</summary>
    public static float RenderScale => Upscaler == UpscalerMode.Off ? 1f : UpscalerQuality switch
    {
        UpscalerQuality.Quality => 0.67f,
        UpscalerQuality.Balanced => 0.58f,
        UpscalerQuality.Performance => 0.5f,
        _ => 0.33f,
    };
    /// <summary>Vertikale Synchronisation (Unity QualitySettings.vSyncCount).</summary>
    public static bool VSync = true;
    /// <summary>Texturqualität (Unity QualitySettings.globalTextureMipmapLimit): 0 = volle Auflösung, 1 = halb, 2 = Viertel.</summary>
    public static int TextureMipLimit;
    /// <summary>Auflösung der Schattenkarten je Kaskade (Unity QualitySettings.shadowResolution).</summary>
    public static int ShadowResolution = 2048;
    /// <summary>Schattenweite in Metern (Unity QualitySettings.shadowDistance), &lt;= 0 = Wert aus LightingConfig.</summary>
    public static float ShadowDistance;
    /// <summary>Aktive Schatten-Kaskaden (2/3/4). Nur die ersten N Kaskaden werden gezeichnet und gesampelt;
    /// die Kartenauflösung je Kaskade bleibt unverändert. 3 = Default.</summary>
    public static int ActiveShadowCascades = 3;
    /// <summary>
    /// Original-Renderer (Render/Original): Original-Shader aus Euro/Shaders/*.sob übersetzt statt der eigenen GLSL-Shader,
    /// Rechnung im Gamma-Raum wie D3D9. Im Aufbau – Stufe 2: opake Objekte der freigegebenen Sätze (OriginalMaterial.EnabledSets,
    /// zuerst TD_Building) laufen über die Original-Shader. Wirkt auf Materialien, die geladen werden, während der Schalter an ist;
    /// Ausschalten fällt sofort auf die eigenen Shader zurück.
    /// </summary>
    public static bool RenderUseOriginal = true;   // Standard: Original-Renderer an
}
