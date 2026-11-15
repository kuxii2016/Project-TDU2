using System.Numerics;

namespace Paradise.Core.World;

/// <summary>Achsparalleles Rechteck in XZ (Ersatz für UnityEngine.Rect: x = X, y = Z).</summary>
public readonly struct RectF
{
    public readonly float X, Y, Width, Height;
    public RectF(float x, float y, float w, float h) { X = x; Y = y; Width = w; Height = h; }
    public float XMin => X;
    public float YMin => Y;
    public float XMax => X + Width;
    public float YMax => Y + Height;
    public bool Contains(Vector2 p) => p.X >= XMin && p.X < XMax && p.Y >= YMin && p.Y < YMax;
    public bool Overlaps(RectF o) => o.XMax > XMin && o.XMin < XMax && o.YMax > YMin && o.YMin < YMax;
}

/// <summary>
/// Koordinatensystem (Spielkoordinaten, Y oben). Portiert aus dem Unity-Projekt.
///   Insel-Raster : 16×16 Areas à 6144 m (Insel-LOD 257² Höhen, 384 m Abstand, Ursprung 0/0)
///   Area A-B     : Ursprung (A*6144, B*6144), 8×8 Sektoren
///   Sektor C-D   : Ursprung (A*6144 + C*768 + (D ungerade ? 384 : 0), B*6144 + D*768)
///                  -> Sektorreihen mit ungeradem D sind um einen halben Sektor nach +X versetzt
///                     (Ziegelmuster). Sektor C=7 einer ungeraden Reihe ragt 384 m in die nächste Area.
/// </summary>
public static class WorldLayout
{
    public const float AreaSize = 6144f;
    public const float SectorSize = 768f;
    public const int SectorsPerArea = 8;
    public const float StaggerX = 384f;
    public const float IslandGridSize = 98304f; // 16 Areas
    public const float IslandSpacing = 384f, AreaSpacing = 24f, SectorSpacing = 3f;

    public static Vector3 AreaOrigin(AreaKey a) => new(a.X * AreaSize, 0, a.Z * AreaSize);

    public static Vector3 SectorOrigin(SectorKey k) =>
        new(k.A * AreaSize + k.C * SectorSize + ((k.D & 1) != 0 ? StaggerX : 0f), 0, k.B * AreaSize + k.D * SectorSize);

    public static RectF SectorRect(SectorKey k)
    {
        var o = SectorOrigin(k);
        return new RectF(o.X, o.Z, SectorSize, SectorSize);
    }

    public static RectF AreaRect(AreaKey a) => new(a.X * AreaSize, a.Z * AreaSize, AreaSize, AreaSize);

    /// <summary>Welcher Sektor enthält die Weltposition (x,z)?</summary>
    public static SectorKey SectorAt(float x, float z)
    {
        int b = (int)MathF.Floor(z / AreaSize);
        int d = (int)MathF.Floor((z - b * AreaSize) / SectorSize);
        d = System.Math.Clamp(d, 0, SectorsPerArea - 1);
        float xs = x - ((d & 1) != 0 ? StaggerX : 0f);
        int a = (int)MathF.Floor(xs / AreaSize);
        int c = System.Math.Clamp((int)MathF.Floor((xs - a * AreaSize) / SectorSize), 0, SectorsPerArea - 1);
        return new SectorKey(a, b, c, d);
    }

    public static AreaKey AreaAt(float x, float z) => new((int)MathF.Floor(x / AreaSize), (int)MathF.Floor(z / AreaSize));

    /// <summary>Kürzeste Distanz (XZ) eines Punktes zu einem Rechteck.</summary>
    public static float DistanceToRect(Vector2 p, RectF r)
    {
        float dx = MathF.Max(MathF.Max(r.XMin - p.X, 0f), p.X - r.XMax);
        float dz = MathF.Max(MathF.Max(r.YMin - p.Y, 0f), p.Y - r.YMax);
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
