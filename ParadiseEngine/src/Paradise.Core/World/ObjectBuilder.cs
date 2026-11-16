using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

public sealed class ObjectBuildOptions
{
    public float LodDistanceScale = 3f;   // Distanz = sqrt(Dateiwert) * Faktor
    public bool CullLastLod = false;      // letzte LOD-Stufe ausblenden?
    public bool SkipOcean = true;         // Ozean-Meshes weglassen (eigene Wasserfläche)
    public bool SkipEffects = true;       // FX_* (Lichtkegel/Volumen) weglassen
    public bool Collision;                // Kollisionsdreiecke der festen Flächen merken (Sektoren, Physik)
    /// <summary>Kollisionsdreiecke aller Meshes merken (Interieur: Unity TDU2Interiors.PrepareStatic entscheidet selbst).</summary>
    public bool CollisionAll;
    /// <summary>Meer-Material dieser Bank (TD_Ocean mit der Colormap der Ebene). Gesetzt -> Ozean-Meshes werden damit gezeichnet.</summary>
    public IGpuMaterial? OceanMaterial;
    /// <summary>Eigenes Material je 2dm-Material (z.B. Wolkenkuppel TD_CloudMesh); null-Ergebnis = Standardmaterial.
    /// Die erzeugten Materialien gehören dem Aufrufer.</summary>
    public Func<MaterialDef?, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?>, IGpuMaterial?>? MaterialOverride;

    /// <summary>Kopie mit eigenem Meer-Material (je Sektor/Area).</summary>
    public ObjectBuildOptions WithOcean(IGpuMaterial? ocean) => new()
    {
        LodDistanceScale = LodDistanceScale,
        CullLastLod = CullLastLod,
        SkipOcean = SkipOcean,
        SkipEffects = SkipEffects,
        Collision = Collision,
        CollisionAll = CollisionAll,
        OceanMaterial = ocean,
    };
}

/// <summary>Teil eines Renderables: ein Submesh mit seinem Material (Unity: Submesh + Material-Slot).</summary>
public sealed class RenderPart
{
    public IGpuMesh Mesh = null!;
    public IGpuMaterial Material = null!;
    /// <summary>
    /// Kollisionsdreiecke (Mesh-lokal, je 3 Punkte) – nur für feste Flächen, wie TDU2MapStreamer.AddColliders im Unity-Projekt;
    /// null = keine Kollision (Laub, Sprites, Gras, Wasser, Leuchteffekte, flache Decals).
    /// </summary>
    public Vector3[]? Collision;
}

/// <summary>Eine LOD-Stufe (Unity: LOD im LODGroup).</summary>
public sealed class RenderLod
{
    public RenderPart[] Parts = Array.Empty<RenderPart>();
    public float MaxDistance = float.PositiveInfinity;
}

/// <summary>Ein platziertes Objekt (Unity: GameObject mit MeshRenderer/LODGroup). Koordinaten = Spielkoordinaten.</summary>
public sealed class RenderObject
{
    /// <summary>Faktor auf alle LOD-Umschaltdistanzen (Unity QualitySettings.lodBias) – <see cref="RenderDistance"/>.</summary>
    public static float LodBias = 1f;

    public string Name = "";
    public Matrix4x4 Model = Matrix4x4.Identity;
    public RenderLod[] Lods = Array.Empty<RenderLod>();
    public bool Active = true;
    public bool CullLastLod;
    public Vector3 Center;
    public Vector3 BoundsMin, BoundsMax;

    /// <summary>LOD für die Kameradistanz (wie LODGroup mit den aus der Datei abgeleiteten Umschaltdistanzen).</summary>
    public RenderLod? SelectLod(Vector3 cameraGame)
    {
        if (Lods.Length == 0) return null;
        if (Lods.Length == 1 && !CullLastLod) return Lods[0];
        float d = Vector3.Distance(cameraGame, Center);
        for (int i = 0; i < Lods.Length; i++)
            if (d < Lods[i].MaxDistance * LodBias) return Lods[i];
        return CullLastLod ? null : Lods[^1];
    }
}

