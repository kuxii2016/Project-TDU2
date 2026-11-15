using Paradise.Core.Diagnostics;
using System.Globalization;
using System.Text;

namespace Paradise.Core.Files;

/// <summary>Verweis auf einen Eintrag in einem Bigfile.</summary>
public readonly struct BigEntryRef
{
    public readonly BigArchive Archive;
    public readonly int Index;
    public BigEntryRef(BigArchive archive, int index) { Archive = archive; Index = index; }
    public int Size => Archive.SizeAt(Index);
    public byte[] Read() => Archive.ReadEntry(Index);
    public void Read(long offset, byte[] buffer, int bufferOffset, int count)
        => Archive.ReadEntry(Index, offset, buffer, bufferOffset, count);
}

/// <summary>
/// Hybrider Dateizugriff auf die TDU2-Spieldaten: lose Dateien unter <c>Euro/Bnk</c> haben Vorrang
/// (Mods), sonst wird in den <c>*.big</c>-Archiven gesucht (Basisspiel, Updates, DLCs).
/// Virtuelle Pfade sind immer <c>euro/bnk/…</c> (so lauten die Hashes in den .map-Indizes).
/// </summary>
public static class GameFiles
{
    private const string NamesResource = "Paradise.Core.Files.bigfile-names.tsv";

