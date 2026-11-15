using Paradise.Core.Audio;

namespace Paradise.Core.Video;

/// <summary>
/// Tonspur aus einem AVI (RIFF "AVI "; die Intro-Dateien Euro\Bnk\Video\*.mkv sind trotz Endung AVI: H.264 + MS-ADPCM).
/// Audio-Stream (strh "auds", Format aus strf = WAVEFORMATEX) aus "movi" (Chunks "NNwb") lesen und als WAV über den
/// vorhandenen <see cref="WavDecoder"/> (PCM16/MS-ADPCM) dekodieren.
/// </summary>
public static class AviAudio
{
    public static AudioClip? Load(string path)
    {
        byte[] d;
        try { d = File.ReadAllBytes(path); } catch (Exception) { return null; }
        if (d.Length < 12 || !Tag(d, 0, "RIFF") || !Tag(d, 8, "AVI ")) return null;
        int stream = -1, idx = 0;
        byte[]? fmt = null;
        var data = new MemoryStream();
        Walk(d, 12, d.Length, ref stream, ref idx, ref fmt, data, false);
        if (fmt == null || data.Length == 0) return null;
        // WAV im Speicher: RIFF/WAVE + fmt (WAVEFORMATEX) + data
        var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        w.Write("RIFF"u8.ToArray()); w.Write(0); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(fmt.Length); w.Write(fmt); if ((fmt.Length & 1) != 0) w.Write((byte)0);
        w.Write("data"u8.ToArray()); w.Write((int)data.Length); data.Position = 0; data.CopyTo(wav);
        w.Flush();
        var bytes = wav.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(4), bytes.Length - 8);
        return AudioClip.FromWav(Path.GetFileNameWithoutExtension(path), bytes);
    }

    static void Walk(byte[] d, int o, int end, ref int stream, ref int idx, ref byte[]? fmt, MemoryStream data, bool inStrl)
    {
        bool isAudio = false;
        while (o + 8 <= end)
        {
            string id = System.Text.Encoding.ASCII.GetString(d, o, 4);
            int len = BitConverter.ToInt32(d, o + 4);
            int p = o + 8;
            if (len < 0 || p + len > d.Length) break;
            if (id == "LIST" && len >= 4)
            {
                string list = System.Text.Encoding.ASCII.GetString(d, p, 4);
                if (list == "strl") { Walk(d, p + 4, p + len, ref stream, ref idx, ref fmt, data, true); idx++; }
                else if (list == "hdrl" || list == "movi" || list == "rec ") Walk(d, p + 4, p + len, ref stream, ref idx, ref fmt, data, false);
            }
            else if (inStrl && id == "strh" && len >= 4) isAudio = Tag(d, p, "auds");
            else if (inStrl && id == "strf" && isAudio && stream < 0) { stream = idx; fmt = d.AsSpan(p, len).ToArray(); }
            else if (!inStrl && stream >= 0 && len > 0 && id[2] == 'w' && id[3] == 'b'
                     && int.TryParse(id.AsSpan(0, 2), out int sn) && sn == stream)
                data.Write(d, p, len);
            o = p + len + (len & 1);
        }
    }

    static bool Tag(byte[] d, int o, string t) => o + 4 <= d.Length && d[o] == t[0] && d[o + 1] == t[1] && d[o + 2] == t[2] && d[o + 3] == t[3];
}
