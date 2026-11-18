namespace Paradise.Core.Formats;

/// <summary>Big-endian reading aids (for the .map indexes of Bigfiles and .sob shader sets).</summary>
public static class Be
{
    public static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    public static int I32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
    public static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
    public static short I16(byte[] d, int o) => (short)((d[o] << 8) | d[o + 1]);
    public static ulong U64(byte[] d, int o) => ((ulong)U32(d, o) << 32) | U32(d, o + 4);
    public static long I64(byte[] d, int o) => (long)U64(d, o);
    public static float F32(byte[] d, int o) => BitConverter.Int32BitsToSingle(I32(d, o));
    public static double F64(byte[] d, int o) => BitConverter.Int64BitsToDouble(I64(d, o));
    /// <summary>4-Zeichen-Kennung (z.B. "IGE2", "MDSC").</summary>
    public static string Tag(byte[] d, int o) => System.Text.Encoding.ASCII.GetString(d, o, 4);
    public static bool TagIs(byte[] d, int o, string tag) =>
        o >= 0 && o + 4 <= d.Length && d[o] == tag[0] && d[o + 1] == tag[1] && d[o + 2] == tag[2] && d[o + 3] == tag[3];
}
