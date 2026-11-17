using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

public enum BankKind { Island, Area, Sector, Exterior, Generic }

/// <summary>An object scene: .3dd (instances/hierarchy) + .3dg (geometry) + .2dm (materials).</summary>
public sealed class ObjectSet
{
    public string Name = "";
    public Scene3DD? Scene;
    public Geometry3DG? Geo;
    public Material2DM? Materials;
    public readonly Dictionary<ulong, MeshData?> Meshes = new();
    /// <summary>Placements relative to the bank's origin (null = once at identity). Exteriors: from entities.xmb.</summary>
    public List<Matrix4x4>? Placements;

    public static ObjectSet Load(BnkArchive bank, BnkEntry e3dd, BnkEntry e3dg, BnkEntry? e2dm, List<string>? log)
    {
        var set = new ObjectSet
        {
            Name = e3dd.Name,
            Scene = Scene3DD.Parse(bank.Read(e3dd)),
            Geo = Geometry3DG.Parse(bank.Read(e3dg)),
        };
        if (e2dm != null) set.Materials = Material2DM.Parse(bank.Read(e2dm));
        foreach (var insOff in set.Scene.InsOffsets)
        {
            var ins = set.Scene.GetIns(insOff);
            foreach (var lod in ins.Lods)
            {
                if (set.Meshes.ContainsKey(lod.ObjKey)) continue;
                try { set.Meshes[lod.ObjKey] = set.Geo.BuildMesh(lod.ObjKey); }
                catch (Exception ex)
                {
                    log?.Add($"Mesh {Bin.KeyToString(lod.ObjKey)}: {ex.Message}");
                    set.Meshes[lod.ObjKey] = null;
                }
            }
        }
        return set;
    }

    public void Release()
    {
        Meshes.Clear();
        Geo = null;
        Scene = null;
    }
}
/// <summary>
/// Fully parsed content of a map bank (Island / Area / Sector / Spot-Exterior). Ported from the
/// Unity project. <see cref="Load"/> is thread-safe and runs in the background; GPU objects are then
/// created by the builders on the main thread.
/// </summary>
public sealed class BankModel
{
    public string Path = "";
    public BankKind Kind;
    public BnkArchive Bank = null!;
    // Terrain (<name>.3dd/.3dg/.2dm, Colormap <name>-c.2db, Splat/Shadow <name>-s.2db)
    public Scene3DD? TerrainScene;
    public Material2DM? TerrainMaterials;
    public HeightGrid? Heights;
    public ulong ColorMapKey, SplatMapKey;
    public MaterialDef? TerrainMaterial;
    // Objects (<name>-o.3dd/.3dg/.2dm) or, for exteriors, all scenes in the bank
    public ObjectSet? Objects;
    public readonly List<ObjectSet> ExtraObjects = new();
    public PmiFile? Vegetation, Placements, Actors, Rails, Fbank;   // -V, -O, -A, -S (guardrails), -F
    public GrassMap? Grass;   // Grass Type Map (.pgr)
    public List<SoundZone>? SoundZones;   // Sound zones (.psa, ESDZ/ZONE) for ambient noise
    public readonly LocalTextureSet Textures = new();
    public readonly List<string> Log = new();

    private static string Stem(BnkEntry e) => e.Name.Length > e.Extension.Length ? e.Name[..^e.Extension.Length] : e.Name;

