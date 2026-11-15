using Paradise.Core.Formats;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Terrain-Löcher an Tunnelportalen und unter Boden-/Straßenobjekten. Portiert aus dem Unity-Projekt (TerrainHoles).
/// Das Höhenraster kennt keine Löcher (regelmäßiges 257²-Gitter), an den Portalen liegt das Terrain über der
/// Tunnelfahrbahn. Hier werden alle 3-m-Zellen ausgeschnitten, deren Terrain in den Tunnelquerschnitt ragt
/// (zwischen Boden und Dach) oder als dünne Decke direkt über dem Dach liegt. Thread-sicher.
/// Matrizen: System.Numerics-Zeilenvektoren (Unity "pm * m" = hier "m * pm").
/// </summary>
public static class TerrainHoles
{
    public static float Clearance = 7.5f;
    public static float MinAbove = 0.25f;
    public static int FlankRings = 2;
    public static float DeepCover = 12f;
    public static float PortalHeight = 9f;
    public static int DriveFlankRings = 3;
    static readonly ulong SemLight = Bin.KeyFromString("LIGHT");

    public static bool IsTunnelRoad(MaterialDef? def)
    {
        if (def == null || !def.IsRoad) return false;
        if (def.Name != null && def.Name.IndexOf("tunnel", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return def.TryGetLayer(SemLight, out var l) && l.TextureKey != 0;
    }

    static List<(InsRecord ins, Matrix4x4 m)> Leaves(Scene3DD scene)
    {
        var byOffset = new Dictionary<int, HieRecord>();
        foreach (var h in scene.Hierarchy) byOffset[h.Offset] = h;
        var leaves = new List<(InsRecord ins, Matrix4x4 m)>();
        if (scene.Hierarchy.Count > 0)
            foreach (var h in scene.Hierarchy) { var ins = scene.GetIns(h.InsOffset); if (ins.Lods.Length > 0) leaves.Add((ins, scene.WorldMatrix(h, byOffset))); }
        else
            foreach (var off in scene.InsOffsets) { var ins = scene.GetIns(off); if (ins.Lods.Length > 0) leaves.Add((ins, ins.Matrix)); }
        return leaves;
    }

    static readonly List<Matrix4x4> IdentityList = new() { Matrix4x4.Identity };

    /// <summary>Alle Tunnel-Dreiecke (Boden + Wände/Decke) eines Objektsatzes in Bank-lokalen Koordinaten (flache Liste).</summary>
    public static List<Vector3> ExtractTunnelTriangles(IEnumerable<ObjectSet>? sets)
    {
        var tri = new List<Vector3>();
        if (sets == null) return tri;
        foreach (var set in sets)
        {
            var scene = set?.Scene;
            if (scene == null || set!.Materials == null) continue;
            var placements = set.Placements ?? IdentityList;
            foreach (var (ins, m) in Leaves(scene))
            {
                if (!set.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) || md == null) continue;
                var bank = scene.ReadMaterialBank(ins.MaterialBankOffset);
                for (int s = 0; s < md.SubMeshes.Count; s++)
                {
                    int mi = md.SubMeshMaterialIndex[s];
                    ulong mk = mi >= 0 && mi < bank.Length ? bank[mi] : 0;
                    if (mk == 0 || !set.Materials.Materials.TryGetValue(mk, out var def) || !IsTunnelRoad(def)) continue;
                    var tris = md.SubMeshes[s];
                    foreach (var pm in placements)
                    {
                        var full = m * pm;
                        for (int t = 0; t + 2 < tris.Count; t += 3)
                        {
                            tri.Add(Vector3.Transform(md.Positions[tris[t]], full));
                            tri.Add(Vector3.Transform(md.Positions[tris[t + 1]], full));
                            tri.Add(Vector3.Transform(md.Positions[tris[t + 2]], full));
                        }
                    }
                }
            }
        }
        return tri;
    }

    /// <summary>Portal-Objekte (Name enthält "TUN_", aber nicht "NETWORK"): die sichtbaren Tunnel-Fassaden.</summary>
    public static List<Vector3> ExtractPortalTriangles(IEnumerable<ObjectSet>? sets)
    {
        var tri = new List<Vector3>();
        if (sets == null) return tri;
        foreach (var set in sets)
        {
            var scene = set?.Scene;
            if (scene == null) continue;
            var placements = set!.Placements ?? IdentityList;
            foreach (var (ins, m) in Leaves(scene))
            {
                string nm = Bin.KeyToString(ins.Lods[0].ObjKey);
                if (nm == null || nm.IndexOf("TUN_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (nm.IndexOf("NETWORK", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!set.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) || md == null) continue;
                for (int s = 0; s < md.SubMeshes.Count; s++)
                {
                    var tris = md.SubMeshes[s];
                    foreach (var pm in placements)
                    {
                        var full = m * pm;
                        for (int t = 0; t + 2 < tris.Count; t += 3)
                        {
                            tri.Add(Vector3.Transform(md.Positions[tris[t]], full));
                            tri.Add(Vector3.Transform(md.Positions[tris[t + 1]], full));
                            tri.Add(Vector3.Transform(md.Positions[tris[t + 2]], full));
                        }
                    }
                }
            }
        }
        return tri;
    }

    static float Max4(float a, float b, float c, float d) => MathF.Max(MathF.Max(a, b), MathF.Max(c, d));
    static float Min4(float a, float b, float c, float d) => MathF.Min(MathF.Min(a, b), MathF.Min(c, d));

    /// <returns>Maske [z * cells + x] (true = ausschneiden) oder null, wenn nichts zu tun ist.</returns>
    public static bool[]? Compute(HeightGrid? g, IEnumerable<ObjectSet> sets, List<Vector3>? extra, out bool[]? driveMask)
    {
        driveMask = null;
        if (g == null) return null;
        var tri = ExtractTunnelTriangles(sets);
        if (extra != null && extra.Count > 0) tri.AddRange(extra);
        var ptri = ExtractPortalTriangles(sets);
        if (tri.Count < 3 && ptri.Count < 3) return null;

        int cells = g.Size - 1, n = cells * cells;
        var rMin = new float[n];
        var rMax = new float[n];
        var touched = new bool[n];
        for (int i = 0; i < n; i++) { rMin[i] = float.PositiveInfinity; rMax[i] = float.NegativeInfinity; }
        for (int t = 0; t + 2 < tri.Count; t += 3)
            Rasterize(g, rMin, rMax, touched, cells, tri[t], tri[t + 1], tri[t + 2]);

        var pMin = new float[n];
        var pMax = new float[n];
        var pTouched = new bool[n];
        for (int i = 0; i < n; i++) { pMin[i] = float.PositiveInfinity; pMax[i] = float.NegativeInfinity; }
        for (int t = 0; t + 2 < ptri.Count; t += 3)
            Rasterize(g, pMin, pMax, pTouched, cells, ptri[t], ptri[t + 1], ptri[t + 2]);

        bool deep = false;
        for (int i = 0; i < n && !deep; i++)
        {
            if (!touched[i]) continue;
            int x = i % cells, z = i / cells;
            float m = Max4(g.Get(x, z), g.Get(x + 1, z), g.Get(x, z + 1), g.Get(x + 1, z + 1));
            if (m - rMax[i] > DeepCover) deep = true;
        }

        bool[]? mask = null;
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int i = z * cells + x;
                if (!touched[i] && !pTouched[i]) continue;
                float h00 = g.Get(x, z), h10 = g.Get(x + 1, z), h01 = g.Get(x, z + 1), h11 = g.Get(x + 1, z + 1);
                float cMax = Max4(h00, h10, h01, h11);
                float cMin = Min4(h00, h10, h01, h11);
                bool cut = false;
                if (pTouched[i])
                    cut = cMax > pMin[i] + MinAbove && cMin < pMin[i] + PortalHeight;
                // Querschnitts-Regel (C++ MarkCarvedCells): Gelände ragt in die Röhre -> auch im Sichtnetz
                // ausschneiden. Galt bisher nur bei tiefen Tunneln; bei flachen blieb die Erdzunge vor dem Portal
                // stehen, obwohl das Auto unsichtbar durchfuhr ("Loch nicht sichtbar").
                // Bei flachen Tunneln 1 m unter dem Gewölbe bleiben: das Gelände liegt dort oft genau AUF dem
                // Tunnelrohr – das ist der Berg darüber und muss stehen bleiben, sonst liegt die Röhre frei.
                if (!cut && touched[i])
                    cut = cMax > rMin[i] + MinAbove && cMin < rMax[i] - (deep ? 0f : 1f);
                if (cut)
                {
                    mask ??= new bool[n];
                    mask[i] = true;
                }
            }

        if (mask != null && FlankRings > 0 && deep)
        {
            var floorArr = (float[])rMin.Clone();
            var roofArr = (float[])rMax.Clone();
            for (int ring = 0; ring < FlankRings; ring++)
            {
                var add = new List<int>();
                for (int z = 0; z < cells; z++)
                    for (int x = 0; x < cells; x++)
                    {
                        int i = z * cells + x;
                        if (mask[i]) continue;
                        float floorRef = float.PositiveInfinity, roofRef = float.NegativeInfinity;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, nz = z + dz;
                                if (nx < 0 || nz < 0 || nx >= cells || nz >= cells) continue;
                                int ni = nz * cells + nx;
                                if (!mask[ni]) continue;
                                if (floorArr[ni] < floorRef) floorRef = floorArr[ni];
                                if (roofArr[ni] > roofRef) roofRef = roofArr[ni];
                            }
                        if (float.IsPositiveInfinity(floorRef)) continue;
                        float h00 = g.Get(x, z), h10 = g.Get(x + 1, z), h01 = g.Get(x, z + 1), h11 = g.Get(x + 1, z + 1);
                        float cMax = Max4(h00, h10, h01, h11);
                        float cMin = Min4(h00, h10, h01, h11);
                        if (cMax > floorRef + MinAbove && cMin < roofRef)
                        {
                            add.Add(i);
                            floorArr[i] = floorRef;
                            roofArr[i] = roofRef;
                        }
                    }
                if (add.Count == 0) break;
                foreach (var i in add) mask[i] = true;
            }
        }

        if (!deep)
        {
            for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    int i = z * cells + x;
                    if (!touched[i]) continue;
                    float cMax = Max4(g.Get(x, z), g.Get(x + 1, z), g.Get(x, z + 1), g.Get(x + 1, z + 1));
                    if (cMax <= rMin[i] + MinAbove) continue;
                    driveMask ??= mask != null ? (bool[])mask.Clone() : new bool[n];
                    driveMask[i] = true;
                }
        }
        if (driveMask == null && mask != null) driveMask = (bool[])mask.Clone();

        // Fahr-Maske am Portalrand einen Ring weiter öffnen – auch bei tiefen Tunneln.
        // Sonst bleibt genau die Zelle stehen, in der das Gelände die Fahrbahn um wenige Meter überragt
        // (Portalzunge, Sektor 3-0-2-7: +3 m Wand trotz Maske) und das Auto bleibt davor hängen.
        if (driveMask != null && DriveFlankRings > 0)
        {
            var floorArr = (float[])rMin.Clone();
            for (int ring = 0; ring < DriveFlankRings; ring++)
            {
                var add = new List<int>();
                for (int z = 0; z < cells; z++)
                    for (int x = 0; x < cells; x++)
                    {
                        int i = z * cells + x;
                        if (driveMask[i]) continue;
                        float floorRef = float.PositiveInfinity;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, nz = z + dz;
                                if (nx < 0 || nz < 0 || nx >= cells || nz >= cells) continue;
                                int ni = nz * cells + nx;
                                // Referenz ist die tiefste Tunnelstelle der Nachbarschaft: auch Zellen, die nur an
                                // einem Dreiecks-Eckpunkt berührt sind (nicht in der Maske), liefern ihre Fahrbahn-
                                // höhe rMin. Sonst bleibt am Portalrand die Geländezunge über der Fahrbahn stehen.
                                float f = driveMask[ni] ? floorArr[ni] : rMin[ni];
                                if (f < floorRef) floorRef = f;
                            }
                        if (float.IsPositiveInfinity(floorRef)) continue;
                        float cMax = Max4(g.Get(x, z), g.Get(x + 1, z), g.Get(x, z + 1), g.Get(x + 1, z + 1));
                        if (cMax > floorRef + MinAbove)
                        {
                            add.Add(i);
                            floorArr[i] = floorRef;
                        }
                    }
                if (add.Count == 0) break;
                foreach (var i in add) driveMask[i] = true;
            }
        }
        return mask;
    }

    /// <summary>
    /// Entfernt aus den Tunnel-Masken die Zellen, unter denen keine befahrbare Fläche liegt
    /// (C++ <c>EdenWorld::PruneUncoveredCarveCells</c>). Eine ausgeschnittene Zelle ohne Boden darunter ist ein
    /// Loch: man sieht hindurch, und das Auto fällt durch. Solche Zellen behalten ihr Gelände – das ist der
    /// vorherige (nicht geschnittene) Zustand und fahrbarer Grund. Nur nach oben zeigende Dreiecke zählen als
    /// Boden (Normalen-Test wie in <c>BuildCarveGround</c>), Wände und Decken nicht.
    /// Geprüft wird auf allen fünf Stützpunkten der Zelle (Mitte + Ecken, wie <see cref="Rasterize"/>), damit
    /// eine Zelle, die eine Fahrbahn nur am Rand berührt, nicht fälschlich als Loch gilt.
    /// Auf die SICHT-Maske anwenden (C++ prunt die Carve-Zellen, die auch das Gelände zeichnen). Die Fahr-Maske
    /// dagegen nur, wenn man in Kauf nehmen will, dass Berggelände wieder mitten in der Röhre steht.
    /// </summary>
    /// <param name="visible">Sicht-Maske (wird an Ort und Stelle geändert) oder null.</param>
    /// <param name="drive">Fahr-Maske (wird an Ort und Stelle geändert) oder null.</param>
    /// <returns>Anzahl der zurückgenommenen Zellen je Maske.</returns>
    public static (int visible, int drive) PruneUncoveredCells(HeightGrid? g, IEnumerable<ObjectSet>? sets, List<Vector3>? extra,
                                                               bool[]? visible, bool[]? drive)
    {
        if (g == null || (visible == null && drive == null)) return (0, 0);
        var tri = ExtractTunnelTriangles(sets);
        if (extra != null && extra.Count > 0) tri.AddRange(extra);
        if (tri.Count < 3) return (0, 0);

        int cells = g.Size - 1, n = cells * cells;
        // Rasterize() erwartet die Höhen-Arrays; für den reinen Abdeckungstest bleiben sie unbenutzt.
        var rMin = new float[n];
        var rMax = new float[n];
        var touched = new bool[n];
        for (int i = 0; i < n; i++) { rMin[i] = float.PositiveInfinity; rMax[i] = float.NegativeInfinity; }
        var floor = new bool[n];
        for (int t = 0; t + 2 < tri.Count; t += 3)
        {
            var a = tri[t];
            var b = tri[t + 1];
            var c = tri[t + 2];
            var normal = Vector3.Cross(b - a, c - a);
            float length = normal.Length();
            if (length < 1e-6f || normal.Y <= 0.5f * length) continue; // Wand / Decke: kein Boden
            Rasterize(g, rMin, rMax, touched, cells, a, b, c, floor);
        }
        // Tunnelsohle je Zelle (tiefstes Dreieck, egal welche Ausrichtung) – Bezug für die "Loch in die Welt"-Prüfung.
        var sMin = new float[n];
        var sMax = new float[n];
        var sTouched = new bool[n];
        for (int i = 0; i < n; i++) { sMin[i] = float.PositiveInfinity; sMax[i] = float.NegativeInfinity; }
        for (int t = 0; t + 2 < tri.Count; t += 3)
            Rasterize(g, sMin, sMax, sTouched, cells, tri[t], tri[t + 1], tri[t + 2]);

        int pv = 0, pd = 0;
        for (int i = 0; i < n; i++)
        {
            if (floor[i]) continue;
            int x = i % cells, z = i / cells;
            float cMax = Max4(g.Get(x, z), g.Get(x + 1, z), g.Get(x, z + 1), g.Get(x + 1, z + 1));
            // Nur zurücknehmen, wenn das Gelände nachweislich UNTER der Tunnelsohle liegt: dann würde der Schnitt
            // ein Loch in die offene Welt (unter Brücken/Hochstraßen) reißen. Steht das Gelände über der Sohle, ist
            // es Fels in der Röhre – der Schnitt muss bleiben, sonst ist das Portal wieder zugebaut.
            bool holeIntoWorld = sTouched[i] && cMax <= sMin[i] - MinAbove;
            if (visible != null && visible[i] && holeIntoWorld) { visible[i] = false; pv++; }
            if (drive != null && drive[i] && (holeIntoWorld || !sTouched[i])) { drive[i] = false; pd++; }
        }
        return (pv, pd);
    }

    static void Rasterize(HeightGrid g, float[] rMin, float[] rMax, bool[] touched, int cells, Vector3 a, Vector3 b, Vector3 c,
                          bool[]? covered = null)
    {
        float sp = g.Spacing;
        int x0 = System.Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / sp) - 1);
        int x1 = System.Math.Min(cells - 1, (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / sp) + 1);
        int z0 = System.Math.Max(0, (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / sp) - 1);
        int z1 = System.Math.Min(cells - 1, (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / sp) + 1);
        if (x0 > x1 || z0 > z1) return;
        float det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(det) < 1e-6f) return;
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                for (int k = 0; k < 5; k++)
                {
                    float px = (k == 0 ? x + 0.5f : x + ((k - 1) & 1)) * sp;
                    float pz = (k == 0 ? z + 0.5f : z + (((k - 1) >> 1) & 1)) * sp;
                    float l1 = ((b.Z - c.Z) * (px - c.X) + (c.X - b.X) * (pz - c.Z)) / det;
                    float l2 = ((c.Z - a.Z) * (px - c.X) + (a.X - c.X) * (pz - c.Z)) / det;
                    float l3 = 1f - l1 - l2;
                    const float e = -0.02f;
                    if (l1 < e || l2 < e || l3 < e) continue;
                    float roadY = l1 * a.Y + l2 * b.Y + l3 * c.Y;
                    int idx = z * cells + x;
                    touched[idx] = true;
                    if (covered != null) covered[idx] = true;
                    if (roadY > rMax[idx]) rMax[idx] = roadY;
                    if (roadY < rMin[idx]) rMin[idx] = roadY;
                }
            }
    }

    // ------------------------------------------------------------------ Gelände unter Boden-/Straßenobjekten
    public static float CoverBelow = 1.5f, CoverAbove = 2.5f;

    static bool IsCoverMaterial(MaterialDef? def)
    {
        if (def == null || !(def.IsRoad || def.IsGround) || IsTunnelRoad(def)) return false;
        var blend = MaterialFactory.GetBlend(def, out _, out _);
        return blend == EdenBlend.Opaque || blend == EdenBlend.Cutout;
    }

    /// <summary>
    /// Zellen, die vollständig von Straßen/Gehwegen/Plätzen bedeckt sind, werden im dargestellten Gelände ausgespart
    /// (sonst schaut das 3-m-Raster als Erdfleck durch). Thread-sicher. Rückgabe: Maske [z * cells + x] oder null.
    /// </summary>
    /// <summary>Unterteilung je 3-m-Zelle für den Abdeckungstest (Ecken, Kantenpunkte und Innenpunkte).</summary>
    public static int CoverSub = 4;

    public static bool[]? ComputeGroundCover(HeightGrid? g, IEnumerable<ObjectSet>? sets, out int count)
    {
        count = 0;
        if (g == null || sets == null) return null;
        int size = g.Size, cells = size - 1;
        float sp = g.Spacing;
        int n = CoverSub, sn = cells * n + 1;
        // Eine Zelle wird nur ausgespart, wenn ALLE (n+1)² Prüfpunkte von einer Boden-/Straßenfläche bedeckt sind.
        // Vorher (nur Ecken + Mitte) blieben an unregelmäßigen Rändern (Felskanten) Lücken, durch die der Himmel schien.
        var cov = new bool[sn * sn];
        bool any = false;
        foreach (var set in sets)
        {
            var scene = set?.Scene;
            if (scene == null || set!.Materials == null) continue;
            var placements = set.Placements ?? IdentityList;
            foreach (var (ins, m) in Leaves(scene))
            {
                if (!set.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) || md == null) continue;
                var bank = scene.ReadMaterialBank(ins.MaterialBankOffset);
                for (int s = 0; s < md.SubMeshes.Count; s++)
                {
                    int mi = md.SubMeshMaterialIndex[s];
                    ulong mk = mi >= 0 && mi < bank.Length ? bank[mi] : 0;
                    if (mk == 0 || !set.Materials.Materials.TryGetValue(mk, out var def) || !IsCoverMaterial(def)) continue;
                    var tris = md.SubMeshes[s];
                    foreach (var pm in placements)
                    {
                        var full = m * pm;
                        for (int t = 0; t + 2 < tris.Count; t += 3)
                        {
                            var a = Vector3.Transform(md.Positions[tris[t]], full);
                            var b = Vector3.Transform(md.Positions[tris[t + 1]], full);
                            var c = Vector3.Transform(md.Positions[tris[t + 2]], full);
                            var nrm = Vector3.Cross(b - a, c - a);
                            float len = nrm.Length();
                            if (len < 1e-6f || MathF.Abs(nrm.Y) / len < 0.5f) continue;
                            if (CoverTri(g, cov, sn, sp / n, a, b, c)) any = true;
                        }
                    }
                }
            }
        }
        if (!any) return null;
        bool[]? mask = null;
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                bool all = true;
                for (int dz = 0; dz <= n && all; dz++)
                    for (int dx = 0; dx <= n; dx++)
                        if (!cov[(z * n + dz) * sn + x * n + dx]) { all = false; break; }
                if (!all) continue;
                mask ??= new bool[cells * cells];
                mask[z * cells + x] = true;
                count++;
            }
        return mask;
    }

    static bool CoverTri(HeightGrid g, bool[] cov, int sn, float step, Vector3 a, Vector3 b, Vector3 c)
    {
        float det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(det) < 1e-6f) return false;
        int x0 = System.Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / step));
        int x1 = System.Math.Min(sn - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)) / step));
        int z0 = System.Math.Max(0, (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / step));
        int z1 = System.Math.Min(sn - 1, (int)MathF.Ceiling(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / step));
        bool hit = false;
        for (int zz = z0; zz <= z1; zz++)
            for (int xx = x0; xx <= x1; xx++)
            {
                float px = xx * step, pz = zz * step;
                float l1 = ((b.Z - c.Z) * (px - c.X) + (c.X - b.X) * (pz - c.Z)) / det;
                float l2 = ((c.Z - a.Z) * (px - c.X) + (a.X - c.X) * (pz - c.Z)) / det;
                float l3 = 1f - l1 - l2;
                const float e = -0.001f;
                if (l1 < e || l2 < e || l3 < e) continue;
                float y = l1 * a.Y + l2 * b.Y + l3 * c.Y;
                float th = g.Sample(px, pz);
                if (y < th - CoverBelow || y > th + CoverAbove) continue;
                cov[zz * sn + xx] = true;
                hit = true;
            }
        return hit;
    }
}
