using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Render;

namespace Paradise.Core.Flash;

/// <summary>
/// Textures from original assets for Flash clips (SetClipTexture): Spot images of the large map (MapScreens/RealtorScr) and
/// dealer brand logos (frontend/AllRes/logotexturepage.bnk, brands_map 64-px tiles) – Unity TDU2BigMap.SpotPicture /
/// TDU2Minimap.LogoTexture.
/// </summary>
public static class FlashTextures
{
    /// <summary>First .2db texture of a bank as a Flash texture (null if none).</summary>
    public static Texture2D? FirstTexture(string bankPath)
    {
        var bank = BnkArchive.Open(bankPath);
        foreach (var e in bank.All(".2db"))
        {
            var def = TextureDef.Parse(bank.Read(e), Path.GetFileNameWithoutExtension(e.Name));
            return def != null ? Texture2D.FromUpload(def.ToUpload(TextureWrap.Clamp, TextureWrap.Clamp)) : null;
        }
        return null;
    }

    /// <summary>.2db texture of a bank whose name contains <paramref name="namePart"/> (e.g., “IMG_GAR” in RealtorScr/&lt;house&gt;.bnk –
    /// the images for the garage/exterior/living space); null if none.</summary>
    public static Texture2D? TextureNamed(string bankPath, string namePart)
    {
        var bank = BnkArchive.Open(bankPath);
        foreach (var e in bank.All(".2db"))
        {
            string n = Path.GetFileNameWithoutExtension(e.Name);
            if (n.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var def = TextureDef.Parse(bank.Read(e), n);
            return def != null ? Texture2D.FromUpload(def.ToUpload(TextureWrap.Clamp, TextureWrap.Clamp)) : null;
        }
        return null;
    }

    static PixelImage? _brandAtlas;
    static bool _brandAtlasTried;
    static readonly Dictionary<int, Texture2D?> _logos = new();

    /// <summary>Brand logo (DBBrands._logo_global_index): 64×64 tile from brands_map; null if unknown.</summary>
    public static Texture2D? BrandLogo(string? bnkDir, int index)
    {
        if (index < 0) return null;
        if (_logos.TryGetValue(index, out var t)) return t;
        if (!_brandAtlasTried)
        {
            _brandAtlasTried = true;
            try
            {
                var f = bnkDir != null ? Vehicles.CarDatabase.FindFile(Path.Combine(bnkDir, "frontend", "AllRes"), "logotexturepage.bnk") : null;
                if (f != null)
                {
                    var bank = BnkArchive.Open(f);
                    var e = bank.Find("brands_map.2db");
                    if (e != null) _brandAtlas = PixelImage.FromTextureDef(TextureDef.Parse(bank.Read(e), "brands_map"));
                }
                if (_brandAtlas == null) 
                    Log.Warn("Brand logos (logotexturepage.bnk/brands_map) cannot be loaded");
            }
            catch (Exception ex) 
            { 
                Log.Error("Brand logos cannot be loaded:" + ex.Message); 
            }
        }
        var a = _brandAtlas;
        if (a == null) return null;
        int cols = System.Math.Max(1, a.Width / 64), col = index % cols, row = index / cols;
        if ((row + 1) * 64 > a.Height) { _logos[index] = null; return null; }
        var px = new Color32[64 * 64];
        for (int y = 0; y < 64; y++)
            Array.Copy(a.Pixels, (row * 64 + y) * a.Width + col * 64, px, y * 64, 64);
        t = new Texture2D(64, 64) { wrapMode = TextureWrapMode.Clamp };
        t.SetPixels32(px);
        t.Apply();
        _logos[index] = t;
        return t;
    }
}