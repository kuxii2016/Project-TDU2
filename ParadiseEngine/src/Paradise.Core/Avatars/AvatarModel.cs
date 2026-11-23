using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.Avatars;

/// <summary>
/// Avatar shader lighting
/// fine-tuning in the developer menu: _TDU2AvRampAdd/_TDU2AvRampRow/_TDU2AvAddMix/_TDU2AvAmbAdd/_TDU2AvHairSheen, 0 = original).
/// Default values: Game/Config/LightingConfig.cs. Call <see cref="Changed"/> after making changes (avatars will update their materials).
/// </summary>
public static class AvatarLight
{
    public static Vector4 IndoorDir = new(0.25f, 1f, 0.35f, 0f);
    public static Vector4 IndoorLight = new(0.5f, 0.47f, 0.43f, 1f);
    public static Vector4 IndoorAmbTop = new(0.46f, 0.44f, 0.41f, 1f), IndoorAmbBottom = new(0.2f, 0.19f, 0.18f, 1f);
    public static float RampAdd, RampRow, AddMix, AmbAdd, HairSheen;
    public static int Version { get; private set; }
    public static void Changed() => Version++;
}

/// <summary>Resources from the Avatar shader sets for TDU2/Avatar and TDU2/AvatarBlend</summary>
public static class AvatarMaterials
{
    static readonly ulong SemSkin = MaterialDef.KeyFromName("SKIN"), SemNormal = MaterialDef.KeyFromName("NORMAL"),
        SemTex = MaterialDef.KeyFromName("TEX"), SemHair = MaterialDef.KeyFromName("HAIR"), SemOcc = MaterialDef.KeyFromName("OCCLUSION"),
        SemL1 = MaterialDef.KeyFromName("LAYER1"), SemL2 = MaterialDef.KeyFromName("LAYER2"), SemL3 = MaterialDef.KeyFromName("LAYER3"),
        SemGrain = MaterialDef.KeyFromName("DETAIL_GRAIN");

    public static readonly Vector4 DefaultBrow = new(0.09f, 0.065f, 0.045f, 1f);

    /// <summary>Overlays that blend the original with custom passes (eye shadows): omitted.</summary>
    public static bool Skip(MaterialDef def) => (def.Name ?? "").IndexOf("EYES_S", StringComparison.OrdinalIgnoreCase) >= 0;

    static bool Layer(MaterialDef def, ulong sem, out MaterialLayer l)
    {
        foreach (var x in def.Layers) if (x.Semantic == sem) { l = x; return true; }
        l = default; return false;
    }

    public delegate IGpuTexture? TexGetter(ulong key, TextureWrap u, TextureWrap v);

    static IGpuTexture? Tex(MaterialLayer l, TexGetter get, bool clamp = false) =>
        l.TextureKey == 0 ? null : clamp ? get(l.TextureKey, TextureWrap.Clamp, TextureWrap.Clamp)
            : get(l.TextureKey, TextureDef.WrapFromEden(l.AddressU), TextureDef.WrapFromEden(l.AddressV));

    static Vector4 Clr(MaterialDef def, string p, Vector4 fallback) => def.TryGetParam(p, out var v) ? v : fallback;

    static bool HasAlpha(IGpuTexture t) => t.Format is GpuTextureFormat.Bc3 or GpuTextureFormat.Rgba8;