    public static BankModel Load(string path, BankKind kind, bool loadObjects = true, bool loadPlacements = true, bool loadGrass = true)
    {
        var m = new BankModel { Path = path, Kind = kind, Bank = BnkArchive.Open(path) };
        var bank = m.Bank;

        BnkEntry? tdd = null, tdg = null, tdm = null, odd = null, odg = null, odm = null;
        foreach (var e in bank.Entries)
        {
            string stem = Stem(e);
            bool isObj = stem.EndsWith("-o", StringComparison.OrdinalIgnoreCase);
            switch (e.Extension)
            {
                case ".3dd": if (isObj) odd ??= e; else tdd ??= e; break;
                case ".3dg": if (isObj) odg ??= e; else tdg ??= e; break;
                case ".2dm": if (isObj) odm ??= e; else tdm ??= e; break;
                case ".2db":
                    {
                        var def = TextureDef.Parse(bank.Read(e), e.Name);
                        if (def == null) break;
                        m.Textures.Add(def);
                        if (stem.EndsWith("-c", StringComparison.OrdinalIgnoreCase) && m.ColorMapKey == 0) m.ColorMapKey = def.Key;
                        else if (stem.EndsWith("-s", StringComparison.OrdinalIgnoreCase) && m.SplatMapKey == 0) m.SplatMapKey = def.Key;
                        break;
                    }
                case ".pmi":
                    if (!loadPlacements) break;
                    if (stem.EndsWith("-V", StringComparison.OrdinalIgnoreCase)) m.Vegetation = PmiFile.Parse(bank.Read(e));
                    else if (stem.EndsWith("-O", StringComparison.OrdinalIgnoreCase)) m.Placements = PmiFile.Parse(bank.Read(e));
                    else if (stem.EndsWith("-A", StringComparison.OrdinalIgnoreCase)) m.Actors = PmiFile.Parse(bank.Read(e));
                    else if (stem.EndsWith("-S", StringComparison.OrdinalIgnoreCase)) m.Rails = PmiFile.Parse(bank.Read(e));
                    else if (stem.EndsWith("-F", StringComparison.OrdinalIgnoreCase)) m.Fbank = PmiFile.Parse(bank.Read(e));
                    break;
                case ".pgr":
                    if (loadGrass) m.Grass = GrassMap.Parse(bank.Read(e));
                    break;
                case ".psa":
                    m.SoundZones = SoundZonesFile.Parse(bank.Read(e));
                    break;
            }
        }

        if (kind == BankKind.Exterior)
        {
            LoadAllScenes(m, loadObjects);
            return m;
        }

        // Terrain
        if (tdd != null && tdg != null)
        {
            m.TerrainScene = Scene3DD.Parse(bank.Read(tdd));
            if (m.TerrainScene.Heightmap.Present)
            {
                var geo = Geometry3DG.Parse(bank.Read(tdg));
                float fallback = kind == BankKind.Sector ? WorldLayout.SectorSpacing : kind == BankKind.Area ? WorldLayout.AreaSpacing : WorldLayout.IslandSpacing;
                m.Heights = HeightGrid.FromTerrainBank(m.TerrainScene, geo, fallback);
                if (tdm != null)
                {
                    m.TerrainMaterials = Material2DM.Parse(bank.Read(tdm));
                    foreach (var md in m.TerrainMaterials.Materials.Values) { m.TerrainMaterial = md; break; }
                }
            }
            else if (odd == null)
            {
                odd = tdd; odg = tdg; odm = tdm;
                m.TerrainScene = null;
            }
        }

        if (loadObjects && odd != null && odg != null)
            m.Objects = ObjectSet.Load(bank, odd, odg, odm, m.Log);
        return m;
    }
    /// <summary>
    /// Spot exteriors (exterior/*.bnk) contain multiple scenes with hash names. They are placed as in the game via
    /// entities.xmb: “Scenery” (main building, at the origin of the spot) and ‘Entity__Actor’/“…ActorShared”
    /// (gates, airplane, plants … with pos/rot/scale). The resource hashes are mapped to
    /// bank entries via hashcodes.xmb. Without entities.xmb: all “_sc_” scenes (fallback).
    /// </summary>
    private static void LoadAllScenes(BankModel m, bool loadObjects)
    {
        if (!loadObjects) return;
        var bank = m.Bank;
        try
        {
            if (LoadByEntities(m)) return;
        }
        catch (Exception ex) { m.Log.Add("entities.xmb: " + ex.Message); }

        var byStem = new Dictionary<string, BnkEntry?[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in bank.Entries)
        {
            int slot = Slot(e);
            if (slot < 0) continue;
            var st = Stem(e);
            if (!byStem.TryGetValue(st, out var arr)) byStem[st] = arr = new BnkEntry?[3];
            arr[slot] = e;
        }
        bool anySc = false;
        foreach (var kv in byStem) if (kv.Value[0] != null && kv.Value[1] != null && kv.Key.Contains("_sc_", StringComparison.OrdinalIgnoreCase)) anySc = true;
        foreach (var kv in byStem)
        {
            var a = kv.Value;
            if (a[0] == null || a[1] == null) continue;
            if (anySc && !kv.Key.Contains("_sc_", StringComparison.OrdinalIgnoreCase)) continue;
            AddSet(m, a[0]!, a[1]!, a[2], null);
        }
    }

