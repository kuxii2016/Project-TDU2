using Paradise.Core.Diagnostics;
using Paradise.Core.Online;
using Paradise.Core.Photos;
using System.Collections.Concurrent;

namespace Paradise.Game.Online;

/// <summary>
/// Fotos der Bilderrahmen im Mehrspieler (Hausparty): Der Besitzer lädt das Foto eines Rahmens hoch ("photoup", Original
/// myhs:fram:vali -> Upload, /:myhsFrameSent), die Gäste holen es per "photoget" vom Server (Original edLivePhotoHandler) und
/// legen es im Zwischenspeicher ab: Dokumente\KuxiiSoft\Test Drive Unlimited 2\Photo\Guests\&lt;Spielernummer&gt;\ (nicht im eigenen Album).
/// Ändert der Gastgeber einen Rahmen, meldet der Server die neue Einrichtung (UDP [11] / houseVer), der Gast holt sie und damit
/// den neuen Dateinamen; das Foto lädt dann hier nach.
/// </summary>
public static class PhotoSync
{
    static readonly ConcurrentQueue<string> _ready = new();
    static readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Größe des hochgeladenen Rahmenfotos [vermutet: Original-Größe nicht bekannt; die Server-Zeile ist auf 1 MB begrenzt].</summary>
    public const int UploadWidth = 1024, UploadHeight = 1024;

    static bool Online => TDU2Session.LoggedIn && !TDU2Session.Offline && TDU2Session.Client != null;

    public static string GuestDir(int host)
    {
        var d = Path.Combine(PhotoAlbum.Dir, "Guests", host.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Foto eines Rahmens für die Gäste hochladen (nur online).</summary>
    public static void Upload(PhotoEntry e)
    {
        if (!Online) return;
        var jpg = PhotoAlbum.LoadScaled(e, UploadWidth, UploadHeight);
        if (jpg == null) return;
        var req = new NetRequest { op = "photoup", session = TDU2Session.SessionId, photo = new PhotoUpload { file = e.File, name = e.Name, data = Convert.ToBase64String(jpg) } };
        TDU2Session.Client.Send(req).ContinueWith(t =>
        {
            if (t.IsFaulted) Log.Warn("Foto-Upload: " + t.Exception?.GetBaseException().Message);
            else if (t.Result != null && !t.Result.ok) Log.Warn("Foto-Upload " + e.File + ": " + t.Result.error);
            else Log.Info("Foto hochgeladen: " + e.File);
        });
    }

    /// <summary>Foto eines anderen Spielers aus dem Zwischenspeicher; null = (noch) nicht da – dann wird es angefordert.</summary>
    public static PhotoEntry? Guest(int host, string file)
    {
        if (host == 0 || string.IsNullOrEmpty(file)) return null;
        var path = Path.Combine(GuestDir(host), file);
        if (File.Exists(path)) return new PhotoEntry { File = host + "/" + file, Path = path, Name = file };
        Request(host, file);
        return null;
    }

    static void Request(int host, string file)
    {
        string key = host + "/" + file;
        if (!Online || _failed.Contains(key)) return;
        lock (_pending) if (!_pending.Add(key)) return;
        var req = new NetRequest { op = "photoget", session = TDU2Session.SessionId, target = host, file = file };
        TDU2Session.Client.Send(req).ContinueWith(t =>
        {
            try
            {
                if (t.IsFaulted || t.Result == null || !t.Result.ok || string.IsNullOrEmpty(t.Result.photoData))
                {
                    Log.Warn("Foto " + key + " vom Server: " + (t.IsFaulted ? t.Exception?.GetBaseException().Message : t.Result?.error));
                    lock (_pending) _failed.Add(key);
                    return;
                }
                File.WriteAllBytes(Path.Combine(GuestDir(host), file), Convert.FromBase64String(t.Result.photoData));
                _ready.Enqueue(key);
            }
            catch (Exception ex) { Log.Warn("Foto " + key + ": " + ex.Message); }
            finally { lock (_pending) _pending.Remove(key); }
        });
    }

    /// <summary>Im Hauptthread: true, wenn seit dem letzten Aufruf Fotos angekommen sind (Rahmen neu belegen).</summary>
    public static bool PollReady()
    {
        bool any = false;
        while (_ready.TryDequeue(out _)) any = true;
        return any;
    }
}
