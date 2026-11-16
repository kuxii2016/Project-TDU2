using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>Ein Terrainstück (Teilrechteck eines Höhenrasters), zugeordnet zu einem Sektor oder einer Area.</summary>
public sealed class TerrainPiece
{
    public SectorKey Sector;       // Sektor, dem das Stück gehört (LOD1-Stücke)
    public AreaKey Area;           // Area (LOD2-Stücke)
    public Vector3 WorldOrigin;    // Weltposition der Stückecke
    public MeshData Mesh = null!;
}

/// <summary>
/// Baut Gitter-Meshes aus Höhenrastern (thread-sicher, liefert MeshData). Portiert aus dem Unity-Projekt.
///   LOD0 = kompletter Sektor (257² Vertices, 3 m)
///   LOD1 = Sektor-Footprints aus dem Area-Raster (24 m), inkl. halber Stücke am Rand ungerader Reihen
///   LOD2 = Area-Footprints aus dem Insel-Raster (384 m)
/// Alle Stücke bekommen "Skirts" (nach unten gezogene Ränder), damit zwischen LOD-Stufen keine Risse sichtbar sind.
/// </summary>
public static class TerrainMeshBuilder
{
    /// <param name="uvExtent">Rasterlokale Meter, die UV 0..1 entsprechen (Colormap-Abdeckung)</param>
    /// <param name="skirt">Skirt-Tiefe in Metern (0 = keine)</param>
    public static MeshData BuildGrid(HeightGrid g, int x0, int z0, int nx, int nz, Vector2 uvExtent, float skirt, string name, bool[]? holes = null)
    {
        var md = new MeshData { Name = name };
        float sp = g.Spacing;
        int vx = nx + 1, vz = nz + 1;
        for (int j = 0; j < vz; j++)
            for (int i = 0; i < vx; i++)
            {
                int gx = x0 + i, gz = z0 + j;
                float h = g.Get(gx, gz);
                md.Positions.Add(new Vector3(i * sp, h, j * sp));
                md.Normals.Add(g.NormalAt(gx, gz));
                md.UV0.Add(new Vector2(gx * sp / uvExtent.X, gz * sp / uvExtent.Y));
            }
        var tris = new List<int>(nx * nz * 6 + (skirt > 0 ? (nx + nz) * 24 : 0));
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                // Loch (Tunnelportal) -> Zelle auslassen; Maske ist auf das ganze Raster bezogen
                if (holes != null)
                {
                    int hc = g.Size - 1, hx = x0 + i, hz = z0 + j;
                    if (hx < hc && hz < hc && holes[hz * hc + hx]) continue;
                }
                int v00 = j * vx + i, v10 = v00 + 1, v01 = v00 + vx, v11 = v01 + 1;
                if (((x0 + i + z0 + j) & 1) == 0)
                {
                    // Diagonale wie das Original (aus BLOCK abgelesen, alle Ibiza-Sektoren mit BLOCK):
                    // (x+z) gerade -> (0,0)-(1,1), ungerade -> (1,0)-(0,1)
                    tris.Add(v00); tris.Add(v01); tris.Add(v11);
                    tris.Add(v00); tris.Add(v11); tris.Add(v10);
                }
                else
                {
                    tris.Add(v00); tris.Add(v01); tris.Add(v10);
                    tris.Add(v10); tris.Add(v01); tris.Add(v11);
                }
            }

