using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Cloud dome as in the original (i.e., the cloud portion of the dome object):
/// <list type="bullet">
/// <item>Data: fx/clouds.bnk for the island – objects CLOUD_A / CLOUD_B / CLOUD_R (+ subobjects …_p), textures clouds/cloudsb,
///   Shader TD_CloudMesh (dome ~200 km radius, 21–35 km high, in world coordinates).</item>
/// <item>Selection: clouds_cloud3dname of the weather type (0xd03130 compares the name; change = crossfade between two locations).</item>
/// <item>Rotation: 0xd028a0 – Angle += clouds_cloud3dSpeed / 1000 · dt around the Y-axis, multiplied by the world origin.</item>
/// <item>Colors: Instance_Color = clouds_cloud3dcolor, ColorDown = clouds_cloud3dcolor2 (0xd03390/0xd05f80), AlphaBlend =
///   clouds_cloud3dAlphaBlend, Haze = Skydome_haze, DirLight_Emissive = sunflare_core_emissive (0xc98817) – via the frame buffer.</item>
/// </list>
/// Open question: which part uses `clouds_cloud3dSpeed.y` (0xd02931: argument > 1) – here, everything rotates using `.x`.
/// </summary>
public sealed class Clouds : IDisposable
{
    sealed class CloudMat { public IGpuMaterial Mat = null!; public MaterialDesc Desc = null!; public float Weight = -1f; }

    readonly IRenderer _r;
    readonly BuildResources _res = new();
    readonly List<RenderObject> _objects = new();
    readonly Dictionary<RenderObject, List<CloudMat>> _mats = new();
    readonly List<CloudMat> _allMats = new();
    BankModel? _model;
    float _angle;
    double _nextMatUpdate;

    Clouds(IRenderer r) => _r = r;

    /// Load <summary>fx/clouds.bnk. Return null if the island doesn't have any.</summary>
    public static Clouds? Load(string fxDir, IRenderer r, GlobalTextureCache textures)
    {
        string path = Path.Combine(fxDir, "clouds.bnk");
        if (!GameFiles.Exists(path)) { Log.Warn("Wolken: clouds.bnk fehlt: " + path); return null; }
        var c = new Clouds(r);
        try
        {
            c._model = BankModel.Load(path, BankKind.Generic, loadObjects: true, loadPlacements: false, loadGrass: false);
            var opt = new ObjectBuildOptions
            {
                LodDistanceScale = 1e9f,
                SkipEffects = false,
                MaterialOverride = (def, getTex) =>
                {
                    IGpuTexture? tex = null;
                    if (def != null && def.TryGetLayer(MaterialDef.SemColor, out var l) && l.TextureKey != 0)
                        tex = getTex(l.TextureKey, TextureDef.WrapFromEden(l.AddressU), TextureDef.WrapFromEden(l.AddressV));
                    if (tex == null && def != null)
                        foreach (var ly in def.Layers)
                            if (ly.TextureKey != 0) { tex = getTex(ly.TextureKey, TextureDef.WrapFromEden(ly.AddressU), TextureDef.WrapFromEden(ly.AddressV)); if (tex != null) break; }
                    var d = new MaterialDesc { Kind = MaterialKind.Cloud, Name = "TD_CloudMesh_" + (def?.Name ?? "?"), Main = tex, CloudWeight = 0f };
                    var m = r.CreateMaterial(d);
                    c._allMats.Add(new CloudMat { Mat = m, Desc = d });
                    return m;
                },
            };
            c._objects.AddRange(ObjectBuilder.BuildObjects(c._model, Vector3.Zero, textures, c._res, opt, r));
            c.AssignCloudLetters();
            foreach (var o in c._objects)
            {
                var list = new List<CloudMat>();
                foreach (var lod in o.Lods)
                    foreach (var p in lod.Parts)
                    {
                        var cm = c._allMats.Find(x => ReferenceEquals(x.Mat, p.Material));
                        if (cm != null && !list.Contains(cm)) list.Add(cm);
                    }
                c._mats[o] = list;
            }
            Log.Info($"Wolken: {c._objects.Count} Objekte ({string.Join(", ", c._objects.Select(o => o.Name))}), {c._allMats.Count} Materialien");
            return c;
        }
        catch (Exception ex) { Log.Warn("Wolken: " + ex.Message); c.Dispose(); return null; }
    }

    readonly Dictionary<RenderObject, char> _letter = new();

