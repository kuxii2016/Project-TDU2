using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Render;

namespace Paradise.Core.Fx;

/// <summary>
/// Effekte einer FX-Bank (z. B. fx/vfx_car.bnk): entities.xmb Fx → SourceData → ParticlesList → Particles
/// (Prt = .prt, rscSubType 18; PrtMat = .2DM (2) + .2db (3)). Ressourcen-Hashes über hashcodes.xmb auf Bankeinträge.
/// Je Effekt die Partikelknoten mit Textur und Material (MaterialKind.Particle). Aus dem .2DM (MAT.+0x40, D3D-Werte): Byte 8/9 =
/// SrcBlend/DestBlend (5/6 Alpha, 5/2 additiv), Byte 1 = Beleuchtung (1 = td_particle-VS mit Ambient/DirLight, 2 = unbeleuchtet;
/// Zuordnung aus den Daten: FX_LIGHT/LightBeam/Flammen 2, Welt und Rauch 1).
/// </summary>
public sealed class FxLibrary : IDisposable
{
    /// <summary>Partikelknoten; Start/End = Ausstoßfenster aus der Zeitachse der .bfx (SPMC), float.MaxValue = endlos.</summary>
    public sealed class Node { public PrtFile Prt = null!; public IGpuMaterial Material = null!; public float Start, End = float.MaxValue; }
    /// <summary>Effekt; Duration aus der .bfx (SPMC, Wert vor dem Chunk), float.MaxValue = läuft bis zum Abbruch.</summary>
    public sealed class Effect { public string Name = ""; public float Duration = float.MaxValue; public readonly List<Node> Nodes = new(); }

    readonly Dictionary<string, Effect> _fx = new(StringComparer.OrdinalIgnoreCase);
    readonly List<IDisposable> _owned = new();

    public int Count => _fx.Count;
    public Effect? Get(string name) => _fx.TryGetValue(name, out var e) ? e : null;

    public static FxLibrary? Load(IRenderer r, string relativeBank)
    {
        try
        {
            var dir = Config.GameData.BnkDir;
            if (dir == null) return null;
            var path = Path.Combine(dir, relativeBank);
            if (!Files.GameFiles.Exists(path)) { Log.Warn("FX: " + path + " fehlt."); return null; }
            var bank = BnkArchive.Open(path);
            var lib = new FxLibrary();
            lib.Read(r, bank);
            Log.Info($"FX: {relativeBank}: {lib.Count} Effekte");
            return lib;
        }
        catch (Exception ex) { Log.Warn("FX " + relativeBank + ": " + ex.Message); return null; }
    }

