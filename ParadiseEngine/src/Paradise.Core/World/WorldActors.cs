using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// World Actors der Insel (islands/insel/scenarics/packs/worldactors.bnk): Ampeln (SF_Xin_*), Schilder (RS_*),
/// Bänke, Mülleimer, Absperrungen. Portiert aus dem Unity-Projekt (WorldActorLib).
/// Die "A"-Platzierungen der Sektoren (-A.pmi) nennen als Schlüssel den __Hc eines Entity__Actor aus entities.xmb;
/// dessen "mesh" verweist über hashcodes.xmb auf .3dd/.3dg/.2dm der Bank (Rückfall: Dateistamm = __Name ohne "_act").
/// </summary>
public sealed class WorldActorLib
{
    public string Path = "";
    public BankModel? Model;
    public readonly Dictionary<ulong, ObjectSet> ByHc = new();
    public readonly Dictionary<ulong, string> NameOf = new();
    /// <summary>Ampeln (ArrayOf_Entity__Actor__TrafficLights).</summary>
    public readonly HashSet<ulong> TrafficLights = new();
    public readonly List<string> Log = new();

    /// <summary>Thread-sicher (Hintergrund): Bank öffnen, alle Actor-Modelle vorbereiten.</summary>
    public static WorldActorLib Load(string path, GlobalTextureCache? textures)
    {
        var lib = new WorldActorLib { Path = path };
        lib.Model = BankModel.Load(path, BankKind.Generic, false, false, false);
        var bank = lib.Model.Bank;
        textures?.IndexBank(bank);
        try { TextureAnimations.PrepareUva(bank); } catch (Exception) { }   // z.B. Pfeil-Schilder (fleche_*.uva)

        var entEntry = bank.Find("entities.xmb");
        if (entEntry == null) { lib.Log.Add("entities.xmb fehlt"); return lib; }
        var ent = XmbFile.Parse(Unwrap(bank.Read(entEntry)));

        // rsc-Hash -> Bankeintrag (hashcodes.xmb: FileIndices / FileReferences)
        var byRsc = new Dictionary<ulong, BnkEntry>();
        var hcEntry = bank.Find("hashcodes.xmb");
        if (hcEntry != null)
        {
            var hc = XmbFile.Parse(Unwrap(bank.Read(hcEntry)));
            var byIndex = new Dictionary<int, BnkEntry>();
            foreach (var e in bank.Entries) byIndex[e.Index] = e;
            var groups = hc?.Get("___CmdSaveFileInfoArray");
            if (groups != null)
                foreach (var g in groups.Items)
                {
                    var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                    if (idx == null || refs == null) continue;
                    int n = System.Math.Min(idx.Items.Count, refs.Items.Count);
                    for (int i = 0; i < n; i++)
                        if (byIndex.TryGetValue(idx.Items[i].AsInt(-1), out var be)) byRsc[refs.Items[i].AsULong()] = be;
                }
        }

        var cache = new Dictionary<string, ObjectSet?>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in ent.Children)
        {
            if (!section.Name.StartsWith("ExportType_", StringComparison.Ordinal)) continue;
            foreach (var arr in section.Children)
            {
                if (!arr.IsArray) continue;
                foreach (var it in arr.Items)
                {
                    var mesh = it.Get("mesh");
                    if (mesh == null) continue;
                    ulong hcKey = it.U("__Hc");
                    string name = it.S("__Name", "") ?? "";
                    if (hcKey == 0) continue;
                    lib.NameOf[hcKey] = name;
                    if (arr.Name.IndexOf("TrafficLights", StringComparison.OrdinalIgnoreCase) >= 0) lib.TrafficLights.Add(hcKey);
                    BnkEntry? dd = null, dg = null, dm = null;
                    foreach (var r in mesh.Items)
                    {
                        if (!byRsc.TryGetValue(r.U("rsc"), out var be)) continue;
                        int sub = r.I("rscSubType", -1);
                        if (sub == 0) dd = be; else if (sub == 1) dg = be; else if (sub == 2) dm = be;
                    }
                    if (dd == null || dg == null)
                    {
                        string stem = name.EndsWith("_act", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
                        foreach (var e in bank.Entries)
                        {
                            if (!e.Name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
                            if (e.Extension == ".3dd" && dd == null) dd = e;
                            else if (e.Extension == ".3dg" && dg == null) dg = e;
                            else if (e.Extension == ".2dm" && dm == null) dm = e;
                        }
                    }
                    if (dd == null || dg == null) { lib.Log.Add(name + ": Modell fehlt"); continue; }
                    if (!cache.TryGetValue(dd.Name, out var set))
                    {
                        try { set = ObjectSet.Load(bank, dd, dg, dm, lib.Log); set.Placements = new List<Matrix4x4>(); }
                        catch (Exception ex) { lib.Log.Add(name + ": " + ex.Message); set = null; }
                        cache[dd.Name] = set;
                    }
                    if (set != null) lib.ByHc[hcKey] = set;
                }
            }
        }
        return lib;
    }

    static byte[] Unwrap(byte[] b)
    {
        if (b == null || XmbFile.IsXmb(b)) return b!;
        for (int i = 1; i + 4 <= b.Length && i < 64; i++)
            if (b[i] == 'X' && b[i + 1] == 'M' && b[i + 2] == 'B' && b[i + 3] == 'F')
            {
                var r = new byte[b.Length - i];
                Array.Copy(b, i, r, 0, r.Length);
                return r;
            }
        return b;
    }

    /// <summary>
    /// Hauptthread: die Actors eines Sektors bauen (Platzierungen relativ zum Sektor-Ursprung, parent = Sektor-Ursprung).
    /// onInstance bekommt je Instanz die Weltmatrix (Spielkoordinaten), z.B. für den Schildertext.
    /// </summary>
    public int Build(PmiFile? pmi, Matrix4x4 parent, GlobalTextureCache textures, BuildResources res, ObjectBuildOptions opt, IRenderer r,
                     List<RenderObject> output, Action<PmiInstance, Matrix4x4>? onInstance = null, Func<bool>? alive = null,
                     Action<string, List<RenderObject>, Matrix4x4>? onActor = null)
    {
        if (pmi == null || Model == null) return 0;
        int count = 0;
        IGpuTexture? GetTex(ulong k, TextureWrap u, TextureWrap v) => textures.Get(k, u, v);
        foreach (var g in pmi.Groups)
        {
            if (!ByHc.TryGetValue(g.Key, out var set) || set == null) continue;
            var list = new List<Matrix4x4>(g.Instances.Count);
            foreach (var inst in g.Instances)
            {
                // Unity: m = TRS(inst) ; m * Pre  ->  Zeilenvektoren: Pre * m
                var m = inst.Matrix;
                list.Add(inst.Pre.HasValue ? inst.Pre.Value * m : m);
            }
            if (list.Count == 0) continue;
            if (onActor != null)
            {
                // je Instanz bauen (Unity: eigenes GameObject je Instanz -> WorldActorBody.Physicalize(inst, grp.name))
                string name = NameOf.TryGetValue(g.Key, out var nm) ? nm : "";
                bool tl = TrafficLights.Contains(g.Key);
                try
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        set.Placements = new List<Matrix4x4> { list[i] };
                        var objs = ObjectBuilder.BuildObjects(set, GetTex, parent, res, opt, r);
                        output.AddRange(objs);
                        if (tl) Paradise.Core.World.TrafficLights.Register(objs, list[i] * parent, alive ?? (() => true));
                        onActor(name, objs, list[i] * parent);
                        // Schildertext gleich danach (gehört zur eben angemeldeten Instanz, fällt mit um)
                        onInstance?.Invoke(g.Instances[i], list[i] * parent);
                    }
                }
                finally { set.Placements = new List<Matrix4x4>(); }
            }
            else if (TrafficLights.Contains(g.Key))
            {
                // Ampeln: je Instanz bauen und anmelden (TDU2TrafficLights.Register mit der Instanz-Wurzel)
                try
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        set.Placements = new List<Matrix4x4> { list[i] };
                        var objs = ObjectBuilder.BuildObjects(set, GetTex, parent, res, opt, r);
                        output.AddRange(objs);
                        Paradise.Core.World.TrafficLights.Register(objs, list[i] * parent, alive ?? (() => true));
                    }
                }
                finally { set.Placements = new List<Matrix4x4>(); }
            }
            else
            {
                set.Placements = list;
                try { output.AddRange(ObjectBuilder.BuildObjects(set, GetTex, parent, res, opt, r)); }
                finally { set.Placements = new List<Matrix4x4>(); }
            }
            if (onInstance != null && onActor == null)
                for (int i = 0; i < g.Instances.Count; i++) onInstance(g.Instances[i], list[i] * parent);
            count += list.Count;
        }
        return count;
    }
}
