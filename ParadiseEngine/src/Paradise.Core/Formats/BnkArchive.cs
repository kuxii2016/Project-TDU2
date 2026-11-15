using Paradise.Core.Files;

namespace Paradise.Core.Formats;

/// <summary>Eintrag in einem TDU2-.bnk-Archiv.</summary>
public sealed class BnkEntry
{
    public int Index;
    public string Name = "";        // z.B. "sector-3-3-3-3.3dg"
    public string Extension = "";   // klein, mit Punkt, z.B. ".3dg"
    public string Directory = "";
    public long Offset;
    public int Size;
    public ushort Type;
    public ushort SubType;
    public uint Hash;

    public override string ToString() => $"{Name} ({Size} bytes, type {Type}/{SubType})";
}

/// <summary>Datenquelle einer Bank: lose Datei oder Eintrag in einem Bigfile.</summary>
public interface IBnkSource
{
    long Length { get; }
    void Read(long offset, byte[] buffer, int bufferOffset, int count);
}

/// <summary>Lose .bnk-Datei (wie bisher: kurzer FileStream je Lesezugriff).</summary>
public sealed class FileBnkSource : IBnkSource
{
    private readonly string _path;
    private readonly object _lock = new();
    public FileBnkSource(string path) => _path = path;
    public long Length => new FileInfo(_path).Length;
    public void Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        lock (_lock)
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Position = offset;
            BnkArchive.ReadExactly(fs, buffer, bufferOffset, count);
        }
    }
}

/// <summary>Bank, die in einem .big-Archiv liegt (inkl. XOR-Entschlüsselung beim Lesen).</summary>
public sealed class BigEntryBnkSource : IBnkSource
{
    private readonly BigEntryRef _entry;
    public BigEntryBnkSource(BigEntryRef entry) => _entry = entry;
    public long Length => _entry.Size;
    public void Read(long offset, byte[] buffer, int bufferOffset, int count)
        => _entry.Read(offset, buffer, bufferOffset, count);
}

/// <summary>
/// Leser für das Eden-Bankformat ("KNAB" in der Datei). Portiert aus dem
/// Unity-Projekt — reiner IO-Code, thread-sicher.
/// </summary>
public sealed class BnkArchive
{
    public string FilePath { get; private set; } = "";
    public readonly List<BnkEntry> Entries = new();
    private IBnkSource _source = null!;

    public static BnkArchive Open(string path)
    {
        var a = new BnkArchive { FilePath = path };
        if (File.Exists(path))
            a._source = new FileBnkSource(path);
        else if (GameFiles.TryGetEntry(path, out var entry))
            a._source = new BigEntryBnkSource(entry);
        else
            throw new FileNotFoundException("Bank nicht gefunden (lose oder im Bigfile): " + path, path);
        a.ReadHeader();
        return a;
    }

    public static BnkArchive Open(BigEntryRef entry, string logicalPath)
    {
        var a = new BnkArchive { FilePath = logicalPath, _source = new BigEntryBnkSource(entry) };
        a.ReadHeader();
        return a;
    }