    void Read(IRenderer r, BnkArchive bank)
    {
        var entEntry = bank.Find("entities.xmb");
        var hcEntry = bank.Find("hashcodes.xmb");
        if (entEntry == null || hcEntry == null) return;
        var ent = XmbFile.Parse(bank.Read(entEntry));
        var hc = XmbFile.Parse(bank.Read(hcEntry));

        // rsc-Hash -> Bankeintrag (wie BankModel.LoadByEntities)
        var byIndex = new Dictionary<int, BnkEntry>();
        foreach (var e in bank.Entries) byIndex[e.Index] = e;
        var byRsc = new Dictionary<ulong, BnkEntry>();
        var groups = hc.Get("___CmdSaveFileInfoArray");
        if (groups != null)
            foreach (var g in groups.Items)
            {
                var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                if (idx == null || refs == null) continue;
                int n = System.Math.Min(idx.Items.Count, refs.Items.Count);
                for (int i = 0; i < n; i++)
                    if (byIndex.TryGetValue(idx.Items[i].AsInt(-1), out var be)) byRsc[refs.Items[i].AsULong()] = be;
            }

        static IEnumerable<XmbNode> Arr(XmbNode root, string type, string arr)
        {
            var t = root.Get(type)?.Get(arr);
            return t != null ? t.Items : Enumerable.Empty<XmbNode>();
        }
        var sources = new Dictionary<ulong, XmbNode>();
        foreach (var s in Arr(ent, "ExportType_FxSourceData", "ArrayOf_Fx__SourceData")) sources[s.U("__Hc")] = s;
        var particles = new Dictionary<ulong, XmbNode>();
        foreach (var p in Arr(ent, "ExportType_FxParticles", "ArrayOf_Fx__Particles")) particles[p.U("__Hc")] = p;

        var prtCache = new Dictionary<ulong, PrtFile?>();
        var matCache = new Dictionary<ulong, IGpuMaterial?>();
        var texCache = new Dictionary<ulong, IGpuTexture?>();
        foreach (var fx in Arr(ent, "ExportType_Fx", "ArrayOf_Fx__Root"))
        {
            string name = fx.S("__Name") ?? "";
            if (name.Length == 0 || !sources.TryGetValue(fx.U("SourceData"), out var src)) continue;
            var eff = new Effect { Name = name };
            // Zeitachse der .bfx (Fx, rscSubType 31): Dauer + je Knoten (Offset, Start, Ende) in Reihenfolge der ParticlesList
            List<(float s, float e)>? timeline = null;
            foreach (var r0 in src.Get("Fx")?.Items ?? new List<XmbNode>())
                if (r0.I("rscSubType") == 31 && byRsc.TryGetValue(r0.U("rsc"), out var be))
                    timeline = ReadTimeline(bank.Read(be), out eff.Duration);
            int nodeIndex = 0;
            var list = src.Get("ParticlesList");
            if (list != null)
                foreach (var it in list.Items)
                {
                    if (!particles.TryGetValue(it.U("ParticlesNode"), out var pn)) continue;
                    ulong prtKey = 0, texKey = 0, matKey = 0;
                    foreach (var r0 in pn.Get("Prt")?.Items ?? new List<XmbNode>()) if (r0.I("rscSubType") == 18) prtKey = r0.U("rsc");
                    foreach (var r0 in pn.Get("PrtMat")?.Items ?? new List<XmbNode>())
                    {
                        if (r0.I("rscSubType") == 3) texKey = r0.U("rsc");
                        else if (r0.I("rscSubType") == 2) matKey = r0.U("rsc");
                    }
                    if (!prtCache.TryGetValue(prtKey, out var prt))
                    {
                        prt = byRsc.TryGetValue(prtKey, out var pe) ? PrtFile.Parse(bank.Read(pe), pe.Name) : null;
                        prtCache[prtKey] = prt;
                    }
                    // Mischart und Beleuchtung aus dem .2DM
                    bool additive = false, lit = true;
                    if (byRsc.TryGetValue(matKey, out var me))
                    {
                        var lib = Material2DM.Parse(bank.Read(me));
                        foreach (var md in lib.Materials.Values)
                        {
                            additive = md.Flags[9] == 2;   // D3DBLEND_ONE als Ziel
                            lit = md.Flags[1] != 2;
                            break;
                        }
                    }
                    ulong cacheKey = texKey ^ (additive ? 0x8000000000000000UL : 0UL) ^ (lit ? 0x4000000000000000UL : 0UL);
                    if (!matCache.TryGetValue(cacheKey, out var mat))
                    {
                        mat = null;
                        if (!texCache.TryGetValue(texKey, out var tex) && byRsc.TryGetValue(texKey, out var te))
                        {
                            var def = TextureDef.Parse(bank.Read(te), te.Name);
                            tex = def != null ? World.TextureInfo.Create(r, def, TextureWrap.Clamp, TextureWrap.Clamp) : null;
                            if (tex != null) _owned.Add(tex);
                            texCache[texKey] = tex;
                        }
                        if (tex != null)
                        {
                            mat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Particle, Name = "Fx " + texKey.ToString("X16"), Main = tex, Additive = additive, Lit = lit ? 1f : 0f });
                            _owned.Add(mat);
                        }
                        matCache[cacheKey] = mat;
                    }
                    var tl = timeline != null && nodeIndex < timeline.Count ? timeline[nodeIndex] : (0f, float.MaxValue);
                    nodeIndex++;
                    if (prt != null && mat != null) eff.Nodes.Add(new Node { Prt = prt, Material = mat, Start = tl.Item1, End = tl.Item2 });
                    if (additive) _additive++;
                    if (lit) _lit++;
                }
            if (eff.Nodes.Count > 0) _fx[name] = eff;
        }
        Log.Info($"FX: Partikelmaterialien beleuchtet {_lit}, additiv {_additive}");
    }

    int _lit, _additive;

    /// <summary>
    /// .bfx: Chunk "SPMC" mit Dauer (float, Tag − 0x0c) und Knotenzahl (Tag − 8), danach je Knoten (u32 Offset, float Start, float Ende).
    /// Klangknoten (erstes folgendes Tag " DNS" statt "CTRP") zählen nicht zu den Partikeln (VFX_Exhaust_PipeFlame: 4 Partikel, 1 Klang).
    /// </summary>
    static List<(float s, float e)>? ReadTimeline(byte[] b, out float duration)
    {
        duration = float.MaxValue;
        int t = -1;
        for (int i = 0x10; i + 4 <= b.Length; i++)
            if (b[i] == (byte)'S' && b[i + 1] == (byte)'P' && b[i + 2] == (byte)'M' && b[i + 3] == (byte)'C') { t = i; break; }
        if (t < 0x0c) return null;
        duration = Bin.F32(b, t - 0x0c);
        int n = (int)Bin.U32(b, t - 8);
        var res = new List<(float, float)>();
        for (int k = 0; k < n && t + 4 + k * 12 + 12 <= b.Length; k++)
        {
            int p = t + 4 + k * 12;
            int off = (int)Bin.U32(b, p);
            if (off < 0 || off >= b.Length) continue;
            bool sound = false;
            for (int i = off; i + 4 <= b.Length; i++)
            {
                if (b[i] == (byte)'C' && b[i + 1] == (byte)'T' && b[i + 2] == (byte)'R' && b[i + 3] == (byte)'P') break;
                if (b[i] == (byte)' ' && b[i + 1] == (byte)'D' && b[i + 2] == (byte)'N' && b[i + 3] == (byte)'S') { sound = true; break; }
            }
            if (!sound) res.Add((Bin.F32(b, p + 4), Bin.F32(b, p + 8)));
        }
        return res;
    }

    public void Dispose()
    {
        foreach (var o in _owned) o.Dispose();
        _owned.Clear();
        _fx.Clear();
    }
}

