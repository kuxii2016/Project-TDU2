using Paradise.Core.Render;

namespace Paradise.Core.Formats;

/// <summary>
/// .2db = Textur ("BMAP"-Chunk). Portiert aus dem Unity-Projekt (TextureDef).
///   Datei: 16 Byte Header, BMAP-Chunk ab 0x10.
///   0x20 u64 Key, 0x28 u16 Breite, 0x2A u16 Höhe, 0x2C u16 Tiefe/Faces (6 = Cubemap),
///   0x2E u8 Mipmaps, 0x2F u8 Mipmaps, 0x30 u32 Format (0x84 = DXT1, 0x86 = DXT3, 0x88 = DXT5, Bit 0x40 = Cubemap),
///   Pixeldaten ab 0x50 (D3D-Reihenfolge: Mip 0 .. n, Blöcke mindestens 4x4).
/// Statt Unity-Texture2D liefert <see cref="ToUpload"/> die Daten für den Renderer.
/// </summary>
public sealed class TextureDef
{
    public ulong Key;
    public int Width, Height, Depth, Mips, Format;
    public byte[]? Data;
    public const int DataOffset = 0x50;
    public string? Name;

    public bool IsCube => (Format & 0x40) != 0 || Depth == 6;
    public int BaseFormat => Format & ~0x40;

    public static TextureDef? Parse(byte[]? d, string? name = null)
    {
        if (d == null || d.Length < DataOffset || !Bin.TagIs(d, 0x10, "BMAP")) return null;
        var def = new TextureDef
        {
            Key = Bin.U64(d, 0x20),
            Width = Bin.U16(d, 0x28),
            Height = Bin.U16(d, 0x2A),
            Depth = Bin.U16(d, 0x2C),
            Mips = System.Math.Max(1, (int)d[0x2E]),
            Format = (int)Bin.U32(d, 0x30),
            Data = d,
            Name = name,
        };
        int maxMips = MaxMips(def.Width, def.Height);
        if (def.Mips > maxMips) def.Mips = maxMips;
        return def;
    }

    /// <summary>Liest nur den Key aus den ersten 0x28 Bytes einer .2db.</summary>
    public static ulong PeekKey(byte[]? head) => head != null && head.Length >= 0x28 ? Bin.U64(head, 0x20) : 0;

    private static int MaxMips(int w, int h) => (int)System.Math.Floor(System.Math.Log2(System.Math.Max(1, System.Math.Max(w, h)))) + 1;

    private static int BlockBytes(int baseFormat) => baseFormat == 0x84 ? 8 : 16;

    private static int MipChainSize(int w, int h, int mips, int blockBytes)
    {
        int total = 0;
        for (int i = 0; i < mips; i++)
        {
            int mw = System.Math.Max(1, w >> i), mh = System.Math.Max(1, h >> i);
            total += System.Math.Max(1, (mw + 3) / 4) * System.Math.Max(1, (mh + 3) / 4) * blockBytes;
        }
        return total;
    }

    /// <summary>
    /// Entspricht TextureDef.CreateTexture aus dem Unity-Projekt: DXT1/DXT5 mit (auf die vorhandenen
    /// Daten gekürzter) Mip-Kette direkt, DXT3 per CPU dekodiert (Mip 0) + Mips erzeugt. Cubemaps: null.
    /// </summary>
    public TextureUpload? ToUpload(TextureWrap wrapU = TextureWrap.Repeat, TextureWrap wrapV = TextureWrap.Repeat)
    {
        if (IsCube || Data == null) return null;
        int avail = Data.Length - DataOffset;
        int bf = BaseFormat;
        string nm = Name ?? Bin.KeyToString(Key);
        if (bf == 0x84 || bf == 0x88)
        {
            int bb = BlockBytes(bf);
            int mips = System.Math.Min(Mips, MaxMips(Width, Height));
            while (mips > 1 && MipChainSize(Width, Height, mips, bb) > avail) mips--;
            int size = MipChainSize(Width, Height, mips, bb);
            if (size > avail || Width % 4 != 0 || Height % 4 != 0) return null;
            var raw = new byte[size];
            Buffer.BlockCopy(Data, DataOffset, raw, 0, size);
            return new TextureUpload
            {
                Width = Width,
                Height = Height,
                Mips = mips,
                Format = bf == 0x84 ? GpuTextureFormat.Bc1 : GpuTextureFormat.Bc3,
                Blocks = raw,
                WrapU = wrapU,
                WrapV = wrapV,
                Name = nm,
            };
        }
        if (bf == 0x86)
        {
            // DXT3: wie im Unity-Projekt per CPU dekodieren (Mip 0), Mips erzeugen (Texture2D.Apply(true))
            var px = DecodeDxt3(Data, DataOffset, Width, Height);
            if (px == null) return null;
            var chain = BuildRgbaMips(px, Width, Height, out int mips);
            return new TextureUpload
            {
                Width = Width,
                Height = Height,
                Mips = mips,
                Format = GpuTextureFormat.Rgba8,
                Blocks = chain,
                WrapU = wrapU,
                WrapV = wrapV,
                Name = nm,
            };
        }
        Diagnostics.Log.Warn($"Unbekanntes Texturformat: 0x{Format:x} ({nm})");
        return null;
    }