    private void ReadHeader()
    {
        byte[] head;
        var first = new byte[0x48];
        _source.Read(0, first, 0, first.Length);
        if (!Bin.TagIs(first, 8, "KNAB"))
            throw new InvalidDataException("Keine TDU2-Bank (KNAB fehlt): " + FilePath);
        int headerSize = (int)Bin.U32(first, 0x44);
        if (headerSize <= 0x48 || headerSize > _source.Length) headerSize = (int)System.Math.Min(_source.Length, 1 << 20);
        head = new byte[headerSize];
        _source.Read(0, head, 0, headerSize);

        int nFiles = (int)Bin.U32(head, 0x28);
        int secFiles = (int)Bin.U32(head, 0x30);
        int secTypes = (int)Bin.U32(head, 0x34);
        int secNames = (int)Bin.U32(head, 0x38);
        int secOrder = (int)Bin.U32(head, 0x3C);

        for (int i = 0; i < nFiles; i++)
        {
            int o = secFiles + 8 + i * 20;
            var e = new BnkEntry
            {
                Index = i,
                Offset = Bin.U32(head, o),
                Size = (int)Bin.U32(head, o + 4),
                Hash = Bin.U32(head, o + 12),
            };
            int t = secTypes + 8 + i * 4;
            e.SubType = Bin.U16(head, t);
            e.Type = Bin.U16(head, t + 2);
            Entries.Add(e);
        }

        var names = ReadNameTree(head, secNames, secOrder, nFiles);

        int orderSize = (int)Bin.U32(head, secOrder);
        bool wide = nFiles > 0 && orderSize >= nFiles * 2;
        for (int ni = 0; ni < names.Count && ni < nFiles; ni++)
        {
            int fi = wide ? Bin.U16(head, secOrder + 8 + ni * 2) : head[secOrder + 8 + ni];
            if (fi < 0 || fi >= nFiles) continue;
            var path = names[ni];
            var e = Entries[fi];
            string leaf = path[^1];
            string ext = path.Length >= 2 ? path[^2] : "";
            e.Name = leaf + ext;
            e.Extension = ext.ToLowerInvariant();
            e.Directory = path.Length > 2 ? string.Join("/", path, 0, path.Length - 2) : "";
        }
        foreach (var e in Entries)
            if (string.IsNullOrEmpty(e.Name)) { e.Name = "file" + e.Index; e.Extension = ""; e.Directory = ""; }
    }

    /// <summary>
    /// Liest den Namensbaum und wählt automatisch die passende Kodierung. Es gibt
    /// mindestens drei Varianten in freier Wildbahn:
    ///  A) Original: Ordner = Byte &gt;= 0x80, danach Kinderzahl als LEB128-Varint.
    ///  B) Einzelbyte: wie A, aber die Kinderzahl ist ein einzelnes Byte (Mod-Werkzeuge;
    ///     z.B. 0x84 = 132 Kinder statt LEB128 "0x84 0x2E" = 5892).
    ///  C) Neu gepackte Bänke (Version 2026): Ordner = [Länge][Kinderzahl][Name], Datei =
    ///     [Länge][Name]; Dateien hängen unter einem Endungsknoten (".2db" ...).
    /// Alle Varianten werden ausfallsicher geparst (keine Exceptions); gewählt wird der
    /// vollständigste, sauber beendete Baum, bevorzugt mit genau <paramref name="nFiles"/> Blättern.
    /// </summary>
    private static List<string[]> ReadNameTree(byte[] head, int secNames, int secOrder, int nFiles)
    {
        var empty = new List<string[]>();
        if (secNames <= 0 || secNames > head.Length - 8) return empty;

        int p = secNames + 8;
        long namesSize = Bin.U32(head, secNames);
        long endL = (long)secNames + 8 + namesSize;
        int end = (int)System.Math.Min(endL, System.Math.Min(secOrder, head.Length));
        if (end <= p) return empty;

        // Variante C erkennt man am ersten Byte: Original- und Einzelbyte-Bäume beginnen
        // mit einem Ordner (>= 0x80), Variante C mit einem Längenbyte < 0x80.
        bool looksV2 = head[p] != 0 && head[p] < 0x80;

        var candidates = new List<(NameWalkResult result, bool preferred, string name)>();
        if (looksV2)
            candidates.Add((WalkNamesV2(head, p, end), true, "V2"));
        else
        {
            candidates.Add((WalkNames(head, p, end, singleByteCount: false), true, "LEB128"));
            candidates.Add((WalkNames(head, p, end, singleByteCount: true), false, "Einzelbyte"));
            candidates.Add((WalkNamesV2(head, p, end), false, "V2"));
        }

        NameWalkResult best = candidates[0].result;
        string bestName = candidates[0].name;
        bool bestPreferred = candidates[0].preferred;
        int bestScore = NameScore(best, nFiles, bestPreferred);
        for (int i = 1; i < candidates.Count; i++)
        {
            int score = NameScore(candidates[i].result, nFiles, candidates[i].preferred);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidates[i].result;
                bestName = candidates[i].name;
                bestPreferred = candidates[i].preferred;
            }
        }

        if (!best.Clean)
            Diagnostics.Log.Warn(
                $"Namensbaum unvollständig ({bestName}): {best.Names.Count}/{nFiles} Namen, {best.Error}");
        else if (!bestPreferred)
            Diagnostics.Log.Debug($"Namensbaum: Variante {bestName} verwendet ({best.Names.Count} Namen)");

