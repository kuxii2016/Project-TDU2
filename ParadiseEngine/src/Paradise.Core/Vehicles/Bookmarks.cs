using Paradise.Core.Config;
using Paradise.Core.Online;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Paradise.Core.Vehicles;

/// <summary>
/// Lesezeichen und Favoriten-Spots der großen Karte (Port Unity TDU2Bookmarks aus TDU2Gps.cs). Angemeldet im Spielstand auf dem
/// Server (SaveData.bookmarks / favoriteSpots, wie Sticker und Kleidung – nach Neuinstallation wieder da), sonst lokal
/// (Unity PlayerPrefs; hier Dateien unter Documents\KuxiiSoft\Test Drive Unlimited 2\Config). Lokal gespeicherte Lesezeichen
/// werden beim ersten Zugriff mit Anmeldung in den Spielstand übernommen.
/// </summary>
public static class TDU2Bookmarks
{
    public sealed class Mark { public int Id; public string Island = ""; public Vector3 Pos; }
    /// <summary>Höchstzahl (danach sperrt der Film "Lesezeichen setzen" mit m_MaxBookmarks_).</summary>
    public static int Max = 100;

    static List<Mark>? _list;
    static string? _key;
    static readonly HashSet<string> _fav = new(StringComparer.OrdinalIgnoreCase);
    static SaveData? Save => TDU2Session.LoggedIn ? TDU2Session.Save : null;
    static string Key => TDU2Session.LoggedIn ? "TDU2.Bookmarks." + TDU2Session.Nick : "TDU2.Bookmarks.offline";

    public static List<Mark> All { get { Load(); return _list!; } }
    public static bool CanAdd { get { Load(); return _list!.Count < Max; } }

    // Ersatz für PlayerPrefs (gleicher Schlüssel als Dateiname)
    static string PrefPath(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return Path.Combine(UserPaths.Config, sb + ".txt");
    }
    static string ReadPref(string key) { try { var p = PrefPath(key); return File.Exists(p) ? File.ReadAllText(p) : ""; } catch (Exception) { return ""; } }
    static void WritePref(string key, string value) { try { File.WriteAllText(PrefPath(key), value); } catch (Exception) { } }
    static void DeletePref(string key) { try { var p = PrefPath(key); if (File.Exists(p)) File.Delete(p); } catch (Exception) { } }

    static void Load()
    {
        var sv = Save;
        string key = Key + (sv != null ? "|srv" : "");
        if (_list != null && _key == key) return;
        _key = key; _list = new List<Mark>(); _fav.Clear();
        // lokal (offline bzw. aus älteren Versionen)
        var local = new List<Mark>(); var localFav = new List<string>();
        string s = ReadPref(Key), f = ReadPref(Key + ".fav");
        var ci = CultureInfo.InvariantCulture;
        foreach (var line in s.Split('\n'))
        {
            var p = line.Trim('\r').Split(';');
            if (p.Length < 5 || !int.TryParse(p[0], out int id)) continue;
            float.TryParse(p[2], NumberStyles.Float, ci, out float x);
            float.TryParse(p[3], NumberStyles.Float, ci, out float y);
            float.TryParse(p[4], NumberStyles.Float, ci, out float z);
            local.Add(new Mark { Id = id, Island = p[1], Pos = new Vector3(x, y, z) });
        }
        foreach (var c in f.Split(';')) if (c.Trim().Length > 0) localFav.Add(c.Trim());
        if (sv == null)
        {
            _list.AddRange(local);
            foreach (var c in localFav) _fav.Add(c);
            return;
        }
        sv.bookmarks ??= new List<BookmarkEntry>();
        sv.favoriteSpots ??= new List<string>();
        foreach (var b in sv.bookmarks) if (b != null) _list.Add(new Mark { Id = b.id, Island = b.island ?? "", Pos = new Vector3(b.x, b.y, b.z) });
        foreach (var c in sv.favoriteSpots) if (!string.IsNullOrEmpty(c)) _fav.Add(c);
        // lokale übernehmen (einmalig, danach lokal leeren)
        if (local.Count > 0 || localFav.Count > 0)
        {
            foreach (var m in local)
            {
                bool dup = false;
                foreach (var e in _list) if (e.Island == m.Island && (e.Pos - m.Pos).LengthSquared() < 1f) { dup = true; break; }
                if (!dup && _list.Count < Max) { m.Id = NextId(); _list.Add(m); }
            }
            foreach (var c in localFav) _fav.Add(c);
            DeletePref(Key); DeletePref(Key + ".fav");
            Save_();
        }
    }

    static int NextId() { int id = 1; foreach (var m in _list!) id = Math.Max(id, m.Id + 1); return id; }

    static void Save_()
    {
        var sv = Save;
        if (sv != null)
        {
            sv.bookmarks = new List<BookmarkEntry>();
            foreach (var m in _list!) sv.bookmarks.Add(new BookmarkEntry { id = m.Id, island = m.Island, x = m.Pos.X, y = m.Pos.Y, z = m.Pos.Z });
            sv.favoriteSpots = new List<string>(_fav);
            TDU2Session.SaveAsync();
            return;
        }
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (var m in _list!) sb.Append(m.Id).Append(';').Append(m.Island).Append(';').Append(m.Pos.X.ToString(ci)).Append(';').Append(m.Pos.Y.ToString(ci)).Append(';').Append(m.Pos.Z.ToString(ci)).Append('\n');
        WritePref(Key, sb.ToString());
        WritePref(Key + ".fav", string.Join(";", _fav));
    }

    public static Mark? Add(string? island, Vector3 game)
    {
        Load();
        if (_list!.Count >= Max) return null;
        var mk = new Mark { Id = NextId(), Island = island ?? "", Pos = game };
        _list.Add(mk); Save_();
        return mk;
    }

    public static void Remove(int id) { Load(); _list!.RemoveAll(m => m.Id == id); Save_(); }

    public static void SetFavorite(string? spotCode, bool on)
    {
        Load();
        if (string.IsNullOrEmpty(spotCode)) return;
        if (on) _fav.Add(spotCode); else _fav.Remove(spotCode);
        Save_();
    }

    public static bool IsFavorite(string? spotCode) { Load(); return !string.IsNullOrEmpty(spotCode) && _fav.Contains(spotCode); }
}