    /// <summary>
    /// "Gepackte" Detailtexturen (d1_*, d2_*, beton_01 ...): nur R = Graustufen-Detail, G/B = andere Daten.
    /// Erkennung über die DXT-Blockfarben von Mip 0.
    /// </summary>
    public bool LooksPacked()
    {
        int bf = BaseFormat;
        if ((bf != 0x84 && bf != 0x88) || Data == null) return false;
        int bb = BlockBytes(bf), colOff = bf == 0x88 ? 8 : 0;
        int blocks = System.Math.Max(1, Width / 4) * System.Math.Max(1, Height / 4);
        int step = System.Math.Max(1, blocks / 2048);
        double r = 0, g = 0, b = 0; int n = 0;
        for (int i = 0; i < blocks; i += step)
        {
            int o = DataOffset + i * bb + colOff;
            if (o + 4 > Data.Length) break;
            for (int k = 0; k < 2; k++)
            {
                int c = Data[o + k * 2] | (Data[o + k * 2 + 1] << 8);
                r += ((c >> 11) & 31) / 31.0; g += ((c >> 5) & 63) / 63.0; b += (c & 31) / 31.0; n++;
            }
        }
        if (n == 0) return false;
        r /= n; g /= n; b /= n;
        return r > 0.2 && g < 0.35 * r && b < 0.2 * r;
    }

    /// <summary>Mittlere Helligkeit (0..1), z. B. zum Normieren von Overlays.</summary>
    public float MeanLum()
    {
        int bf = BaseFormat;
        if ((bf != 0x84 && bf != 0x88) || Data == null) return 0.5f;
        int bb = BlockBytes(bf), colOff = bf == 0x88 ? 8 : 0;
        int blocks = System.Math.Max(1, Width / 4) * System.Math.Max(1, Height / 4);
        int step = System.Math.Max(1, blocks / 2048);
        double sum = 0; int n = 0;
        for (int i = 0; i < blocks; i += step)
        {
            int o = DataOffset + i * bb + colOff;
            if (o + 4 > Data.Length) break;
            for (int k = 0; k < 2; k++)
            {
                int c = Data[o + k * 2] | (Data[o + k * 2 + 1] << 8);
                sum += 0.3 * ((c >> 11) & 31) / 31.0 + 0.59 * ((c >> 5) & 63) / 63.0 + 0.11 * (c & 31) / 31.0; n++;
            }
        }
        return n > 0 ? (float)(sum / n) : 0.5f;
    }

    /// <summary>Eden-Adressmodus: 1 = Wrap, 2 = Clamp (Lightmaps, Colormaps), 3 = Mirror, 4 = Clamp.</summary>
    public static TextureWrap WrapFromEden(byte mode) => mode switch
    {
        2 => TextureWrap.Clamp,
        3 => TextureWrap.Mirror,
        4 => TextureWrap.Clamp,
        _ => TextureWrap.Repeat,
    };

    /// <summary>
    /// Mip 0 als RGBA8 auf der CPU (Zeile 0 = erste Zeile der Datei), für DXT1/DXT3/DXT5 – z.B. Sticker und Werks-Beklebung, deren
    /// Stickermap auf der CPU zusammengesetzt wird (Core/Vehicles/Livery.cs). Cubemaps/unbekannt: null.
    /// </summary>
    public byte[]? DecodeRgba()
    {
        if (IsCube || Data == null || Width <= 0 || Height <= 0) return null;
        return BaseFormat switch
        {
            0x84 => DecodeBc(Data, DataOffset, Width, Height, false),
            0x88 => DecodeBc(Data, DataOffset, Width, Height, true),
            0x86 => DecodeDxt3(Data, DataOffset, Width, Height),
            _ => null,
        };
    }