    /// <summary>Unity AvatarMaterials.Create: States from the material flags (.2dm +0x40), mode from the layers.</summary>
    public static MaterialDesc Create(MaterialDef? def, TexGetter get, int uvSets)
    {
        // [1] 2 = beidseitig, [3] 3 = Alpha-Test mit Referenz [6]/255, [8]/[9] 5/6 = SrcAlpha/InvSrcAlpha (geblendet)
        var fl = def?.Flags;
        bool blend = fl != null && fl[8] == 5 && fl[9] == 6;
        bool test = fl != null && fl[3] == 3;
        var m = new MaterialDesc { Kind = MaterialKind.Avatar, Name = def != null ? "AVT " + def.Name : "AVT ?", AvBlend = blend };
        if (def == null) { m.AvMode = 2f; m.Color = new Vector4(0.6f, 0.6f, 0.6f, 1f); return m; }
        if (test) m.Cutoff = System.Math.Clamp(fl![6] / 255f, 0.02f, 0.95f);
        if (blend && !test) m.Cutoff = 0.004f;
        if (def.TryGetParam("SPECPARM", out var sp)) m.AvSpec = new Vector4(sp.X, MathF.Max(1f, sp.Y), sp.Z, 0f);
        if (def.TryGetParam("CARINTEG", out var ci)) m.AvCarInteg = ci;
        if (def.TryGetParam("ATTEFACT", out var af)) m.AvAtteFact = af;
        // UVOFF: Atlas-Zelle (Iris, Augenbrauen); AvatarModel überschreibt sie je Figur
        if (def.TryGetParam("UVOFF", out var uo)) m.AvUVOff = new Vector4(uo.X, uo.Y, 0f, 0f);
        IGpuTexture? t;
        if (Layer(def, SemSkin, out var skin))
        {
            m.AvMode = 0f;
            m.Main = Tex(skin, get);
            // SKINCLR ist die Farbe des durchscheinenden Lichts (TD_Skinface), nicht die Hautfarbe; GLOSSDETAILCUT = 5. Layer
            m.Color = Vector4.One;
            m.AvSkinClr = Clr(def, "SKINCLR", Vector4.One);
            m.AvDclClr = Clr(def, "DCLCLR", new Vector4(0f, 0f, 0f, 1f));
            if (def.TryGetParam("COEFFS", out var co)) m.AvCoeffs = co;
            if (def.Layers.Count >= 5 && (t = Tex(def.Layers[4], get)) != null) { m.AvGloss = t; m.AvGlossOn = 1f; }
            // SPECTRALSCATTERING = 4. Layer: Hautton-Rampe der Herkunft (color_white / color_asia / color_black), Clamp
            if (def.Layers.Count >= 4 && (t = Tex(def.Layers[3], get, true)) != null) { m.AvRamp = t; m.AvRampOn = 1f; }
            if (Layer(def, SemNormal, out var nl) && (t = Tex(nl, get)) != null) { m.AvBump = t; m.AvBumpOn = 1f; }
        }
        else if (Layer(def, SemOcc, out var occ))
        {
            m.AvMode = 1f;
            m.AvBaseClr = Clr(def, "BASECLR", new Vector4(0.5f, 0.5f, 0.5f, 1f));
            if ((t = Tex(occ, get)) != null) { m.AvBump = t; m.AvBumpOn = 1f; }
            if (Layer(def, SemGrain, out var gr) && (t = Tex(gr, get)) != null) { m.Detail = t; m.AvDetailOn = 1f; }
            // UVSCALE: xy = Kachelung LAYER1 (auf UV1), zw = Stoffkorn (auf UV0)
            if (def.TryGetParam("UVSCALE", out var uvs))
            {
                if (MathF.Abs(uvs.X) < 1e-4f || MathF.Abs(uvs.Y) < 1e-4f) { uvs.X = 1f; uvs.Y = 1f; }
                m.AvUVScale = uvs;
            }
            var on = Vector4.Zero;
            // TD_Clothe2: LAYER1 auf UV1, LAYER2/LAYER3 auf UV2 (fehlende Sätze -> letzter vorhandener)
            var lu = new Vector4(System.Math.Min(1, uvSets - 1), System.Math.Min(2, uvSets - 1), System.Math.Min(2, uvSets - 1), 0f);
            if (Layer(def, SemL1, out var l1) && (t = Tex(l1, get)) != null) { m.AvLayer1 = t; on.X = 1f; m.AvClrL1 = Clr(def, "CLRL1", Vector4.One); }
            if (Layer(def, SemL2, out var l2) && (t = Tex(l2, get)) != null) { m.AvLayer2 = t; on.Y = 1f; m.AvClrL2 = Clr(def, "CLRL2", Vector4.One); }
            if (Layer(def, SemL3, out var l3) && (t = Tex(l3, get)) != null) { m.AvLayer3 = t; on.Z = 1f; m.AvClrL3 = Clr(def, "CLRL3", Vector4.One); }
            m.AvLayerOn = on;
            m.AvLayerUV = new Vector4(MathF.Max(0, lu.X), MathF.Max(0, lu.Y), MathF.Max(0, lu.Z), 0f);
        }
        else if (Layer(def, SemHair, out var hair))
        {
            m.AvMode = 3f;
            m.Main = Tex(hair, get);
            m.AvPrimClr = Clr(def, "PRIMCLR", new Vector4(0f, 0f, 0f, 1f));
            m.AvScdClr = Clr(def, "SCDCLR", new Vector4(0.5f, 0.35f, 0.2f, 1f));
            if (!test && !blend) m.Cutoff = 0.35f;
        }
        else
        {
            Layer(def, SemTex, out var tex);
            // "EYEBROW"/"EYELASHES" sind keine Augen; Augäpfel = TD_Eye: deckend, ohne COLOR-Parameter
            string dn = def.Name ?? "";
            bool eye = dn.IndexOf("EYE", StringComparison.OrdinalIgnoreCase) >= 0
                       && dn.IndexOf("LASH", StringComparison.OrdinalIgnoreCase) < 0
                       && dn.IndexOf("BROW", StringComparison.OrdinalIgnoreCase) < 0
                       && !blend && !def.TryGetParam("COLOR", out _);
            m.AvMode = eye ? 4f : 2f;
            // TD_Avatar des Kopfes: Farbe = TEX x COLOR (Zähne, Wimpern/Brauen); Kleidung/Accessoires unverändert
            bool face = dn.IndexOf("LASH", StringComparison.OrdinalIgnoreCase) >= 0 || dn.IndexOf("BROW", StringComparison.OrdinalIgnoreCase) >= 0
                        || dn.IndexOf("TEETH", StringComparison.OrdinalIgnoreCase) >= 0 || dn.IndexOf("TONGUE", StringComparison.OrdinalIgnoreCase) >= 0;
            if (face && def.TryGetParam("COLOR", out var tc)) m.Color = new Vector4(tc.X, tc.Y, tc.Z, 1f);
            if ((t = Tex(tex, get)) != null)
            {
                m.Main = t;
                // ohne Flags-Angabe: Texturen mit Alpha (Gurte, Accessoires) -> Alpha-Test
                if (!test && !blend && HasAlpha(t)) m.Cutoff = 0.3f;
                // geblendet, Textur ohne Alpha (DXT1: Augenbrauen, Wimpern) -> Maske aus der Helligkeit, Farbe = COLOR
                if (blend && !eye && !HasAlpha(t))
                {
                    m.AvAlphaLum = 1f;
                    m.Color = def.TryGetParam("COLOR", out var cc) ? new Vector4(cc.X, cc.Y, cc.Z, 1f) : DefaultBrow;
                    m.Cutoff = 0.02f;
                }
            }
            // Brillengläser (TEX = OMBRE2-Verlauf): Farbe/Deckkraft zwischen BCOLOR (unten) und TCOLOR (oben)
            if (def.TryGetParam("BCOLOR", out var bc))
            {
                m.AvOmbre = 1f;
                m.AvBColor = bc;
                m.AvTColor = Clr(def, "TCOLOR", new Vector4(0f, 0f, 0f, 1f));
            }
        }
        return m;
    }
}

