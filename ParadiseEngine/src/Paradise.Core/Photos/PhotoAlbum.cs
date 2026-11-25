using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using System.Text;

namespace Paradise.Core.Photos;

/// <summary>Ein Foto des Albums (Fotomodus des Pausemenüs).</summary>
public sealed class PhotoEntry
{
    /// <summary>Dateiname wie im Original "%07X%c.JPG" (exe 0xce9f3c).</summary>
    public string File = "";
    public string Path = "";
    /// <summary>Name, den der Spieler eingegeben hat (N: der Info-Zeile).</summary>
    public string Name = "";
    /// <summary>Spieler-Id (ID: der Info-Zeile, nur online gesetzt).</summary>
    public int PlayerId;
    /// <summary>FMT: der Info-Zeile (exe Byte 0x16e0698).</summary>
    public int Format;
    public DateTime Taken;
}

/// <summary>
/// Fotoalbum des Spielers (exe MngPhotoMode, Speichern 0xcead80): jedes Foto ist eine JPG-Datei "%07X%c.JPG" im Behälter 2 des
/// Speichersystems, zusammen mit einer Info-Zeile "(N:&lt;Name&gt;)(ID:&lt;Spieler-Id&gt;)(FMT:&lt;n&gt;)" (offline ohne ID). Höchstens
/// <see cref="MaxPhotos"/> Fotos (exe 0xcea5c6: Zähler &lt; 60, sonst ist "Foto machen" gesperrt bzw. /:isLimitPhoto).
/// Ablage hier: Dokumente\KuxiiSoft\Test Drive Unlimited 2\Photo\. Die Info-Zeile steht als JPEG-Kommentar (COM-Segment) in der
/// Datei selbst [vermutet: wo die PC-Fassung sie ablegt, ist nicht geklärt] – keine Zusatzdateien.
/// </summary>
public static class PhotoAlbum
{
    public const int MaxPhotos = 60;
    /// <summary>Höchstlänge des Namens (exe 0x4375fb, Puffer 0x80).</summary>
    public const int MaxNameLength = 0x7f;

    public static string Dir
    {
        get { var d = System.IO.Path.Combine(UserPaths.Root, "Photo"); Directory.CreateDirectory(d); return d; }
    }

    static List<PhotoEntry>? _cache;

    /// <summary>Alle Fotos, älteste zuerst.</summary>
    public static IReadOnlyList<PhotoEntry> List()
    {
        if (_cache != null) return _cache;
        var list = new List<PhotoEntry>();
        try
        {
            foreach (var f in Directory.GetFiles(Dir, "*.JPG"))
            {
                var e = Read(f);
                if (e != null) list.Add(e);
            }
        }
        catch (Exception ex) { Log.Error("Fotoalbum: " + ex.Message); }
        list.Sort((a, b) => a.Taken.CompareTo(b.Taken));
        return _cache = list;
    }

    public static int Count => List().Count;
    public static bool IsFull => Count >= MaxPhotos;

    /// <summary>Album nach Änderungen von außen neu einlesen.</summary>
    public static void Invalidate() => _cache = null;

    static PhotoEntry? Read(string path)
    {
        try
        {
            var e = new PhotoEntry { Path = path, File = System.IO.Path.GetFileName(path), Taken = File.GetLastWriteTime(path) };
            var info = ReadComment(path);
            if (info != null) ParseInfo(info, e);
            if (string.IsNullOrEmpty(e.Name)) e.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            return e;
        }
        catch (Exception ex) { Log.Warn("Foto " + path + ": " + ex.Message); return null; }
    }

    /// <summary>Info-Zeile wie die exe (0xceae0b / 0xceae21).</summary>
    public static string Info(string name, int playerId, int format)
        => playerId > 0 ? $"(N:{name})(ID:{playerId})(FMT:{format})" : $"(N:{name})(FMT:{format})";

    /// <summary>Klammern im Namen ersetzt die exe (0xce9760, Tabelle "()" -> "²µ"), damit die Info-Zeile lesbar bleibt.</summary>
    public static string CleanName(string? name)
    {
        var s = (name ?? "").Replace('(', '²').Replace(')', 'µ');
        return s.Length > MaxNameLength ? s[..MaxNameLength] : s;
    }

