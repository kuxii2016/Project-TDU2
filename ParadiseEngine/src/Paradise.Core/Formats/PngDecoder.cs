using System.IO.Compression;

namespace Paradise.Core.Formats;

/// <summary>
/// PNG lesen (ohne Zusatzpakete): 8 Bit, nicht verschachtelt; Graustufen, RGB, Palette, Grau+Alpha, RGBA.
/// Ergebnis: RGBA8, Zeile 0 = oben.
/// </summary>
public static class PngDecoder
{
    public static byte[]? Decode(byte[] d, out int width, out int height)
    {
        width = height = 0;
        if (d.Length < 33 || d[0] != 0x89 || d[1] != (byte)'P' || d[2] != (byte)'N' || d[3] != (byte)'G') return null;
        int o = 8, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? pal = null, trns = null;
        using var idat = new MemoryStream();
        while (o + 8 <= d.Length)
        {
            int len = (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
            string type = System.Text.Encoding.ASCII.GetString(d, o + 4, 4);
            int p = o + 8;
            if (len < 0 || p + len > d.Length) return null;
            switch (type)
            {
                case "IHDR":
                    width = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
                    height = (d[p + 4] << 24) | (d[p + 5] << 16) | (d[p + 6] << 8) | d[p + 7];
                    bitDepth = d[p + 8]; colorType = d[p + 9]; interlace = d[p + 12];
                    break;
                case "PLTE": pal = d.AsSpan(p, len).ToArray(); break;
                case "tRNS": trns = d.AsSpan(p, len).ToArray(); break;
                case "IDAT": idat.Write(d, p, len); break;
                case "IEND": o = d.Length; continue;
            }
            o = p + len + 4;   // + CRC
        }
        if (width <= 0 || height <= 0 || bitDepth != 8 || interlace != 0) return null;
        int ch = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (ch == 0) return null;
        int stride = width * ch;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int n = 0, r;
            while (n < raw.Length && (r = z.Read(raw, n, raw.Length - n)) > 0) n += r;
            if (n < raw.Length) return null;
        }
        // Filter rückgängig (None, Sub, Up, Average, Paeth)
        var px = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int f = raw[y * (stride + 1)], src = y * (stride + 1) + 1, dst = y * stride, prev = dst - stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= ch ? px[dst + x - ch] : 0;
                int b = y > 0 ? px[prev + x] : 0;
                int c = x >= ch && y > 0 ? px[prev + x - ch] : 0;
                int v = raw[src + x];
                v += f switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                px[dst + x] = (byte)v;
            }
        }
        var rgba = new byte[width * height * 4];
        for (int i = 0, n = width * height; i < n; i++)
        {
            int s = i * ch, t = i * 4;
            switch (colorType)
            {
                case 0: rgba[t] = rgba[t + 1] = rgba[t + 2] = px[s]; rgba[t + 3] = 255; break;
                case 2: rgba[t] = px[s]; rgba[t + 1] = px[s + 1]; rgba[t + 2] = px[s + 2]; rgba[t + 3] = 255; break;
                case 3:
                    int k = px[s];
                    if (pal != null && k * 3 + 2 < pal.Length) { rgba[t] = pal[k * 3]; rgba[t + 1] = pal[k * 3 + 1]; rgba[t + 2] = pal[k * 3 + 2]; }
                    rgba[t + 3] = trns != null && k < trns.Length ? trns[k] : (byte)255;
                    break;
                case 4: rgba[t] = rgba[t + 1] = rgba[t + 2] = px[s]; rgba[t + 3] = px[s + 1]; break;
                default: rgba[t] = px[s]; rgba[t + 1] = px[s + 1]; rgba[t + 2] = px[s + 2]; rgba[t + 3] = px[s + 3]; break;
            }
        }
        return rgba;
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = System.Math.Abs(p - a), pb = System.Math.Abs(p - b), pc = System.Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