/// <summary>
/// An avatar (game character) like in the game (port of Unity TDU2Avatar, rendering part): a skeleton (.xsb) skins the body, head, and all
/// clothing items. Load in the background (<see cref="Create"/>), then for each frame: Set pose via <see cref="Rig"/>, <see cref="Draw"/>
/// unwraps on the CPU (avatar space) and renders via IRenderer.DrawDynamic using TDU2/avatar materials.
/// Avatar space = game space (right-handed, +Z forward); <c>model</c> maps it to the render space.
/// </summary>
public sealed partial class AvatarModel : IDisposable
{
    public AvatarConfig Config { get; }
    public AvatarRig? Rig { get; private set; }
    public XsbSkeleton? Skeleton => Rig?.Skeleton;
    public bool Ready { get; private set; }
    public event Action<AvatarModel>? Built;
    public bool Visible = true;

    readonly IRenderer _r;
    Task<(XsbSkeleton? skel, List<AvatarPartData> body, List<(ClotheInfo info, AvatarPartData? data)> clothes)>? _load;

    sealed class Piece
    {
        public SkinPrim Prim = null!;
        public MaterialDesc Desc = null!;
        public IGpuMaterial Mat = null!;
        public uint[] Indices = Array.Empty<uint>();
        public bool Head;
        /// <summary>COEFFS / SPECPARM der 2DM (Haut): Vorgabe, wenn die Konfiguration keinen Wert setzt.</summary>
        public Vector4 BaseCoeffs, BaseSpec;
    }
    readonly List<Piece> _pieces = new();
    readonly List<AvatarPartData> _parts = new();
    ObjectVertex[] _verts = Array.Empty<ObjectVertex>();
    bool _inCar, _indoor, _headHidden;
    int _lightVersion = -1;