        return best.Names;
    }

    private static int NameScore(NameWalkResult r, int nFiles, bool preferred)
    {
        int score = r.Clean ? 1_000_000 : -1_000_000;
        if (r.Names.Count == nFiles) score += 100_000;
        score += System.Math.Min(r.Names.Count, 10_000);
        if (preferred) score += 1_000;
        return score;
    }

    /// <summary>Ergebnis eines Namensbaum-Versuchs (immer ausfallsicher, wirft nie).</summary>
    private sealed class NameWalkResult
    {
        public readonly List<string[]> Names = new();
        public bool Clean;          // Stack am Ende leer und Baum sauber beendet
        public bool Terminated;     // am Null-Terminator bzw. exakt am Bereichsende angekommen
        public int MaxDepth;
        public int EndPos;
        public string Error = "";
    }

    /// <summary>Gehärteter Walker für die Varianten A/B (Ordner mit 0x80-Markierung).</summary>
    private static NameWalkResult WalkNames(byte[] d, int start, int end, bool singleByteCount)
    {
        const int MaxDepth = 64;
        var result = new NameWalkResult();
        var stack = new List<string>(16);
        bool failed = false;
        string error = "";

        int Rec(int p, int depth)
        {
            if (failed) return end;
            if (p < 0 || p >= end) { failed = true; error = $"Position 0x{p:X} außerhalb 0x{start:X}..0x{end:X}"; return end; }
            if (depth > MaxDepth) { failed = true; error = $"Rekursionstiefe > {MaxDepth}"; return end; }
            if (depth > result.MaxDepth) result.MaxDepth = depth;

            byte b = d[p];
            if (b >= 0x80)
            {
                int len = 0x100 - b;
                int q = p + 1, count;

                if (singleByteCount)
                {
                    if (q >= end) { failed = true; error = "Kinderzahl fehlt"; return end; }
                    count = d[q++];
                }
                else
                {
                    count = 0;
                    int shift = 0;
                    while (true)
                    {
                        if (q >= end) { failed = true; error = "Varint läuft über das Bereichsende"; return end; }
                        byte v = d[q++];
                        count |= (v & 0x7F) << shift;
                        if ((v & 0x80) == 0) break;
                        shift += 7;
                        if (shift > 21) { failed = true; error = "Varint ungültig"; return end; }
                    }
                }

                if (len <= 0 || q + len > end) { failed = true; error = "Ordnername läuft über das Bereichsende"; return end; }
                // Jedes Kind belegt mindestens ein Byte; eine größere Zahl ist immer falsch.
                if (count > end - (q + len)) { failed = true; error = $"Kinderzahl {count} größer als Restbereich {end - (q + len)}"; return end; }

                string s = Bin.Latin1(d, q, len);
                stack.Add(s);
                p = q + len;
                for (int i = 0; i < count; i++)
                {
                    p = Rec(p, depth + 1);
                    if (failed) { stack.RemoveAt(stack.Count - 1); return end; }
                }
                stack.RemoveAt(stack.Count - 1);
                return p;
            }
            else
            {
                int leafLen = b;
                if (leafLen <= 0) { failed = true; error = "Dateiname mit Länge 0"; return end; }
                if (p + 1 + leafLen > end) { failed = true; error = "Dateiname läuft über das Bereichsende"; return end; }
                var full = new string[stack.Count + 1];
                stack.CopyTo(full);
                full[stack.Count] = Bin.Latin1(d, p + 1, leafLen);
                result.Names.Add(full);
                return p + 1 + leafLen;
            }
        }

        int pos = start;
        while (pos < end && !failed)
        {
            if (d[pos] == 0) { result.Terminated = true; break; }
            int next = Rec(pos, 0);
            if (failed) break;
            if (next <= pos) { failed = true; error = "Parser macht keinen Fortschritt"; break; }
            pos = next;
        }

        result.EndPos = pos;
        if (!failed && pos >= end) result.Terminated = true;
        result.Clean = !failed && stack.Count == 0 && result.Terminated;
        result.Error = failed ? error : "";
        return result;
    }

    /// <summary>Gehärteter Walker für Variante C (neu gepackte Bänke, ohne 0x80-Markierung).</summary>
    private static NameWalkResult WalkNamesV2(byte[] d, int start, int end)
    {
        const int MaxDepth = 64;
        var result = new NameWalkResult();
        var stack = new List<string>(16);
        bool failed = false;
        string error = "";

        int Rec(int p, int depth)
        {
            if (failed) return end;
            if (p < 0 || p >= end) { failed = true; error = $"Position 0x{p:X} außerhalb 0x{start:X}..0x{end:X}"; return end; }
            if (depth > MaxDepth) { failed = true; error = $"Rekursionstiefe > {MaxDepth}"; return end; }
            if (depth > result.MaxDepth) result.MaxDepth = depth;

            bool leaf = stack.Count > 0 && stack[^1].StartsWith('.');
            int len = d[p];
            if (len <= 0) { failed = true; error = "Knoten mit Länge 0"; return end; }
            // In Variante C sind alle Längen Einzelbytes < 0x80; 0x80+ bedeutet, dass es
            // gar kein V2-Baum ist (dann schlägt der Versuch sauber fehl).
            if (len >= 0x80) { failed = true; error = "Längenbyte >= 0x80 (kein V2-Baum)"; return end; }

            if (leaf)
            {
                if (p + 1 + len > end) { failed = true; error = "Dateiname läuft über das Bereichsende"; return end; }
                var full = new string[stack.Count + 1];
                stack.CopyTo(full);
                full[stack.Count] = Bin.Latin1(d, p + 1, len);
                result.Names.Add(full);
                return p + 1 + len;
            }

            int q = p + 1, count = 0, shift = 0;
            while (true)
            {
                if (q >= end) { failed = true; error = "Varint läuft über das Bereichsende"; return end; }
                byte v = d[q++];
                count |= (v & 0x7F) << shift;
                if ((v & 0x80) == 0) break;
                shift += 7;
                if (shift > 21) { failed = true; error = "Varint ungültig"; return end; }
            }
            if (q + len > end) { failed = true; error = "Ordnername läuft über das Bereichsende"; return end; }
            if (count > end - (q + len)) { failed = true; error = $"Kinderzahl {count} größer als Restbereich {end - (q + len)}"; return end; }

            stack.Add(Bin.Latin1(d, q, len));
            p = q + len;
            for (int i = 0; i < count; i++)
            {
                p = Rec(p, depth + 1);
                if (failed) { stack.RemoveAt(stack.Count - 1); return end; }
            }
            stack.RemoveAt(stack.Count - 1);
            return p;
        }

        int pos = start;
        while (pos < end && !failed)
        {
            if (d[pos] == 0) { result.Terminated = true; break; }
            int next = Rec(pos, 0);
            if (failed) break;
            if (next <= pos) { failed = true; error = "Parser macht keinen Fortschritt"; break; }
            pos = next;
        }

        result.EndPos = pos;
        if (!failed && pos >= end) result.Terminated = true;
        result.Clean = !failed && stack.Count == 0 && result.Terminated;
        result.Error = failed ? error : "";
        return result;
    }

    internal static void ReadExactly(Stream s, byte[] buf, int off, int count)
    {
        while (count > 0)
        {
            int r = s.Read(buf, off, count);
            if (r <= 0) throw new EndOfStreamException();
            off += r; count -= r;
        }
    }

    /// <summary>Liest einen kompletten Eintrag (thread-sicher).</summary>
    public byte[] Read(BnkEntry e)
    {
        var buf = new byte[e.Size];
        _source.Read(e.Offset, buf, 0, e.Size);
        return buf;
    }

    /// <summary>Liest nur die ersten <paramref name="count"/> Bytes eines Eintrags (für Header-Peek).</summary>
    public byte[] ReadPartial(BnkEntry e, int offset, int count)
    {
        count = System.Math.Min(count, e.Size - offset);
        if (count <= 0) return Array.Empty<byte>();
        var buf = new byte[count];
        _source.Read(e.Offset + offset, buf, 0, count);
        return buf;
    }

    public BnkEntry? Find(string name)
    {
        foreach (var e in Entries)
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    public IEnumerable<BnkEntry> All(string ext)
    {
        foreach (var e in Entries) if (e.Extension == ext) yield return e;
    }
}
