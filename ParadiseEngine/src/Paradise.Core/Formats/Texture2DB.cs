using Paradise.Core.Render;

namespace Paradise.Core.Formats;

/// <summary>
/// .2db = Texture (“BMAP” chunk).
///   File: 16-byte header, BMAP chunk starting at 0x10.
///   0x20 u64 key, 0x28 u16 width, 0x2A u16 height, 0x2C u16 depth/faces (6 = cubemap),
///   0x2E u8 Mipmaps, 0x2F u8 Mipmaps, 0x30 u32 Format (0x84 = DXT1, 0x86 = DXT3, 0x88 = 16-Byte-Blöcke, bit 0x40 = Cubemap),
///   0x3C u8 D3D-Format (0x33 DXT1, 0x35 DXT3, 0x37 DXT5 – entscheidet bei 0x88 zwischen DXT3 und DXT5, s310),
///   Pixel data starting at 0x50 (D3D order: Mip 0 .. n, blocks at least 4x4).
/// </summary>
public sealed class TextureDef
{
    public ulong Key;
    public int Width, Height, Depth, Mips, Format;
    /// <summary>
    /// D3D-Formatbyte der BMAP (Datei +0x3C = Chunk +0x2C), s310: 0x33 = DXT1, 0x35 = DXT3, 0x37 = DXT5 (commonworld.bnk: alle 0x84 = 0x33,
    /// 124 × 0x88 = 0x37, 2 × 0x88 = 0x35 = arrows.2db und r_m_00.2db). Format 0x88 allein sagt nur „16 Byte je Block“ – die beiden
    /// Markierungstexturen sind DXT3 (explizites 4-Bit-Alpha) und wurden als DXT5 dekodiert: ausgefranste, verwaschene Straßenmarkierungen.
    /// [aus den Daten abgeleitet, Leser in der exe noch nicht gesucht]
    /// </summary>
    public int D3dFormat;
    public bool IsDxt3Block => BaseFormat == 0x88 && D3dFormat == 0x35;
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
            D3dFormat = d[0x3C],
            Data = d,
            Name = name,
        };
        int maxMips = MaxMips(def.Width, def.Height);
        if (def.Mips > maxMips) def.Mips = maxMips;
        return def;
    }

    /// <summary>Reads only the key from the first 0x28 bytes of a .2db file.</summary>
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
    /// Corresponds to TextureDef.CreateTexture from the Unity project: DXT1/DXT5 with a mipmap chain (truncated to fit the available
    /// data) directly; DXT3 decoded by the CPU (Mip 0) + mipmaps generated. Cubemaps: null.
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
            // Keine Kopie der kompletten Mip-Kette: der .2db-Puffer bleibt unverändert, CreateTexture liest ab BlockOffset.
            // Vorher: new byte[size] + BlockCopy je Textur – ein Sektor mit vielen Texturen bedeutete etliche MB GC pro Nachladebild.
            return new TextureUpload
            {
                Width = Width,
                Height = Height,
                Mips = mips,
                Format = bf == 0x84 ? GpuTextureFormat.Bc1 : IsDxt3Block ? GpuTextureFormat.Bc2 : GpuTextureFormat.Bc3,
                Blocks = Data,
                BlockOffset = DataOffset,
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
    /// “Packed” detail textures (d1_*, d2_*, beton_01 ...): R = grayscale detail only, G/B = other data.
    /// Identified by the DXT block colors of Mip 0.
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

    /// <summary>Average brightness (0..1), e.g., for normalizing overlays.</summary>
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
    /// <summary>
    /// Adressmodus der .2DM-Ebene (MAT.-Ebene +24/+25): 1 Wrap, 2 Clamp, 3 Mirror, 4 Clamp (Zuordnung des Unity-Ports).
    /// s294 hatte das als D3DTEXTUREADDRESS gelesen (2 = Mirror, 3 = Clamp) – danach wiederholten sich die Straßenmarkierungen
    /// (MARKS 2/1) quer über die Fahrbahn und das Kennzeichen (DETAIL 2/2) verschwand (Test 24.11.); vorher mit 2 = Clamp korrekt.
    /// Die Sektortextur (3/3, exe 0xc50d24) liegt genau auf 0..1, dort sind Mirror und Clamp gleich. Die Umsetzung in der exe
    /// (Eden-Wert -> SetSamplerState) ist noch nicht gefunden [vermutet aus dem Bild].
    /// s312: wieder D3DTEXTUREADDRESS (1 Wrap, 2 Mirror, 3 Clamp, 4 Border->Clamp). Gründe: Feldwege/Felsen (TD_Ground COLOR/DETAIL
    /// 2/x, z. B. V_Rocks_Lod, SF_gliss_*) kacheln im Original längs, mit 2 = Clamp entstanden die Streifen (Test 24.11. 20:25); seit s309
    /// gilt der Modus je Ebene, vorher hatte die geteilte Textur oft zufällig Wrap. Die Gegenprobe aus s294 lief noch mit der
    /// DXT3-als-DXT5-Dekodierung (s310) und ohne Adressmodus je Ebene (s309). 3/3 (projizierte Sektortextur) = Clamp passt dazu.
    /// </summary>
    public static TextureWrap WrapFromEden(byte mode) => mode switch
    {
        2 => TextureWrap.Mirror,
        3 => TextureWrap.Clamp,
        4 => TextureWrap.Clamp,
        _ => TextureWrap.Repeat,
    };

    /// <summary>
    /// Mip 0 as RGBA8 on the CPU (line 0 = first line of the file), for DXT1/DXT3/DXT5—e.g., stickers and factory liveries whose
    /// sticker map is assembled on the CPU (Core/Vehicles/Livery.cs). Cubemaps/unknown: null.
    /// </summary>
    public byte[]? DecodeRgba()
    {
        if (IsCube || Data == null || Width <= 0 || Height <= 0) return null;
        return BaseFormat switch
        {
            0x84 => DecodeBc(Data, DataOffset, Width, Height, false),
            0x88 when IsDxt3Block => DecodeDxt3(Data, DataOffset, Width, Height),   // s310: D3D-Formatbyte 0x35
            0x88 => DecodeBc(Data, DataOffset, Width, Height, true),
            0x86 => DecodeDxt3(Data, DataOffset, Width, Height),
            _ => null,
        };
    }

    /// <summary>DXT1 (BC1, with 1-bit alpha) or DXT5 (BC3: interpolated alpha block + color block).</summary>
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

    /// <summary>RGBA8 mipmap chain using a 2x2 box filter (like Texture2D.Apply(updateMipmaps: true)).</summary>
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