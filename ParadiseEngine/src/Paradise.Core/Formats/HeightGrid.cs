using System.Numerics;

namespace Paradise.Core.Formats;

/// <summary>
/// Terrain-Höhenraster. Portiert aus dem Unity-Projekt.
/// In TDU2 ist das Terrain ein HMAP-Objekt im .3dd + ein DXVB im .3dg:
///   Stream 0: 257×257 float Höhen (Zeile = Z, Spalte = X), Stream 1: half4 Normalen.
///   Abstand: Sektor 3 m (768 m), Area 24 m (6144 m), Insel 384 m (98304 m).
///   Ursprung = Ecke (0,0) der Kachel. X/Z in Spielkoordinaten, Y = Höhe.
/// </summary>
public sealed class HeightGrid
{
    public int Size;            // Vertices pro Seite (257)
    public float Spacing;       // Meter pro Zelle
    public float[] Heights = Array.Empty<float>();   // [z * Size + x]
    public Vector3[]? Normals;  // optional (aus Datei)
    public float Min, Max;

    public float Extent => (Size - 1) * Spacing;

    public static HeightGrid? FromTerrainBank(Scene3DD? scene, Geometry3DG? geo, float fallbackSpacing)
    {
        if (geo == null || geo.FirstVertexBuffer < 0) return null;
        var vb = geo.ReadVertexBuffer(geo.FirstVertexBuffer);
        if (vb == null || vb.Heights == null) return null;
        int size = (int)System.Math.Round(System.Math.Sqrt(vb.Count));
        if (size * size != vb.Count) return null;
        var g = new HeightGrid
        {
            Size = size,
            Spacing = scene != null && scene.Heightmap.Present && scene.Heightmap.Spacing > 0 ? scene.Heightmap.Spacing : fallbackSpacing,
            Heights = vb.Heights,
            Normals = vb.Normals,
        };
        g.Min = float.MaxValue; g.Max = float.MinValue;
        foreach (var h in g.Heights) { if (h < g.Min) g.Min = h; if (h > g.Max) g.Max = h; }
        return g;
    }

    public float Get(int x, int z)
    {
        x = System.Math.Clamp(x, 0, Size - 1); z = System.Math.Clamp(z, 0, Size - 1);
        return Heights[z * Size + x];
    }

    /// <summary>Bilineare Höhe an lokaler Position (Meter, relativ zur Kachelecke).</summary>
    public float Sample(float lx, float lz)
    {
        float fx = lx / Spacing, fz = lz / Spacing;
        int x0 = System.Math.Clamp((int)MathF.Floor(fx), 0, Size - 2);
        int z0 = System.Math.Clamp((int)MathF.Floor(fz), 0, Size - 2);
        float tx = System.Math.Clamp(fx - x0, 0f, 1f), tz = System.Math.Clamp(fz - z0, 0f, 1f);
        float h00 = Heights[z0 * Size + x0], h10 = Heights[z0 * Size + x0 + 1];
        float h01 = Heights[(z0 + 1) * Size + x0], h11 = Heights[(z0 + 1) * Size + x0 + 1];
        float a = h00 + (h10 - h00) * tx, b = h01 + (h11 - h01) * tx;
        return a + (b - a) * tz;
    }

    public Vector3 NormalAt(int x, int z)
    {
        float hl = Get(x - 1, z), hr = Get(x + 1, z), hd = Get(x, z - 1), hu = Get(x, z + 1);
        return Vector3.Normalize(new Vector3(hl - hr, 2f * Spacing, hd - hu));
    }
}