    static void ParseInfo(string info, PhotoEntry e)
    {
        string? Field(string key)
        {
            int i = info.IndexOf("(" + key + ":", StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length + 2;
            int j = info.IndexOf(')', i);
            return j < 0 ? null : info[i..j];
        }
        e.Name = Field("N") ?? "";
        if (int.TryParse(Field("ID"), out int id)) e.PlayerId = id;
        if (int.TryParse(Field("FMT"), out int fmt)) e.Format = fmt;
    }

    /// <summary>Freier Dateiname "%07X%c.JPG" (exe 0xce9f00: Zufallszahl, so lange neu, bis der Name noch nicht vergeben ist).</summary>
    public static string NewFileName(char letter)
    {
        var used = new HashSet<string>(List().Select(p => p.File), StringComparer.OrdinalIgnoreCase);
        string name;
        do name = $"{Random.Shared.Next(0, 0x10000000):X7}{letter}.JPG";
        while (used.Contains(name) || File.Exists(System.IO.Path.Combine(Dir, name)));
        return name;
    }

    /// <summary>Foto speichern: JPG mit der Info-Zeile als Kommentar. null, wenn das Album voll ist oder das Schreiben scheitert.</summary>
    public static PhotoEntry? Save(byte[] jpg, string name, int playerId, int format, char letter)
    {
        if (IsFull) { Log.Warn("Fotoalbum voll (" + MaxPhotos + ")"); return null; }
        name = CleanName(name);
        var file = NewFileName(letter);
        var path = System.IO.Path.Combine(Dir, file);
        try
        {
            File.WriteAllBytes(path, WithComment(jpg, Info(name, playerId, format)));
            var e = new PhotoEntry { File = file, Path = path, Name = name, PlayerId = playerId, Format = format, Taken = DateTime.Now };
            List();
            _cache!.Add(e);
            Log.Info($"Foto gespeichert: {file} \"{name}\" ({jpg.Length / 1024} KB), Album {_cache.Count}/{MaxPhotos}");
            return e;
        }
        catch (Exception ex) { Log.Error("Foto speichern: " + ex.Message); return null; }
    }

    public static bool Delete(PhotoEntry e)
    {
        try { File.Delete(e.Path); _cache?.Remove(e); return true; }
        catch (Exception ex) { Log.Error("Foto löschen: " + ex.Message); return false; }
    }

    public static byte[]? Load(PhotoEntry e)
    {
        try { return File.ReadAllBytes(e.Path); }
        catch (Exception ex) { Log.Error("Foto laden: " + ex.Message); return null; }
    }

    /// <summary>Rgba (Zeile 0 = oben) mit Kastenfilter auf höchstens maxW x maxH verkleinern (Seitenverhältnis bleibt).</summary>
    public static byte[] Downscale(byte[] rgba, int w, int h, int maxW, int maxH, out int nw, out int nh)
    {
        float k = System.Math.Min(1f, System.Math.Min(maxW / (float)w, maxH / (float)h));
        nw = System.Math.Max(1, (int)(w * k)); nh = System.Math.Max(1, (int)(h * k));
        if (nw == w && nh == h) return rgba;
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int y0 = y * h / nh, y1 = System.Math.Max(y0 + 1, (y + 1) * h / nh);
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * w / nw, x1 = System.Math.Max(x0 + 1, (x + 1) * w / nw);
                int r = 0, g = 0, bl = 0, c = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++) { int q = (sy * w + sx) * 4; r += rgba[q]; g += rgba[q + 1]; bl += rgba[q + 2]; c++; }
                int o = (y * nw + x) * 4;
                dst[o] = (byte)(r / c); dst[o + 1] = (byte)(g / c); dst[o + 2] = (byte)(bl / c); dst[o + 3] = 255;
            }
        }
        return dst;
    }

    /// <summary>
    /// Foto verkleinert als JPG (für den Upload: die TCP-Zeile des Servers ist auf 1 MB begrenzt – ein 1,4-MB-Foto als Base64
    /// trennte die Verbindung). Info-Zeile bleibt erhalten.
    /// </summary>
    public static byte[]? LoadScaled(PhotoEntry e, int maxW, int maxH, int quality = 85)
    {
        var jpg = Load(e);
        if (jpg == null) return null;
        try
        {
            var rgba = Paradise.Core.Formats.WicImage.Decode(jpg, out int w, out int h);
            if (rgba == null || w <= 0 || h <= 0) return null;
            var px = Downscale(rgba, w, h, maxW, maxH, out int nw, out int nh);
            return WithComment(Paradise.Core.Formats.JpegEncoder.Encode(px, nw, nh, quality), Info(e.Name, e.PlayerId, e.Format));
        }
        catch (Exception ex) { Log.Error("Foto verkleinern: " + ex.Message); return null; }
    }

    // ------------------------------------------------------------------ JPEG-Kommentar (COM, FF FE)
    static byte[] WithComment(byte[] jpg, string text)
    {
        if (jpg.Length < 2 || jpg[0] != 0xFF || jpg[1] != 0xD8) return jpg;
        var data = Encoding.UTF8.GetBytes(text);
        if (data.Length > 0xFFFD) Array.Resize(ref data, 0xFFFD);
        int len = data.Length + 2;
        var r = new byte[jpg.Length + 2 + len];
        r[0] = 0xFF; r[1] = 0xD8; r[2] = 0xFF; r[3] = 0xFE; r[4] = (byte)(len >> 8); r[5] = (byte)len;
        Buffer.BlockCopy(data, 0, r, 6, data.Length);
        Buffer.BlockCopy(jpg, 2, r, 6 + data.Length, jpg.Length - 2);
        return r;
    }

    static string? ReadComment(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[4];
        if (fs.Read(head, 0, 2) != 2 || head[0] != 0xFF || head[1] != 0xD8) return null;
        while (fs.Read(head, 0, 4) == 4 && head[0] == 0xFF)
        {
            int len = (head[2] << 8) | head[3];
            if (head[1] == 0xDA || len < 2) break;   // Bilddaten beginnen
            var buf = new byte[len - 2];
            if (fs.Read(buf, 0, buf.Length) != buf.Length) break;
            if (head[1] == 0xFE) return Encoding.UTF8.GetString(buf);
        }
        return null;
    }
}