    AvatarModel(IRenderer r, AvatarConfig cfg) { _r = r; Config = cfg; }

    /// <summary>Avatar anlegen und im Hintergrund laden (AvatarLibrary muss initialisiert sein).</summary>
    public static AvatarModel Create(IRenderer r, AvatarConfig? cfg)
    {
        var a = new AvatarModel(r, cfg ?? AvatarConfig.Default());
        EditorRegister(a);   // Szenen-Editor (F1 > Open Editor)
        a.StartLoad();
        return a;
    }

    void StartLoad()
    {
        var cfg = Config.Clone();
        _load = Task.Run(() =>
        {
            var skel = AvatarLibrary.Skeleton(cfg.Sex);
            var body = AvatarLibrary.LoadBody(cfg.Sex, cfg.Ethnic);
            var clothes = new List<(ClotheInfo, AvatarPartData?)>();
            bool hat = false;
            foreach (int h in cfg.Items) { var hc = AvatarLibrary.Clothe(h); if (hc != null && hc.Part == ClothePart.Helmet && !hc.IsNothing) hat = true; }
            for (int i = 0; i < cfg.Items.Count; i++)
            {
                var ci = AvatarLibrary.Clothe(cfg.Items[i]);
                if (ci == null) continue;
                if (ci.Sex == 1 && cfg.Sex == 0 || ci.Sex == 2 && cfg.Sex == 1) continue;   // falsches Geschlecht
                AvatarPartData? data = null;
                try { data = AvatarLibrary.LoadClothe(ci, i < cfg.Colors.Count ? cfg.Colors[i] : 0, hat); }
                catch (Exception ex)
                {
                    Log.Error("Clothes " + ci.File + ": " + ex.Message);
                }
                clothes.Add((ci, data));
            }
            return (skel, body, clothes);
        });
    }

    /// <summary>Jedes Bild (Main-Thread): fertig geladene Daten aufbauen.</summary>
    public void Update()
    {
        if (_load == null || !_load.IsCompleted) return;
        var t = _load; _load = null;
        if (t.IsFaulted)
        {
            Log.Warn("Avatar loading: " + t.Exception?.GetBaseException().Message);
            return;
        }
        try { Build(t.Result.skel, t.Result.body, t.Result.clothes); }
        catch (Exception ex)
        {
            Log.Error("Avatar Create: " + ex);
        }
    }

    void Build(XsbSkeleton? skel, List<AvatarPartData> body, List<(ClotheInfo info, AvatarPartData? data)> clothes)
    {
        if (skel == null)
        {
            Log.Warn("Avatar: missing skeleton (avatar/common_bodies.bnk?)");
            return;
        }
        Rig = new AvatarRig(skel);
        // Sichtbare Körperteile: ein Teil bleibt sichtbar, solange kein getragenes Stück es verdeckt
        var visible = new Dictionary<ulong, bool>();
        foreach (var f in AvatarLibrary.BodyFlags)
        {
            bool vis = true;
            foreach (var c in clothes) if (c.info != null && c.info.Visible.TryGetValue(f, out bool v) && !v) { vis = false; break; }
            string up = f.ToUpperInvariant();
            foreach (var full in new[] { "TOP_" + up, "BOTTOM_" + up, "TOP_ARMS_" + up, up })
                visible[MaterialDef.KeyFromName(full)] = vis;
        }
        foreach (var part in body) AddPart(part, visible, part.Name.Contains("_head_"));
        foreach (var c in clothes)
            if (c.data != null)
            {
                bool head = c.info.Part is ClothePart.Hair or ClothePart.Helmet or ClothePart.Glasses or ClothePart.Beard;
                AddPart(c.data, null, head);
            }
        int nv = 0; foreach (var p in _pieces) nv = System.Math.Max(nv, p.Prim.Pos.Length);
        _verts = new ObjectVertex[nv];
        ApplyBrowColor();
        ApplySkinParams();
        Ready = true;
        try { Built?.Invoke(this); } catch (Exception ex) { Log.Error("Avatar Build: " + ex.Message); }
    }

