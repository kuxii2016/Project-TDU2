using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Render;

namespace Paradise.Core.Flash;

/// <summary>
/// Texturen aus Original-Bänken für Flash-Clips (SetClipTexture): Spot-Bilder der großen Karte (MapScreens/RealtorScr) und
/// Markenlogos der Händler (frontend/AllRes/logotexturepage.bnk, brands_map 64-px-Kacheln) – Unity TDU2BigMap.SpotPicture /
/// TDU2Minimap.LogoTexture.
/// </summary>
public static class FlashTextures
{
    /// <summary>Erste .2db-Textur einer Bank als Flash-Textur (null, wenn keine).</summary>
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

    /// <summary>.2db-Textur einer Bank, deren Name <paramref name="namePart"/> enthält (z.B. "IMG_GAR" in RealtorScr/&lt;haus&gt;.bnk –
    /// die Bilder für Garage/Außenansicht/Wohnraum); null, wenn keine.</summary>
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

    /// <summary>Markenlogo (DBBrands._logo_global_index): 64×64-Kachel aus brands_map; null, wenn unbekannt.</summary>
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
                if (_brandAtlas == null) Log.Warn("Markenlogos (logotexturepage.bnk/brands_map) nicht ladbar");
            }
            catch (Exception ex) { Log.Warn("Markenlogos nicht ladbar: " + ex.Message); }
        }
        var a = _brandAtlas;
        if (a == null) return null;
        int cols = System.Math.Max(1, a.Width / 64), col = index % cols, row = index / cols;
        if ((row + 1) * 64 > a.Height) { _logos[index] = null; return null; }
        // Zeile 0 der Rohdaten = Zeile 0 der Textur (wie Unity GetPixels(col*64, row*64) auf der unveränderten DXT-Textur)
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
