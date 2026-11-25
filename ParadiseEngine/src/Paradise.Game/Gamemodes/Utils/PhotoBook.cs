using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Photos;
using Paradise.Core.Render;
using System.Globalization;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Fotobuch von home.vmf (exe MngPhotoAlbum) – dieselbe Buch-Komponente an drei Stellen:
///   /diary/photo_book_content/book (MyTDULife "Meine Fotos"),
///   /myhs_editor/frames/book (My House: Foto für einen Bilderrahmen),
///   /mylf_book/book (MyTDULife-Kasten mit Foto).
/// Der Film setzt /:path_photo, /:num_page (1..), /:num_photo (1..4) und schickt:
///   phpa = Seite füllen (/:photo_name1..4, /:photo_taken1..4, Bild in &lt;path_photo&gt;/photo&lt;n&gt;/lure; /:nb_pages vorher),
///   phph = anderes Foto gewählt, phal:dele = Foto löschen (danach _root.EndDeletePhoto), phsp:phin / phsp:phcl = Album auf/zu.
/// Vier Fotos je Seite (photo1..photo4).
/// </summary>
internal static class PhotoBook
{
    public const int PerPage = 4;
    static readonly Dictionary<string, Texture2D?> _flashTex = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, IGpuTexture?> _gpuTex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Befehle der Buch-Komponente; true = behandelt.</summary>
    public static bool Command(string c, VmfPlayer? ui)
    {
        if (ui == null) return false;
        switch (c)
        {
            case "phsp:phin": Open(ui); return true;
            case "phsp:phcl": return true;
            case "phpa": FillPage(ui); return true;
            case "phph": Focus(ui); return true;
            case "phal:dele": Delete(ui); return true;
        }
        return false;
    }

    /// <summary>Album neu lesen, Seitenzahl setzen (/:nb_pages), /:MyHouseNoPhotos.</summary>
    public static void Open(VmfPlayer ui)
    {
        PhotoAlbum.Invalidate();
        int n = PhotoAlbum.Count;
        ui.SetValue("/:nb_pages", Math.Max(1, (n + PerPage - 1) / PerPage));
        ui.SetValue("/:MyHouseNoPhotos", n == 0);
        Log.Info($"Fotoalbum: {n} Foto(s)");
    }

    static int Int(VmfPlayer ui, string v, int def)
    {
        var s = ui.GetVariable(v);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (int)Math.Round(d) : def;
    }

    public static PhotoEntry? At(int page, int slot)
    {
        var l = PhotoAlbum.List();
        int i = (page - 1) * PerPage + (slot - 1);
        return i >= 0 && i < l.Count ? l[i] : null;
    }

    /// <summary>Das gewählte Foto (Seite /:num_page, Platz /:num_photo).</summary>
    public static PhotoEntry? Selected(VmfPlayer ui) => At(Int(ui, "/:num_page", 1), Int(ui, "/:num_photo", 1));

    static string SlotPath(VmfPlayer ui, int slot)
    {
        string b = ui.GetVariable("/:path_photo") ?? "";
        // diary: "/diary/photo_book_content/book/photo" (+n), My House: "/myhs_editor/frames/book" (+ "/photo" n) – exe "%s/photo%d/lure"
        return (b.EndsWith("photo", StringComparison.OrdinalIgnoreCase) ? b + slot : b + "/photo" + slot) + "/lure";
    }

    public static void FillPage(VmfPlayer ui)
    {
        int page = Int(ui, "/:num_page", 1);
        for (int i = 1; i <= PerPage; i++)
        {
            var e = At(page, i);
            ui.SetValue("/:photo_taken" + i, e != null);
            ui.SetVariable("/:photo_name" + i, e?.Name ?? "");
            ui.SetClipTexture(SlotPath(ui, i), (e != null ? FlashTexture(e) : null)!);
        }
    }

    /// <summary>Gewähltes Foto groß (diary: lure_full, wird mit A aufgezoomt).</summary>
    public static void Focus(VmfPlayer ui)
    {
        string b = ui.GetVariable("/:path_photo") ?? "";
        if (!b.StartsWith("/diary", StringComparison.OrdinalIgnoreCase)) return;
        var e = Selected(ui);
        ui.SetClipTexture("/diary/photo_book_content/lure_full/lure", (e != null ? FlashTexture(e) : null)!);
    }

    static void Delete(VmfPlayer ui)
    {
        var e = Selected(ui);
        if (e != null)
        {
            _flashTex.Remove(e.File);
            PhotoAlbum.Delete(e);
            Log.Info("Foto gelöscht: " + e.File + " \"" + e.Name + "\"");
            PhotoDeleted?.Invoke(e);
        }
        Open(ui);
        if (!ui.Call("/diary/photo_book_content", "EndDeletePhoto")) ui.Input("EndDeletePhoto");
    }

    public static event Action<PhotoEntry>? PhotoDeleted;

    /// <summary>Foto als Flash-Textur, zwischengespeichert. Zeile 0 = oben wie die JPG-Datei (wie das Polaroid im Fotomodus,
    /// das die Aufnahme direkt bekommt und richtig herum steht).</summary>
    public static Texture2D? FlashTexture(PhotoEntry e)
    {
        if (_flashTex.TryGetValue(e.File, out var t)) return t;
        t = null;
        var rgba = Decode(e, out int w, out int h);
        if (rgba != null) rgba = PhotoAlbum.Downscale(rgba, w, h, 512, 512, out w, out h);   // Buchseite: kleine Vorschau reicht
        if (rgba != null)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = (y * w + x) * 4;
                    px[y * w + x] = new Color32(rgba[s], rgba[s + 1], rgba[s + 2], 255);
                }
            t = new Texture2D(w, h, linear: true) { wrapMode = TextureWrapMode.Clamp };   // Bildwerte wie in der Datei (sonst dunkel)
            t.SetPixels32(px);
            t.Apply();
        }
        _flashTex[e.File] = t;
        return t;
    }

    /// <summary>Foto als GPU-Textur für ein Material (Bilderrahmen), Zeile 0 = oben wie die Spieltexturen.</summary>
    public static IGpuTexture? GpuTexture(PhotoEntry e, IRenderer r)
    {
        if (_gpuTex.TryGetValue(e.File, out var t)) return t;
        t = null;
        var rgba = Decode(e, out int w, out int h);
        if (rgba != null) rgba = PhotoAlbum.Downscale(rgba, w, h, 1024, 1024, out w, out h);
        if (rgba != null)
            t = r.CreateTexture(new TextureUpload
            {
                Width = w,
                Height = h,
                Format = GpuTextureFormat.Rgba8,
                Mips = 1,
                Blocks = rgba,
                WrapU = TextureWrap.Clamp,
                WrapV = TextureWrap.Clamp,
                Name = "Foto " + e.File,
            });
        _gpuTex[e.File] = t;
        return t;
    }

    static byte[]? Decode(PhotoEntry e, out int w, out int h)
    {
        w = h = 0;
        var jpg = PhotoAlbum.Load(e);
        if (jpg == null) return null;
        try { return Paradise.Core.Formats.WicImage.Decode(jpg, out w, out h); }
        catch (Exception ex) { Log.Warn("Foto " + e.File + ": " + ex.Message); return null; }
    }
}
