using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Runtime.CompilerServices;

namespace Paradise.Core.World;

/// <summary>
/// Additional information about generated textures – replaces TextureDef.PackedTextures / MeanLuminance
/// </summary>
public static class TextureInfo
{
    private sealed class Info { public bool Packed; public float MeanLum; }
    private static readonly ConditionalWeakTable<IGpuTexture, Info> _info = new();

    /// <summary>Create a texture from a TextureDef (like TextureDef.CreateTexture) and store the information. Main thread.</summary>
    public static IGpuTexture? Create(IRenderer r, TextureDef def, TextureWrap u, TextureWrap v, bool linear = false)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var up = def.ToUpload(u, v);
        if (up == null) return null;
        up.Linear = linear;
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        IGpuTexture tex;
        try
        {
            tex = r.CreateTexture(up);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Textur {def.Name}: {ex.Message}");
            return null;
        }
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        _info.AddOrUpdate(tex, new Info { Packed = def.LooksPacked(), MeanLum = def.MeanLum() });
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Diagnostics.PerfLog.Add("Textur: Umwandeln (je Textur)", (t1 - t0) * k);
        Diagnostics.PerfLog.Add("Textur: GPU anlegen (je Textur)", (t2 - t1) * k);
        Diagnostics.PerfLog.Add("Textur: Helligkeit/Packung (je Textur)", (t3 - t2) * k);
        return tex;
    }

    /// <summary>“Packed” detail texture (R only = grayscale detail).</summary>
    public static bool IsPacked(IGpuTexture? t) => t != null && _info.TryGetValue(t, out var i) && i.Packed;

    /// <summary>Mittlere Helligkeit (0..1) einer Textur.</summary>
    public static bool TryGetMeanLuminance(IGpuTexture? t, out float lum)
    {
        lum = 0.5f;
        if (t == null || !_info.TryGetValue(t, out var i)) return false;
        lum = i.MeanLum;
        return true;
    }
}

/// <summary>
/// Textures for a single loaded bank (sector/area/island). Ported from the Unity project.
/// The TextureDefs are read in the background; the GPU textures are generated only when needed on the
/// main thread and released when the bank is unloaded.
/// </summary>
public sealed class LocalTextureSet
{
    public readonly Dictionary<ulong, TextureDef> Defs = new();
    private readonly Dictionary<ulong, IGpuTexture?> _created = new();

    public void Add(TextureDef? def)
    {
        if (def != null && !Defs.ContainsKey(def.Key)) Defs[def.Key] = def;
    }

    public bool TryGet(ulong key, TextureWrap u, TextureWrap v, IRenderer r, out IGpuTexture? tex)
    {
        if (_created.TryGetValue(key, out tex)) return tex != null;
        if (!Defs.TryGetValue(key, out var def)) { tex = null; return false; }
        tex = TextureInfo.Create(r, def, u, v);
        _created[key] = tex;
        def.Data = null; // Rohdaten freigeben
        return tex != null;
    }

    public void Destroy()
    {
        foreach (var t in _created.Values) t?.Dispose();
        _created.Clear();
        Defs.Clear();
    }
}

/// <summary>
/// Global texture index (commonworld.bnk, tallgrass.bnk, sector/area banks, etc.): Key -> bank entry,
/// loaded lazily and cached. Ported from the Unity project.
/// </summary>
public sealed class GlobalTextureCache
{
    private readonly IRenderer _renderer;
    private readonly Dictionary<ulong, (BnkArchive bank, BnkEntry entry)> _index = new();
    private readonly Dictionary<ulong, IGpuTexture?> _loaded = new();
    private readonly Dictionary<string, ulong> _byName = new(StringComparer.OrdinalIgnoreCase);

    public GlobalTextureCache(IRenderer renderer) => _renderer = renderer;

    public int Count { get { lock (_index) return _index.Count; } }
    /// <summary>Generated GPU textures (diagnostics).</summary>
    public int LoadedCount => _loaded.Count;

    /// <summary>Reads the keys of all .2db files in a bank (only 40 bytes per texture). Can be called thread-safely.</summary>
    public void IndexBank(BnkArchive bank)
    {
        var add = new List<(ulong, BnkEntry, string)>();
        foreach (var e in bank.All(".2db"))
        {
            var head = bank.ReadPartial(e, 0, 0x28);
            ulong key = TextureDef.PeekKey(head);
            if (key != 0) add.Add((key, e, e.Name));
        }
        lock (_index)
        {
            foreach (var (key, e, name) in add)
            {
                if (!_index.ContainsKey(key)) _index[key] = (bank, e);
                _byName[name] = key;
            }
        }
    }

    /// <summary>Index a bank from a path (ignores errors/missing files).</summary>
    public void IndexBankFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !GameFiles.Exists(path))
            return;
        try
        {
            IndexBank(BnkArchive.Open(path));
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Texturindex {path}: {ex.Message}");
        }
    }

    public bool Contains(ulong key) { lock (_index) return _index.ContainsKey(key); }

    public ulong KeyByName(string fileName) { lock (_index) return _byName.TryGetValue(fileName, out var k) ? k : 0; }

    public IGpuTexture? Get(ulong key, TextureWrap u = TextureWrap.Repeat, TextureWrap v = TextureWrap.Repeat)
    {
        if (key == 0) return null;
        if (_loaded.TryGetValue(key, out var t)) return t;
        (BnkArchive bank, BnkEntry entry) src;
        lock (_index) { if (!_index.TryGetValue(key, out src)) return null; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var def = TextureDef.Parse(src.bank.Read(src.entry), src.entry.Name);
        Diagnostics.PerfLog.Add("Textur: Bank lesen (je globale Textur)", System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        t = def != null ? TextureInfo.Create(_renderer, def, u, v) : null;
        _loaded[key] = t;
        return t;
    }

    public void Clear()
    {
        foreach (var t in _loaded.Values) t?.Dispose();
        _loaded.Clear();
    }
}