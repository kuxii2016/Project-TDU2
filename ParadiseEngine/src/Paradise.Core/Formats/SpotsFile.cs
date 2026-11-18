using System.Numerics;

namespace Paradise.Core.Formats;

public sealed class Spot
{
    public int Index;
    public ulong Key;          // komprimierter Name
    public string Code = "";   // lesbarer Teil, z.B. "ECD_ITAL" (= exterior/ecd_ital.bnk)
    public Vector3 Position;   // Weltposition (Inselkoordinaten)
    public Vector3 Position2;  // zweite Position (Ein-/Ausfahrt)
    public float Value1, Heading2;
    public int AreaX, AreaZ, SectorX, SectorZ; // Bytes 52..55
    public ushort TypeA, TypeB;                // u16 @256/@258
    public Matrix4x4 Box1, Box2;               // zwei Transformationen (Trigger/Kamera?), Zeilenvektoren wie in der Datei

    public string Category => SpotsFile.CategoryFromCode(Code);

    public override string ToString() =>
        $"{Index}, {Key}, {Code}, {Position}, {Position2}, {Value1}, {Heading2}, {AreaX}, {AreaZ}, {SectorX}, {SectorZ}, {TypeA}, {TypeB}";
}

/// <summary>
/// level/spots.bin = POIs (dealerships, tuning shops, houses, car washes, etc.). Ported from the Unity project.
///   “.SPT” u32 Version(2) u32 1 u32 Hash, “SPTA” @0x10, entries starting at 0x20, 272 bytes each:
///   +0 u64 Key, +8 char[8] Code, +16 float3 Position, +28 float, +32 float, +36 float,
///   +40 float3 Position2, +52 u8 areaX, areaZ, sectorX, sectorZ, +56 float Direction,
///   +64 u16[32] Sector list (0xFFFF = empty), +128 2× float4x4, +256 u16 typA, u16 typB.
/// </summary>
public sealed class SpotsFile
{
    public readonly List<Spot> Spots = new();
    private const int Stride = 272;

    public static SpotsFile Parse(byte[]? d)
    {
        var f = new SpotsFile();
        if (d == null || d.Length < 0x20 || !Bin.TagIs(d, 0, ".SPT")) return f;
        int n = (d.Length - 0x20) / Stride;
        for (int i = 0; i < n; i++)
        {
            int o = 0x20 + i * Stride;
            var s = new Spot
            {
                Index = i,
                Key = Bin.U64(d, o),
                Code = Bin.CString(d, o + 8, 8),
                Position = new Vector3(Bin.F32(d, o + 16), Bin.F32(d, o + 20), Bin.F32(d, o + 24)),
                Value1 = Bin.F32(d, o + 32),
                Position2 = new Vector3(Bin.F32(d, o + 40), Bin.F32(d, o + 44), Bin.F32(d, o + 48)),
                AreaX = d[o + 52],
                AreaZ = d[o + 53],
                SectorX = d[o + 54],
                SectorZ = d[o + 55],
                Heading2 = Bin.F32(d, o + 56),
                TypeA = Bin.U16(d, o + 256),
                TypeB = Bin.U16(d, o + 258),
                Box1 = ReadMatrix(d, o + 128),
                Box2 = ReadMatrix(d, o + 192),
            };
            if (float.IsNaN(s.Position.X)) continue;
            f.Spots.Add(s);
        }
        return f;
    }

    private static Matrix4x4 ReadMatrix(byte[] d, int p) => new(
        Bin.F32(d, p + 0), Bin.F32(d, p + 4), Bin.F32(d, p + 8), Bin.F32(d, p + 12),
        Bin.F32(d, p + 16), Bin.F32(d, p + 20), Bin.F32(d, p + 24), Bin.F32(d, p + 28),
        Bin.F32(d, p + 32), Bin.F32(d, p + 36), Bin.F32(d, p + 40), Bin.F32(d, p + 44),
        Bin.F32(d, p + 48), Bin.F32(d, p + 52), Bin.F32(d, p + 56), Bin.F32(d, p + 60));

    public static string CategoryFromCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return "Unbekannt";
        var c = code.ToUpperInvariant();
        if (c.StartsWith("ECDC")) return "Car dealer Classic";
        if (c.StartsWith("ECD")) return "Car dealer";
        if (c.StartsWith("EBD")) return "Motorcycle Dealer";
        if (c.StartsWith("ETUN")) return "Tuning";
        if (c.StartsWith("ECW")) return "Car Wash";
        if (c.StartsWith("EPH")) return "House";
        if (c.StartsWith("ECH")) return "Club House";
        if (c.StartsWith("EAS")) return "Clothing Store";
        if (c.StartsWith("EHD")) return "Hair Shop";
        if (c.StartsWith("EGAM")) return "Comunity Racing Center";
        if (c.StartsWith("EAIR")) return "Airport";
        if (c.StartsWith("EDI")) return "EDI";
        if (c.StartsWith("ELAND")) return "Landing Strip";
        if (c.StartsWith("ECAS")) return "Casino";
        return "Spot";
    }
}