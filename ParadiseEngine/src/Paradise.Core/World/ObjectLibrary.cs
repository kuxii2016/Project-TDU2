using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

public sealed class LibraryLod
{
    public RenderPart[] Parts = Array.Empty<RenderPart>();   // pro Submesh (Unity: Mesh + Material[])
    public float MaxDistance;                                 // Weltmeter (bereits skaliert)
    // Quelle für das Biegen der Leitplanken (Unity: Mesh-Daten inkl. UV4/UV5)
    public MeshData? Source;
    public ulong GeoKey;
    public ulong[] BankKeys = Array.Empty<ulong>();
    public int BankOffset;
}

public sealed class LibraryObject
{
    public ulong Key;
    public string Name = "";
    public LibraryLod[] Lods = Array.Empty<LibraryLod>();
    public float Radius;
    public Vector3 BoundsMin, BoundsMax;   // lokale Bounds von LOD0
}

/// <summary>
/// commonworld.bnk: gemeinsame Objekt-Library der Insel (Bäume, Büsche, Felsen, Props). Portiert aus dem Unity-Projekt.
/// Die .pmi-Dateien der Sektoren referenzieren INS-Keys dieser Library.
///   library.3dd: INSA (Key -> INS mit LOD-Liste + MBNK), OBJA, HIEA
///   library.3dg: Geometrien, library.2dm: Materialien, *.2db: Texturen
/// </summary>
public sealed class ObjectLibrary
{
    public BnkArchive Bank = null!;
    public Scene3DD? Scene;
    public Geometry3DG? Geo;
    public Material2DM? Materials;
    public readonly BuildResources Resources = new();
    private readonly Dictionary<ulong, LibraryObject?> _objects = new();
    /// <summary>Gebaute Bibliotheksobjekte (Diagnose).</summary>
    public int BuiltCount => _objects.Count;
    private readonly HashSet<ulong> _missing = new();

    public int MissingCount => _missing.Count;

    /// <summary>Thread-sicher (Hintergrund).</summary>
    public static ObjectLibrary Load(string path, GlobalTextureCache? textures)
    {
        var lib = new ObjectLibrary { Bank = BnkArchive.Open(path) };
        var e3dd = lib.Bank.All(".3dd").FirstOrDefault();
        var e3dg = lib.Bank.All(".3dg").FirstOrDefault();
        var e2dm = lib.Bank.All(".2dm").FirstOrDefault();
        if (e3dd != null) lib.Scene = Scene3DD.Parse(lib.Bank.Read(e3dd));
        if (e3dg != null) lib.Geo = Geometry3DG.Parse(lib.Bank.Read(e3dg));
        if (e2dm != null) lib.Materials = Material2DM.Parse(lib.Bank.Read(e2dm));
        textures?.IndexBank(lib.Bank);
        return lib;
    }

    public bool Contains(ulong insKey) => Scene != null && Scene.InsHash.ContainsKey(insKey);

    /// <summary>Main-Thread: Meshes/Materialien eines Library-Objekts (gecacht).</summary>
    public LibraryObject? Get(ulong insKey, GlobalTextureCache textures, ObjectBuildOptions opt, IRenderer r)
    {
        if (_objects.TryGetValue(insKey, out var obj)) return obj;
        if (Scene == null || Geo == null || !Scene.InsHash.TryGetValue(insKey, out int insOff))
        {
            _missing.Add(insKey);
            _objects[insKey] = null;
            return null;
        }
        var ins = Scene.GetIns(insOff);
        var bankKeys = Scene.ReadMaterialBank(ins.MaterialBankOffset);
        var lods = new List<LibraryLod>();
        float radius = 1f;
        Vector3 bmin = Vector3.Zero, bmax = Vector3.Zero;
        IGpuTexture? GetTex(ulong k, TextureWrap u, TextureWrap v) => textures.Get(k, u, v);
        foreach (var l in ins.Lods)
        {
            MeshData? md;
            try { md = Geo.BuildMesh(l.ObjKey); } catch (Exception) { md = null; }
            var rr = ObjectBuilder.GetRenderable(md, l.ObjKey, bankKeys, Materials, GetTex, Resources, opt, ins.MaterialBankOffset, r);
            if (rr == null) continue;
            if (lods.Count == 0) { bmin = rr.Min; bmax = rr.Max; }
            lods.Add(new LibraryLod
            {
                Parts = rr.Parts,
                MaxDistance = ObjectBuilder.LodDistance(l.RawDistance, opt.LodDistanceScale),
                Source = md,
                GeoKey = l.ObjKey,
                BankKeys = bankKeys,
                BankOffset = ins.MaterialBankOffset,
            });
            radius = System.Math.Max(radius, l.Sphere.W);
        }
        obj = lods.Count == 0 ? null : new LibraryObject { Key = insKey, Name = Bin.KeyToString(insKey), Lods = lods.ToArray(), Radius = radius, BoundsMin = bmin, BoundsMax = bmax };
        _objects[insKey] = obj;
        return obj;
    }

    public void Destroy()
    {
        Resources.Destroy();
        _objects.Clear();
    }
}
