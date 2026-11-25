using Paradise.Core.Diagnostics;
using System.Globalization;
using System.Text;

namespace Paradise.Core.Files;

/// <summary>Reference to an entry in a Bigfile.</summary>
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
/// Hybrid file access to TDU2 game data: individual files under <c>Euro/Bnk</c> take precedence
/// (mods); otherwise, the <c>*.big</c> archives are searched (base game, updates, DLCs).
/// Virtual paths are always <c>euro/bnk/…</c> (these are the hashes in the .map indexes).
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

    /// <summary>Installation folder (contains <c>bigfile_*.big</c> or <c>Euro/Bnk</c>).</summary>
    public static string? Root => _root;
    /// <summary>Physical/logical folder <c>Euro/Bnk</c> (may contain only video files in packaged installations).</summary>
    public static string? BnkDir => _bnkDir;
    public static bool IsPacked => _packed;
    public static int ArchiveCount => Archives.Count;
    public static int NamedEntryCount => Names.Count;

    /// <summary>
    /// Initializes the resolution. Returns the normalized root folder.
    /// Multiple calls with the same path are inexpensive.
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

            Archives.Sort((a, b) =>
            {
                int c = a.LoadOrder.CompareTo(b.LoadOrder);
                return c != 0 ? c : string.Compare(a.MapPath, b.MapPath, StringComparison.OrdinalIgnoreCase);
            });

            foreach (var a in Archives)
                for (int i = 0; i < a.Count; i++)
                    Entries[a.HashAt(i)] = new BigEntryRef(a, i);

            _packed = Archives.Count > 0;
            LoadNames();
            LoadArchiveFileLists();
            BuildDirs();

            if (_packed)
                Log.Info($"GameFiles: {Archives.Count} large files, {Entries.Count} entries ({Names.Count} named) – BnkDir = {_bnkDir}");
            else
                Log.Info($"GameFiles: individual files (not large files) – BnkDir = {_bnkDir}");
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
            if (s == null)
            {
                Log.Warn("GameFiles: bigfile-names.tsv is missing (no directory names)");
                return;
            }
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
        catch (Exception ex)
        {
            Log.Error("GameFiles: Names are unreadable: " + ex.Message);
        }
    }

    /// <summary>
    /// Ergänzt Namen aus <c>filelist.txt</c>-Einträgen der geladenen Bigfiles. XMBEdit/ModPacker legt
    /// diese Datei in jedes Custom-Archiv; die eingebettete TSV bleibt primär und wird nicht überschrieben.
    /// </summary>
    private static void LoadArchiveFileLists()
    {
        if (Entries.Count == 0) return;
        // XMBEdit: MapHash.GenerateHash("filelist.txt") – identisch mit BigHash.Hash().
        ulong listHash = BigHash.Hash("filelist.txt");
        int added = 0;

        // Bewusst pro Archiv: mehrere Custom-Bigfiles enthalten denselben filelist.txt-Hash,
        // das globale Entries-Dictionary würde nur das letzte Archiv behalten.
        foreach (var archive in Archives)
        {
            if (!archive.TryFind(listHash, out int index)) continue;

            string text;
            try { text = Encoding.UTF8.GetString(archive.ReadEntry(index)); }
            catch (Exception ex)
            {
                Log.Warn($"GameFiles: filelist.txt in {Path.GetFileName(archive.MapPath)} unreadable: {ex.Message}");
                continue;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line[0] == '#') continue;
                int q1 = line.IndexOf('"');
                if (q1 <= 0) continue;
                int q2 = line.IndexOf('"', q1 + 1);
                if (q2 <= q1 + 1) continue;
                if (!ulong.TryParse(line[..q1].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash)) continue;

                var path = line[(q1 + 1)..q2].Replace('\\', '/').ToLowerInvariant();
                if (path.StartsWith("bnk/", StringComparison.Ordinal)) path = "euro/" + path;
                if (!path.StartsWith("euro/bnk/", StringComparison.Ordinal)) continue;
                if (!Entries.ContainsKey(hash) || Names.ContainsKey(hash)) continue;

                Names[hash] = path;
                added++;
            }
        }

        if (added > 0) Log.Info($"GameFiles: {added} additional names from archive filelists");
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

    /// <summary>Converts a physical path under <c>Euro/Bnk</c> to the virtual Bigfile path.</summary>
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

    /// <summary>Does the file exist as a separate file or as part of a bigfile?</summary>
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
        throw new FileNotFoundException("Game file not found (separate file or in Bigfile):" + path, path);
    }

    public static Stream? OpenRead(string path)
    {
        if (File.Exists(path)) return File.OpenRead(path);
        if (TryGetEntry(path, out var e)) return new MemoryStream(e.Read(), writable: false);
        return null;
    }

    public static string ReadAllText(string path)
    {
        using var s = OpenRead(path) ?? throw new FileNotFoundException("Game file not found:" + path, path);
        using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    public static string[] ReadAllLines(string path)
    {
        using var s = OpenRead(path) ?? throw new FileNotFoundException("Game file not found:" + path, path);
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

    public static string[] GetFiles(string dir, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly) => EnumerateFiles(dir, pattern, option).ToArray();

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

    /// <summary>Name (virtual path) of a hash, if known.</summary>
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