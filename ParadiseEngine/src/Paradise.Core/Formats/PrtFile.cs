using System.Numerics;

namespace Paradise.Core.Formats;

/// <summary>
/// Particle system “.prt” (Eden edParticles, version 0x1b). Structure from the exe (re/prt_spec.md): “.PRT” chunk at 0x0c/0x10,
/// Payload starting at 0x20; all offsets within it are counted from the beginning of the file. The blocks read are those used by the runtime code:
/// System A (gravity, random acceleration), Emitter B, Groups (curves, flags), Render Block C (atlas, size).
/// </summary>
public sealed class PrtFile
{
    public string Name = "";
    public int MaxParticles;

    // ---- System A (exe 0x115a560: Beschleunigung = A+0x00, Zufallsbeschleunigung A+0x10..A+0x28)
    public Vector3 Gravity;
    public Vector3 RandomAccelMin, RandomAccelMax;
    public bool HasForces;          // |A+0x00..0x28| > 1e-4 (Flag 2/3)

    // ---- Emitter B
    public Matrix4x4 EmitterMatrix = Matrix4x4.Identity;   // B+0x00 (Form im Emitterraum)
    /// <summary>
    /// B+0x40: Position of the emitter within the effect (from B+0xc0 offset, B+0xe0 scale, B+0xf0 Euler angles). Scale = size of the
    /// emitter shape (exhaust 0.1 m, tire ring 0.5 m); rotation = ejection direction (exhaust: top → rear). Applied to shape, initial velocity,
    /// and forces (CHECKLIST: Application point not found in the exe; values consistent in all 132 .prt files).
    /// </summary>
    public Matrix4x4 LocalMatrix = Matrix4x4.Identity;
    public Vector3 Direction;                              // B+0xd0 (Richtungsmodus 2)
    public float WeightMean, WeightSpread;                 // B+0x110 / B+0x120
    public float SizeMean, SizeSpread;                     // B+0x114 / B+0x124
    public float MassMean, MassSpread;                     // B+0x118 / B+0x128 (Partikel +0x6c)
    public float RotMean, RotSpread;                       // B+0x11c / B+0x12c (Partikel +0x34)
    public byte[] ColorMean = new byte[4], ColorSpread = new byte[4];   // B+0x130 / B+0x134
    public float SpeedMean, SpeedSpread;                   // B+0x140 / B+0x144
    public float ConeMean, ConeSpread;                     // B+0x148 / B+0x14c (Bogenmaß)
    public float Rate;                                     // B+0x150 Emissionen/s
    public float LifeMean, LifeSpread;                     // B+0x158 / B+0x15c
    public int Recycle;                                    // B+0x160 (1 = ältestes Partikel ersetzen, wenn voll)
    public int SpeedDist, ConeDist, LifeDist;              // B+0x168 / B+0x16c / B+0x180
    public float SizeFactor;                               // B+0x170
    public int Flags174;                                   // B+0x174 (Bit0: Gewicht × Größe²)
    public int Shape;                                      // B+0x178 (1..9)
    public int ShapeSub, DirMode;                          // B+0x17c / B+0x17e
    public float EmitFactor, PartsPerEmission, MinPerEmission;   // B+0x184 / B+0x188 / B+0x18c
    public uint Seed;                                      // B+0x190
    /// <summary>B+0x1a4: Ausstoß entlang der Emitterbewegung des Bildes verteilen (exe 0x1159de4: Versatz = (B+0x30 − B+0xc0) × −Zeitanteil).</summary>
    public bool Interpolate;
    /// <summary>B+0x1ac: Startwert des Emissionszählers (in allen .prt 1,0 → sofortiger Ausstoß beim Start).</summary>
    public float StartTimer;
    public int LocalSpace;                                 // B+0x1a0

    // ---- Gruppe 0 (je 0x180 Byte)
    public Vector3 GroupAccel;                             // Gruppe+0xd0 (Flag 1)
    public int GroupFlags;                                 // Gruppe+0x11c
    public readonly List<Curve> Curves = new();

    // ---- Renderblock C
    public int AtlasCols = 1, AtlasRows = 1;               // C+0x270 / C+0x274
    /// <summary>C+0x210 Texturschalter (exe 0x114f30a/0x1151f3a): Bit 0 = Atlasbilder (0x114fc20), sonst ganze Textur (0x114ffe0);
    /// Bit 1 = zufälliges Spiegeln in U, Bit 2 = in V (die exe setzt daraus C+0x268/0x26c).</summary>
    public int TexFlags;
    public bool Atlas => (TexFlags & 1) != 0;
    public bool FlipU, FlipV;                              // aus C+0x210 Bit 1 / Bit 2
    public int FrameStart, FrameStartRand, FrameCount, FrameCountRand;   // C+0x280/284/288/28c
    public int AnimMode;                                   // C+0x29c (0 = über die Lebenszeit, sonst Bilder/s)
    public float AnimFps;                                  // C+0x2a0
    public float SizeScale = 1f;                           // C+0x214
    public int MaterialIndex;                              // C+0x200

    /// <summary>Kurve: Byte- oder Float-Ziel im Partikel, 28 Stützwerte über die Lebenszeit (exe 0x1154ac0/0x1154b40).</summary>
    public sealed class Curve
    {
        public int Flags;           // u16 +2
        public int Channel;         // 0xeffe00NN, NN
        public bool ByteTarget;     // Flags & 0x40
        public readonly float[] Samples = new float[28];
    }

