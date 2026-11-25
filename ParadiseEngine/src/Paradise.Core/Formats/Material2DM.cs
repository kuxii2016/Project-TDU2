using System.Numerics;

namespace Paradise.Core.Formats;

public struct MaterialLayer
{
    public ulong Semantic;   // 8 Byte, z.B. "COLOR", "SHADOW", "NORMAL", "DETAIL1" (teils komprimiert)
    public ulong TextureKey; // Key einer .2db (BMAP) Textur
    public byte AddressU;    // D3DTEXTUREADDRESS: 1 = Wrap, 2 = Mirror, 3 = Clamp, 4 = Border (TextureDef.WrapFromEden)
    public byte AddressV;
    /// <summary>Slot-Nummer im Material (= Sampler sN des Original-Shaders).</summary>
    public int Slot;
    public string SemanticName => Bin.KeyToString(Semantic);
}

/// <summary>An Eden asset.</summary>
public sealed class MaterialDef
{
    public ulong Key;
    public string Name = "";
    public byte[] Flags = new byte[16];
    /// <summary>TD_Water (pools “Oasis_*”, fountains).</summary>
    public const ulong ShaderWater = 0x0123000000000000UL;
    public bool IsPoolWater => ShaderSet == ShaderWater;
    /// <summary>Shader set from the original (MAT.+0x310), e.g., TD_Road / TD_Ground / TD_Building.</summary>
    public ulong ShaderSet;
    /// <summary>48 Shader-Schalter (S_NBTEXTURES … S_USERTYPE16) hinter der Satz-GUID (OpenTDU2 eden_shader_system.h: MAT.+0x310
    /// relativ zum Chunk-Inhalt = hier +0x320, die GUID liegt hier entsprechend bei +0x310). Für den Original-Renderer.</summary>
    public byte[]? Spec;
    /// <summary>Diffuse color from the color block (MAT.+0xC0 ambient/diffuse/specular/emissive).</summary>
    public Vector4 Diffuse = Vector4.One;
    /// <summary>D3DMATERIAL9-Farben nach Diffuse (MAT. +0xD0/+0xE0/+0xF0, OpenTDU2 2dm.cpp): für die Original-Shader-Konstanten.</summary>
    public Vector4 Ambient = Vector4.One, Specular = new(0, 0, 0, 1), Emissive = new(0, 0, 0, 1);
    public const ulong ShaderRoad = 0xc8ad93c95455291bUL, ShaderGround = 0xb0f1234b0f123473UL;
    public readonly List<MaterialLayer> Layers = new();
    /// <summary>Shader parameter (Key = 8-byte name, e.g., “RAMPCLR1”) -> float4.</summary>
    public readonly Dictionary<ulong, Vector4> Params = new();

    public bool TryGetParam(string name, out Vector4 v) => Params.TryGetValue(Bin.KeyFromString(name), out v);

    /// <summary>Material key = “defective” name in uppercase (bytes cyclically padded to 8 bytes).</summary>
    public static ulong KeyFromName(string name)
    {
        var b = new byte[8];
        string u = name.ToUpperInvariant();
        for (int i = 0; i < u.Length; i++) b[i % 8] = (byte)(b[i % 8] + (byte)u[i]);
        return BitConverter.ToUInt64(b, 0);
    }

    public static readonly ulong SemColor = Bin.KeyFromString("COLOR");
    public static readonly ulong SemShadow = Bin.KeyFromString("SHADOW");
    public static readonly ulong SemNormal = Bin.KeyFromString("NORMAL");
    public static readonly ulong SemDetail = Bin.KeyFromString("DETAIL");
    public static readonly ulong SemDetail1 = Bin.KeyFromString("DETAIL1");
    public static readonly ulong SemDetail2 = Bin.KeyFromString("DETAIL2");

    public bool TryGetLayer(ulong semantic, out MaterialLayer layer)
    {
        foreach (var l in Layers)
            if (l.Semantic == semantic && l.TextureKey != 0) { layer = l; return true; }
        layer = default;
        return false;
    }

    public bool IsOcean => Name.Contains("ocean", StringComparison.OrdinalIgnoreCase);
    /// <summary>FX Materials (Light Cones, Halos) – Volumetric Effects.</summary>
    public bool IsEffect => Name.StartsWith("FX_", StringComparison.OrdinalIgnoreCase);
    /// <summary>Roads: The main texture is the DETAIL layer on the last UV set; COLOR is just a variation.</summary>
    public bool IsRoad => ShaderSet == ShaderRoad || Name.StartsWith("Road", StringComparison.OrdinalIgnoreCase) || Name.StartsWith("crossroad", StringComparison.OrdinalIgnoreCase);
    /// <summary>TD_Ground (plazas, sidewalks, bases): Color = COLOR x DETAIL.r.</summary>
    public bool IsGround => ShaderSet == ShaderGround;
}

