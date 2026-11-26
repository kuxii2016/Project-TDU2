using Paradise.Core.Render;

namespace Paradise.Game.Config;

/// <summary>
/// Bloom, Ambient Occlusion, God Rays, height/distance fog and the player's volumetric headlight – engine addition, NOT in
/// the original (TDU2 only has the Photoshop color table, see GraphicsConfig.Photoshop). Change values only here in the code
/// (like GraphicsConfig/LightingConfig); the developer menu (F1 > Rendering) changes them at runtime for testing purposes;
/// nothing is saved there. Implementation: Core/Render/PostFx.cs (values), Render/Vulkan/VulkanRendererPostFx.cs (passes).
/// Requires the UNORM view of the swap chain (like Photoshop); without it, all five are disabled.
/// </summary>
public sealed class PostFxConfig
{
    /// <summary>Turn Bloom on/off.</summary>
    public bool Bloom { get; set; } = false;
    // Hinweis: Das Bild ist 8 Bit (kein HDR). Helle Flächen (Himmel, weiße Wände, Straße in der Sonne) liegen deshalb schon bei
    // 0,8–1,0 – eine niedrige Schwelle lässt sie alle strahlen. Darum Schwelle hoch, Stärke moderat; Lampen/Neon kommen über die
    // Bloom-Maske (BloomEmissive) mit eigener Schwelle (BloomEmissiveThreshold).
    /// <summary>Intensity 0–2 (0 = off, 1 = bright spots fully on).</summary>
    public float BloomIntensity { get; set; } = 0.6f;
    /// <summary>Threshold 0–1: An image pixel is considered lit starting at this brightness level (the brightest color channel in the final image).</summary>
    public float BloomThreshold { get; set; } = 0.88f;
    /// <summary>Smooth transition below the threshold 0–0.5 (0 = hard edge).</summary>
    public float BloomKnee { get; set; } = 0.08f;
    /// <summary>Beam width 0.25–8 (1 = default value; higher values = wider beam).</summary>
    public float BloomRadius { get; set; } = 1.5f;
    /// <summary>Glowing materials 0–4 (car lights, flares, neon, illuminated windows, spot holograms): glow regardless of
    /// the threshold. 0 = off (threshold only).</summary>
    public float BloomEmissive { get; set; } = 1f;
    /// <summary>Glowing materials only glow above this brightness 0–1 (brightest color channel). Keeps the faint parts of
    /// lamp light cones/halos and the background behind them from glowing. 0 = everything under the mask glows (old behaviour).</summary>
    public float BloomEmissiveThreshold { get; set; } = 0.55f;

    /// <summary>Ambient Occlusion on/off.</summary>
    public bool AmbientOcclusion { get; set; } = true;
    /// <summary>Brightness 0–1 (0 = off, 1 = full blackout).</summary>
    public float AoIntensity { get; set; } = 1f;
    /// <summary>Search radius in meters, 0.1–5: how far neighboring areas obscure a location.</summary>
    public float AoRadius { get; set; } = .5f;
    /// <summary>Slope 0–0.5: prevents flat areas from darkening themselves (higher values result in less darkening, but a cleaner result).</summary>
    public float AoBias { get; set; } = 0f;
    /// <summary>At this distance in meters, the darkening effect fades out (distant terrain remains unaffected).</summary>
    public float AoMaxDistance { get; set; } = 150f;

    /// <summary>Screen-space God Rays from the projected sun. Off in the original.</summary>
    public bool GodRays { get; set; } = false;
    /// <summary>Ray brightness 0–2 added before the colour LUT.</summary>
    public float GodIntensity { get; set; } = 0.55f;
    /// <summary>Sky brightness threshold 0–1. Depth rejects walls; the sun neighbourhood gets a minimum contribution.</summary>
    public float GodThreshold { get; set; } = 0.55f;
    /// <summary>Sample decay 0.5–0.995. Higher = longer rays.</summary>
    public float GodDecay { get; set; } = 0.96f;
    /// <summary>Radial samples 4–24 at quarter resolution.</summary>
    public int GodSamples { get; set; } = 20;