    private static int Slot(BnkEntry e) => e.Extension.ToLowerInvariant() switch { ".3dd" => 0, ".3dg" => 1, ".2dm" => 2, _ => -1 };

    private static void AddSet(BankModel m, BnkEntry dd, BnkEntry dg, BnkEntry? dm, List<Matrix4x4>? placements)
    {
        try
        {
            var set = ObjectSet.Load(m.Bank, dd, dg, dm, m.Log);
            set.Placements = placements;
            if (m.Objects == null) m.Objects = set; else m.ExtraObjects.Add(set);
        }
        catch (Exception ex) { m.Log.Add(dd.Name + ": " + ex.Message); }
    }

    /// <summary>Load the interiors of the locations (car dealerships, houses) (“ArrayOf_Entity__Actor__Interior”).</summary>
    public static bool IncludeInteriors = true;

    private static bool LoadByEntities(BankModel m)
    {
        var bank = m.Bank;
        var entEntry = bank.Find("entities.xmb");
        var hcEntry = bank.Find("hashcodes.xmb");
        if (entEntry == null || hcEntry == null) return false;
        var ent = XmbFile.Parse(bank.Read(entEntry));
        var hc = XmbFile.Parse(bank.Read(hcEntry));

        // rsc-Hash -> Bankeintrag
        var byRsc = new Dictionary<ulong, BnkEntry?>();
        var groups = hc.Get("___CmdSaveFileInfoArray");
        if (groups == null) return false;
        foreach (var g in groups.Items)
        {
            var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
            if (idx == null || refs == null) continue;
            int n = System.Math.Min(idx.Items.Count, refs.Items.Count);
            for (int i = 0; i < n; i++)
            {
                int fi = idx.Items[i].AsInt(-1);
                if (fi >= 0 && fi < bank.Entries.Count) byRsc[refs.Items[i].AsULong()] = BankEntryByIndex(bank, fi);
            }
        }

        // Szene (3dd) -> Liste der Platzierungen
        var sets = new Dictionary<BnkEntry, (BnkEntry dg, BnkEntry? dm, List<Matrix4x4> list)>();
        void Add(XmbNode? rscList, Matrix4x4 placement)
        {
            if (rscList == null) return;
            BnkEntry? dd = null, dg = null, dm = null;
            foreach (var r in rscList.Items)
            {
                int sub = r.I("rscSubType", -1);
                if (!byRsc.TryGetValue(r.U("rsc"), out var be) || be == null) continue;
                if (sub == 0) dd = be; else if (sub == 1) dg = be; else if (sub == 2) dm = be;
            }
            if (dd != null && dg == null)
            {
                // nur die Szene angegeben (z.B. Casino Sc_Cashier/Sc_Bar): 3dg mit gleichem Stamm
                string st0 = Stem(dd);
                foreach (var e in bank.Entries) if (Slot(e) == 1 && string.Equals(Stem(e), st0, StringComparison.OrdinalIgnoreCase)) { dg = e; break; }
            }
            if (dd == null || dg == null) return;
            if (dm == null)
            {
                string st = Stem(dd);
                foreach (var e in bank.Entries) if (Slot(e) == 2 && string.Equals(Stem(e), st, StringComparison.OrdinalIgnoreCase)) { dm = e; break; }
            }
            if (!sets.TryGetValue(dd, out var v)) { v = (dg, dm, new List<Matrix4x4>()); sets[dd] = v; }
            v.list.Add(placement);
        }

        foreach (var section in ent.Children)
        {
            if (!section.Name.StartsWith("ExportType_", StringComparison.Ordinal)) continue;
            foreach (var arr in section.Children)
            {
                if (!arr.IsArray) continue;
                bool scenery = arr.Name == "ArrayOf_Scenery";
                // Alle Actor-Typen mit Mesh (inkl. abgeleiteter Casino-Typen); Interieur-Platzhalter nur falls gewünscht
                bool actor = arr.Name.StartsWith("ArrayOf_Entity__Actor", StringComparison.Ordinal)
                             && (arr.Name != "ArrayOf_Entity__Actor__Interior" || IncludeInteriors)
                             && arr.Name.IndexOf("Avatar", StringComparison.OrdinalIgnoreCase) < 0;
                if (!scenery && !actor) continue;
                foreach (var it in arr.Items)
                {
                    if (scenery) { Add(it.Get("Model"), Matrix4x4.Identity); continue; }
                    var ops = it.Get("orient_pos_scale");
                    Add(it.Get("mesh"), ops != null ? EntityMatrix(ops) : Matrix4x4.Identity);
                }
            }
        }
        if (sets.Count == 0) return false;
        foreach (var kv in sets) AddSet(m, kv.Key, kv.Value.dg, kv.Value.dm, kv.Value.list);
        return true;
    }

