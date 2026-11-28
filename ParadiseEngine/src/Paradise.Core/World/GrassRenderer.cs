using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Generates grass and bushes around the viewer based on a sector's grass type map (.pgr). Ported from the Unity project (GrassRenderer).
/// Grass types from fx/grasstypes.xml, textures from fx/tallgrass.bnk (setgrass.2db = 16 sprites, setbush.2db = 4 sprites).
/// The game procedurally generates grass at runtime; the density/distribution here is an approximation.
/// Coordinates: sector-local (origin = sector corner), Origin = sector origin in game coordinates.
/// </summary>
public sealed class GrassRenderer : IDisposable
{
    public Vector3 Origin;
    public HeightGrid Heights = null!;
    public GrassMap Map = null!;
    public GrassTypes Types = null!;
    public IGpuMaterial? GrassMaterial, BushMaterial;
    public IGpuTexture? GrassAtlas, BushAtlas;
    public int GrassAtlasCells = 16, BushAtlasCells = 4;
    public float Radius = 120f;
    public float ChunkSize = 32f;
    public float Spacing = 1.2f;
    public float Density = 0.7f;
    public float BushChance = 0.03f;
    public float SizeScale = 1f;
    /// <summary>Unity TDU2/Grass: mindestens 30 % reine Grasfarbe gegen die Bodenfarbe.
    /// Der Original-Shader nimmt dafür oT3.x (Vertex-Alpha); der Wert wird deshalb beim Erzeugen der
    /// Vertizes gebacken, damit der Shader unverändert bleiben kann.</summary>
    public float GroundTipMix = 0.3f;
    public int MaxChunksPerFrame = 6;
    public float SectorSize = WorldLayout.SectorSize;
    public int Seed;
    /// <summary>fx/tallgrass.bnk noise.rle (256×256 bytes) – size noise for UseNoiseScale (exe 0xc68c28).</summary>
    public GrassMap? Noise;

    sealed class Chunk { public IGpuMesh? Grass, Bush; }
    private readonly Dictionary<(int x, int z), Chunk> _chunks = new();
    private readonly List<(int x, int z)> _tmpRemove = new();
    private readonly List<(float d, (int x, int z) c)> _todo = new();
    // Ein Sektor baut seine Chunks nacheinander; danach hat UploadMesh die Daten längst kopiert. Die Listen werden
    // wiederverwendet statt pro Chunk vier neue Arrays (ToArray) zu erzeugen – das war ein GC-Brokengem bei jedem Nachladen.
    private readonly List<ObjectVertex> _grassV = new(4096), _bushV = new(1024);
    private readonly List<uint> _grassI = new(8192), _bushI = new(2048);
    private double _nextCheck;
    private bool _pending;

    public int ChunkCount => _chunks.Count;

    /// <summary>Neue Größe (z.B. aus dem DevMenü) und Chunks damit sofort neu bauen.</summary>
    public void SetSizeScale(float scale)
    {
        if (System.MathF.Abs(SizeScale - scale) < 0.001f) return;
        SizeScale = scale;
        Invalidate();
    }

    /// <summary>Bodenfarben-Anteil live ändern (0 = dunkel, 1 = reine Grasfarbe).</summary>
    public void SetGroundTipMix(float mix)
    {
        mix = System.Math.Clamp(mix, 0f, 1f);
        if (System.MathF.Abs(GroundTipMix - mix) < 0.001f) return;
        GroundTipMix = mix;
        Invalidate();
    }

    /// <summary>Alle Chunks verwerfen; Update baut die Stücke um den Betrachter wieder auf.</summary>
    public void Invalidate()
    {
        foreach (var ch in _chunks.Values) { ch.Grass?.Dispose(); ch.Bush?.Dispose(); }
        _chunks.Clear();
        _pending = false;
        _nextCheck = 0;
    }

    /// <summary>Per frame viewerGame = camera in game coordinates.</summary>
    public void Update(IRenderer r, Vector3 viewerGame, double time)
    {
        if (Heights == null || Map == null || Types == null || GrassMaterial == null) return;
        if (time < _nextCheck) return;
        _nextCheck = time + (_pending ? 0.0 : 0.2);   // solange Stücke fehlen: jedes Bild weiterbauen
        var local = viewerGame - Origin;

        int nChunks = (int)MathF.Ceiling(SectorSize / ChunkSize);
        int built = 0;
        int cx0 = System.Math.Max(0, (int)MathF.Floor((local.X - Radius) / ChunkSize));
        int cx1 = System.Math.Min(nChunks - 1, (int)MathF.Floor((local.X + Radius) / ChunkSize));
        int cz0 = System.Math.Max(0, (int)MathF.Floor((local.Z - Radius) / ChunkSize));
        int cz1 = System.Math.Min(nChunks - 1, (int)MathF.Floor((local.Z + Radius) / ChunkSize));
        // nächste Stücke zuerst
        _todo.Clear();
        for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                var c = (cx, cz);
                if (_chunks.ContainsKey(c)) continue;
                float d = ChunkDistance(c, local);
                if (d > Radius) continue;
                _todo.Add((d, c));
            }
        _todo.Sort((a, b) => a.d.CompareTo(b.d));
        foreach (var (_, c) in _todo)
        {
            if (built >= MaxChunksPerFrame) break;
            _chunks[c] = BuildChunk(r, c);
            built++;
        }
        _pending = _todo.Count > built;