        if (skirt > 0f)
        {
            AddSkirt(md, tris, vx, vz, skirt, Edge.Bottom, holes, x0, z0, nx, nz, g.Size - 1);
            AddSkirt(md, tris, vx, vz, skirt, Edge.Top, holes, x0, z0, nx, nz, g.Size - 1);
            AddSkirt(md, tris, vx, vz, skirt, Edge.Left, holes, x0, z0, nx, nz, g.Size - 1);
            AddSkirt(md, tris, vx, vz, skirt, Edge.Right, holes, x0, z0, nx, nz, g.Size - 1);
        }
        md.SubMeshes.Add(tris);
        md.SubMeshMaterialIndex.Add(0);
        md.SubMeshIsSprite.Add(false);
        md.SubMeshUvSets.Add(1);
        return md;
    }

    private enum Edge { Bottom, Top, Left, Right }

    private static void AddSkirt(MeshData md, List<int> tris, int vx, int vz, float depth, Edge edge,
                                 bool[]? holes, int x0, int z0, int nx, int nz, int cellSize)
    {
        int count = (edge == Edge.Bottom || edge == Edge.Top) ? vx : vz;
        int start = md.Positions.Count;
        var skip = new bool[count - 1];
        if (holes != null)
            for (int k = 0; k < count - 1; k++)
            {
                int ci, cj;
                switch (edge)
                {
                    case Edge.Bottom: ci = k; cj = 0; break;
                    case Edge.Top: ci = k; cj = nz - 1; break;
                    case Edge.Left: ci = 0; cj = k; break;
                    default: ci = nx - 1; cj = k; break;
                }
                int hx = x0 + ci, hz = z0 + cj;
                if (hx < cellSize && hz < cellSize && holes[hz * cellSize + hx]) skip[k] = true;
            }
        for (int k = 0; k < count; k++)
        {
            int src = EdgeIndex(edge, k, vx, vz);
            var p = md.Positions[src];
            md.Positions.Add(new Vector3(p.X, p.Y - depth, p.Z));
            md.Normals.Add(md.Normals[src]);
            md.UV0.Add(md.UV0[src]);
        }
        for (int k = 0; k < count - 1; k++)
        {
            if (skip[k]) continue;   // kein Skirt quer über einer Portalöffnung
            int a = EdgeIndex(edge, k, vx, vz), b = EdgeIndex(edge, k + 1, vx, vz);
            int c = start + k, d = start + k + 1;
            // beidseitig, damit die Wicklung egal ist
            tris.Add(a); tris.Add(b); tris.Add(c);
            tris.Add(b); tris.Add(d); tris.Add(c);
            tris.Add(a); tris.Add(c); tris.Add(b);
            tris.Add(b); tris.Add(c); tris.Add(d);
        }
    }

    private static int EdgeIndex(Edge e, int k, int vx, int vz) => e switch
    {
        Edge.Bottom => k,
        Edge.Top => (vz - 1) * vx + k,
        Edge.Left => k * vx,
        _ => k * vx + (vx - 1),
    };

    /// <summary>
    /// Zellmaske ((Size−1)²) aus den HMAP-Loch-Kacheln (Tiles×Tiles, Index = Z·Tiles + X): das Original zeichnet diese
    /// Kacheln nicht als Höhenraster (LOD-Fehler −1), dort liegt das Sektormodell BLOCK.
    /// </summary>
    public static bool[] HoleCells(HeightGrid g, int tiles, bool[] holeTiles)
    {
        int n = g.Size - 1, per = n / tiles;
        var cells = new bool[n * n];
        for (int tz = 0; tz < tiles; tz++)
            for (int tx = 0; tx < tiles; tx++)
            {
                if (!holeTiles[tz * tiles + tx]) continue;
                for (int z = tz * per; z < (tz + 1) * per; z++)
                    for (int x = tx * per; x < (tx + 1) * per; x++) cells[z * n + x] = true;
            }
        return cells;
    }

    /// <summary>
    /// Sektormodell BLOCK (Gelände an den Loch-Kacheln, Portalöffnung ausgespart) an das Sektorgelände anhängen.
    /// Original (0xc50e74): dessen Material (Shaderset 0xa0a0a1a1a2a2a3a3) wird wie TD_Ground mit der Sektortextur
    /// gezeichnet – die Vertices sind sektorlokal und decken sich an den Kachelrändern exakt mit dem Höhenraster
    /// (geprüft sector-3-0-3-7: 485 von 569 Vertices auf Rasterpunkten mit gleicher Höhe). UV wie das Raster (Position/Extent).
    /// Liefert die Zahl angehängter Dreiecke.
    /// </summary>
    public static int AppendTerrainBlock(MeshData terrain, ObjectSet objects, HeightGrid g)
    {
        var scene = objects.Scene;
        if (scene == null || objects.Materials == null) return 0;
        var tris = terrain.SubMeshes[0];
        int added = 0;
        var byOffset = new Dictionary<int, HieRecord>();
        foreach (var h in scene.Hierarchy) byOffset[h.Offset] = h;
        var leaves = new List<(InsRecord ins, Matrix4x4 m)>();
        if (scene.Hierarchy.Count > 0) foreach (var h in scene.Hierarchy) leaves.Add((scene.GetIns(h.InsOffset), scene.WorldMatrix(h, byOffset)));
        else foreach (var off in scene.InsOffsets) { var i = scene.GetIns(off); leaves.Add((i, i.Matrix)); }
        foreach (var (ins, m) in leaves)
        {
            if (ins.Lods.Length == 0 || (ins.Key != ObjectBuilder.BlockKey && ins.Lods[0].ObjKey != ObjectBuilder.BlockKey)) continue;
            if (!objects.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) || md == null) continue;
            var bank = scene.ReadMaterialBank(ins.MaterialBankOffset);
            bool hasN = md.Normals.Count == md.Positions.Count;
            for (int s = 0; s < md.SubMeshes.Count; s++)
            {
                int mi = md.SubMeshMaterialIndex[s];
                ulong mk = mi >= 0 && mi < bank.Length ? bank[mi] : 0;
                if (!objects.Materials.Materials.TryGetValue(mk, out var def) || def.ShaderSet != ObjectBuilder.TerrainBlockShader) continue;
                var map = new Dictionary<int, int>();
                foreach (int vi in md.SubMeshes[s])
                {
                    if (!map.TryGetValue(vi, out int ni))
                    {
                        ni = terrain.Positions.Count;
                        map[vi] = ni;
                        var p = Vector3.Transform(md.Positions[vi], m);
                        terrain.Positions.Add(p);
                        terrain.Normals.Add(hasN ? Vector3.Normalize(Vector3.TransformNormal(md.Normals[vi], m)) : Vector3.UnitY);
                        terrain.UV0.Add(new Vector2(p.X / g.Extent, p.Z / g.Extent));
                    }
                    tris.Add(ni);
                }
                added += md.SubMeshes[s].Count / 3;
            }
        }
        return added;
    }

    /// <summary>LOD0: kompletter Sektor, ohne Skirts (das Original hat keine; Kanten benachbarter Sektoren sind höhengleich).</summary>
    public static MeshData BuildSector(HeightGrid g, bool[]? holes = null)
    {
        int n = g.Size - 1;
        return BuildGrid(g, 0, 0, n, n, new Vector2(g.Extent, g.Extent), 0f, "SectorTerrain", holes);
    }

    /// <summary>
    /// Skirt-Stücke eines Sektors (Engine-Zusatz gegen Risse zum gröberen LOD1 – im Original nicht vorhanden): links, rechts
    /// und je zwei Hälften unten/oben (die Nachbarreihe ist um einen halben Sektor versetzt). Probe = sektorlokaler Punkt
    /// knapp jenseits der Kante; das Stück wird nur gezeigt, solange der Nachbarsektor dort nicht als LOD0 steht
    /// (sonst hinge es z. B. in Tunnel unter Sektorgrenzen). Loch-Kacheln bekommen keinen Skirt.
    /// </summary>
    public static List<(MeshData Mesh, Vector2 Probe)> BuildSectorSkirts(HeightGrid g, float depth, bool[]? holes)
    {
        var list = new List<(MeshData, Vector2)>();
        if (depth <= 0f) return list;
        int n = g.Size - 1, h = n / 2;
        float e = g.Extent, q = e * 0.25f;
        list.Add((Skirt(g, depth, holes, Edge.Left, 0, n), new Vector2(-1f, e * 0.5f)));
        list.Add((Skirt(g, depth, holes, Edge.Right, 0, n), new Vector2(e + 1f, e * 0.5f)));
        list.Add((Skirt(g, depth, holes, Edge.Bottom, 0, h), new Vector2(q, -1f)));
        list.Add((Skirt(g, depth, holes, Edge.Bottom, h, n), new Vector2(e - q, -1f)));
        list.Add((Skirt(g, depth, holes, Edge.Top, 0, h), new Vector2(q, e + 1f)));
        list.Add((Skirt(g, depth, holes, Edge.Top, h, n), new Vector2(e - q, e + 1f)));
        return list;
    }

    /// <summary>Ein Skirt-Band entlang einer Rasterkante, Vertices k0..k1 (eigene Vertices, beidseitig).</summary>
    private static MeshData Skirt(HeightGrid g, float depth, bool[]? holes, Edge edge, int k0, int k1)
    {
        var md = new MeshData { Name = "SectorSkirt" };
        var tris = new List<int>();
        int n = g.Size - 1;
        float sp = g.Spacing, ext = g.Extent;
        for (int k = k0; k <= k1; k++)
        {
            var (gx, gz) = edge switch
            {
                Edge.Bottom => (k, 0), Edge.Top => (k, n), Edge.Left => (0, k), _ => (n, k),
            };
            float y = g.Get(gx, gz);
            var nrm = g.NormalAt(gx, gz);
            var uv = new Vector2(gx * sp / ext, gz * sp / ext);
            md.Positions.Add(new Vector3(gx * sp, y, gz * sp)); md.Normals.Add(nrm); md.UV0.Add(uv);
            md.Positions.Add(new Vector3(gx * sp, y - depth, gz * sp)); md.Normals.Add(nrm); md.UV0.Add(uv);
        }
        for (int k = k0; k < k1; k++)
        {
            var (cx, cz) = edge switch
            {
                Edge.Bottom => (k, 0), Edge.Top => (k, n - 1), Edge.Left => (0, k), _ => (n - 1, k),
            };
            if (holes != null && holes[cz * n + cx]) continue;   // Loch-Kachel: dort liegt BLOCK
            int a = (k - k0) * 2, c = a + 1, b = a + 2, d = a + 3;
            tris.Add(a); tris.Add(b); tris.Add(c);
            tris.Add(b); tris.Add(d); tris.Add(c);
            tris.Add(a); tris.Add(c); tris.Add(b);
            tris.Add(b); tris.Add(c); tris.Add(d);
        }
        md.SubMeshes.Add(tris);
        md.SubMeshMaterialIndex.Add(0);
        md.SubMeshIsSprite.Add(false);
        md.SubMeshUvSets.Add(1);
        return md;
    }

    /// <summary>LOD1: zerlegt ein Area-Raster (257², 24 m) in Sektor-Footprints (32×32 Zellen, ungerade Reihen um 16 Zellen versetzt).</summary>
    public static List<TerrainPiece> BuildAreaPieces(HeightGrid g, AreaKey area, float skirt)
    {
        var list = new List<TerrainPiece>();
        int cellsPerSector = (int)MathF.Round(WorldLayout.SectorSize / g.Spacing); // 32
        int half = cellsPerSector / 2;
        var uvExt = new Vector2(g.Extent, g.Extent);
        var areaOrigin = WorldLayout.AreaOrigin(area);
        for (int d = 0; d < WorldLayout.SectorsPerArea; d++)
        {
            int z0 = d * cellsPerSector;
            if ((d & 1) == 0)
            {
                for (int c = 0; c < WorldLayout.SectorsPerArea; c++)
                    list.Add(Piece(g, c * cellsPerSector, z0, cellsPerSector, cellsPerSector, uvExt, skirt, new SectorKey(area.X, area.Z, c, d), areaOrigin));
            }
            else
            {
                // linker halber Streifen gehört zu Sektor 7 der vorherigen Area
                list.Add(Piece(g, 0, z0, half, cellsPerSector, uvExt, skirt, new SectorKey(area.X - 1, area.Z, 7, d), areaOrigin));
                for (int c = 0; c < WorldLayout.SectorsPerArea - 1; c++)
                    list.Add(Piece(g, half + c * cellsPerSector, z0, cellsPerSector, cellsPerSector, uvExt, skirt, new SectorKey(area.X, area.Z, c, d), areaOrigin));
                // rechter halber Streifen = linke Hälfte von Sektor 7 dieser Area
                list.Add(Piece(g, half + 7 * cellsPerSector, z0, half, cellsPerSector, uvExt, skirt, new SectorKey(area.X, area.Z, 7, d), areaOrigin));
            }
        }
        return list;
    }

    private static TerrainPiece Piece(HeightGrid g, int x0, int z0, int nx, int nz, Vector2 uvExt, float skirt, SectorKey key, Vector3 gridOrigin) => new()
    {
        Sector = key,
        Area = key.Area,
        WorldOrigin = gridOrigin + new Vector3(x0 * g.Spacing, 0, z0 * g.Spacing),
        Mesh = BuildGrid(g, x0, z0, nx, nz, uvExt, skirt, key + "_LOD1"),
    };

    /// <summary>LOD2: Area-Footprints aus dem Insel-Raster. Colormap deckt areasX×areasZ Areas ab.</summary>
    public static List<TerrainPiece> BuildIslandPieces(HeightGrid g, int areasX, int areasZ, float skirt)
    {
        var list = new List<TerrainPiece>();
        int cellsPerArea = (int)MathF.Round(WorldLayout.AreaSize / g.Spacing); // 16
        var uvExt = new Vector2(System.Math.Max(1, areasX) * WorldLayout.AreaSize, System.Math.Max(1, areasZ) * WorldLayout.AreaSize);
        for (int b = 0; b < areasZ; b++)
            for (int a = 0; a < areasX; a++)
            {
                int x0 = a * cellsPerArea, z0 = b * cellsPerArea;
                if (x0 + cellsPerArea >= g.Size || z0 + cellsPerArea >= g.Size) continue;
                var key = new AreaKey(a, b);
                list.Add(new TerrainPiece
                {
                    Area = key,
                    WorldOrigin = new Vector3(x0 * g.Spacing, 0, z0 * g.Spacing),
                    Mesh = BuildGrid(g, x0, z0, cellsPerArea, cellsPerArea, uvExt, skirt, key + "_LOD2"),
                });
            }
        return list;
    }

    /// <summary>
    /// MeshData (ein Submesh) in GPU-Vertices/Indizes umsetzen – ersetzt MeshData.ToMesh() aus dem
    /// Unity-Projekt. uv wählt den UV-Kanal (0 = UV0, 1 = UV1, 2 = UV2, 3 = UV3).
    /// </summary>
    public static (MeshVertex[] V, uint[] I) ToVertices(MeshData md, int subMesh = -1, int uv = 0)
    {
        var uvs = uv switch { 1 => md.UV1, 2 => md.UV2, 3 => md.UV3, _ => md.UV0 };
        if (uvs.Count != md.Positions.Count) uvs = md.UV0;
        bool hasN = md.Normals.Count == md.Positions.Count;
        if (subMesh < 0)
        {
            var v = new MeshVertex[md.Positions.Count];
            for (int i = 0; i < v.Length; i++)
                v[i] = new MeshVertex(md.Positions[i], hasN ? md.Normals[i] : Vector3.UnitY, i < uvs.Count ? uvs[i] : Vector2.Zero);
            int total = 0;
            foreach (var s in md.SubMeshes) total += s.Count;
            var idx = new uint[total];
            int o = 0;
            foreach (var s in md.SubMeshes) foreach (var t in s) idx[o++] = (uint)t;
            return (v, idx);
        }
        // ein Submesh, kompakt (nur die referenzierten Vertices)
        var tris = md.SubMeshes[subMesh];
        var map = new Dictionary<int, uint>();
        var verts = new List<MeshVertex>();
        var ind = new uint[tris.Count];
        for (int k = 0; k < tris.Count; k++)
        {
            int gi = tris[k];
            if (!map.TryGetValue(gi, out uint li))
            {
                li = (uint)verts.Count;
                map[gi] = li;
                verts.Add(new MeshVertex(md.Positions[gi], hasN ? md.Normals[gi] : Vector3.UnitY, gi < uvs.Count ? uvs[gi] : Vector2.Zero));
            }
            ind[k] = li;
        }
        return (verts.ToArray(), ind);
    }
}