/// <summary>Sammelt alle erzeugten GPU-Ressourcen, damit sie beim Entladen freigegeben werden. Portiert aus dem Unity-Projekt.</summary>
public sealed class BuildResources
{
    public readonly List<IDisposable> Owned = new();
    // Caches pro Szene (Schlüssel enthält die Materialbibliothek der Szene): mehrere Exteriors in einer Kachel
    // verwenden oft gleiche Objekt-/Materialnamen (gleicher Hash) mit unterschiedlichem Inhalt.
    public readonly Dictionary<(object scope, ulong key), IGpuMaterial?> Materials = new();
    public readonly Dictionary<(object scope, long key), Renderable?> Renderables = new();

    public void Destroy()
    {
        foreach (var o in Owned)
        {
            if (o is IGpuMaterial m) { MaterialFactory.Forget(m); TextureAnimations.Forget(m); }
            o.Dispose();
        }
        Owned.Clear(); Materials.Clear(); Renderables.Clear();
    }
}

/// <summary>Mesh + Materialien einer Geometrie (Unity: (Mesh, Material[])) plus lokale Bounds.</summary>
public sealed class Renderable
{
    public RenderPart[] Parts = Array.Empty<RenderPart>();
    public Vector3 Min, Max;
}

/// <summary>Portiert aus dem Unity-Projekt (ObjectBuilder). Statt GameObjects entstehen RenderObjects.</summary>
public static class ObjectBuilder
{
    /// <summary>Distanz aus dem Dateiwert (vermutlich quadratisch gespeichert). 0 = unbegrenzt.</summary>
    /// <summary>Name/Hash des Straßennetz-Modells eines Sektors ("NETWORK").</summary>
    public const ulong NetworkKey = 0x004b524f5754454eUL;
    /// <summary>Gelände-Ersatz an Loch-Kacheln: Sektormodell "BLOCK", Material-Shaderset 0xa0a0a1a1a2a2a3a3 ("tunnel_mesh").</summary>
    public const ulong BlockKey = 0x4b434f4c42UL, TerrainBlockShader = 0xa0a0a1a1a2a2a3a3UL;

    public static float LodDistance(float raw, float scale) => raw > 0f ? MathF.Sqrt(raw) * scale : float.PositiveInfinity;

