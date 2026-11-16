using Paradise.Core.Render;
using Paradise.Core.World;

namespace Paradise.Game.Config;

/// <summary>
/// Grafik-Einstellungen (Grundwerte). Werte nur hier im Code ändern (wie AudioConfig/LightingConfig); das Optionsmenü
/// (Screens/Options, Unity TDU2Options) ändert sie zur Laufzeit und speichert die Auswahl im Profil.
/// </summary>
public sealed class GraphicsConfig
{
    /// <summary>Objekt-Sichtweite (Unity TDU2RenderDistance): VeryHigh ×4, High ×2, Normal, Low ÷2, VeryLow ÷4.</summary>
    public RenderDistanceLevel RenderDistance { get; set; } = RenderDistanceLevel.Normal;
    /// <summary>Grundwert des LOD-Bias (Unity QualitySettings.lodBias der Grafikoption "LOD"), 1 = Werte aus den Dateien.</summary>
    public float LodBias { get; set; } = 1f;
    /// <summary>Kantenglättung (Optionsmenü "Anti-Aliasing", Unity-Standard 4x): 0/1 aus, 2, 4, 8.</summary>
    public int AntiAliasing { get; set; } = 4;
    /// <summary>Vertikale Synchronisation (Unity-Standard an). Aus = Mailbox/Immediate (Messung ohne Bildschirmtakt).</summary>
    public bool VSync { get; set; } = false;
    /// <summary>Texturqualität 0 niedrig (Viertel), 1 mittel (halb), 2 hoch (volle Auflösung) – Unity-Standard hoch.</summary>
    public int TextureQuality { get; set; } = 2;
    /// <summary>Effektqualität 0 niedrig, 1 mittel, 2 hoch: Schattenauflösung 1024/2048/4096, Schattenweite 60/120 m bzw. LightingConfig.</summary>
    public int EffectsQuality { get; set; } = 2;
    /// <summary>
    /// Photoshop-Nachbearbeitung des Originals (Farbtabelle aus den Wetterwerten photoshop_*: Weiches Licht, Farbbalance,
    /// Ineinanderkopieren, Bloom, Gamma – Docs\re\postfx_spec.md §2). Aus = Bild ohne Nachbearbeitung (zum Vergleichen).
    /// </summary>
    public bool Photoshop { get; set; } = true;
    /// <summary>
    /// Autolicht des Originals (GS_CarDisplay, Core/World/TDU2CarLighting): Lack/Glas mit sun_ambient/diffuse/specular × car_sun_*_factor,
    /// entsättigt (car_lighting_saturation), ENVCOEFF aus dem Wetter. Aus = bisheriges Unity-Licht am Auto (zum Vergleichen).
    /// </summary>
    public bool OriginalCarLight { get; set; } = true;
    /// <summary>
    /// Flocken-Variante des Original-Lacks TD_CarPaint2 (Lacke mit FLAKE-Ebene): Verlauf RAMPCLR2 -> RAMPCLR1 über den Halbvektor
    /// statt über N·V (heller Ton nur im Glanzbereich) + Glitzer RAMPFLAKE. Gilt für Autos, die danach geladen werden.
    /// </summary>
    public bool OriginalCarFlakes { get; set; } = true;
    /// <summary>
    /// Außenglas der Autos nach dem Original-Shader (Familie TD_CarPaint, Docs\re\car_glass_spec.md §2): schwarze Grundfarbe (G_I),
    /// Licht + Glanz + Spiegelung, Deckkraft aus der Glastextur. Aus = bisherige TD_CarGlassDirt-Formel. Neu geladene Autos.
    /// </summary>
    public bool OriginalCarGlass { get; set; } = true;

    /// <summary>Werte der Effektstufe (Unity TDU2Options "fx_qual": shadowResolution Low/Medium/VeryHigh, shadowDistance 60/120/250).
    /// Hoch nutzt die Schattenweite aus LightingConfig (0).</summary>
    public static void ApplyEffects(int level)
    {
        level = System.Math.Clamp(level, 0, 2);
        GraphicsSettings.ShadowResolution = new[] { 1024, 2048, 4096 }[level];
        GraphicsSettings.ShadowDistance = new[] { 60f, 120f, 0f }[level];
    }

    public void Apply()
    {
        GraphicsSettings.Msaa = System.Math.Max(1, AntiAliasing);
        GraphicsSettings.VSync = VSync;
        GraphicsSettings.TextureMipLimit = 2 - System.Math.Clamp(TextureQuality, 0, 2);
        ApplyEffects(EffectsQuality);
        PhotoshopFx.Enabled = Photoshop;
        CarLighting.Enabled = OriginalCarLight;
        Paradise.Core.Vehicles.CarMaterials.OriginalFlakes = OriginalCarFlakes;
        Paradise.Core.Vehicles.CarMaterials.OriginalGlass = OriginalCarGlass;
        Paradise.Core.World.RenderDistance.SetBaseLodBias(LodBias);
        Paradise.Core.World.RenderDistance.Level = RenderDistance;
    }
}