/// <summary>
/// .2dm = Material library.
///   MATA: HASH + MAT. chunks. MAT. (offsets relative to the start of the chunk):
///     +0x10 u64 key, +0x20 char[32] name, +0x40 flags,
///     +0xC0.. colors (float4 ambient/diffuse/specular/emissive),
///     +0x10C u16 number of layers, +0x110 layers (32 bytes each) {u64 semantics, u64 texture key, 8 bytes 0, u8 addrU, u8 addrV, u8, u8, 4 bytes}
///     followed by shader hash and shader parameters.
/// </summary>
public sealed class Material2DM
{
    public static ulong KeyFromName(string name) => MaterialDef.KeyFromName(name);

    public readonly Dictionary<ulong, MaterialDef> Materials = new();

    public static Material2DM Parse(byte[] d)
    {
        var lib = new Material2DM();
        foreach (var c in EdenChunks.TopLevel(d))
        {
            if (c.Tag != "MATA") continue;
            foreach (var m in EdenChunks.Children(d, c))
            {
                if (m.Tag != "MAT.") continue;
                var def = ParseMat(d, m);
                if (def != null) lib.Materials[def.Key] = def;
            }
        }
        return lib;
    }

    private static MaterialDef? ParseMat(byte[] d, Chunk m)
    {
        int o = m.Offset;
        if (m.Size < 0x110) return null;
        var def = new MaterialDef
        {
            Key = Bin.U64(d, o + 0x10),
            Name = Bin.CString(d, o + 0x20, 32),
        };
        Array.Copy(d, o + 0x40, def.Flags, 0, 16);
        if (o + 0xD0 <= m.End)
            def.Diffuse = new Vector4(Bin.F32(d, o + 0xC0), Bin.F32(d, o + 0xC4), Bin.F32(d, o + 0xC8), Bin.F32(d, o + 0xCC));
        if (o + 0x100 <= m.End)
        {
            def.Ambient = new Vector4(Bin.F32(d, o + 0xD0), Bin.F32(d, o + 0xD4), Bin.F32(d, o + 0xD8), Bin.F32(d, o + 0xDC));
            def.Specular = new Vector4(Bin.F32(d, o + 0xE0), Bin.F32(d, o + 0xE4), Bin.F32(d, o + 0xE8), Bin.F32(d, o + 0xEC));
            def.Emissive = new Vector4(Bin.F32(d, o + 0xF0), Bin.F32(d, o + 0xF4), Bin.F32(d, o + 0xF8), Bin.F32(d, o + 0xFC));
        }
        if (o + 0x318 <= m.End) def.ShaderSet = Bin.U64(d, o + 0x310);
        if (o + 0x320 + 48 <= m.End) { def.Spec = new byte[48]; Array.Copy(d, o + 0x320, def.Spec, 0, 48); }
        int n = Bin.U16(d, o + 0x10C);
        if (n > 16) n = 16;
        for (int i = 0; i < n; i++)
        {
            int p = o + 0x110 + i * 32;
            if (p + 32 > m.End) break;
            var l = new MaterialLayer
            {
                Semantic = Bin.U64(d, p),
                TextureKey = Bin.U64(d, p + 8),
                AddressU = d[p + 24],
                AddressV = d[p + 25],
                Slot = i,
            };
            if (l.Semantic == 0 && l.TextureKey == 0) continue;
            def.Layers.Add(l);
        }
        ParseParams(d, m, def, o + 0x110 + n * 32);
        return def;
    }

    /// <summary>
    /// Parameter list following the layers: 16-byte descriptors {u64 Name, u8 2, u8 0, u8 Flag, u8 0, u32 16},
    /// followed immediately by one float4 per parameter (in the same order).
    /// </summary>
    private static void ParseParams(byte[] d, Chunk m, MaterialDef def, int start)
    {
        int end = m.End;
        bool IsDesc(int q) => q + 16 <= end && d[q + 8] == 2 && d[q + 9] == 0 && Bin.U32(d, q + 12) == 16 && Bin.U64(d, q) != 0;
        int p = start;
        while (p + 16 <= end && !IsDesc(p)) p += 8;
        var names = new List<ulong>();
        while (IsDesc(p)) { names.Add(Bin.U64(d, p)); p += 16; }
        for (int i = 0; i < names.Count; i++)
        {
            int q = p + 16 * i;
            if (q + 16 > end) break;
            def.Params[names[i]] = new Vector4(Bin.F32(d, q), Bin.F32(d, q + 4), Bin.F32(d, q + 8), Bin.F32(d, q + 12));
        }
    }
}