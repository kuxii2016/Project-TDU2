namespace Paradise.Core.Render;

/// <summary>
/// Grafik-Einstellungen zur Laufzeit (Optionsmenü, Unity QualitySettings). Der Renderer übernimmt Änderungen am Ende des Bildes
/// (MSAA/VSync/Schattenauflösung: Swapchain + Pipelines neu; Texturstufe: für neu geladene Texturen).
/// </summary>
public static class GraphicsSettings
{
    /// <summary>Kantenglättung (Unity QualitySettings.antiAliasing): 1 = aus, 2, 4, 8 Abtastungen.</summary>
    public static int Msaa = 2;
    /// <summary>Vertikale Synchronisation (Unity QualitySettings.vSyncCount).</summary>
    public static bool VSync = true;
    /// <summary>Texturqualität (Unity QualitySettings.globalTextureMipmapLimit): 0 = volle Auflösung, 1 = halb, 2 = Viertel.</summary>
    public static int TextureMipLimit;
    /// <summary>Auflösung der Schattenkarten je Kaskade (Unity QualitySettings.shadowResolution).</summary>
    public static int ShadowResolution = 2048;
    /// <summary>Schattenweite in Metern (Unity QualitySettings.shadowDistance), &lt;= 0 = Wert aus LightingConfig.</summary>
    public static float ShadowDistance;
    /// <summary>
    /// Original-Renderer (Render/Original): Original-Shader aus Euro/Shaders/*.sob übersetzt statt der eigenen GLSL-Shader,
    /// Rechnung im Gamma-Raum wie D3D9. Im Aufbau – Stufe 2: opake Objekte der freigegebenen Sätze (OriginalMaterial.EnabledSets,
    /// zuerst TD_Building) laufen über die Original-Shader. Wirkt auf Materialien, die geladen werden, während der Schalter an ist;
    /// Ausschalten fällt sofort auf die eigenen Shader zurück.
    /// </summary>
    public static bool RenderUseOriginal = true;   // Standard: Original-Renderer an
}
