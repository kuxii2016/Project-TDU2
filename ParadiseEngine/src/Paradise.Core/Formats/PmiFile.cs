using System.Numerics;

namespace Paradise.Core.Formats;

public struct PmiInstance
{
    public Vector3 Position;   // sektor-lokal (m)
    public Quaternion Rotation;
    public Vector3 Scale;
    public ushort Extra;       // Bits 0-8 Culling-Zelle, 9-15 Klasse
    /// <summary>Leitplanken (Art 3): erste Zusatzmatrix, wird VOR der Platzierung angewendet. null sonst.</summary>
    public Matrix4x4? Pre;
    /// <summary>Leitplanken (Art 3): alle Knochenmatrizen (Index = Knochen-Index im Mesh).</summary>
    public Matrix4x4[]? Bones;
    /// <summary>Schilder (Art 4): Zusatzbytes = Vorlage + Glyphen-Codes. null sonst.</summary>
    public byte[]? SignCodes;

    /// <summary>Platzierungsmatrix (Skalierung, Rotation, Position – wie Unity TRS), Zeilenvektoren.</summary>
    public readonly Matrix4x4 Matrix =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
}

public sealed class PmiGroup
{
    public ulong Key;          // INS-Key in der Library (commonworld.bnk), z.B. "V_CHEN_C"
    public ushort Unknown;     // Sichtweite (~60 x Modellradius)
    public ushort Version;     // Art: 1 normal (O, V), 2 World-Actor (A), 3 Leitplanke (S), 4 Schild mit Text, 5 Effekt (F)
    public ushort ExtraBytes;  // Zusatzbytes je Instanz (Art 3: 7-8 Matrizen, Art 4: Schildtext)
    public readonly List<PmiInstance> Instances = new();
    public string Name => Bin.KeyToString(Key);
}

/// <summary>
/// .pmi = "Placed Mesh Instances" pro Sektor. Portiert aus dem Unity-Projekt.
///   Sector-...-V.pmi : Vegetation, -O.pmi : weitere Objekte, -A.pmi : Actors (worldactors.bnk), -F/S.pmi
///   MOBA-Chunk @0x10, payload: u32 Gruppen, float hMin, float hMax, ... ; Kinder ab MOBA+hdr:
///   MOB.-Chunk payload: u64 Key8, u32 Count, u16 Unknown, u16 Version, danach je Instanz 32 Byte:
///     +0 u16 x, +2 u16 y, +4 u16 z   (x,z: v/65535*1024-128 ; y: hMin + v/65535*(hMax-hMin))
///     +6 u16 sx, sy, sz             (Skalierung, 0x4000 = 1.0)
///     +12 s16 rotX, +14 u16 rotY, +16 s16 rotZ (65536 = 360°)
///     +18 u16 ?, +20 half3 Farbe1, +26 half3 Farbe2
/// </summary>
public sealed class PmiFile
{
    public float HeightMin, HeightMax;
    public readonly List<PmiGroup> Groups = new();
    public int SkippedGroups;

    public const float RangeMin = -128f, RangeSize = 1024f;

    private static Quaternion AngleAxis(float deg, Vector3 axis) => Quaternion.CreateFromAxisAngle(axis, deg * (MathF.PI / 180f));

    public static PmiFile Parse(byte[]? d)
    {
        var f = new PmiFile();
        if (d == null || d.Length < 0x30 || !Bin.TagIs(d, 0x10, "MOBA")) return f;

        int mobaSize = (int)Bin.U32(d, 0x18), mobaHdr = (int)Bin.U32(d, 0x1C);
        f.HeightMin = Bin.F32(d, 0x24);
        f.HeightMax = Bin.F32(d, 0x28);
        float hRange = f.HeightMax - f.HeightMin;

        foreach (var c in EdenChunks.Read(d, 0x10 + mobaHdr, System.Math.Min(d.Length, 0x10 + mobaSize)))
        {
            if (c.Tag != "MOB.") continue;
            int p = c.DataOffset;

            var g = new PmiGroup
            {
                Key = Bin.U64(d, p),
                Unknown = Bin.U16(d, p + 12),
                Version = Bin.U16(d, p + 14),
                ExtraBytes = Bin.U16(d, p + 10),
            };
            if (g.Version < 1 || g.Version > 5) { f.SkippedGroups++; continue; }

            int cnt = Bin.U16(d, p + 8);
            int stride = 32 + ((g.ExtraBytes + 15) / 16) * 16;
            int ip = c.Offset + 32;

            for (int i = 0; i < cnt; i++, ip += stride)
            {
                if (ip + stride > c.End) break;

                float x = Bin.U16(d, ip) / 65535f * RangeSize + RangeMin;
                float y = f.HeightMin + Bin.U16(d, ip + 2) / 65535f * hRange;
                float z = Bin.U16(d, ip + 4) / 65535f * RangeSize + RangeMin;

                var s = new Vector3(Bin.U16(d, ip + 6), Bin.U16(d, ip + 8), Bin.U16(d, ip + 10)) / 16384f;
                if (s.X <= 0.001f || s.Y <= 0.001f || s.Z <= 0.001f) s = Vector3.One;

                const float toDeg = 360f / 65536f;
                float pitch = Bin.I16(d, ip + 12) * toDeg,
                      head = Bin.U16(d, ip + 14) * toDeg,
                      roll = Bin.I16(d, ip + 16) * toDeg;
                var rot = AngleAxis(head, Vector3.UnitY) * AngleAxis(roll, Vector3.UnitZ) * AngleAxis(pitch, Vector3.UnitX);

                Matrix4x4? pre = null;
                Matrix4x4[]? bones = null;
                if (g.Version == 3 && g.ExtraBytes >= 64)
                {
                    int nb = System.Math.Min(g.ExtraBytes / 64, 16);
                    bones = new Matrix4x4[nb];
                    for (int b = 0; b < nb; b++)
                    {
                        int mp = ip + 32 + b * 64;
                        // Datei = D3D-Zeilenvektoren = System.Numerics-Konvention (Unity transponierte hier)
                        bones[b] = new Matrix4x4(
                            Bin.F32(d, mp + 0), Bin.F32(d, mp + 4), Bin.F32(d, mp + 8), Bin.F32(d, mp + 12),
                            Bin.F32(d, mp + 16), Bin.F32(d, mp + 20), Bin.F32(d, mp + 24), Bin.F32(d, mp + 28),
                            Bin.F32(d, mp + 32), Bin.F32(d, mp + 36), Bin.F32(d, mp + 40), Bin.F32(d, mp + 44),
                            Bin.F32(d, mp + 48), Bin.F32(d, mp + 52), Bin.F32(d, mp + 56), Bin.F32(d, mp + 60));
                    }
                    pre = bones[0];
                }

                byte[]? signCodes = null;
                if (g.Version == 4 && g.ExtraBytes > 1 && ip + 32 + g.ExtraBytes <= d.Length)
                {
                    signCodes = new byte[g.ExtraBytes];
                    Buffer.BlockCopy(d, ip + 32, signCodes, 0, g.ExtraBytes);
                }

                g.Instances.Add(new PmiInstance
                {
                    Position = new Vector3(x, y, z),
                    Rotation = rot,
                    Scale = s,
                    Extra = Bin.U16(d, ip + 18),
                    Pre = pre,
                    Bones = bones,
                    SignCodes = signCodes,
                });
            }
            f.Groups.Add(g);
        }
        return f;
    }
}