    /// <summary>Height + distance fog reconstructed from scene depth. Off in the original; off by default here too because it
    /// changed the car brightness from frame to frame. Can be switched on again at runtime in F1 > Rendering.</summary>
    public bool HeightFog { get; set; } = false;
    /// <summary>Fog strength 0–1 (1 = full fog colour at the far distance).</summary>
    public float FogIntensity { get; set; } = 0.35f;
    /// <summary>Distance in meters where fog starts.</summary>
    public float FogStart { get; set; } = 60f;
    /// <summary>Distance in meters where fog reaches full strength.</summary>
    public float FogEnd { get; set; } = 900f;
    /// <summary>Height falloff 1/m above <see cref="FogHeightBase"/> (0 = no height dependence).</summary>
    public float FogHeightDensity { get; set; } = 0.02f;
    /// <summary>World Y in meters used as the fog base height.</summary>
    public float FogHeightBase { get; set; } = 0f;

    /// <summary>Volumetric light of the player's headlight (screen-space, uses the fog height profile). Off by default because
    /// it made the car lights come and go; can be switched on again at runtime in F1 > Rendering.</summary>
    public bool Volumetric { get; set; } = false;
    /// <summary>Scattered light strength 0–2 added before the colour LUT.</summary>
    public float VolumetricIntensity { get; set; } = 0.35f;
    /// <summary>Raymarch steps 4–16 along the camera ray at quarter resolution.</summary>
    public int VolumetricSteps { get; set; } = 8;

    /// <summary>Volumetric sunlight in the height fog. Replaces the classic God Rays to avoid adding the sun twice.</summary>
    public bool SunVolumetric { get; set; } = false;
    /// <summary>Scattered sunlight strength 0–2 added before the colour LUT.</summary>
    public float SunVolumetricIntensity { get; set; } = 0.35f;
    /// <summary>Raymarch steps 4–24 along the camera ray at quarter resolution.</summary>
    public int SunVolumetricSteps { get; set; } = 12;

    public void Apply()
    {
        PostFx.BloomEnabled = Bloom;
        PostFx.BloomIntensity = System.Math.Clamp(BloomIntensity, 0f, 2f);
        PostFx.BloomThreshold = System.Math.Clamp(BloomThreshold, 0f, 1f);
        PostFx.BloomKnee = System.Math.Clamp(BloomKnee, 0f, 0.5f);
        PostFx.BloomRadius = System.Math.Clamp(BloomRadius, 0.25f, 8f);
        PostFx.BloomEmissive = System.Math.Clamp(BloomEmissive, 0f, 4f);
        PostFx.BloomEmissiveThreshold = System.Math.Clamp(BloomEmissiveThreshold, 0f, 1f);
        PostFx.AoEnabled = AmbientOcclusion;
        PostFx.AoIntensity = System.Math.Clamp(AoIntensity, 0f, 1f);
        PostFx.AoRadius = System.Math.Clamp(AoRadius, 0.1f, 5f);
        PostFx.AoBias = System.Math.Clamp(AoBias, 0f, 0.5f);
        PostFx.AoMaxDistance = System.Math.Clamp(AoMaxDistance, 5f, 2000f);
        PostFx.GodRaysEnabled = GodRays;
        PostFx.GodIntensity = System.Math.Clamp(GodIntensity, 0f, 2f);
        PostFx.GodThreshold = System.Math.Clamp(GodThreshold, 0f, 1f);
        PostFx.GodDecay = System.Math.Clamp(GodDecay, 0.5f, 0.995f);
        PostFx.GodSamples = System.Math.Clamp(GodSamples, 4, 24);
        PostFx.FogEnabled = HeightFog;
        PostFx.FogIntensity = System.Math.Clamp(FogIntensity, 0f, 1f);
        PostFx.FogStart = System.Math.Clamp(FogStart, 0f, 5000f);
        PostFx.FogEnd = System.Math.Max(PostFx.FogStart + 1f, System.Math.Clamp(FogEnd, 1f, 10000f));
        PostFx.FogHeightDensity = System.Math.Clamp(FogHeightDensity, 0f, 1f);
        PostFx.FogHeightBase = System.Math.Clamp(FogHeightBase, -500f, 5000f);
        PostFx.VolumetricEnabled = Volumetric;
        PostFx.VolumetricIntensity = System.Math.Clamp(VolumetricIntensity, 0f, 2f);
        PostFx.VolumetricSteps = System.Math.Clamp(VolumetricSteps, 4, 16);
        PostFx.SunVolumetricEnabled = SunVolumetric;
        PostFx.SunVolumetricIntensity = System.Math.Clamp(SunVolumetricIntensity, 0f, 2f);
        PostFx.SunVolumetricSteps = System.Math.Clamp(SunVolumetricSteps, 4, 24);
    }
}
