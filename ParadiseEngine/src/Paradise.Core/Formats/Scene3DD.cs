using System.Numerics;

namespace Paradise.Core.Formats;

/// <summary>A reference from an instance to an OBJ (a LOD level).</summary>
public struct LodRef
{
    public ulong ObjKey;      // Key des OBJ = Key der Geometrie im .3dg (GEOA-Hash)
    public int ObjOffset;     // Offset des OBJ.-Chunks
    public float RawDistance; // Wert aus der Datei (vermutlich quadratische Distanz)
    public int PrimCount;
    public Vector4 Sphere;    // Bounding-Sphere (lokal)
}

public sealed class InsRecord
{
    public int Offset;
    public ulong Key;          // nur gesetzt, wenn über den INSA-Hash gefunden
    /// <summary>Matrix wie in der Datei (D3D, Zeilenvektoren) = System.Numerics-Konvention.</summary>
    public Matrix4x4 Matrix;
    public int MaterialBankOffset;
    public LodRef[] Lods = Array.Empty<LodRef>();
}

public sealed class HieRecord
{
    public int Offset;
    public int InsOffset;
    public int ChildCount;
    public int ParentOffset;   // 0 = Wurzel
}

public struct HeightmapInfo
{
    public bool Present;
    public float HalfSize;     // 384 (Sektor) / 3072 (Area) / 49152 (Insel)
    public float Radius;
    public int Tiles;          // 16
    public float Spacing;      // 3 / 24 / 384 m
    /// <summary>
    /// Tiles (Tiles×Tiles, Index = Z-Tile·Tiles + X-Tile) that the original does not render as a heightmap:
    /// HMAP tile set (24 bytes: 4 float LOD errors, float average height, u32) with an error of −1. The terrain is represented there as
    /// “BLOCK” object in the sector (tunnel portals; its material is rendered with the sector texture, just like TD_Ground).
    /// </summary>
    public bool[]? HoleTiles;
}

/// <summary>
/// .3dd = Scene description. Ported from the Unity project.
///   MBNA: List of MBNKs (material banks: 16 bytes each {u64 materialKey, 8 bytes pad})
///   OBJA: HASH + OBJ. (payload: 3x u32 0, u32 primCount, float4 sphere) – or HMAP for terrain
///   INSA: HASH + INS. (payload 160 bytes):
///         +0  float4x4 matrix (D3D, row vectors, translation in row 3)
///         +68 u32 MBNK offset, +78 u16 number of LODs
///         +80 LOD entries, 20 bytes each {u32 pointer to OBJA hash entry, float distance, 12 bytes of 0}
///   HIEA: HASH + HIE. (payload 16 bytes: u32 0, u32 INS offset, u16 children, u16 ‘AP’, u32 parent HIE offset)
/// Matrices: Unity transposes to column vectors; System.Numerics uses row vectors
/// like D3D -> the file is imported unchanged, products are rotated accordingly.
/// </summary>
public sealed class Scene3DD
{
    public byte[] Data = Array.Empty<byte>();
    public Dictionary<ulong, int> ObjHash = new();
    public Dictionary<ulong, int> InsHash = new();
    public Dictionary<ulong, int> HieHash = new();
    // MBNK listen sind unveränderlich pro Datei; BuildObjectsAt las dieselben Offset mehrfach.
    readonly Dictionary<int, ulong[]> _materialBankCache = new();
    public readonly List<int> ObjOffsets = new();
    public readonly List<int> InsOffsets = new();
    public readonly List<HieRecord> Hierarchy = new();
    public readonly List<int> MaterialBankOffsets = new();
    public HeightmapInfo Heightmap;

    private readonly Dictionary<int, InsRecord> _insCache = new();

    public static Scene3DD Parse(byte[] d)
    {
        var s = new Scene3DD { Data = d };
        foreach (var c in EdenChunks.TopLevel(d))
        {
            switch (c.Tag)
            {
                case "MBNA":
                    foreach (var m in EdenChunks.Children(d, c))
                        if (m.Tag == "MBNK") s.MaterialBankOffsets.Add(m.Offset);
                    break;
                case "OBJA":
                    {
                        var list = new List<Chunk>();
                        s.ObjHash = EdenChunks.ReadArray(d, c, null, list);
                        foreach (var o in list)
                        {
                            if (o.Tag == "OBJ.") s.ObjOffsets.Add(o.Offset);
                            else if (o.Tag == "HMAP") s.Heightmap = ReadHmap(d, o);
                        }
                        break;
                    }
                case "INSA":
                    {
                        var list = new List<Chunk>();
                        s.InsHash = EdenChunks.ReadArray(d, c, "INS.", list);
                        foreach (var o in list) s.InsOffsets.Add(o.Offset);
                        break;
                    }
                case "HIEA":
                    {
                        var list = new List<Chunk>();
                        s.HieHash = EdenChunks.ReadArray(d, c, "HIE.", list);
                        foreach (var o in list)
                        {
                            int p = o.DataOffset;
                            s.Hierarchy.Add(new HieRecord
                            {
                                Offset = o.Offset,
                                InsOffset = (int)Bin.U32(d, p + 4),
                                ChildCount = Bin.U16(d, p + 8),
                                ParentOffset = (int)Bin.U32(d, p + 12),
                            });
                        }
                        break;
                    }
            }
        }
        // Keys der Instanzen zuordnen (für Library-Lookups und Namen)
        foreach (var kv in s.InsHash)
            s.GetIns(kv.Value).Key = kv.Key;
        return s;
    }