    public static PrtFile? Parse(byte[] d, string name)
    {
        if (d.Length < 0x60 || !Bin.TagIs(d, 0x0c, ".PRT") || !Bin.TagIs(d, 0x10, ".PRT") || Bin.U16(d, 0x14) != 0x1b) return null;
        var p = new PrtFile { Name = name };
        int A = (int)Bin.U32(d, 0x30), B = (int)Bin.U32(d, 0x34), G = (int)Bin.U32(d, 0x38), C = (int)Bin.U32(d, 0x48);
        p.MaxParticles = (int)Bin.U32(d, 0x44);
        if (A <= 0 || B <= 0 || G <= 0 || A + 0x80 > d.Length || B + 0x200 > d.Length || G + 0x180 > d.Length) return null;

        Vector3 V(int o) => new(Bin.F32(d, o), Bin.F32(d, o + 4), Bin.F32(d, o + 8));
        p.Gravity = V(A);
        p.RandomAccelMin = V(A + 0x10);
        p.RandomAccelMax = V(A + 0x20);
        for (int o = 0; o <= 0x28; o += 4)
            if ((o & 0xc) != 0xc && MathF.Abs(Bin.F32(d, A + o)) > 1e-4f) { p.HasForces = true; break; }

        var m = new float[16];
        for (int i = 0; i < 16; i++) m[i] = Bin.F32(d, B + i * 4);
        p.EmitterMatrix = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        for (int i = 0; i < 16; i++) m[i] = Bin.F32(d, B + 0x40 + i * 4);
        p.LocalMatrix = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        p.Direction = V(B + 0xd0);
        p.WeightMean = Bin.F32(d, B + 0x110); p.WeightSpread = Bin.F32(d, B + 0x120);
        p.SizeMean = Bin.F32(d, B + 0x114); p.SizeSpread = Bin.F32(d, B + 0x124);
        p.MassMean = Bin.F32(d, B + 0x118); p.MassSpread = Bin.F32(d, B + 0x128);
        p.RotMean = Bin.F32(d, B + 0x11c); p.RotSpread = Bin.F32(d, B + 0x12c);
        for (int i = 0; i < 4; i++) { p.ColorMean[i] = d[B + 0x130 + i]; p.ColorSpread[i] = d[B + 0x134 + i]; }
        p.SpeedMean = Bin.F32(d, B + 0x140); p.SpeedSpread = Bin.F32(d, B + 0x144);
        p.ConeMean = Bin.F32(d, B + 0x148); p.ConeSpread = Bin.F32(d, B + 0x14c);
        p.Rate = Bin.F32(d, B + 0x150);
        p.LifeMean = Bin.F32(d, B + 0x158); p.LifeSpread = Bin.F32(d, B + 0x15c);
        p.Recycle = Bin.I32(d, B + 0x160);
        p.SpeedDist = Bin.I32(d, B + 0x168); p.ConeDist = Bin.I32(d, B + 0x16c);
        p.SizeFactor = Bin.F32(d, B + 0x170);
        p.Flags174 = Bin.I32(d, B + 0x174);
        p.Shape = Bin.I32(d, B + 0x178);
        p.ShapeSub = Bin.U16(d, B + 0x17c); p.DirMode = Bin.U16(d, B + 0x17e);
        p.LifeDist = Bin.I32(d, B + 0x180);
        p.EmitFactor = Bin.F32(d, B + 0x184); p.PartsPerEmission = Bin.F32(d, B + 0x188); p.MinPerEmission = Bin.F32(d, B + 0x18c);
        p.Seed = Bin.U32(d, B + 0x190);
        p.Interpolate = Bin.I32(d, B + 0x1a4) != 0;
        p.StartTimer = Bin.F32(d, B + 0x1ac);
        p.LocalSpace = Bin.I32(d, B + 0x1a0);

        p.GroupAccel = V(G + 0xd0);
        p.GroupFlags = Bin.U16(d, G + 0x11c);
        int cn = Bin.I32(d, G + 0x114), co = (int)Bin.U32(d, G + 0x118);
        for (int i = 0; i < cn && co + i * 0x80 + 0x80 <= d.Length; i++)
        {
            int o = co + i * 0x80;
            uint ch = Bin.U32(d, o + 8);
            if ((ch >> 16) != 0xeffe) continue;
            var c = new Curve { Flags = Bin.U16(d, o + 2), Channel = (int)(ch & 0xffff) };
            c.ByteTarget = (c.Flags & 0x40) != 0;
            for (int k = 0; k < 28; k++) c.Samples[k] = Bin.F32(d, o + 0x10 + k * 4);
            p.Curves.Add(c);
        }

        if (C > 0 && C + 0x2a4 <= d.Length)
        {
            p.MaterialIndex = Bin.I32(d, C + 0x200);
            p.SizeScale = Bin.F32(d, C + 0x214);
            p.TexFlags = Bin.I32(d, C + 0x210);
            p.FlipU = ((p.TexFlags >> 1) & 1) != 0; p.FlipV = ((p.TexFlags >> 2) & 1) != 0;
            p.AtlasCols = System.Math.Max(1, Bin.I32(d, C + 0x270)); p.AtlasRows = System.Math.Max(1, Bin.I32(d, C + 0x274));
            p.FrameStart = Bin.I32(d, C + 0x280); p.FrameStartRand = Bin.I32(d, C + 0x284);
            p.FrameCount = Bin.I32(d, C + 0x288); p.FrameCountRand = Bin.I32(d, C + 0x28c);
            p.AnimMode = Bin.I32(d, C + 0x29c); p.AnimFps = Bin.F32(d, C + 0x2a0);
        }
        return p;
    }
}