    /// <summary>Lichtkegel/-schein der Straßenlaternen (FX_LIGHT, FX_SHAFT): nur nachts sichtbar.</summary>
    public static bool IsLampBeam(string? name) => name != null && (name.StartsWith("FX_LIGHT", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FX_SHAFT", StringComparison.OrdinalIgnoreCase));

    /// <summary>Mesh + Materialien für eine Geometrie, gefiltert (Ozean) und gecacht.</summary>
    public static Renderable? GetRenderable(
        MeshData? md, ulong geoKey, ulong[] materialBank, Material2DM? matLib,
        Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTex,
        BuildResources res, ObjectBuildOptions opt, int bankOffset, IRenderer r, HashSet<ulong>? wetMaterials = null)
    {
        object scope = (object?)matLib ?? (object?)md ?? res;
        var cacheKey = (scope, (long)geoKey ^ ((long)bankOffset << 1));
        if (res.Renderables.TryGetValue(cacheKey, out var cached)) return cached;
        if (md == null) { res.Renderables[cacheKey] = null; return null; }

        var parts = new List<RenderPart>();
        var collParts = new List<(ObjectVertex[] v, uint[] idx)>();
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int i = 0; i < md.SubMeshes.Count; i++)
        {
            int mi = md.SubMeshMaterialIndex[i];
            bool sprite = i < md.SubMeshIsSprite.Count && md.SubMeshIsSprite[i];
            ulong mk = mi >= 0 && mi < materialBank.Length ? materialBank[mi] : 0;
            MaterialDef? def = null;
            if (mk != 0 && matLib != null) matLib.Materials.TryGetValue(mk, out def);
            if (def != null && def.IsOcean && opt.SkipOcean && opt.OceanMaterial == null) continue;
            // Geländeblock (Shaderset 0xa0a0a1a1a2a2a3a3): kein Objekt, sondern Teil des Geländes (TerrainMeshBuilder.AppendTerrainBlock).
            // Original 0xc50e74: Material wie TD_Ground mit Sektortextur (Sampler +0x150) + SECTROFF.
            if (def != null && def.ShaderSet == TerrainBlockShader) continue;
            // FX_*: Lichtvolumen weglassen – außer den Lichtkegeln der Laternen (FX_LIGHT/FX_SHAFT), die nachts leuchten
            if (def != null && opt.SkipEffects && def.IsEffect && !IsLampBeam(def.Name)) continue;
            int uvSets = i < md.SubMeshUvSets.Count ? md.SubMeshUvSets[i] : 1;
            bool tintUv = uvSets >= 3;   // DETAIL2 (Farbpalette/Schmutz) liegt auf UV-Satz 3
            bool fourUv = uvSets >= 4;   // Straßen: MARKS2 auf UV-Satz 3
            var cacheMat = (scope, (sprite ? mk ^ 0x5350524954450000UL : mk) ^ (tintUv ? 0x8000000000000000UL : 0UL) ^ (fourUv ? 0x4000000000000000UL : 0UL));
            if (!res.Materials.TryGetValue(cacheMat, out var mat))
            {
                if (def != null && def.IsOcean && opt.OceanMaterial != null) mat = opt.OceanMaterial;   // gehört dem Aufrufer
                else if (opt.MaterialOverride != null && opt.MaterialOverride(def, getTex) is { } om) mat = om;   // gehört dem Aufrufer
                else
                {
                    mat = MaterialFactory.CreateObjectMaterial(def, getTex, sprite, tintUv, fourUv, wetMaterials != null && wetMaterials.Contains(mk));
                    if (mat != null && !MaterialFactory.IsShared(mat)) res.Owned.Add(mat);
                }
                res.Materials[cacheMat] = mat;
            }
            if (mat == null) continue; // ausgeblendet (z.B. Leuchteffekte am Tag, noch fehlender Shader)

            var (v, idx) = ToObjectVertices(md, i, ref min, ref max);
            if (idx.Length == 0) continue;
            var mesh = r.CreateMesh(v, idx);
            res.Owned.Add(mesh);
            parts.Add(new RenderPart { Mesh = mesh, Material = mat });
            collParts.Add((v, idx));
        }
        if (opt.Collision && parts.Count > 0 && (opt.CollisionAll || IsSolid(parts, min, max)))
            for (int k = 0; k < parts.Count; k++) parts[k].Collision = OrientedTriangles(collParts[k].v, collParts[k].idx);
        var result = parts.Count == 0 ? null : new Renderable { Parts = parts.ToArray(), Min = min, Max = max };
        res.Renderables[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Unity TDU2MapStreamer.AddColliders: Entscheidung je Mesh (MeshFilter mit allen Submeshes), nicht je Teil. Fest, wenn ein
    /// Material opak ist (Laub/Sprite/Leuchten/Gras/Wasser und additive Leuchteffekte zählen nicht); sonst Alpha-Test/Transparent
    /// (Zäune, Glas) nur, wenn das ganze Mesh aufrecht steht (Höhe ≥ 0,5 m). Netz braucht zwei Ausdehnungen (UnityUtil.IsCollidable).
    /// Dann bekommt das ganze Mesh Kollision (Unity: MeshCollider.sharedMesh = mf.sharedMesh, alle Submeshes).
    /// Vorher entschied die Engine je Teil und ließ flache Straßen-/Bodenteile mit Alpha-Rand zusätzlich kollidieren (Bordsteinkanten
    /// als harte 10-cm-Stufen -> Dämpferspitzen bis 200 kN, Auto hüpft).
    /// </summary>
    static bool IsSolid(List<RenderPart> parts, Vector3 min, Vector3 max)
    {
        bool solid = false, thin = false;
        foreach (var p in parts)
        {
            var mat = p.Material;
            if (mat.Kind is MaterialKind.Foliage or MaterialKind.Sprite or MaterialKind.SpriteGlow or MaterialKind.Grass or MaterialKind.Water) continue;
            bool cutout = false, transparent = mat.Kind == MaterialKind.Transparent;
            if (MaterialFactory.TryGetInfo(mat, out var info))
            {
                cutout = info.Desc.Cutoff > 0.01f;
                if (transparent && info.Desc.Additive) continue;
            }
            if (!cutout && !transparent) { solid = true; break; }
            thin = true;
        }
        var sz = max - min;
        if (!solid && thin && sz.Y >= 0.5f) solid = true;
        if (!solid) return false;
        int axes = (sz.X > 1e-3f ? 1 : 0) + (sz.Y > 1e-3f ? 1 : 0) + (sz.Z > 1e-3f ? 1 : 0);
        return axes >= 2;
    }

    /// <summary>Dreiecke eines Teils für Bepu (einseitig, nach Vertexnormalen ausgerichtet).</summary>
    static Vector3[] OrientedTriangles(ObjectVertex[] v, uint[] idx)
    {
        // Bepu-Dreiecke sind einseitig (Kontakte und Raycasts nur von der Vorderseite, Vorderseite = Cross(b-a, c-a) wie beim
        // Gelände). Die Wicklung der Spielmeshes passt nicht dazu -> jedes Dreieck nach seinen Vertexnormalen (sichtbare Seite)
        // ausrichten; ohne brauchbare Normale beidseitig eintragen. (Unity: MeshCollider mit gespiegelter Welt.)
        var t = new List<Vector3>(idx.Length);
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            var va = v[idx[i]]; var vb = v[idx[i + 1]]; var vc = v[idx[i + 2]];
            var gn = Vector3.Cross(vb.Position - va.Position, vc.Position - va.Position);
            var vn = va.Normal + vb.Normal + vc.Normal;
            float d = Vector3.Dot(gn, vn);
            float scale = gn.Length() * vn.Length();
            if (scale > 1e-12f && MathF.Abs(d) > 0.05f * scale)
            {
                t.Add(va.Position);
                if (d > 0f) { t.Add(vb.Position); t.Add(vc.Position); }
                else { t.Add(vc.Position); t.Add(vb.Position); }
            }
            else
            {
                t.Add(va.Position); t.Add(vb.Position); t.Add(vc.Position);
                t.Add(va.Position); t.Add(vc.Position); t.Add(vb.Position);
            }
        }
        return t.ToArray();
    }

    /// <summary>Ein Submesh von MeshData in Objekt-Vertices umsetzen (ersetzt MeshData.ToMesh()).</summary>
    public static (ObjectVertex[] V, uint[] I) ToObjectVertices(MeshData md, int subMesh, ref Vector3 min, ref Vector3 max)
    {
        var tris = md.SubMeshes[subMesh];
        int n = md.Positions.Count;
        bool hasN = md.Normals.Count == n, hasC = md.Colors.Count == n;
        bool h0 = md.UV0.Count == n, h1 = md.UV1.Count == n, h2 = md.UV2.Count == n, h3 = md.UV3.Count == n;
        var map = new Dictionary<int, uint>();
        var verts = new List<ObjectVertex>();
        var ind = new uint[tris.Count];
        for (int k = 0; k < tris.Count; k++)
        {
            int gi = tris[k];
            if (!map.TryGetValue(gi, out uint li))
            {
                li = (uint)verts.Count;
                map[gi] = li;
                var p = md.Positions[gi];
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                var c = hasC ? md.Colors[gi] : new Rgba32(128, 128, 128, 255);   // fehlende Farben = 128 (neutral bei x2)
                var uv0 = h0 ? md.UV0[gi] : Vector2.Zero;
                verts.Add(new ObjectVertex
                {
                    Position = p,
                    Normal = hasN ? md.Normals[gi] : Vector3.UnitY,
                    Color = ObjectVertex.PackColor(c.R, c.G, c.B, c.A),
                    // UV1/UV2 bei fehlendem UV-Satz mit UV0 gefüllt (wie MeshData im Unity-Projekt)
                    Uv0 = uv0,
                    Uv1 = h1 ? md.UV1[gi] : uv0,
                    Uv2 = h2 ? md.UV2[gi] : uv0,
                    Uv3 = h3 ? md.UV3[gi] : Vector2.Zero,
                });
            }
            ind[k] = li;
        }
        return (verts.ToArray(), ind);
    }

    /// <summary>Alle Objekte einer Bank (Hierarchie HIE -> INS -> LODs). Positionen = Spielkoordinaten (Bank-Ursprung + lokal).</summary>
    public static List<RenderObject> BuildObjects(BankModel model, Vector3 bankOrigin, GlobalTextureCache global, BuildResources res, ObjectBuildOptions opt, IRenderer r, Action<RenderObject>? onCreated = null)
        => BuildObjects(model, Matrix4x4.CreateTranslation(bankOrigin), global, res, opt, r, onCreated);

    /// <param name="zeroLocalPosition">Objekte mit diesem Namen auf lokale Position 0 setzen (Spot-Exteriors: Tore/Türen).</param>
    public static List<RenderObject> BuildObjects(BankModel model, Matrix4x4 parent, GlobalTextureCache global, BuildResources res, ObjectBuildOptions opt, IRenderer r, Action<RenderObject>? onCreated = null, Func<string, bool>? zeroLocalPosition = null)
    {
        var all = new List<RenderObject>();
        IGpuTexture? GetTex(ulong k, TextureWrap u, TextureWrap v) => model.GetTexture(k, u, v, global, r);
        foreach (var set in model.AllObjectSets())
            all.AddRange(BuildObjects(set, GetTex, parent, res, opt, r, onCreated, zeroLocalPosition));
        return all;
    }

    public static List<RenderObject> BuildObjects(ObjectSet set, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTex, Matrix4x4 parent, BuildResources res, ObjectBuildOptions opt, IRenderer r, Action<RenderObject>? onCreated = null, Func<string, bool>? zeroLocalPosition = null)
    {
        if (set.Placements == null) return BuildObjectsAt(set, getTex, parent, res, opt, r, onCreated, zeroLocalPosition);
        var all = new List<RenderObject>();
        // Actor-Platzierungen (entities.xmb orient_pos_scale) enthalten bereits die Lage des Szenen-Wurzelobjekts
        // (in den Daten geprüft: pos == INS-Translation der Wurzel, z.B. PORTAILD/PORTAILG/DOOR/ESSOR in eph_l0c).
        // Die Wurzelmatrix darf dann nicht zusätzlich angewendet werden (sonst doppelter Versatz). Scenery = Identität.
        foreach (var pm in set.Placements)
            all.AddRange(BuildObjectsAt(set, getTex, pm * parent, res, opt, r, onCreated, zeroLocalPosition, !pm.IsIdentity));
        return all;
    }

    private static List<RenderObject> BuildObjectsAt(ObjectSet set, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTex, Matrix4x4 parent, BuildResources res, ObjectBuildOptions opt, IRenderer r, Action<RenderObject>? onCreated, Func<string, bool>? zeroLocalPosition, bool dropRoot = false)
    {
        var created = new List<RenderObject>();
        var scene = set.Scene;
        if (scene == null) return created;

        var byOffset = new Dictionary<int, HieRecord>();
        foreach (var h in scene.Hierarchy) byOffset[h.Offset] = h;

        IEnumerable<(InsRecord ins, Matrix4x4 m)> Leaves()
        {
            if (scene.Hierarchy.Count > 0)
            {
                foreach (var h in scene.Hierarchy)
                {
                    var ins = scene.GetIns(h.InsOffset);
                    if (ins.Lods.Length == 0) continue;
                    var wm = scene.WorldMatrix(h, byOffset);
                    if (dropRoot && Matrix4x4.Invert(scene.RootMatrix(h, byOffset), out var inv)) wm *= inv;
                    yield return (ins, wm);
                }
            }
            else
            {
                foreach (var off in scene.InsOffsets)
                {
                    var ins = scene.GetIns(off);
                    if (ins.Lods.Length == 0) continue;
                    yield return (ins, dropRoot && scene.InsOffsets.Count == 1 ? Matrix4x4.Identity : ins.Matrix);
                }
            }
        }

        // WFX_WetGrd (FUN_00aa0a30): Regen nur auf den Materialien des Sektormodells "NETWORK" (Hash 0x004b524f5754454e).
        // Der Schalter sitzt im Original am Material – teilt ein anderes Objekt ein NETWORK-Material, wird es ebenfalls nass.
        HashSet<ulong>? wet = null;
        foreach (var (ins, _) in Leaves())
        {
            if (ins.Key != NetworkKey && (ins.Lods.Length == 0 || ins.Lods[0].ObjKey != NetworkKey)) continue;
            wet ??= new HashSet<ulong>();
            foreach (var k in scene.ReadMaterialBank(ins.MaterialBankOffset)) if (k != 0) wet.Add(k);
        }

        foreach (var (ins, m) in Leaves())
        {
            var bankKeys = scene.ReadMaterialBank(ins.MaterialBankOffset);
            var lods = new List<RenderLod>();
            Renderable? first = null;
            foreach (var lod in ins.Lods)
            {
                set.Meshes.TryGetValue(lod.ObjKey, out var md);
                var rr = GetRenderable(md, lod.ObjKey, bankKeys, set.Materials, getTex, res, opt, ins.MaterialBankOffset, r, wet);
                if (rr == null) continue;
                first ??= rr;
                lods.Add(new RenderLod { Parts = rr.Parts, MaxDistance = LodDistance(lod.RawDistance, opt.LodDistanceScale) });
            }
            if (lods.Count == 0 || first == null) continue;

            string name = ins.Key != 0 ? Bin.KeyToString(ins.Key) : Bin.KeyToString(ins.Lods[0].ObjKey);
            var local = m;
            if (zeroLocalPosition != null && zeroLocalPosition(name) && local.Translation != Vector3.Zero)
            {
                Diagnostics.Log.Info($"Fixe Objektposition: {name} | Alt: {local.Translation}");
                local.Translation = Vector3.Zero;
            }
            var world = local * parent;
            var obj = new RenderObject
            {
                Name = name,
                Model = world,
                Lods = lods.ToArray(),
                CullLastLod = opt.CullLastLod,
            };
            TransformBounds(first.Min, first.Max, world, out obj.BoundsMin, out obj.BoundsMax);
            var s = ins.Lods[0].Sphere;
            obj.Center = s.W > 0f ? Vector3.Transform(new Vector3(s.X, s.Y, s.Z), world) : (obj.BoundsMin + obj.BoundsMax) * 0.5f;
            created.Add(obj);
            onCreated?.Invoke(obj);
        }
        return created;
    }

    /// <summary>Lokale AABB mit einer Matrix in eine Welt-AABB umrechnen (8 Ecken).</summary>
    public static void TransformBounds(Vector3 min, Vector3 max, Matrix4x4 m, out Vector3 wmin, out Vector3 wmax)
    {
        wmin = new Vector3(float.MaxValue); wmax = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
            var w = Vector3.Transform(c, m);
            wmin = Vector3.Min(wmin, w); wmax = Vector3.Max(wmax, w);
        }
    }
}