/// <summary>Laufende Instanz eines Effekts: je Partikelknoten ein Emitter.</summary>
public sealed class FxInstance
{
    readonly FxLibrary.Effect _fx;
    readonly ParticleEmitter[] _em;
    public string Name => _fx.Name;

    public FxInstance(FxLibrary.Effect fx)
    {
        _fx = fx;
        _em = new ParticleEmitter[fx.Nodes.Count];
        for (int i = 0; i < _em.Length; i++) _em[i] = new ParticleEmitter(fx.Nodes[i].Prt);
    }

    public bool Alive { get { foreach (var e in _em) if (e.Count > 0) return true; return false; } }
    /// <summary>Zeit seit dem Start (für die Ausstoßfenster der .bfx).</summary>
    public float Time { get; private set; }
    /// <summary>Endlicher Effekt (z. B. Flamme 0,9 s) abgelaufen und alle Partikel erloschen.</summary>
    public bool Finished => Time >= _fx.Duration && !Alive;

    public void Update(float dt, bool emit, in System.Numerics.Matrix4x4 emitter, in FxParams p, bool mirrorX)
    {
        for (int k = 0; k < _em.Length; k++)
        {
            var n = _fx.Nodes[k];
            bool on = emit && Time >= n.Start && Time <= n.End && Time < _fx.Duration;
            _em[k].Update(dt, on, emitter, p, mirrorX);
        }
        if (emit) Time += dt;
    }

    public void Shift(System.Numerics.Vector3 d) { foreach (var e in _em) e.Shift(d); }

    readonly List<ObjectVertex> _v = new();
    readonly List<uint> _i = new();

    public int Particles { get { int n = 0; foreach (var e in _em) n += e.Count; return n; } }

    public float Depth(System.Numerics.Vector3 camPos, System.Numerics.Vector3 camFwd)
    {
        float s = 0f; int n = 0;
        foreach (var e in _em) if (e.Count > 0) { s += e.Depth(camPos, camFwd); n++; }
        return n > 0 ? s / n : 0f;
    }

    public void Render(IRenderer r, System.Numerics.Vector3 right, System.Numerics.Vector3 up, System.Numerics.Vector3 camPos, System.Numerics.Vector3 camFwd, System.Numerics.Matrix4x4 model)
    {
        for (int k = 0; k < _em.Length; k++)
        {
            if (_em[k].Count == 0) continue;
            _v.Clear(); _i.Clear();
            _em[k].Build(_v, _i, right, up, camPos, camFwd);
            if (_i.Count > 0)
                r.DrawDynamic(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_v), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_i), _fx.Nodes[k].Material, model);
        }
    }
}
