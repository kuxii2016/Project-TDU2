using System.Numerics;

namespace Paradise.Core.Render;

/// <summary>
/// "Photoshop"-Nachbearbeitung des Originals (TDU2 PFX, Docs\re\postfx_spec.md §2): der letzte Bilddurchgang vor dem HUD.
/// Ablauf je Bild (umgesetzt in Render/Vulkan/VulkanRendererPostFx.cs, Shader in PostFxShaders.cs):
///   1. Bild auf ein Viertel verkleinern (4 Abtastungen),
///   2. Gauß-Unschärfe 5 Abtastungen waagerecht + senkrecht,
///   3. Farbtabelle (LUT, 256×256, 8 Bit) aus den Werten unten berechnen: Weiches Licht + Farbbalance + Ineinanderkopieren +
///      Bloom-Rampe + Gamma,
///   4. Anwenden: jeder Farbkanal wird über die Tabelle ersetzt, Zeile = Helligkeit aus Bild und Unschärfe gemischt.
/// Es gibt im Original keinen eigenen Glow-/Bloom-Durchgang, "Bloom" steckt nur in dieser Tabelle (postfx_spec §0).
///
/// Wer setzt was:
///   - <see cref="Enabled"/>: Schalter aus Game/Config/GraphicsConfig (Photoshop).
///   - Wetterwerte (photoshop_* aus fx/game_weathers.xmb, Blöcke 1–6 je Wetterart): WorldWeather.Update, wie FUN_00d769e0.
///   - Gamma / BlurredLumFactor: feste Werte des Originals (§2.1, §2.6), nicht vom Wetter.
/// Vorgaben = Grundwerte der exe (§2.1), gelten bis das Wetter geladen ist.
/// </summary>
public static class PhotoshopFx
{
    /// <summary>Durchgang an/aus (Game/Config). Aus = Bild wie bisher, ohne Nachbearbeitung.</summary>
    public static bool Enabled;

    /// <summary>photoshop_softlight_opacity – Deckkraft "Weiches Licht" (exe-Grundwert 0.55). LUT-Konstante params.x.</summary>
    public static float SoftlightOpacity = 0.55f;
    /// <summary>photoshop_softlight_brightness – Helligkeit der Mischebene (1.25). params.y.</summary>
    public static float SoftlightBrightness = 1.25f;
    /// <summary>photoshop_overlay_opacity – Deckkraft "Ineinanderkopieren" (0.30). params.z.</summary>
    public static float OverlayOpacity = 0.30f;

    /// <summary>photoshop_color_balance_shadows/midtones/hightones (je RGB, Grundwert 0). balance[0..2].</summary>
    public static Vector3 BalanceShadows, BalanceMidtones, BalanceHightones;

    /// <summary>photoshop_bloom_black_level / white_level / intensity (0.5 / 1.0 / 0.25). bloom_params.</summary>
    public static float BloomBlack = 0.5f, BloomWhite = 1.0f, BloomIntensity = 0.25f;

    /// <summary>
    /// "Gamma" der Tabelle (§2.6): 0.8 bei Helligkeitsoption 0.5 (Grundwert). Bereich im Original [0.4, 1.6].
    /// </summary>
    public static float Gamma = .85f;

    /// <summary>"Blurred Lumincance Factor" (§2.1): Anteil der Unschärfe an der Tabellenzeile, fest 0.6 (Bereich [0, 4]).</summary>
    public static float BlurredLumFactor = 0.6f;

    /// <summary>
    /// photoshop_crappymode_enable: Tiefenunschärfe-Variante (ps_1, braucht die Tiefe aus "FullscreenDepthTarget", §2.5/§6).
    /// Nur zur Info: noch nicht umgesetzt, der Renderer meldet es einmal im Log, wenn ein Wetter sie einschaltet.
    /// </summary>
    public static bool CrappyMode;
}
