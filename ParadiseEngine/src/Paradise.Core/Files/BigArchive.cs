using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;

namespace Paradise.Core.Files;

/// <summary>
/// A TDU2 bigfile pair (<c>*.big</c> + <c>*.map</c>). The index is an XMBF document (usually big-endian,
/// though some mod tools write it in little-endian format), the entries are stored consecutively in the .big file and are
/// XOR-encrypted with the 4-byte key <c>D7 A8 E2 D4</c> (only full 32-bit words; the rest remains unencrypted).
/// </summary>
public sealed class BigArchive : IDisposable
{
    private static readonly byte[] XorKey = { 0xD7, 0xA8, 0xE2, 0xD4 };

    public string MapPath { get; private set; } = "";
    public string BigPath { get; private set; } = "";
    /// <summary>Load order from the index (0 = base game, higher = update/DLC/mod).</summary>
    public int LoadOrder { get; private set; }

    public int Count => _hashes.Length;

    private ulong[] _hashes = Array.Empty<ulong>();
    private uint[] _sizes = Array.Empty<uint>();
    private uint[] _offsets = Array.Empty<uint>();
    private readonly Dictionary<ulong, int> _index = new();
    private FileStream? _stream;
    private readonly object _lock = new();

    public ulong HashAt(int i) => _hashes[i];
    public int SizeAt(int i) => (int)_sizes[i];

    public static BigArchive? Load(string mapPath)
    {
        try
        {
            var a = new BigArchive { MapPath = mapPath };
            a.ReadMap();
            a.BigPath = Path.ChangeExtension(mapPath, ".big");
            if (!File.Exists(a.BigPath))
            {
                // Groß-/Kleinschreibung des .big ermitteln
                var dir = Path.GetDirectoryName(mapPath);
                var stem = Path.GetFileNameWithoutExtension(mapPath);
                if (dir != null)
                    foreach (var f in Directory.EnumerateFiles(dir, "*.big"))
                        if (string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase))
                        { a.BigPath = f; break; }
            }
            if (!File.Exists(a.BigPath))
            {
                Log.Warn($"Bigfile: {Path.GetFileName(mapPath)} without a matching .big extension");
                return null;
            }
            return a;
        }
        catch (Exception ex)
        {
            Log.Error($"Bigfile: {Path.GetFileName(mapPath)} cannot be read: {ex.Message}");
            return null;
        }
    }

    private void ReadMap()
    {
        var bytes = File.ReadAllBytes(MapPath);
        var root = XmbFile.Parse(bytes);
        var version = root.Get("Version");
        LoadOrder = version?.AsInt() ?? 0;

        var hashNode = root.Get("Hashcodes");
        var fileNode = root.Get("Files");
        if (hashNode == null || fileNode == null)
            throw new InvalidDataException("Directory.Hashcodes/Files are missing");

        int n = hashNode.Items.Count;
        if (fileNode.Items.Count != n)
            throw new InvalidDataException($"Hashcodes ({n}) and files ({fileNode.Items.Count}) differ");

        _hashes = new ulong[n];
        _sizes = new uint[n];
        _offsets = new uint[n];
        for (int i = 0; i < n; i++)
        {
            _hashes[i] = hashNode.Items[i].AsULong();
            var f = fileNode.Items[i];
            _sizes[i] = (uint)f.U("UncompressedSize");
            _offsets[i] = (uint)f.U("BigfileOffset");
            _index[_hashes[i]] = i;
        }
    }

    public bool TryFind(ulong hash, out int index) => _index.TryGetValue(hash, out index);

    /// <summary>Reads an entry in its entirety (including XOR decryption).</summary>
    public byte[] ReadEntry(int index)
    {
        var buf = new byte[_sizes[index]];
        ReadEntry(index, 0, buf, 0, buf.Length);
        return buf;
    }

    /// <summary>Reads a portion of an entry; <paramref name="offset"/> is relative to the beginning of the entry.</summary>
    public void ReadEntry(int index, long offset, byte[] buffer, int bufferOffset, int count)
    {
        int size = (int)_sizes[index];
        if (offset < 0 || count < 0 || offset + count > size) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count == 0) return;
        ReadRaw((long)_offsets[index] + offset, buffer, bufferOffset, count);
        Decrypt(buffer, bufferOffset, count, offset, size);
    }

    /// <summary>Reads directly from the .big file (without XOR) – for streams that decrypt themselves.</summary>
    public void ReadRaw(long offset, byte[] buffer, int bufferOffset, int count)
    {
        lock (_lock)
        {
            _stream ??= new FileStream(BigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _stream.Position = offset;
            int done = 0;
            while (done < count)
            {
                int r = _stream.Read(buffer, bufferOffset + done, count - done);
                if (r <= 0) throw new EndOfStreamException($"{BigPath} ends at {offset + done}");
                done += r;
            }
        }
    }

    /// <summary>
    /// XOR decryption of a portion of an entry. <paramref name="entryOffset"/> is the offset within the entry
    /// (determines the key phase), <paramref name="entrySize"/> limits the decryption to
    /// full 32-bit words—the last <c>len % 4</c> bytes are clearly within the file.
    /// </summary>
    public static void Decrypt(byte[] buffer, int offset, int count, long entryOffset, int entrySize)
    {
        long limit = entrySize & ~3L;
        for (int i = 0; i < count; i++)
        {
            long abs = entryOffset + i;
            if (abs >= limit) break;
            buffer[offset + i] ^= XorKey[(int)(abs & 3)];
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}