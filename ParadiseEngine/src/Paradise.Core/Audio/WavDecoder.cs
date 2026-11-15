namespace Paradise.Core.Audio;

public static class WavDecoder
{
    static readonly int[] AdaptTable = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };

    public static bool TryDecode(byte[] b, out float[]? samples, out int channels, out int rate)
    {
        samples = null; channels = 0; rate = 0;
        if (b == null || b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return false;
        int o = 12, fmtOff = -1, fmtLen = 0, dataOff = -1, dataLen = 0;
        while (o + 8 <= b.Length)
        {
            int sz = BitConverter.ToInt32(b, o + 4);
            if (sz < 0) break;
            if (b[o] == 'f' && b[o + 1] == 'm' && b[o + 2] == 't') { fmtOff = o + 8; fmtLen = sz; }
            else if (b[o] == 'd' && b[o + 1] == 'a' && b[o + 2] == 't' && b[o + 3] == 'a') { dataOff = o + 8; dataLen = System.Math.Min(sz, b.Length - dataOff); }
            o += 8 + sz + (sz & 1);
        }
        if (fmtOff < 0 || dataOff < 0) return false;
        int tag = BitConverter.ToUInt16(b, fmtOff);
        channels = BitConverter.ToUInt16(b, fmtOff + 2);
        rate = BitConverter.ToInt32(b, fmtOff + 4);
        int align = BitConverter.ToUInt16(b, fmtOff + 12);
        int bits = BitConverter.ToUInt16(b, fmtOff + 14);
        if (channels < 1 || channels > 2) return false;

        if (tag == 1 && bits == 16)
        {
            int n = dataLen / 2;
            samples = new float[n];
            for (int i = 0; i < n; i++) samples[i] = BitConverter.ToInt16(b, dataOff + 2 * i) / 32768f;
            return true;
        }
        if (tag != 2 || fmtLen < 22) return false;

        int spb = BitConverter.ToUInt16(b, fmtOff + 18);
        int nCoef = BitConverter.ToUInt16(b, fmtOff + 20);
        var c1 = new int[nCoef]; var c2 = new int[nCoef];
        for (int i = 0; i < nCoef; i++) { c1[i] = BitConverter.ToInt16(b, fmtOff + 22 + 4 * i); c2[i] = BitConverter.ToInt16(b, fmtOff + 24 + 4 * i); }

        int blocks = dataLen / align;
        var outp = new List<float>(blocks * spb * channels);
        var pred = new int[2]; var delta = new int[2]; var s1 = new int[2]; var s2 = new int[2];
        for (int bk = 0; bk < blocks; bk++)
        {
            int p = dataOff + bk * align, end = p + align;
            for (int c = 0; c < channels; c++) pred[c] = System.Math.Min(b[p++], nCoef - 1);
            for (int c = 0; c < channels; c++) { delta[c] = BitConverter.ToInt16(b, p); p += 2; }
            for (int c = 0; c < channels; c++) { s1[c] = BitConverter.ToInt16(b, p); p += 2; }
            for (int c = 0; c < channels; c++) { s2[c] = BitConverter.ToInt16(b, p); p += 2; }
            for (int c = 0; c < channels; c++) outp.Add(s2[c] / 32768f);
            for (int c = 0; c < channels; c++) outp.Add(s1[c] / 32768f);
            int ch = 0;
            int produced = 0, needed = (spb - 2) * channels;
            while (p < end && produced < needed)
            {
                int by = b[p++];
                for (int h = 0; h < 2 && produced < needed; h++)
                {
                    int nib = h == 0 ? by >> 4 : by & 15;
                    int sn = (nib & 8) != 0 ? nib - 16 : nib;
                    int pv = ((s1[ch] * c1[pred[ch]]) + (s2[ch] * c2[pred[ch]])) >> 8;
                    int v = pv + sn * delta[ch];
                    if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                    s2[ch] = s1[ch]; s1[ch] = v;
                    delta[ch] = System.Math.Max(16, (AdaptTable[nib] * delta[ch]) >> 8);
                    outp.Add(v / 32768f);
                    produced++;
                    ch = (ch + 1) % channels;
                }
            }
        }
        samples = outp.ToArray();
        return true;
    }
}