    private static HeightmapInfo ReadHmap(byte[] d, Chunk c)
    {
        int p = c.DataOffset;
        var info = new HeightmapInfo
        {
            Present = true,
            HalfSize = Bin.F32(d, p + 16),
            Radius = Bin.F32(d, p + 28),
            Tiles = (int)Bin.U32(d, p + 32),
            Spacing = Bin.F32(d, p + 44),
        };
        // Kachelsätze ab +48 (je 24 Byte); LOD-Fehler < 0 = Loch-Kachel (Ibiza: 2–4 Kacheln je Tunnelsektor)
        int t = info.Tiles, rec = p + 48;
        if (t > 0 && t <= 64 && rec + t * t * 24 <= c.End)
        {
            bool any = false;
            var holes = new bool[t * t];
            for (int i = 0; i < t * t; i++)
                if (Bin.F32(d, rec + i * 24) < 0f) { holes[i] = true; any = true; }
            if (any) info.HoleTiles = holes;
        }
        return info;
    }

    public InsRecord GetIns(int offset)
    {
        if (_insCache.TryGetValue(offset, out var r)) return r;
        var d = Data;
        int p = offset + 16;
        var m = new Matrix4x4(
            Bin.F32(d, p + 0), Bin.F32(d, p + 4), Bin.F32(d, p + 8), Bin.F32(d, p + 12),
            Bin.F32(d, p + 16), Bin.F32(d, p + 20), Bin.F32(d, p + 24), Bin.F32(d, p + 28),
            Bin.F32(d, p + 32), Bin.F32(d, p + 36), Bin.F32(d, p + 40), Bin.F32(d, p + 44),
            Bin.F32(d, p + 48), Bin.F32(d, p + 52), Bin.F32(d, p + 56), Bin.F32(d, p + 60));
        int nLod = Bin.U16(d, p + 78);
        if (nLod > 4) nLod = 4;
        var lods = new List<LodRef>();
        for (int i = 0; i < nLod; i++)
        {
            int le = p + 80 + i * 20;
            int ptr = (int)Bin.U32(d, le);
            float dist = Bin.F32(d, le + 4);
            if (ptr <= 0 || ptr + 16 > d.Length) continue;
            var lr = new LodRef
            {
                ObjKey = Bin.U64(d, ptr),
                ObjOffset = (int)Bin.U32(d, ptr + 8),
                RawDistance = dist,
            };
            if (lr.ObjOffset > 0 && lr.ObjOffset + 48 <= d.Length && Bin.TagIs(d, lr.ObjOffset, "OBJ."))
            {
                int q = lr.ObjOffset + 16;
                lr.PrimCount = (int)Bin.U32(d, q + 12);
                lr.Sphere = new Vector4(Bin.F32(d, q + 16), Bin.F32(d, q + 20), Bin.F32(d, q + 24), Bin.F32(d, q + 28));
            }
            lods.Add(lr);
        }
        r = new InsRecord
        {
            Offset = offset,
            Matrix = m,
            MaterialBankOffset = (int)Bin.U32(d, p + 68),
            Lods = lods.ToArray(),
        };
        _insCache[offset] = r;
        return r;
    }

    /// <summary>Material list (MBNK) for an instance. Index = PRIM.materialIndex.</summary>
    public ulong[] ReadMaterialBank(int mbnkOffset)
    {
        if (_materialBankCache.TryGetValue(mbnkOffset, out var cached)) return cached;
        var d = Data;
        if (mbnkOffset <= 0 || mbnkOffset + 16 > d.Length || !Bin.TagIs(d, mbnkOffset, "MBNK"))
            return Array.Empty<ulong>();
        int size = (int)Bin.U32(d, mbnkOffset + 8);
        int n = (size - 16) / 16;
        if (n <= 0) return Array.Empty<ulong>();
        var r = new ulong[n];
        for (int i = 0; i < n; i++) r[i] = Bin.U64(d, mbnkOffset + 16 + i * 16);
        _materialBankCache[mbnkOffset] = r;
        return r;
    }

    /// <summary>World matrix of an HIE node (product over the parent chain, row vectors: child * parent).</summary>
    public Matrix4x4 WorldMatrix(HieRecord h, Dictionary<int, HieRecord> byOffset)
    {
        var m = GetIns(h.InsOffset).Matrix;
        int guard = 0;
        var cur = h;
        while (cur.ParentOffset != 0 && byOffset.TryGetValue(cur.ParentOffset, out var parent) && guard++ < 64)
        {
            m = m * GetIns(parent.InsOffset).Matrix;
            cur = parent;
        }
        return m;
    }

    /// <summary>Matrix of the top-level ancestor (root of the hierarchy) of an HIE node.</summary>
    public Matrix4x4 RootMatrix(HieRecord h, Dictionary<int, HieRecord> byOffset)
    {
        var cur = h;
        int guard = 0;
        while (cur.ParentOffset != 0 && byOffset.TryGetValue(cur.ParentOffset, out var parent) && guard++ < 64) cur = parent;
        return GetIns(cur.InsOffset).Matrix;
    }
}