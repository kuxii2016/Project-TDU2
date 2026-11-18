using System.Text;

namespace Paradise.Core.Files;

/// <summary>
/// The path hash of the TDU2 bigfiles (CRC-64, MSB-first): based on the lowercase path with ‘/’,
/// result with the 32-bit halves swapped. Example: <c>euro/bnk/fx/sun.bnk</c> → 029495bc4ec264f3.
/// </summary>
public static class BigHash
{
    private const ulong Poly = 0x0060034000F0D50BUL;
    private const ulong Seed = 0xFAC432B10CD5E44AUL;

    private static readonly ulong[] Table = BuildTable();

    private static ulong[] BuildTable()
    {
        var t = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            ulong v = (ulong)i << 56;
            for (int b = 0; b < 8; b++)
                v = (v & 0x8000000000000000UL) != 0 ? (v << 1) ^ Poly : v << 1;
            t[i] = v;
        }
        return t;
    }

    /// <summary>Hash of a Bigfile path (<c>euro/bnk/...</c>, case-insensitive).</summary>
    public static ulong Hash(string path)
    {
        ulong h = Seed;
        if (string.IsNullOrEmpty(path)) return Swap(h);
        // The paths are in Latin-1 (e.g., “vfx/common/hawaï”); ToLowerInvariant returns the same bytes for ASCII.
        var bytes = Encoding.Latin1.GetBytes(path.ToLowerInvariant());
        foreach (byte b in bytes)
            h = ((h << 8) | b) ^ Table[h >> 56];
        return Swap(h);
    }

    /// <summary>True if <paramref name="path"/> is already a complete Bigfile path.</summary>
    public static bool IsBigPath(string path) => path.StartsWith("euro/bnk/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("euro\\bnk\\", StringComparison.OrdinalIgnoreCase);

    private static ulong Swap(ulong h) => (h << 32) | (h >> 32);
}