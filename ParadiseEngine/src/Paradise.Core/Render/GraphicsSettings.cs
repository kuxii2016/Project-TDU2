namespace Paradise.Core.Render;

/// <summary>
/// Grafik-Einstellungen zur Laufzeit (Optionsmenü, Unity QualitySettings). Der Renderer übernimmt Änderungen am Ende des Bildes
/// (MSAA/VSync/Schattenauflösung: Swapchain + Pipelines neu; Texturstufe: für neu geladene Texturen).
/// </summary>
public static class GraphicsSettings
{
    /// <summary>Kantenglättung (Unity QualitySettings.antiAliasing): 1 = aus, 2, 4, 8 Abtastungen.</summary>
    public static int Msaa = 1;
    /// <summary>Vertikale Synchronisation (Unity QualitySettings.vSyncCount).</summary>
    public static bool VSync = true;
    /// <summary>Texturqualität (Unity QualitySettings.globalTextureMipmapLimit): 0 = volle Auflösung, 1 = halb, 2 = Viertel.</summary>
    public static int TextureMipLimit;
    /// <summary>Auflösung der Schattenkarten je Kaskade (Unity QualitySettings.shadowResolution).</summary>
    public static int ShadowResolution = 2048;
    /// <summary>Schattenweite in Metern (Unity QualitySettings.shadowDistance), &lt;= 0 = Wert aus LightingConfig.</summary>
    public static float ShadowDistance;
}