    void AddPart(AvatarPartData part, Dictionary<ulong, bool>? visible, bool head)
    {
        if (part.Prims.Count == 0) return;
        _parts.Add(part);
        for (int i = 0; i < part.Prims.Count; i++)
        {
            var def = part.PrimMaterials[i];
            if (def != null && visible != null && visible.TryGetValue(def.Key, out bool vis) && !vis) continue;
            if (def != null && AvatarMaterials.Skip(def)) continue;
            var pr = part.Prims[i];
            var desc = AvatarMaterials.Create(def, (key, u, v) => GetTexture(part, key, u, v), pr.Uv.Length);
            if (part.HasHairColor && desc.AvMode > 2.5f && desc.AvMode < 3.5f) { desc.AvPrimClr = part.HairPrim; desc.AvScdClr = part.HairScd; }
            ApplyLight(desc);
            var idx = new uint[pr.Tris.Length];
            for (int k = 0; k < idx.Length; k++) idx[k] = (uint)pr.Tris[k];
            _pieces.Add(new Piece { Prim = pr, Desc = desc, Mat = _r.CreateMaterial(desc), Indices = idx, Head = head, BaseCoeffs = desc.AvCoeffs, BaseSpec = desc.AvSpec });
        }
    }

    IGpuTexture? GetTexture(AvatarPartData part, ulong key, TextureWrap u, TextureWrap v)
    {
        if (key == 0) return null;
        if (part.Textures.TryGet(key, u, v, _r, out var t)) return t;
        return AvatarLibrary.CommonTextures?.Get(key, u, v);
    }
    /// <summary>
    /// Face like the original (td2.exe 0x67CB20 / 0x67CD60): “EYES” UVOFF = EyeID%25 cell of the 5x5 iris atlas, “EYEBROW” UVOFF = cell
    /// EyebrowsID of the 2x4 atlas and COLOR = EyebrowsColor. Without .avt specifications: Eyebrows in the same color as the hairstyle.
    /// </summary>
    void ApplyBrowColor()
    {
        var col = AvatarMaterials.DefaultBrow;
        if (Config.HasBrowColor) col = Config.BrowColor;
        else foreach (var p in _parts) if (p.HasHairColor) { col = Vector4.Lerp(p.HairPrim, p.HairScd, 0.25f) * 0.85f; col.W = 1f; break; }
        foreach (var pc in _pieces)
        {
            var d = pc.Desc;
            string n = d.Name ?? "";
            bool brow = n.IndexOf("BROW", StringComparison.OrdinalIgnoreCase) >= 0;
            bool changed = false;
            if (brow && d.AvMode > 1.5f && d.AvMode < 2.5f) { d.Color = col; changed = true; }
            if (brow && Config.BrowId >= 0)
            {
                int k = Config.BrowId % 8;
                d.AvUVOff = new Vector4((k / 4) * 0.5f, (k % 4) * 0.25f, 0f, 0f); changed = true;
            }
            else if (n == "AVT EYES" && Config.EyeId >= 0)
            {
                int e = Config.EyeId % 25;
                d.AvUVOff = new Vector4((e / 5) * 0.2f, (e % 5) * 0.2f, 0f, 0f); changed = true;
            }
            if (changed) _r.UpdateMaterial(pc.Mat, d);
        }
    }

    /// <summary>
    /// Haut-Konstanten aus der AvatarConfiguration wie das Original (skin_zones_spec §3.3, exe 0x683E60 / 0x6834D0 / 0x6833B0):
    /// TD_Skin2/TD_Skinface: COEFFS.y = freckle (Sommersprossen-Maske G.b), COEFFS.z = ScatteringCoeff (Tint, Zeile der Hautton-Rampe;
    /// ohne Wert die der 2DM), SPECPARM.w = age (Falten-Normalmap N2 – im Avatar-Shader der Engine noch nicht ausgewertet, CHECKLIST).
    /// </summary>
    void ApplySkinParams()
    {
        foreach (var pc in _pieces)
        {
            var d = pc.Desc;
            if (d.AvMode > 0.5f) continue;   // nur Haut (Modus 0)
            var co = pc.BaseCoeffs;
            co.Y = Config.Freckle;
            if (Config.Tint >= 0f) co.Z = Config.Tint;
            var sp = pc.BaseSpec;
            sp.W = Config.Age;
            if (co == d.AvCoeffs && sp == d.AvSpec) continue;
            d.AvCoeffs = co; d.AvSpec = sp;
            _r.UpdateMaterial(pc.Mat, d);
        }
    }

