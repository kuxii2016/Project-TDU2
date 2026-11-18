using System.Numerics;

namespace Paradise.Core.Formats;

/// <summary>A sound zone within a sector: a polygon in XZ (game coordinates) with an ambient type (TypeID from ambiences.xmb).</summary>
public sealed class SoundZone
{
    /// <summary>Environment.TypeID for the island-ambiences.xmb (Ibiza 0..15, Hawaii 0..12).</summary>
    public int Type;
    public Vector2 Center;
    /// <summary>Eckpunkte absolut, x = Spiel-X, y = Spiel-Z.</summary>
    public Vector2[] Points = Array.Empty<Vector2>();
    public float Area;
    public float MinX, MinZ, MaxX, MaxZ;

    /// <summary>Punkt im Polygon (gerade/ungerade Regel).</summary>
    public bool Contains(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ || Points.Length < 3) return false;
        bool inside = false;
        for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
        {
            var a = Points[i]; var b = Points[j];
            if ((a.Y > z) != (b.Y > z) && x < (b.X - a.X) * (z - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// Sector-*.psa = Sound zones (ambient noise) of a sector. Read from the original data:
///   0x00 u32 0x20000, 0x0C “ESDZ”, 0x10 “ZONE”, 0x18 u32 payload length, 0x20 u32 number of zones + 12 bytes (0xDEADBEEF),
///   32 bytes header per zone {i32 type, f32 centerX, f32 centerZ, f32 0, u32 0, i32 score, 2× 0xDEADBEEF},
///   followed by score × {f32 dx, f32 0, f32 dz, f32 0} relative to the center.
///   Z is mirrored in the file (sector 5-0-0-3 Hawaii: file −3005 = game 3005), so Z = −file-Z.
/// </summary>
public static class SoundZonesFile
{
    public static List<SoundZone>? Parse(byte[]? d)
    {
        if (d == null || d.Length < 0x30 || !Bin.TagIs(d, 0x0C, "ESDZ") || !Bin.TagIs(d, 0x10, "ZONE")) return null;
        int n = (int)Bin.U32(d, 0x20);
        if (n < 0 || n > 4096) return null;
        var list = new List<SoundZone>(n);
        int o = 0x30;
        for (int i = 0; i < n && o + 32 <= d.Length; i++)
        {
            int type = Bin.I32(d, o);
            float cx = Bin.F32(d, o + 4), cz = -Bin.F32(d, o + 8);
            int np = Bin.I32(d, o + 20);
            o += 32;
            if (np < 0 || o + np * 16 > d.Length) break;
            var z = new SoundZone { Type = type, Center = new Vector2(cx, cz), Points = new Vector2[np] };
            z.MinX = z.MinZ = float.MaxValue; z.MaxX = z.MaxZ = float.MinValue;
            for (int p = 0; p < np; p++, o += 16)
            {
                var v = new Vector2(cx + Bin.F32(d, o), cz - Bin.F32(d, o + 8));
                z.Points[p] = v;
                z.MinX = MathF.Min(z.MinX, v.X); z.MaxX = MathF.Max(z.MaxX, v.X);
                z.MinZ = MathF.Min(z.MinZ, v.Y); z.MaxZ = MathF.Max(z.MaxZ, v.Y);
            }
            float a = 0f;
            for (int p = 0, q = np - 1; p < np; q = p++) a += z.Points[q].X * z.Points[p].Y - z.Points[p].X * z.Points[q].Y;
            z.Area = MathF.Abs(a) * 0.5f;
            list.Add(z);
        }
        return list;
    }
}