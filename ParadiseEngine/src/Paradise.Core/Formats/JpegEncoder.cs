namespace Paradise.Core.Formats;

/// <summary>
/// Baseline JPEG (JFIF, 4:4:4, standard Huffman tables per ITU T.81 Annex K) – replacement for Unity Texture2D.EncodeToJPG
/// (MyTDULife avatar photo). Quality as per the IJG scale (1…100). Input Rgba8, line 0 = topmost image line of the file.
/// </summary>
public static class JpegEncoder
{
    // Zickzack-Reihenfolge: k-ter übertragener Koeffizient = natürlicher Index Zig[k]
    static readonly byte[] Zig =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    };

    // Quantisierung (Anhang K.1), natürliche Reihenfolge
    static readonly byte[] QLum =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    };
    static readonly byte[] QChr =
    {
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    };

    // Huffman (Anhang K.3): Anzahl Codes je Länge 1…16, dann Werte
    static readonly byte[] DcLumBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    static readonly byte[] DcLumVals = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    static readonly byte[] DcChrBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    static readonly byte[] DcChrVals = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    static readonly byte[] AcLumBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    static readonly byte[] AcLumVals =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };
    static readonly byte[] AcChrBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    static readonly byte[] AcChrVals =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };

    sealed class Huff { public readonly ushort[] Code = new ushort[256]; public readonly byte[] Size = new byte[256]; }

    static Huff Build(byte[] bits, byte[] vals)
    {
        var h = new Huff();
        int code = 0, k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < bits[len - 1]; i++) { h.Code[vals[k]] = (ushort)code; h.Size[vals[k]] = (byte)len; k++; code++; }
            code <<= 1;
        }
        return h;
    }

    static readonly float[] Cos = BuildCos();
    static float[] BuildCos()
    {
        // Cos[u*8+x] = C(u)/2 · cos((2x+1)uπ/16), C(0) = 1/√2
        var c = new float[64];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                c[u * 8 + x] = (float)((u == 0 ? 1.0 / System.Math.Sqrt(2.0) : 1.0) * 0.5 * System.Math.Cos((2 * x + 1) * u * System.Math.PI / 16.0));
        return c;
    }

    static byte[] Scale(byte[] q, int quality)
    {
        quality = System.Math.Clamp(quality, 1, 100);
        int s = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var r = new byte[64];
        for (int i = 0; i < 64; i++) r[i] = (byte)System.Math.Clamp((q[i] * s + 50) / 100, 1, 255);
        return r;
    }

    /// <summary>Encode image. <paramref name="flipVertical"/>: The bottom row of input becomes the top row of the file.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality, bool flipVertical = false)
    {
        if (width <= 0 || height <= 0 || width > 65535 || height > 65535 || rgba.Length < width * height * 4)
            throw new ArgumentException("JpegEncoder: ungültige Bildgröße");
        var ql = Scale(QLum, quality);
        var qc = Scale(QChr, quality);
        var dcL = Build(DcLumBits, DcLumVals); var acL = Build(AcLumBits, AcLumVals);
        var dcC = Build(DcChrBits, DcChrVals); var acC = Build(AcChrBits, AcChrVals);

        var o = new MemoryStream(width * height / 2 + 1024);
        void W8(int v) => o.WriteByte((byte)v);
        void W16(int v) { o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)v); }

        // SOI, APP0 (JFIF 1.01, 1:1)
        W16(0xFFD8);
        W16(0xFFE0); W16(16); W8('J'); W8('F'); W8('I'); W8('F'); W8(0); W8(1); W8(1); W8(0); W16(1); W16(1); W8(0); W8(0);
        // DQT (Zickzack-Reihenfolge)
        W16(0xFFDB); W16(2 + 2 * 65);
        W8(0); for (int k = 0; k < 64; k++) W8(ql[Zig[k]]);
        W8(1); for (int k = 0; k < 64; k++) W8(qc[Zig[k]]);
        // SOF0: 8 Bit, 3 Komponenten, je 1×1 (4:4:4)
        W16(0xFFC0); W16(17); W8(8); W16(height); W16(width); W8(3);
        W8(1); W8(0x11); W8(0);
        W8(2); W8(0x11); W8(1);
        W8(3); W8(0x11); W8(1);
        // DHT
        void Dht(int cls, byte[] bits, byte[] vals)
        {
            W16(0xFFC4); W16(2 + 1 + 16 + vals.Length); W8(cls);
            foreach (var b in bits) W8(b);
            foreach (var v in vals) W8(v);
        }
        Dht(0x00, DcLumBits, DcLumVals); Dht(0x10, AcLumBits, AcLumVals);
        Dht(0x01, DcChrBits, DcChrVals); Dht(0x11, AcChrBits, AcChrVals);
        // SOS
        W16(0xFFDA); W16(12); W8(3); W8(1); W8(0x00); W8(2); W8(0x11); W8(3); W8(0x11); W8(0); W8(63); W8(0);

        // Entropie-kodierte Daten
        uint acc = 0; int nbits = 0;
        void Bits(int code, int size)
        {
            for (int i = size - 1; i >= 0; i--)
            {
                acc = (acc << 1) | (uint)((code >> i) & 1); nbits++;
                if (nbits == 8) { o.WriteByte((byte)acc); if ((byte)acc == 0xFF) o.WriteByte(0); acc = 0; nbits = 0; }
            }
        }

        var ys = new float[64]; var cbs = new float[64]; var crs = new float[64];
        var tmp = new float[64]; var coef = new int[64];
        int prevY = 0, prevCb = 0, prevCr = 0;

        int Block(float[] src, byte[] q, Huff dc, Huff ac, int prev)
        {
            // 2D-DCT getrennt: Zeilen, dann Spalten (F(v,u) = Σy Σx f(y,x)·Cos[v,y]·Cos[u,x])
            for (int y = 0; y < 8; y++)
                for (int u = 0; u < 8; u++)
                {
                    float s = 0f;
                    for (int x = 0; x < 8; x++) s += src[y * 8 + x] * Cos[u * 8 + x];
                    tmp[y * 8 + u] = s;
                }
            for (int v = 0; v < 8; v++)
                for (int u = 0; u < 8; u++)
                {
                    float s = 0f;
                    for (int y = 0; y < 8; y++) s += tmp[y * 8 + u] * Cos[v * 8 + y];
                    coef[v * 8 + u] = (int)MathF.Round(s / q[v * 8 + u]);
                }
            // DC
            int dcv = coef[0];
            int diff = dcv - prev;
            int cat = Category(diff);
            Bits(dc.Code[cat], dc.Size[cat]);
            if (cat > 0) Bits(diff < 0 ? (diff - 1) & ((1 << cat) - 1) : diff, cat);
            // AC
            int run = 0;
            for (int k = 1; k < 64; k++)
            {
                int c = coef[Zig[k]];
                if (c == 0) { run++; continue; }
                while (run > 15) { Bits(ac.Code[0xF0], ac.Size[0xF0]); run -= 16; }
                int ct = Category(c);
                int sym = (run << 4) | ct;
                Bits(ac.Code[sym], ac.Size[sym]);
                Bits(c < 0 ? (c - 1) & ((1 << ct) - 1) : c, ct);
                run = 0;
            }
            if (run > 0) Bits(ac.Code[0x00], ac.Size[0x00]);
            return dcv;
        }

        for (int by = 0; by < height; by += 8)
            for (int bx = 0; bx < width; bx += 8)
            {
                for (int y = 0; y < 8; y++)
                {
                    int sy = System.Math.Min(by + y, height - 1);   // Rand: letzte Zeile/Spalte wiederholen
                    if (flipVertical) sy = height - 1 - sy;
                    for (int x = 0; x < 8; x++)
                    {
                        int sx = System.Math.Min(bx + x, width - 1);
                        int p = (sy * width + sx) * 4;
                        float r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
                        int i = y * 8 + x;
                        ys[i] = 0.299f * r + 0.587f * g + 0.114f * b - 128f;
                        cbs[i] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                        crs[i] = 0.5f * r - 0.418688f * g - 0.081312f * b;
                    }
                }
                prevY = Block(ys, ql, dcL, acL, prevY);
                prevCb = Block(cbs, qc, dcC, acC, prevCb);
                prevCr = Block(crs, qc, dcC, acC, prevCr);
            }
        if (nbits > 0) Bits(0x7F, 8 - nbits);   // mit Einsen auffüllen
        W16(0xFFD9);
        return o.ToArray();
    }

    static int Category(int v)
    {
        if (v < 0) v = -v;
        int n = 0;
        while (v != 0) { n++; v >>= 1; }
        return n;
    }
}