    /// <summary>
    /// Leaf assignment to CLOUD_A/B/R via the scene hierarchy (HIEA): Under CLOUDS, there is one branch for each cloud type
    /// named “CLOUD_x_0” (…→ Cx0_F) and a second branch whose leaf is also named “…_x_…”. The names exist only as Eden keys
    /// (uppercase letters, bytes summed modulo 8); characters 5–7 of the names remain unchanged if the name is ≤ 13 characters: “_x_”.
    /// A leaf belongs to cloud type x if it itself or an ancestor carries this key.
    /// </summary>
    void AssignCloudLetters()
    {
        var scene = _model?.Objects?.Scene;
        if (scene == null) return;
        var keyOf = new Dictionary<int, ulong>();
        foreach (var kv in scene.HieHash) keyOf[kv.Value] = kv.Key;
        var byOffset = new Dictionary<int, HieRecord>();
        foreach (var h in scene.Hierarchy) byOffset[h.Offset] = h;
        var byName = new Dictionary<string, char>();
        foreach (var h in scene.Hierarchy)
        {
            var ins = scene.GetIns(h.InsOffset);
            if (ins.Lods.Length == 0) continue;
            char letter = '\0';
            var cur = h; int guard = 0;
            while (true)
            {
                if (keyOf.TryGetValue(cur.Offset, out var k) && LetterOf(k) is var l && l != '\0') { letter = l; break; }
                if (cur.ParentOffset == 0 || !byOffset.TryGetValue(cur.ParentOffset, out var parent) || guard++ > 64) break;
                cur = parent;
            }
            string name = ins.Key != 0 ? Bin.KeyToString(ins.Key) : (ins.Lods.Length > 0 ? Bin.KeyToString(ins.Lods[0].ObjKey) : "");
            if (letter != '\0') byName[name] = letter;
        }
        foreach (var o in _objects)
            if (byName.TryGetValue(o.Name, out var l)) _letter[o] = l;
        Log.Info("Wolken: Zuordnung " + string.Join(", ", _objects.Select(o => o.Name + "=" + (_letter.TryGetValue(o, out var l) ? l : '-'))));
    }

    static char LetterOf(ulong key)
    {
        byte b5 = (byte)(key >> 40), b6 = (byte)(key >> 48), b7 = (byte)(key >> 56);
        return b5 == (byte)'_' && b7 == (byte)'_' && b6 >= (byte)'A' && b6 <= (byte)'Z' ? (char)b6 : '\0';
    }

    /// <summary>clouds_cloud3dname “CLOUD_B” -> ‘B’.</summary>
    static char LetterOf(string cloud) => cloud.Length > 6 && cloud.StartsWith("CLOUD_", StringComparison.OrdinalIgnoreCase) ? char.ToUpperInvariant(cloud[6]) : '\0';

    bool IsPart(RenderObject o, string cloud) => _letter.TryGetValue(o, out var l) && l == LetterOf(cloud);

    /// <summary>Rotation and transition weights (material values only when changed, at most every 0.1 s).</summary>
    public void Update(float dt, double time)
    {
        _angle += RenderFx.CloudSpeed.X / 1000f * dt;
        if (time < _nextMatUpdate) return;
        _nextMatUpdate = time + 0.1;
        foreach (var cm in _allMats)
        {
            float w = 0f;
            foreach (var kv in _mats)
            {
                if (!kv.Value.Contains(cm)) continue;
                w = MathF.Max(w, WeightOf(kv.Key));
            }
            if (MathF.Abs(w - cm.Weight) < 0.01f && !(w == 0f && cm.Weight != 0f) && !(w == 1f && cm.Weight != 1f)) continue;
            cm.Weight = w;
            cm.Desc.CloudWeight = w;
            _r.UpdateMaterial(cm.Mat, cm.Desc);
        }
    }

    float WeightOf(RenderObject o)
    {
        string cur = RenderFx.CloudName, prev = RenderFx.CloudNamePrev;
        float b = System.Math.Clamp(RenderFx.CloudBlend, 0f, 1f);
        if (string.Equals(cur, prev, StringComparison.OrdinalIgnoreCase)) return IsPart(o, cur) ? 1f : 0f;
        return (IsPart(o, cur) ? b : 0f) + (IsPart(o, prev) ? 1f - b : 0f);
    }

    /// <summary>Draw (game coordinates -> render space via root).</summary>
    public void Draw(Matrix4x4 root)
    {
        var rot = Matrix4x4.CreateRotationY(_angle);
        foreach (var o in _objects)
        {
            if (WeightOf(o) <= 0f || o.Lods.Length == 0) continue;
            var m = o.Model * rot * root;
            foreach (var p in o.Lods[0].Parts) _r.DrawMesh(p.Mesh, p.Material, m);
        }
    }

    public void Dispose()
    {
        foreach (var cm in _allMats) cm.Mat.Dispose();
        _allMats.Clear();
        foreach (var d in _res.Owned) d.Dispose();
        _res.Owned.Clear();
        _model?.Release();
        _model = null;
    }
}