    /// <summary>DXT1 (BC1, mit 1-Bit-Alpha) bzw. DXT5 (BC3: interpolierter Alpha-Block + Farbblock).</summary>
    private static byte[]? DecodeBc(byte[] d, int off, int w, int h, bool bc3)
    {
        int bw = System.Math.Max(1, (w + 3) / 4), bh = System.Math.Max(1, (h + 3) / 4), bb = bc3 ? 16 : 8;
        if (off + bw * bh * bb > d.Length) return null;
        var px = new byte[w * h * 4];
        Span<byte> pal = stackalloc byte[16];
        Span<byte> al = stackalloc byte[8];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int b = off + (by * bw + bx) * bb;
                ulong abits = 0;
                if (bc3)
                {
                    al[0] = d[b]; al[1] = d[b + 1];
                    if (al[0] > al[1]) for (int i = 1; i < 7; i++) al[i + 1] = (byte)(((7 - i) * al[0] + i * al[1]) / 7);
                    else { for (int i = 1; i < 5; i++) al[i + 1] = (byte)(((5 - i) * al[0] + i * al[1]) / 5); al[6] = 0; al[7] = 255; }
                    for (int i = 0; i < 6; i++) abits |= (ulong)d[b + 2 + i] << (8 * i);
                }
                int cb = bc3 ? b + 8 : b;
                ushort c0 = Bin.U16(d, cb), c1 = Bin.U16(d, cb + 2);
                C565(c0, pal, 0); C565(c1, pal, 4);
                bool four = bc3 || c0 > c1;
                for (int ch = 0; ch < 3; ch++)
                {
                    if (four)
                    {
                        pal[8 + ch] = (byte)((pal[ch] * 2 + pal[4 + ch]) / 3);
                        pal[12 + ch] = (byte)((pal[ch] + pal[4 + ch] * 2) / 3);
                    }
                    else
                    {
                        pal[8 + ch] = (byte)((pal[ch] + pal[4 + ch]) / 2);
                        pal[12 + ch] = 0;
                    }
                }
                pal[11] = 255; pal[15] = (byte)(four ? 255 : 0);
                uint bits = Bin.U32(d, cb + 4);
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                    if (x >= w || y >= h) continue;
                    int pi = (int)((bits >> (2 * i)) & 3) * 4;
                    int o = (y * w + x) * 4;
                    px[o] = pal[pi]; px[o + 1] = pal[pi + 1]; px[o + 2] = pal[pi + 2];
                    px[o + 3] = bc3 ? al[(int)((abits >> (3 * i)) & 7)] : pal[pi + 3];
                }
            }
        return px;
    }

    private static byte[]? DecodeDxt3(byte[] d, int off, int w, int h)
    {
        int bw = System.Math.Max(1, w / 4), bh = System.Math.Max(1, h / 4);
        if (off + bw * bh * 16 > d.Length) return null;
        var px = new byte[w * h * 4];
        Span<byte> pal = stackalloc byte[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int b = off + (by * bw + bx) * 16;
                ushort c0 = Bin.U16(d, b + 8), c1 = Bin.U16(d, b + 10);
                C565(c0, pal, 0); C565(c1, pal, 4);
                for (int ch = 0; ch < 3; ch++)
                {
                    pal[8 + ch] = (byte)((pal[ch] * 2 + pal[4 + ch]) / 3);
                    pal[12 + ch] = (byte)((pal[ch] + pal[4 + ch] * 2) / 3);
                }
                uint bits = Bin.U32(d, b + 12);
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                    if (x >= w || y >= h) continue;
                    int pi = (int)((bits >> (2 * i)) & 3) * 4;
                    int an = (d[b + (i >> 1)] >> ((i & 1) * 4)) & 0xF;
                    int o = (y * w + x) * 4;
                    px[o] = pal[pi]; px[o + 1] = pal[pi + 1]; px[o + 2] = pal[pi + 2]; px[o + 3] = (byte)(an * 17);
                }
            }
        return px;
    }

    private static void C565(ushort c, Span<byte> p, int o)
    {
        p[o] = (byte)(((c >> 11) & 31) * 255 / 31);
        p[o + 1] = (byte)(((c >> 5) & 63) * 255 / 63);
        p[o + 2] = (byte)((c & 31) * 255 / 31);
        p[o + 3] = 255;
    }

    /// <summary>RGBA8-Mip-Kette per 2x2-Box-Filter (wie Texture2D.Apply(updateMipmaps: true)).</summary>
    public static byte[] BuildRgbaMips(byte[] mip0, int w, int h, out int mips)
    {
        var levels = new List<byte[]> { mip0 };
        int cw = w, chh = h;
        var cur = mip0;
        while (cw > 1 || chh > 1)
        {
            int nw = System.Math.Max(1, cw / 2), nh = System.Math.Max(1, chh / 2);
            var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int x0 = System.Math.Min(x * 2, cw - 1), x1 = System.Math.Min(x * 2 + 1, cw - 1);
                        int y0 = System.Math.Min(y * 2, chh - 1), y1 = System.Math.Min(y * 2 + 1, chh - 1);
                        int s = cur[(y0 * cw + x0) * 4 + c] + cur[(y0 * cw + x1) * 4 + c] + cur[(y1 * cw + x0) * 4 + c] + cur[(y1 * cw + x1) * 4 + c];
                        next[(y * nw + x) * 4 + c] = (byte)((s + 2) / 4);
                    }
            levels.Add(next);
            cur = next; cw = nw; chh = nh;
        }
        mips = levels.Count;
        int total = 0;
        foreach (var l in levels) total += l.Length;
        var all = new byte[total];
        int p = 0;
        foreach (var l in levels) { Buffer.BlockCopy(l, 0, all, p, l.Length); p += l.Length; }
        return all;
    }
}