    private static readonly object Lock = new();
    private static readonly List<BigArchive> Archives = new();
    private static readonly Dictionary<ulong, BigEntryRef> Entries = new();
    private static readonly Dictionary<ulong, string> Names = new();
    private static readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);

    private static string? _root;
    private static string? _bnkDir;
    private static string? _bnkDirFull;
    private static string? _initRoot;
    private static bool _packed;

    /// <summary>Installationsordner (enthält <c>bigfile_*.big</c> oder <c>Euro/Bnk</c>).</summary>
    public static string? Root => _root;
    /// <summary>Physischer/logischer Ordner <c>Euro/Bnk</c> (kann bei gepackten Installationen nur Video enthalten).</summary>
    public static string? BnkDir => _bnkDir;
    public static bool IsPacked => _packed;
    public static int ArchiveCount => Archives.Count;
    public static int NamedEntryCount => Names.Count;

    /// <summary>
    /// Initialisiert die Auflösung. Liefert den normalisierten Wurzelordner zurück.
    /// Mehrfachaufrufe mit demselben Pfad sind billig.
    /// </summary>
    public static string? Init(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) { Reset(); return null; }
        string full;
        try { full = Path.GetFullPath(root); } catch { return root; }

        lock (Lock)
        {
            if (_initRoot != null && string.Equals(_initRoot, full, StringComparison.OrdinalIgnoreCase)) return _root;
            ResetLocked();
            _initRoot = full;
            _root = full;
            _bnkDir = DetectBnkDir(full);
            _bnkDirFull = Path.GetFullPath(_bnkDir);

            foreach (var map in FindIndexFiles(full))
            {
                var a = BigArchive.Load(map);
                if (a != null) Archives.Add(a);
            }

            // Load-Order: Basisspiel 0, Updates 3..15, Mods 100/1337. Bei gleichem Wert entscheidet der Name.
            Archives.Sort((a, b) =>
            {
                int c = a.LoadOrder.CompareTo(b.LoadOrder);
                return c != 0 ? c : string.Compare(a.MapPath, b.MapPath, StringComparison.OrdinalIgnoreCase);
            });

            foreach (var a in Archives)
                for (int i = 0; i < a.Count; i++)
                    Entries[a.HashAt(i)] = new BigEntryRef(a, i);   // später geladene Archive überschreiben

            _packed = Archives.Count > 0;
            LoadNames();
            BuildDirs();

            if (_packed)
                Log.Info($"GameFiles: {Archives.Count} Bigfiles, {Entries.Count} Einträge ({Names.Count} benannt) – BnkDir = {_bnkDir}");
            else
                Log.Info($"GameFiles: lose Dateien (keine Bigfiles) – BnkDir = {_bnkDir}");
            return _root;
        }
    }

    private static void Reset()
    {
        lock (Lock) { ResetLocked(); _initRoot = null; }
    }

    private static void ResetLocked()
    {
        foreach (var a in Archives) a.Dispose();
        Archives.Clear();
        Entries.Clear();
        Names.Clear();
        Dirs.Clear();
        _root = null; _bnkDir = null; _bnkDirFull = null; _packed = false;
    }

    private static string DetectBnkDir(string root)
    {
        if (string.Equals(Path.GetFileName(root.TrimEnd('\\', '/')), "Bnk", StringComparison.OrdinalIgnoreCase))
            return root;
        var euroBnk = Path.Combine(root, "Euro", "Bnk");
        if (Directory.Exists(euroBnk)) return euroBnk;
        var bnk = Path.Combine(root, "Bnk");
        if (Directory.Exists(bnk)) return bnk;
        // Gepackte Installationen haben oft kein Euro/Bnk – der logische Pfad bleibt trotzdem gültig,
        // weil GameFiles ihn über den Bigfile-Hash auflöst.
        return euroBnk;
    }

    private static IEnumerable<string> FindIndexFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        string[] files;
        try { files = Directory.GetFiles(root, "*.map", SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var f in files)
        {
            var big = Path.ChangeExtension(f, ".big");
            if (File.Exists(big)) { yield return f; continue; }
            var stem = Path.GetFileNameWithoutExtension(f);
            bool found = false;
            try
            {
                foreach (var b in Directory.EnumerateFiles(root, "*.big", SearchOption.TopDirectoryOnly))
                    if (string.Equals(Path.GetFileNameWithoutExtension(b), stem, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            }
            catch { }
            if (found) yield return f;
        }
    }

    /// <summary>True, wenn der Ordner eine TDU2-Installation (lose oder gepackt) enthält.</summary>
    public static bool IsGameRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        try
        {
            if (Directory.Exists(Path.Combine(root, "Euro", "Bnk", "islands"))) return true;
            if (Directory.Exists(Path.Combine(root, "Bnk", "islands"))) return true;
            if (Directory.Exists(Path.Combine(root, "islands"))) return true;
            foreach (var m in Directory.EnumerateFiles(root, "*.map", SearchOption.TopDirectoryOnly))
                if (File.Exists(Path.ChangeExtension(m, ".big"))) return true;
        }
        catch { }
        return false;
    }

    private static void LoadNames()
    {
        if (Entries.Count == 0) return;
        try
        {
            using var s = typeof(GameFiles).Assembly.GetManifestResourceStream(NamesResource);
            if (s == null) { Log.Warn("GameFiles: bigfile-names.tsv fehlt (keine Verzeichnisnamen)"); return; }
            using var r = new StreamReader(s, Encoding.UTF8);
            string? line = r.ReadLine();   // Kopfzeile: hash archive kind path
            while ((line = r.ReadLine()) != null)
            {
                var f = line.Split('\t');
                if (f.Length < 4) continue;
                if (!ulong.TryParse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash)) continue;
                var path = f[3].Trim().Replace('\\', '/');
                if (!path.StartsWith("euro/bnk/", StringComparison.OrdinalIgnoreCase)) continue;
                path = path.ToLowerInvariant();
                if (Entries.ContainsKey(hash)) Names[hash] = path;
            }
        }
        catch (Exception ex) { Log.Warn("GameFiles: Namen nicht lesbar: " + ex.Message); }
    }

    private static void BuildDirs()
    {
        Dirs.Add("euro/bnk");
        foreach (var p in Names.Values)
        {
            int slash = p.LastIndexOf('/');
            while (slash > 0)
            {
                Dirs.Add(p[..slash]);
                slash = p.LastIndexOf('/', slash - 1);
            }
        }
    }

    /// <summary>Wandelt einen physischen Pfad unter <c>Euro/Bnk</c> in den virtuellen Bigfile-Pfad um.</summary>
    public static string? ToVirtual(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || _bnkDirFull == null) return null;
        string full;
        try { full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_bnkDirFull, path)); }
        catch { return null; }
        if (string.Equals(full, _bnkDirFull, StringComparison.OrdinalIgnoreCase)) return "euro/bnk";
        if (full.Length <= _bnkDirFull.Length || !full.StartsWith(_bnkDirFull, StringComparison.OrdinalIgnoreCase)) return null;
        char sep = full[_bnkDirFull.Length];
        if (sep != '\\' && sep != '/') return null;
        return "euro/bnk/" + full[(_bnkDirFull.Length + 1)..].Replace('\\', '/').ToLowerInvariant();
    }

    public static bool TryGetEntry(string path, out BigEntryRef entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) return false;
        var v = ToVirtual(path);
        if (v == null) return false;
        return Entries.TryGetValue(BigHash.Hash(v), out entry);
    }

    /// <summary>Existiert die Datei lose oder in einem Bigfile?</summary>
    public static bool Exists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (File.Exists(path)) return true;
        var v = ToVirtual(path);
        return v != null && Entries.ContainsKey(BigHash.Hash(v));
    }

    public static byte[] ReadAllBytes(string path)
    {
        if (File.Exists(path)) return File.ReadAllBytes(path);
        if (TryGetEntry(path, out var e)) return e.Read();
        throw new FileNotFoundException("Spieldatei nicht gefunden (lose oder im Bigfile): " + path, path);
    }

    public static Stream? OpenRead(string path)
    {
        if (File.Exists(path)) return File.OpenRead(path);
        if (TryGetEntry(path, out var e)) return new MemoryStream(e.Read(), writable: false);
        return null;
    }

    public static string ReadAllText(string path)
    {
        using var s = OpenRead(path) ?? throw new FileNotFoundException("Spieldatei nicht gefunden: " + path, path);
        using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    public static string[] ReadAllLines(string path)
    {
        using var s = OpenRead(path) ?? throw new FileNotFoundException("Spieldatei nicht gefunden: " + path, path);
        using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        string? line;
        while ((line = r.ReadLine()) != null) lines.Add(line);
        return lines.ToArray();
    }

    public static bool DirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Directory.Exists(path)) return true;
        if (!_packed) return false;
        var v = ToVirtual(path);
        if (v == null) return false;
        if (Dirs.Contains(v)) return true;
        string prefix = v.Equals("euro/bnk", StringComparison.OrdinalIgnoreCase) ? "euro/bnk/" : v + "/";
        foreach (var p in Names.Values)
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static IEnumerable<string> EnumerateFiles(string dir, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(dir))
        {
            string[] physical;
            try { physical = Directory.GetFiles(dir, pattern, option); }
            catch { physical = Array.Empty<string>(); }
            foreach (var f in physical)
            {
                if (seen.Add(Path.GetFullPath(f))) yield return f;
            }
        }

        if (!_packed) yield break;
        var vdir = ToVirtual(dir);
        if (vdir == null) yield break;
        string prefix = vdir.Equals("euro/bnk", StringComparison.OrdinalIgnoreCase) ? "euro/bnk/" : vdir + "/";

        foreach (var kv in Names)
        {
            var vp = kv.Value;
            if (!vp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = vp[prefix.Length..];
            if (option == SearchOption.TopDirectoryOnly && rest.Contains('/')) continue;
            var fileName = rest[(rest.LastIndexOf('/') + 1)..];
            if (!Wildcard(fileName, pattern)) continue;
            var rel = rest.Replace('/', '\\');
            var logical = Path.Combine(dir, rel);
            if (seen.Add(Path.GetFullPath(logical))) yield return logical;
        }
    }

    public static string[] GetFiles(string dir, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly)
        => EnumerateFiles(dir, pattern, option).ToArray();

    public static IEnumerable<string> EnumerateDirectories(string dir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(dir))
        {
            string[] physical;
            try { physical = Directory.GetDirectories(dir); }
            catch { physical = Array.Empty<string>(); }
            foreach (var d in physical)
                if (seen.Add(Path.GetFileName(d))) yield return d;
        }

        if (!_packed) yield break;
        var vdir = ToVirtual(dir);
        if (vdir == null) yield break;
        string prefix = vdir.Equals("euro/bnk", StringComparison.OrdinalIgnoreCase) ? "euro/bnk/" : vdir + "/";

        foreach (var d in Dirs)
        {
            if (!d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = d[prefix.Length..];
            if (rest.Length == 0) continue;
            int slash = rest.IndexOf('/');
            var child = slash < 0 ? rest : rest[..slash];
            if (seen.Add(child)) yield return Path.Combine(dir, child);
        }
    }

    public static string[] GetDirectories(string dir) => EnumerateDirectories(dir).ToArray();

    /// <summary>Name (virtueller Pfad) zu einem Hash, falls bekannt.</summary>
    public static string? NameOf(ulong hash) => Names.TryGetValue(hash, out var p) ? p : null;

    private static bool Wildcard(string text, string pattern)
    {
        int ti = 0, pi = 0, star = -1, match = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || char.ToUpperInvariant(pattern[pi]) == char.ToUpperInvariant(text[ti])))
            {
                ti++; pi++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi++; match = ti;
            }
            else if (star >= 0)
            {
                pi = star + 1; ti = ++match;
            }
            else return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }
}