        _tmpRemove.Clear();
        foreach (var kv in _chunks)
            if (ChunkDistance(kv.Key, local) > Radius + ChunkSize) _tmpRemove.Add(kv.Key);
        foreach (var k in _tmpRemove)
        {
            var ch = _chunks[k];
            ch.Grass?.Dispose(); ch.Bush?.Dispose();
            _chunks.Remove(k);
        }
    }

    public void Render(IRenderer r, Matrix4x4 root)
    {
        if (_chunks.Count == 0 || GrassMaterial == null) return;
        var m = Matrix4x4.CreateTranslation(Origin) * root;
        foreach (var ch in _chunks.Values)
        {
            if (ch.Grass != null) r.DrawMesh(ch.Grass, GrassMaterial, m);
            if (ch.Bush != null) r.DrawMesh(ch.Bush, BushMaterial ?? GrassMaterial, m);
        }
    }

    float ChunkDistance((int x, int z) c, Vector3 local)
    {
        var rect = new RectF(c.x * ChunkSize, c.z * ChunkSize, ChunkSize, ChunkSize);
        return WorldLayout.DistanceToRect(new Vector2(local.X, local.Z), rect);
    }

    Chunk BuildChunk(IRenderer r, (int x, int z) c)
    {
        var grassV = _grassV; var grassI = _grassI;
        var bushV = _bushV; var bushI = _bushI;
        grassV.Clear(); grassI.Clear(); bushV.Clear(); bushI.Clear();

        float x0 = c.x * ChunkSize, z0 = c.z * ChunkSize;
        int steps = System.Math.Max(1, (int)MathF.Round(ChunkSize / Spacing));
        int seed = Seed;
        // UV1 = Position in der Colormap des Sektors (wie das Terrain: Rastermeter / Ausdehnung)
        float ext = Heights.Extent > 0f ? Heights.Extent : SectorSize;
        for (int j = 0; j < steps; j++)
            for (int i = 0; i < steps; i++)
            {
                int gx = (int)MathF.Round(x0 / Spacing) + i, gz = (int)MathF.Round(z0 / Spacing) + j;
                float r0 = Hash01(gx, gz, seed), r1 = Hash01(gx, gz, seed + 1), r2 = Hash01(gx, gz, seed + 2);
                float x = x0 + (i + r0) * Spacing, z = z0 + (j + r1) * Spacing;
                if (x < 0 || z < 0 || x >= SectorSize || z >= SectorSize) continue;
                // exe 0xc68b03: Klassenwert 255 oder >= Typanzahl -> kein Gras; sonst Typ = Dateireihenfolge
                int count = Types.Ordered.Count;
                int cls = Map.Sample(x, z, SectorSize);
                if (cls == 255 || cls >= count) continue;
                var t = Types.Ordered[cls];
                // exe 0xc68b6a: Variante wechselt den ganzen Typ: (Typ + TypeVariation + Anzahl) % Anzahl, wenn Zufall < p
                if (t.Grass.TypeVariation != 0 && Hash01(gx, gz, seed + 5) < t.Grass.VariationProbability)
                    t = Types.Ordered[((cls + t.Grass.TypeVariation) % count + count) % count];

                float yaw = r2 * MathF.PI;
                float y = Heights.Sample(x, z);
                float r3 = Hash01(gx, gz, seed + 3);
                if (BushMaterial != null && t.Bush.BitmapIndex >= 0 && r3 < BushChance)
                {
                    float s = 1f + (Hash01(gx, gz, seed + 4) - 0.5f) * 2f * t.Bush.SizeDelta;
                    float bh = t.Bush.Height * s * SizeScale;
                    BlendMix(t.Bush.BlendMode, GroundTipMix, out byte bRoot, out byte bTip);
                    AddClump(bushV, bushI, new Vector3(x, y, z), bh * CellAspect(BushAtlas, BushAtlasCells), bh, yaw, t.Bush.BitmapIndex, BushAtlasCells, r0, ext, bRoot, bTip);
                    continue;
                }
                if (r3 > Density || t.Grass.BitmapIndex < 0) continue;
                var g = t.Grass;
                // exe 0xc68ba8/0xc68c15: Zufall r in [0, SizeDelta/2]
                float rnd = Hash01(gx, gz, seed + 6) * g.SizeDelta * 0.5f;
                float scale = 1f;
                if (g.UseNoiseScale && Noise != null && Noise.Width > 0 && Noise.Height > 0)
                {
                    // exe 0xc68c4b–0xc68d02: (Welt - Sektorursprung) / Sektorgröße, 256 Schritte, Index*4 & 255
                    int ix = ((int)(x / SectorSize * 256f) * 4) & 255, iz = ((int)(z / SectorSize * 256f) * 4) & 255;
                    float sn = Noise.Cells[(iz % Noise.Height) * Noise.Width + (ix % Noise.Width)] / 255f;
                    // exe 0xc68d14: scale = (2 - 4P)·s² + (4P - 1)·s; scale + r < ScaleClamp -> Büschel entfällt
                    float pw = g.ScalePower * 4f;
                    scale = (2f - pw) * sn * sn + (pw - 1f) * sn;
                    if (scale + rnd < g.ScaleClamp) continue;
                }
                // exe Kartenpaket: Hebel = Höhe·scale/2, Basis-Y = r -> Kartenhöhe = Höhe·scale + 2r
                float gh = (g.Height * scale + 2f * rnd) * SizeScale;
                if (gh <= 0.01f) continue;
                BlendMix(g.BlendMode, GroundTipMix, out byte gRoot, out byte gTip);
                // Breite aus dem Seitenverhältnis der Atlas-Zelle
                AddClump(grassV, grassI, new Vector3(x, y, z), gh * CellAspect(GrassAtlas, GrassAtlasCells), gh, yaw, g.BitmapIndex, GrassAtlasCells, r1, ext, gRoot, gTip);
            }

        var ch = new Chunk();
        // Span direkt auf die Liste: CreateMesh/UploadMesh kopiert synchron in den Hostpuffer, danach dürfen die Listen weiterleben.
        if (grassI.Count > 0) ch.Grass = r.CreateMesh(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(grassV), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(grassI));
        if (bushI.Count > 0) ch.Bush = r.CreateMesh(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bushV), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bushI));
        return ch;
    }

    static float CellAspect(IGpuTexture? t, int cells)
    {
        if (t == null || t.Height <= 0 || cells <= 0) return 1.5f;
        return System.Math.Clamp((float)t.Width / cells / t.Height, 0.25f, 4f);
    }

    /// <summary>TD_TallGrass oT3.x per corner (exe table 0xc68dd8): Anteil reiner Grasfarbe; 0 = nur Bodenfarbe.
    /// Unitys Korrektur hebt den dunklen Anteil auf _GroundTipMix (0,3) an, ohne den originalen Shader zu ändern.</summary>
    static void BlendMix(int mode, float minMix, out byte root, out byte tip)
    {
        byte min = (byte)System.Math.Clamp(minMix * 255f, 0f, 255f);
        switch (mode)
        {
            case 1: root = min; tip = min; break;
            case 2: root = min; tip = 255; break;
            default: root = 255; tip = 255; break;
        }
    }

    static void AddClump(List<ObjectVertex> v, List<uint> tris, Vector3 p, float width, float height, float yaw, int atlasIndex, int cells, float tint, float ext, byte mixRoot, byte mixTip)
    {
        float u0 = (float)atlasIndex / cells, u1 = (float)(atlasIndex + 1) / cells;
        byte shade = (byte)(200 + tint * 55);
        // Alpha = Bodenfarben-Mischung (TD_TallGrass lerp(Bodenfarbe, 1, a)), Wurzel/Spitze getrennt
        uint col = ObjectVertex.PackColor(shade, shade, shade, mixRoot);
        uint colTip = ObjectVertex.PackColor(shade, shade, shade, mixTip);
        for (int q = 0; q < 2; q++)
        {
            float a = yaw + q * MathF.PI * 0.5f;
            var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * (width * 0.5f);
            uint b = (uint)v.Count;
            void Add(Vector3 pos, Vector2 uv) => v.Add(new ObjectVertex
            {
                Position = pos,
                Normal = Vector3.UnitY,
                Color = uv.Y < 0.5f ? colTip : col,
                Uv0 = uv,
                Uv1 = new Vector2(pos.X / ext, pos.Z / ext),
                Uv2 = uv,
                Uv3 = uv,
            });
            Add(p - dir, new Vector2(u0, 1)); Add(p + dir, new Vector2(u1, 1));
            Add(p - dir + Vector3.UnitY * height, new Vector2(u0, 0)); Add(p + dir + Vector3.UnitY * height, new Vector2(u1, 0));
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
        }
    }

    /// <summary>MathUtil.Hash01 from the Unity project.</summary>
    public static float Hash01(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * (int)2246822519u);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    public void Dispose()
    {
        foreach (var ch in _chunks.Values) { ch.Grass?.Dispose(); ch.Bush?.Dispose(); }
        _chunks.Clear();
    }
}