    /// <summary>ScatteringCoeff (COEFFS.z) der Haut-2DM, -1 solange nicht gebaut – Vorgabe, wenn die Konfiguration keinen Tint setzt.</summary>
    public float SkinScattering { get { foreach (var p in _pieces) if (p.Desc.AvMode < 0.5f) return p.BaseCoeffs.Z; return -1f; } }

    FaceFxFace? _face;
    bool _faceTried;

    /// <summary>
    /// Gesicht ohne Neuaufbau ändern (Schönheitsklinik: jeder Regler-Schritt): Gesichtsparameter, Alter, Sommersprossen, Tint, Augen, Brauen.
    /// </summary>
    public void SetFace(AvatarConfig cfg)
    {
        Config.CopyFace(cfg);
        if (!Ready) return;
        ApplyBrowColor();
        ApplySkinParams();
    }

    /// <summary>Gesichtsform (FaceFX-Register aus Config.Face) auf die Arbeits-Pose legen – nach dem Körper-Clip, vor dem Häuten.</summary>
    void ApplyFace()
    {
        if (Rig == null || !Config.HasFace) return;
        if (_face == null)
        {
            if (_faceTried) return;
            _faceTried = true;
            _face = FaceFxFace.Create(Rig, Config.Sex);
            if (_face == null) return;
        }
        _face.Apply(Rig, Config.Face);
    }

    /// <summary>Fußraum-Schatten der Avatar-Shader (im Auto).</summary>
    public bool InCar { get => _inCar; set { if (_inCar == value) return; _inCar = value; Refresh(); } }
    /// <summary>Innenraum: Deckenlicht statt Sonne (Unity _TDU2AvatarIndoor, TDU2Interiors.SetAvatarIndoor).</summary>
    public bool Indoor { get => _indoor; set { if (_indoor == value) return; _indoor = value; Refresh(); } }

    void Refresh()
    {
        foreach (var p in _pieces)
        {
            ApplyLight(p.Desc);
            _r.UpdateMaterial(p.Mat, p.Desc);
        }
    }

    void ApplyLight(MaterialDesc d)
    {
        d.AvInCar = _inCar ? 1f : 0f; d.AvIndoor = _indoor ? 1f : 0f;
        d.AvIndoorDir = AvatarLight.IndoorDir; d.AvIndoorLight = AvatarLight.IndoorLight;
        d.AvIndoorAmbTop = AvatarLight.IndoorAmbTop; d.AvIndoorAmbBottom = AvatarLight.IndoorAmbBottom;
        d.AvTweak = new Vector4(AvatarLight.RampAdd, AvatarLight.RampRow, AvatarLight.AddMix, AvatarLight.AmbAdd);
        d.AvHairSheen = AvatarLight.HairSheen;
    }

    /// <summary>Kopf, Haare, Brille, Bart aus-/einblenden (Cockpit-Kamera: nur noch Schatten).</summary>
    public void HideHead(bool hide) => _headHidden = hide;

    /// <summary>Häuten (aktuelle Pose von <see cref="Rig"/>) und zeichnen. <paramref name="model"/>: Avatar-Raum -> Render-Raum.</summary>
    public void Draw(Matrix4x4 model)
    {
        if (!Ready || !Visible || EditorHidden || Rig == null) return;
        EditorLastModel = model; EditorDrawnTick = System.Environment.TickCount64;   // Szenen-Editor: Lage/Markierung
        if (_lightVersion != AvatarLight.Version) { _lightVersion = AvatarLight.Version; Refresh(); }
        ApplyFace();
        Rig.UpdateSkin();
        foreach (var p in _pieces)
        {
            int n = p.Prim.Pos.Length;
            if (n == 0 || p.Indices.Length == 0) continue;
            if (p.Head && _headHidden) continue;   // Cockpit-Kamera: Kopfteile nicht zeichnen
            Rig.Skin(p.Prim, _verts, 0);
            _r.DrawDynamic(new ReadOnlySpan<ObjectVertex>(_verts, 0, n), p.Indices, p.Mat, model);
        }
    }

    public void Dispose()
    {
        EditorDisposed = true;
        foreach (var p in _pieces) p.Mat.Dispose();
        _pieces.Clear();
        foreach (var p in _parts) p.Textures.Destroy();
        _parts.Clear();
        Ready = false;
    }
}