    private static BnkEntry? BankEntryByIndex(BnkArchive bank, int fileIndex)
    {
        foreach (var e in bank.Entries) if (e.Index == fileIndex) return e;
        return fileIndex < bank.Entries.Count ? bank.Entries[fileIndex] : null;
    }

    /// <summary>
    /// orient_pos_scale from entities.xmb. The quaternions are stored like D3D matrices (row vectors), i.e., the
    /// rotation corresponds to the inverse quaternion (as verified in the Unity project using the spot cameras).
    /// Result in row-vector convention (scaling * rotation * translation = transposed Unity TRS).
    /// </summary>
    public static Matrix4x4 EntityMatrix(XmbNode ops)
    {
        var p = ops.Get("pos"); var r = ops.Get("rot"); var sc = ops.Get("scale");
        var pos = p != null ? new Vector3(p.F("X"), p.F("Y"), p.F("Z")) : Vector3.Zero;
        var q = r != null ? new Quaternion(-r.F("X"), -r.F("Y"), -r.F("Z"), r.F("W", 1f)) : Quaternion.Identity;
        float qm = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        q = qm < 1e-6f ? Quaternion.Identity : new Quaternion(q.X / qm, q.Y / qm, q.Z / qm, q.W / qm);
        var s = sc != null ? new Vector3(sc.F("X", 1f), sc.F("Y", 1f), sc.F("Z", 1f)) : Vector3.One;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(pos);
    }

    public IEnumerable<ObjectSet> AllObjectSets()
    {
        if (Objects != null) yield return Objects;
        foreach (var s in ExtraObjects) yield return s;
    }

    /// <summary>Texture from this bank, otherwise from the global cache (commonworld). Main thread.</summary>
    public IGpuTexture? GetTexture(ulong key, TextureWrap u, TextureWrap v, GlobalTextureCache? global, IRenderer r)
    {
        if (key == 0) return null;
        if (Textures.TryGet(key, u, v, r, out var t)) return t;
        return global?.Get(key, u, v);
    }

    public void Release()
    {
        Textures.Destroy();
        foreach (var s in AllObjectSets()) s.Release();
        Objects = null;
        ExtraObjects.Clear();
    }
}
