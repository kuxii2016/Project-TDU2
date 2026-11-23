using Paradise.Core.Render;

namespace Paradise.Game.Config;

/// <summary>
/// Bloom and Ambient Occlusion – Engine addition, NOT in the original (TDU2 only has the Photoshop color table,
/// see GraphicsConfig.Photoshop). Change values only here in the code (like GraphicsConfig/LightingConfig); the developer menu
/// (F1 > Rendering > Bloom / Ambient Occlusion) changes them at runtime for testing purposes; nothing is saved there.
/// Implementation: Core/Render/PostFx.cs (values), Render/Vulkan/VulkanRendererPostFx.cs (passes). Requires the UNORM view of the
/// swap chain (like Photoshop); without it, both are disabled.
/// </summary>
public sealed class PostFxConfig
{
    /// <summary>Turn Bloom on/off.</summary>
    public bool Bloom { get; set; } = true;
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
    }
}
