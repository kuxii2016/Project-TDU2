using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

public enum EdenBlend { Opaque, Cutout, AlphaBlend, Additive, Decal }

/// <summary>
/// Erzeugt Materialien aus Eden-.2dm-Materialien. Portiert aus dem Unity-Projekt (Built-in-Pfad).
/// Interpretation der Flag-Bytes (MAT.+0x40, empirisch):
///   [3] 2 = einseitig, 3 = zweiseitig
///   [5] 0 = Alpha-Test an (Referenz = [7]/255), 1 = aus
///   [8],[9] D3DBLEND src/dst: (2,1) = opak, (5,6) = Alpha-Blend, (5,2) = additiv
/// Shader, die noch nicht portiert sind (TDU2/Water, Sprite, SpriteGlow, Foliage), verhalten sich wie im
/// Unity-Code bei fehlendem Shader: Foliage -> TDU2/Object mit _PlantMode; Water/Sprite -> ausgelassen.
/// </summary>
public static class MaterialFactory
{
    /// <summary>Wird vom Streamer gesetzt (Unity: global verfügbare Grafik-API).</summary>
    public static IRenderer? Renderer;

    public static float VertexColorStrength = 0.5f;
    public static float ShadowMapStrength = 0.6f;
    public static bool ShowGlowMaterials = true;
    public static bool Interior;
    /// <summary>Möbel/Szenen ohne Interieur-Bank (Unity InteriorFx.Apply(null, …)): nur Glas transparent.</summary>
    public static bool InteriorGlass;
    public static float GlowIntensity = 1f;
    /// <summary>Helligkeit der Nacht-Lightmap-Gebäude am Tag (Game/Config LightingConfig.NightLightmapDayBoost).</summary>
    public static float NightLightmapDayBoost = 2f;
    public static float HaloIntensity = 1f;
    public static float WindStrength = 1f;
    public static float ObjectBrightness = .5f, WrapLighting = 0.35f;
    /// <summary>Pool-/Brunnenwasser: Farbe (a = Mindest-Deckung) für "TDU2/Water" _PoolTint.</summary>
    public static Vector4 PoolColor = new(0.10f, 0.42f, 0.55f, 0.55f);

    // Shader-Verfügbarkeit (Unity: Shaders.X != null). Portiert: Object, ObjectTransparent, Terrain.
    public static bool HasWaterShader => true;
    public static bool HasSpriteShader => true;
    public static bool HasFoliageShader => true;

    private const ulong ShaderGouraud = 0x73078865ca37336dUL, ShaderNeonDecal = 0x63078165ca37416dUL, ShaderActors = 0xd5013ed0dcacafafUL, ShaderCasinoScroll = 0xfafafafafafafafaUL;

    private static IGpuMaterial? _fallback;

    public static EdenBlend GetBlend(MaterialDef def, out float alphaRef, out bool twoSided)
    {
        var f = def.Flags;
        twoSided = f[3] == 3;
        alphaRef = f[7] / 255f;
        bool alphaTest = f[5] == 0 && f[7] > 2;
        if (f[8] == 5 && f[9] == 2)
        {
            // SrcAlpha/One: Schilder & Markierungen (Decals) vs. Leuchteffekte
            return IsDecalName(def.Name) ? EdenBlend.Decal : EdenBlend.Additive;
        }
        if (alphaTest) return EdenBlend.Cutout;
        if (f[8] == 5 && f[9] == 6)
        {
            // Vegetation mit Alpha-Blend als Cutout rendern (keine Sortierprobleme)
            if (def.Name.StartsWith("V_", StringComparison.OrdinalIgnoreCase)) { alphaRef = 0.5f; return EdenBlend.Cutout; }
            return EdenBlend.AlphaBlend;
        }
        return EdenBlend.Opaque;
    }

    private static bool IsDecalName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        n = n.ToLowerInvariant();
        return n.StartsWith("alpha_") || n.Contains("parking") || n.Contains("sign") || n.Contains("wline") || n.Contains("arrow")
            || n.Contains("mark") || n.Contains("stripe") || n.Contains("logo") || n.Contains("decal");
    }

    private static readonly ulong SemMaterial = Bin.KeyFromString("MATERIAL");
    private static readonly ulong SemTex = Bin.KeyFromString("TEX");
    private static readonly ulong SemNeon = Bin.KeyFromString("NEONGLOW");
    private static readonly ulong SemFrontColor = MaterialDef.KeyFromName("FRONTCOLOR"), SemBackColor = MaterialDef.KeyFromName("BACKCOLOR");
    private static readonly ulong SemDiffuse = Bin.KeyFromString("DIFFUSE");
    private static readonly ulong SemMarks = Bin.KeyFromString("MARKS");
    private static readonly ulong SemMarks2 = Bin.KeyFromString("MARKS2");
    private static readonly ulong SemGloss = Bin.KeyFromString("GLOSS");
    private static readonly ulong SemWindow = MaterialDef.KeyFromName("WINDOWMAP");
    private static readonly ulong SemNightLm = MaterialDef.KeyFromName("NIGHTLIGHTMAP");   // 0x47494CA489949D96
    private static readonly ulong SemReflection = MaterialDef.KeyFromName("REFLECTION");
    private const ulong ShaderFurnitures = 0xd5013ed0db79afafUL;   // TD_Furnitures.sob (Unity InteriorFx GuidFurnitures)
    private const ulong KeyBiasCoef = 0x46454f4353414988UL;        // Key8("biasCoeff"): Fresnel-Grundwert der Spiegelung
    /// <summary>Unity InteriorFx.PlanarReflections / MinFloorReflection (Garage: GarageFloorReflection 0,45).</summary>
    public static bool PlanarReflections = true;
    public static float MinFloorReflection = 0f;

    /// <summary>Unity InteriorFx.IsGlass: Glas-Materialnamen (im Interieur immer transparent).</summary>
    public static bool IsGlassName(string? n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        n = n.ToLowerInvariant();
        return n.Contains("glass") || n.Contains("vitre") || n.Contains("verre") || n.Contains("window") || n.Contains("fenetre") || n.Contains("glace");
    }

    /// <summary>Haupt-Farbtextur: COLOR, sonst MATERIAL/TEX/DIFFUSE/NEONGLOW (z.B. Flugzeug, Neonschilder).</summary>
    private static bool TryGetColorLayer(MaterialDef def, out MaterialLayer layer)
    {
        if (def.TryGetLayer(MaterialDef.SemColor, out layer)) return true;
        if (def.TryGetLayer(SemMaterial, out layer)) return true;
        if (def.TryGetLayer(SemTex, out layer)) return true;
        if (def.TryGetLayer(SemDiffuse, out layer)) return true;
        if (def.TryGetLayer(SemNeon, out layer)) return true;
        if (def.TryGetLayer(SemFrontColor, out layer) && layer.TextureKey != 0) return true;
        if (def.TryGetLayer(SemBackColor, out layer) && layer.TextureKey != 0) return true;
        return false;
    }

    /// <summary>Leuchteffekte, die im Spiel nur nachts an sind. Ampel-Halos bleiben immer an.</summary>
    public static bool IsNightGlow(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        string n = name.ToUpperInvariant();
        if (n.Contains("TLIGH") || n.Contains("XIN") || n.Contains("HALO_R") || n.Contains("HALO_O") || n.Contains("HALO_G") || n.Contains("FEU")) return false;
        return true;
    }

    /// <summary>Fallback-Material (grau 0.6) für Objekte ohne Materialdefinition.</summary>
    public static IGpuMaterial? Fallback
    {
        get
        {
            if (_fallback == null && Renderer != null)
                _fallback = Renderer.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Object, Name = "TDU2_Fallback", Color = new Vector4(0.6f, 0.6f, 0.6f, 1f), Brightness = ObjectBrightness, Wrap = WrapLighting, VertexColorStrength = VertexColorStrength });
            return _fallback;
        }
    }

    private static IGpuMaterial? _water, _pool;
    private static MaterialDesc? _waterDesc;

    /// <summary>Meer (TDU2/Water, Wellen ohne Normalmap bis water_n.2db geladen ist).</summary>
    public static IGpuMaterial? Water
    {
        get
        {
            if (_water == null && Renderer != null)
            {
                _waterDesc = new MaterialDesc { Kind = MaterialKind.Water, Name = "TDU2_Water" };
                _water = Renderer.CreateMaterial(_waterDesc);
            }
            return _water;
        }
    }

    /// <summary>
    /// Wasserfläche für Pools/Brunnen (TD_Water: "Oasis_*", "Water"): Meer-Shader mit eigener Wasserfarbe und Deckung. Die
    /// Originalmaterialien tragen in COLOR/NORMAL nur Normalmaps und Kacheln – als gewöhnliches Material dunkel/schwarz.
    /// </summary>
    public static IGpuMaterial? PoolWater
    {
        get
        {
            if (_pool == null && Renderer != null)
                _pool = Renderer.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Water, Name = "TDU2_PoolWater", PoolTint = PoolColor, Main = _waterDesc?.Main });
            return _pool;
        }
    }

    /// <summary>Normalmap des Meeres setzen (water_n.2db aus commonworld, Unity: Water.SetTexture("_NormalMap")).</summary>
    public static void SetWaterNormalMap(IGpuTexture? normalMap)
    {
        if (normalMap == null || Renderer == null) return;
        _ = Water;
        if (_waterDesc == null || _water == null) return;
        _waterDesc.Main = normalMap;
        Renderer.UpdateMaterial(_water, _waterDesc);
        if (_pool != null) Renderer.UpdateMaterial(_pool, new MaterialDesc { Kind = MaterialKind.Water, Name = "TDU2_PoolWater", PoolTint = PoolColor, Main = normalMap });
    }

    /// <summary>NoiseNMap des Meeres (World/OceanNoise, vom MapStreamer gesetzt).</summary>
    public static IGpuTexture? OceanNoiseTexture;

    /// <summary>
    /// Meer wie das Original TD_Ocean (FUN_008be1e0): Colormap der Ebene (Sektor/Area/Insel, TD_HeightMap COLOR) und deren
    /// Ausdehnung in Spielkoordinaten; Normalmap = NoiseNMap. level: 0 Sektor, 1 Area, 2 Insel.
    /// </summary>
    public static IGpuMaterial? CreateOceanMaterial(IGpuTexture? colorMap, Vector2 origin, Vector2 extent, int level)
    {
        if (Renderer == null) return null;
        return Renderer.CreateMaterial(new MaterialDesc
        {
            Kind = MaterialKind.Water,
            Name = level == 0 ? "TD_Ocean (Sektor)" : level == 1 ? "TD_Ocean (Area)" : "TD_Ocean (Insel)",
            Main = OceanNoiseTexture,
            Ground = colorMap,
            Ocean = true,
            OceanLevel = level,
            OceanColorUv = new Vector4(origin.X, origin.Y, 1f / MathF.Max(1f, extent.X), 1f / MathF.Max(1f, extent.Y)),
        });
    }

    /// <summary>TDU2/Grass (Atlas, Bodenfarbe/Schatten pro Sektor wie Unitys MaterialPropertyBlock).</summary>
    public static IGpuMaterial? CreateGrassMaterial(IGpuTexture? atlas, float cutoff, IGpuTexture? ground = null, IGpuTexture? splat = null, float shadowStrength = 0.6f)
    {
        if (Renderer == null || atlas == null) return null;
        return Renderer.CreateMaterial(new MaterialDesc
        {
            Kind = MaterialKind.Grass,
            Name = "TDU2_Grass",
            Main = atlas,
            Cutoff = cutoff,
            Ground = ground,
            Splat = splat,
            ShadowStrength = splat != null ? shadowStrength : 0f,
        });
    }

    // ------------------------------------------------------------------ Material-Infos (Textur-Animation, Debug-Auswahl)
    public sealed class MaterialInfo
    {
        public MaterialDef Def = null!;
        public MaterialDesc Desc = null!;
        public string? MainTexture, DetailTexture, ShadowTexture, TintTexture;
        public IGpuTexture? NeonMask;
        public EdenBlend Blend;
        public bool Lightmap, Packed, Sprite;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IGpuMaterial, MaterialInfo> _info = new();

    public static bool TryGetInfo(IGpuMaterial? m, out MaterialInfo info)
    {
        info = null!;
        return m != null && _info.TryGetValue(m, out info!);
    }

    /// <summary>
    /// Neon (TD_NEONDECAL): ohne Animation steht die Maske auf Schwarz (Neon aus); mit .uva die echte NEONGLOW-Maske einsetzen –
    /// die Animation verschiebt sie (Lauflicht, blinkende Birnen, Pfeile). (Unity: InteriorFx.UseNeonMask)
    /// </summary>
    public static void UseNeonMask(IGpuMaterial m)
    {
        if (Renderer == null || !TryGetInfo(m, out var info) || info.NeonMask == null || info.Desc.Main == info.NeonMask) return;
        info.Desc.Main = info.NeonMask;
        info.Desc.Color = Vector4.One;
        Renderer.UpdateMaterial(m, info.Desc);
    }

    /// <summary>Gemeinsame Materialien (nicht pro Objekt besitzen/zerstören).</summary>
    public static bool IsShared(IGpuMaterial? m) => m != null && (m == _fallback || m == _water || m == _pool);

    /// <param name="getTexture">key, wrapU, wrapV -> Textur (lokal oder global)</param>
    public static IGpuMaterial? CreateObjectMaterial(MaterialDef? def, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTexture, bool sprite = false, bool tintUv = false, bool fourUv = false, bool wetGround = false)
    {
        var r = Renderer ?? throw new InvalidOperationException("MaterialFactory.Renderer nicht gesetzt.");
        if (def == null) return Fallback;
        if (def.IsOcean) return Water;
        // Pools/Brunnen ("Water"): im Spiel Reflexions-Shader mit Normalmaps -> eigene Wasserfläche verwenden
        if (def.IsPoolWater || def.Name.Equals("Water", StringComparison.OrdinalIgnoreCase)) return PoolWater;

        var missing = new List<ulong>();
        var d = BuildObjectDesc(def, getTexture, sprite, tintUv, fourUv, wetGround, missing, out var info);
        if (d == null) return null;
        var mat = r.CreateMaterial(d);
        _info.AddOrUpdate(mat, info);
        // Textur noch nicht bekannt (liegt in einer Bank, die erst später indiziert wird) -> später das GANZE Material neu
        // entscheiden (Kind, Lightmap-Verstärkung, Palette, Fenster ...), nicht nur die Textur einsetzen
        if (missing.Count > 0)
            _pending.Add(new PendingMat { Mat = mat, Def = def, Get = getTexture, Sprite = sprite, TintUv = tintUv, FourUv = fourUv, WetGround = wetGround, Missing = missing });
        return mat;
    }

    /// <summary>
    /// Material-Entscheidungen (portiert aus Unity MaterialFactory.CreateObjectMaterial). Jede angeforderte, aber (noch)
    /// nicht gefundene Textur landet in <paramref name="missing"/>. null = Material ausgeblendet (Leuchteffekt aus).
    /// </summary>
    private static MaterialDesc? BuildObjectDesc(MaterialDef def, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTexture0, bool sprite, bool tintUv, bool fourUv,
        bool wetGround, List<ulong> missing, out MaterialInfo info)
    {
        info = null!;
        IGpuTexture? getTexture(ulong k, TextureWrap u, TextureWrap v)
        {
            var t = getTexture0(k, u, v);
            if (t == null && k != 0 && !missing.Contains(k)) missing.Add(k);
            return t;
        }
        var blend = GetBlend(def, out float alphaRef, out bool twoSided);
        IGpuTexture? color = null, shadow = null, detail = null;
        bool hasColorLayer = TryGetColorLayer(def, out var cl);
        if (hasColorLayer)
            color = getTexture(cl.TextureKey, TextureDef.WrapFromEden(cl.AddressU), TextureDef.WrapFromEden(cl.AddressV));
        // SHADOW mit Adressmodus 3/3 ist die projizierte Sektor-Schattenkarte (kein UV-Satz) -> weglassen
        if (def.TryGetLayer(MaterialDef.SemShadow, out var sl) && !(sl.AddressU == 3 && sl.AddressV == 3))
            shadow = getTexture(sl.TextureKey, TextureDef.WrapFromEden(sl.AddressU), TextureDef.WrapFromEden(sl.AddressV));
        // DETAIL: eigentliche Oberflächentextur (Beton, Holz, Asphalt ...). COLOR ist dann Schmutz-/Farbvariation
        // oder eine Lightmap (lm_*.2db) auf UV0. Straßen nutzen den letzten UV-Satz, sonst UV-Satz 2.
        bool hasDetailLayer = def.TryGetLayer(MaterialDef.SemDetail, out var dl) || def.TryGetLayer(MaterialDef.SemDetail1, out dl);
        if (hasDetailLayer)
            detail = getTexture(dl.TextureKey, TextureDef.WrapFromEden(dl.AddressU), TextureDef.WrapFromEden(dl.AddressV));
        IGpuTexture? tint = null, marks = null, marks2 = null;
        if (tintUv && def.TryGetLayer(MaterialDef.SemDetail2, out var t2))
            tint = getTexture(t2.TextureKey, TextureDef.WrapFromEden(t2.AddressU), TextureDef.WrapFromEden(t2.AddressV));
        if (def.IsRoad && def.TryGetLayer(SemMarks, out var mk))
            marks = getTexture(mk.TextureKey, TextureDef.WrapFromEden(mk.AddressU), TextureDef.WrapFromEden(mk.AddressV));
        if (def.IsRoad && fourUv && def.TryGetLayer(SemMarks2, out var mk2))
            marks2 = getTexture(mk2.TextureKey, TextureDef.WrapFromEden(mk2.AddressU), TextureDef.WrapFromEden(mk2.AddressV));
        if (blend == EdenBlend.Cutout || blend == EdenBlend.AlphaBlend) twoSided = true;

        if (sprite && (blend == EdenBlend.Additive || blend == EdenBlend.AlphaBlend))
        {
            // Halos (SF_TLight_Halo ...): additives Billboard statt beleuchteter Cutout-Karte (die wäre schwarz)
            var gd = new MaterialDesc
            {
                Kind = MaterialKind.SpriteGlow,
                Name = def.Name + "_Glow",
                Main = color,
                Intensity = blend == EdenBlend.Additive ? HaloIntensity : 1f,
                Additive = blend == EdenBlend.Additive,
                NightOnly = IsNightGlow(def.Name),
            };
            info = new MaterialInfo { Def = def, Desc = gd, Blend = blend, Sprite = sprite, MainTexture = color?.Name };
            return gd;
        }
        if (sprite)
        {
            var sd = new MaterialDesc
            {
                Kind = MaterialKind.Sprite,
                Name = def.Name + "_Sprite",
                Main = color,
                WindStrength = WindStrength,
                Cutoff = blend == EdenBlend.Cutout ? System.Math.Clamp(alphaRef, 0.05f, 0.95f) : 0.5f,
            };
            // TD_VegetationSPR: RAMPCLR1.x = Höhe der Kronenmitte (kugelförmige Normalen für die Beleuchtung)
            if (def.TryGetParam("RAMPCLR1", out var ramp)) { sd.CrownHeight = ramp.X; sd.CrownOn = true; }
            info = new MaterialInfo { Def = def, Desc = sd, Blend = blend, Sprite = sprite, MainTexture = color?.Name };
            return sd;
        }

        // Unity InteriorFx (PrepareRest/Apply): ausschließlich REFLECTION ohne COLOR/DETAIL (CubeCar) = unsichtbarer Hilfskörper
        if (Interior && !hasColorLayer && !hasDetailLayer && def.TryGetLayer(SemReflection, out var rl0) && rl0.TextureKey != 0) return null;
        bool transparent = blend == EdenBlend.AlphaBlend || blend == EdenBlend.Additive || blend == EdenBlend.Decal;
        // Unity InteriorFx.MakeGlass: Glas im Interieur immer transparent (TDU2/ObjectTransparent, SrcAlpha/OneMinusSrcAlpha)
        bool glass = (Interior || InteriorGlass) && !transparent && IsGlassName(def.Name);
        bool interiorFx = Interior || InteriorGlass;   // Unity InteriorFx.Apply läuft über alle Materialien von Szene und Möbeln
        if (glass) transparent = true;
        bool lightmap = color?.Name != null && color.Name.StartsWith("lm_", StringComparison.OrdinalIgnoreCase);
        bool foliage = blend == EdenBlend.Cutout && HasFoliageShader && def.Name.StartsWith("V_", StringComparison.OrdinalIgnoreCase) && detail == null;
        if (blend == EdenBlend.Additive && !ShowGlowMaterials) return null; // Leuchteffekte (nur nachts) ausblenden

        var d = new MaterialDesc
        {
            Kind = transparent ? MaterialKind.Transparent : foliage ? MaterialKind.Foliage : MaterialKind.Object,
            Name = def.Name,
            Main = color,
            Brightness = ObjectBrightness,
            Wrap = WrapLighting,
            VertexColorStrength = VertexColorStrength,
        };

        if (!transparent)
        {
            d.Shadow = shadow;
            d.ShadowStrength = shadow != null ? ShadowMapStrength : 0f;
            // Vegetation ohne TDU2/Foliage (V_* Cutout): Beleuchtung wie Laub/Gras/Boden, keine Vertexfarbe
            bool plant = !foliage && blend == EdenBlend.Cutout && def.Name.StartsWith("V_", StringComparison.OrdinalIgnoreCase);
            if (plant) { d.PlantMode = true; d.VertexColorStrength = 0f; }
            if (detail != null)
            {
                d.Detail = detail;
                d.DetailMode = true;
                d.DetailUvLast = def.IsRoad;
                d.ColorStrength = def.IsRoad ? 0.35f : 1f;
                bool packed = TextureInfo.IsPacked(detail);
                d.DetailBoost = lightmap || def.IsRoad ? 2f : packed ? 1f : 1.2f;
                d.DetailGray = packed;
                if (def.IsGround && !lightmap)
                {
                    d.DetailGray = true;
                    d.DetailBoost = 1.25f / 1.8f;   // Shader: R x 1.8 x Boost -> COLOR x DETAIL.r x 1.25
                }
            }
            if (tint != null)
            {
                d.Tint = tint;
                d.TintMode = true;
                float mean = TextureInfo.TryGetMeanLuminance(tint, out var ml) ? ml : 0.5f;
                d.TintScale = System.Math.Clamp(1f / System.Math.Max(0.05f, mean), 1f, 2f);
            }
            if (marks != null) { d.Marks = marks; d.MarksMode = true; }
            if (marks2 != null) { d.Marks2 = marks2; d.Marks2Mode = true; }
            // Regen/Pfützen wie WFX_WetGrd (FUN_00aa0a30/0890): nur TD_Road-Materialien (MAT-GUID c8ad93c95455291b) des
            // NETWORK-Modells eines Sektors – nicht jedes TD_Road-Material (Tunnelwände/-decken, Brücken-Unterseiten)
            d.WetSurface = def.ShaderSet == MaterialDef.ShaderRoad && wetGround;   // exakt TD_Road – nicht "Road…"-Namen wie RoadTunnelMater (0xe8ad93c00455291b)
            if (def.ShaderSet == 0xc913741cbe17490dUL || def.ShaderSet == 0x18315cacac6cff57UL)
                d.NightEmit = def.ShaderSet == 0xc913741cbe17490dUL ? 2.5f : 1.5f;
            // Nachtfenster der Gebäude (TD_Building mit GLOSS + WINDOWMAP, luminance_power.x > 0)
            if ((def.ShaderSet == 0x183157b81c6cff57UL || def.ShaderSet == 0x18aa57b81c6cff57UL)
                && def.TryGetLayer(SemGloss, out var gl) && def.TryGetLayer(SemWindow, out var wl)
                && def.Params.TryGetValue(MaterialDef.KeyFromName("luminance_power"), out var lp) && lp.X > 0f)
            {
                var gt = getTexture(gl.TextureKey, TextureWrap.Repeat, TextureWrap.Repeat);
                var wt = getTexture(wl.TextureKey, TextureWrap.Repeat, TextureWrap.Repeat);
                if (gt != null && wt != null) { d.GlossTex = gt; d.WinTex = wt; d.WinParams = lp; d.WinMode = true; }
            }
            // Nacht-Lightmap (TD_Building s8): teilt sich den Sampler mit SHADOW (kommt in TD_Building nicht zusammen vor)
            if (shadow == null && def.TryGetLayer(SemNightLm, out var nll))
            {
                d.NightLm = getTexture(nll.TextureKey, TextureWrap.Clamp, TextureWrap.Clamp);
                d.NightLmDayBoost = NightLightmapDayBoost;
                // Original: Albedo = COLOR x DETAIL ohne Verstärkung (Unity InteriorFx: _DetailBoost 1). Mit Boost 2 schnitt die
                // Begrenzung auf 1 helle Lightmap-Flächen ab -> Dächer/Wände ohne Detailtextur (flach). Tageshelligkeit kommt
                // danach im Shader (NightLmDayBoost) ohne Begrenzung dazu.
                if (d.NightLm != null && d.DetailMode) d.DetailBoost = 1f;
                // Interieur (Unity InteriorFx.Apply: _NightMode 1, _DetailBoost 1, _Brightness 1): immer die gebackene Nacht-Lightmap
                if (d.NightLm != null && Interior) { d.InteriorLm = true; d.DetailBoost = 1f; d.Brightness = 1f; }
            }
            // TD_Furnitures (Unity InteriorFx.Apply): _Color = baseColor.rgb
            if (Interior && def.ShaderSet == ShaderFurnitures)
            {
                var fbc = Vector4.One; float bias = 0f;
                if (def.Params.TryGetValue(MaterialDef.KeyFromName("baseColor"), out var pbc)) { fbc = pbc; d.Color = new Vector4(fbc.X, fbc.Y, fbc.Z, d.Color.W); }
                if (def.Params.TryGetValue(KeyBiasCoef, out var pb)) bias = pb.X;
                // Spiegelanteil: 1 - baseColor.a; Garagenböden haben fast 1 -> Fresnel-Grundwert biasCoeff (Unity InteriorFx.Apply)
                float mix = System.Math.Clamp(System.Math.Max(1f - fbc.W, bias * 0.8f), 0f, 1f);
                if (mix > 0.001f) mix = System.Math.Max(mix, MinFloorReflection);
                if (PlanarReflections && mix > 0.001f) d.ReflMix = mix;
            }
            d.Cutoff = blend == EdenBlend.Cutout ? System.Math.Clamp(alphaRef, 0.05f, 0.95f) : (foliage ? 0.5f : 0f);
        }
        else
        {
            d.Additive = blend == EdenBlend.Additive;
            d.Detail = detail;
            d.DetailMode = detail != null;
            d.DetailUvLast = def.IsRoad;
            // wie bei opaken Objekten (Unity setzt _DetailTex/_DetailBoost/_DetailGray für beide Shader gleich)
            bool packedT = detail != null && TextureInfo.IsPacked(detail);
            d.DetailBoost = lightmap || def.IsRoad ? 2f : packedT ? 1f : 1.2f;
            d.DetailGray = packedT;
            d.ColorStrength = def.IsRoad ? 0.35f : 1f;
            // Glas/Fenster: Alpha aus der DETAIL-Textur (vitre, glass_*), COLOR ist die Lightmap
            d.AlphaFromDetail = detail != null && detail.Format != GpuTextureFormat.Bc1;
            if (def.IsGround && detail != null)
            {
                d.AlphaFromDetail = true;
                d.DetailGray = true;
                d.DetailBoost = lightmap ? 2f : 1.25f;
            }
            d.Lit = lightmap ? 0f : 1f;
            d.Intensity = blend == EdenBlend.Additive ? GlowIntensity : 1f;
            if (blend == EdenBlend.Additive) d.Lit = 0f;
            if (blend == EdenBlend.Additive && IsNightGlow(def.Name) && !Interior) d.NightOnly = true;
            if (glass)
            {
                // Unity MakeGlass: _Lit 1, _Intensity 1; deckende Textur (DXT1) -> feste Durchsicht 0,3
                d.Lit = 1f; d.Intensity = 1f;
                var at = d.AlphaFromDetail ? detail : color;
                bool opaqueTex = at == null || at.Format == GpuTextureFormat.Bc1;
                if (opaqueTex || d.Color.W > 0.95f) d.Color = new Vector4(d.Color.X, d.Color.Y, d.Color.Z, opaqueTex ? 0.3f : d.Color.W);
            }
        }
        if (foliage) { d.WindStrength = WindStrength; d.FlutterStrength = WindStrength; }
        ApplyOriginalShader(def, d, transparent);
        if (interiorFx && d.Kind == MaterialKind.Object) d.DetailAlpha = true;   // Unity InteriorFx.Apply: _DetailAlpha 1 (Pflanzen: Alpha-Test aus DETAIL)
        if (glass) { d.Lit = 1f; d.VcMul = false; d.Unlit = 0f; }   // Unity MakeGlass: neues ObjectTransparent-Material, beleuchtet

        // TD_NEONDECAL mit NEONGLOW-Ebene: Maske (vor ApplyOriginalShader die COLOR/NEONGLOW-Textur) für die .uva merken
        IGpuTexture? neonMask = def.ShaderSet == ShaderNeonDecal && def.TryGetLayer(SemNeon, out _) ? color : null;
        info = new MaterialInfo
        {
            Def = def,
            Desc = d,
            Blend = blend,
            Lightmap = lightmap,
            Sprite = sprite,
            MainTexture = color?.Name,
            DetailTexture = detail?.Name,
            ShadowTexture = shadow?.Name,
            TintTexture = tint?.Name,
            Packed = detail != null && TextureInfo.IsPacked(detail),
            NeonMask = neonMask,
        };
        return d;
    }

    // ------------------------------------------------------------------ fehlende Texturen nachreichen
    private sealed class PendingMat
    {
        public IGpuMaterial Mat = null!;
        public MaterialDef Def = null!;
        public Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> Get = null!;
        public bool WetGround;   // Material des NETWORK-Modells (Regen/Pfützen)
        public bool Sprite, TintUv, FourUv;
        public List<ulong> Missing = null!;
        public long Since = Environment.TickCount64;
        public bool Logged;
    }
    private static readonly List<PendingMat> _pending = new();

    /// <summary>Materialien, denen noch Texturen fehlen.</summary>
    public static int PendingTextures => _pending.Count;

    /// <summary>Material wurde freigegeben -> nicht mehr nachreichen.</summary>
    public static void Forget(IGpuMaterial m) => _pending.RemoveAll(p => p.Mat == m);

    /// <summary>
    /// Main-Thread, regelmäßig (Streamer): sobald eine fehlende Textur im Index auftaucht, das Material komplett neu entscheiden
    /// (wie beim ersten Bau mit allen Texturen) und ersetzen. <paramref name="get"/> = globaler Index (zusätzlich zur Bank des Materials).
    /// </summary>
    public static int RetryPendingTextures(Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> get)
    {
        if (_pending.Count == 0 || Renderer == null) return 0;
        int fixedN = 0;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            bool any = false;
            foreach (var k in p.Missing)
            {
                IGpuTexture? t = null;
                try { t = get(k, TextureWrap.Repeat, TextureWrap.Repeat); } catch (Exception) { }
                if (t != null) { any = true; break; }
            }
            if (!any)
            {
                // Prüf-Log: nach 15 s noch nicht auflösbar -> Textur liegt in keiner geladenen Bank
                if (!p.Logged && Environment.TickCount64 - p.Since > 15000)
                {
                    p.Logged = true;
                    Paradise.Core.Diagnostics.Log.Warn($"TEXTUR FEHLT: Material '{p.Def.Name}' Shader {p.Def.ShaderSet:x16} Schlüssel " + string.Join(" ", p.Missing.ConvertAll(k => k.ToString("x16"))));
                }
                continue;
            }
            var missing = new List<ulong>();
            MaterialDesc? d;
            MaterialInfo info;
            try { d = BuildObjectDesc(p.Def, (k, u, v) => p.Get(k, u, v) ?? get(k, u, v), p.Sprite, p.TintUv, p.FourUv, p.WetGround, missing, out info); }
            catch (Exception) { continue; }
            if (d == null) { _pending.RemoveAt(i); continue; }
            // Neon-Maske / laufende Textur-Animation: die Hauptebene bleibt die der Animation
            if (_info.TryGetValue(p.Mat, out var old) && old.NeonMask != null && old.Desc.Main == old.NeonMask) d.Main = old.Desc.Main;
            Renderer.UpdateMaterial(p.Mat, d);
            _info.AddOrUpdate(p.Mat, info);
            TextureAnimations.Rebind(p.Mat, d);
            if (missing.Count == 0) _pending.RemoveAt(i); else p.Missing = missing;
            fixedN++;
        }
        return fixedN;
    }

    /// <summary>
    /// Original-Pixelshader der Interieur-/Casino-Sets: td_actors / td_casino_scroll (unbeleuchtet, baseColor),
    /// TD_Gouraud (Interieur: Textur x Vertexfarbe, unbeleuchtet), TD_NEONDECAL (Vertexfarbe, Leuchtmaske).
    /// </summary>
    private static void ApplyOriginalShader(MaterialDef def, MaterialDesc m, bool transparent)
    {
        ulong ss = def.ShaderSet;
        if (ss == ShaderActors || ss == ShaderCasinoScroll)
        {
            if (transparent) { m.Lit = 0f; return; }
            if (def.Params.TryGetValue(MaterialDef.KeyFromName("baseColor"), out var bc)) m.Color = new Vector4(bc.X, bc.Y, bc.Z, 1f);
            m.Unlit = 1f; m.DetailBoost = 1f; m.ColorStrength = 1f; m.DetailGray = false; m.VertexColorStrength = 0f; m.Brightness = 1f;
            return;
        }
        if (ss == ShaderGouraud && Interior)
        {
            if (transparent) { m.Lit = 0f; m.VcMul = true; return; }
            m.Unlit = 1f; m.VertexColorStrength = 1f; m.VcScale = 1f; m.Brightness = 1f; m.DetailMode = false;
            return;
        }
        if (ss == ShaderNeonDecal)
        {
            // Leuchtfarbe steckt in der Vertexfarbe; die NEONGLOW-Ebene ist nur eine (animierte) Maske -> ohne Animation aus
            bool masked = def.TryGetLayer(SemNeon, out _);
            if (transparent)
            {
                m.Lit = 0f; m.VcMul = true; m.DetailMode = false;
                m.Main = null; m.Color = masked ? new Vector4(0, 0, 0, 0) : Vector4.One;   // blackTexture / whiteTexture
                // Original TD_NEONDECAL: Leuchten x rgbExposure_EmissiveChannel x WeatherEmissive – leuchtet auch am Tag
                // (Schriftzüge/Banner wie "Community Racing Center" sind zum Erkennen da), nicht nur nachts
                m.NightOnly = false;
                return;
            }
            m.Main = null; m.Color = masked ? new Vector4(0, 0, 0, 0) : Vector4.One;
            m.Unlit = 1f; m.VertexColorStrength = 1f; m.VcScale = 1f; m.Brightness = 1f; m.DetailMode = false;
        }
    }

    /// <summary>Terrain-Material: Colormap + optional Splat/Shadow-Map + Detailtexturen.</summary>
    public static IGpuMaterial CreateTerrainMaterial(IGpuTexture? colorMap, IGpuTexture? splatMap, IGpuTexture? detail1, IGpuTexture? detail2, float detailTiling, float detailStrength)
    {
        var r = Renderer ?? throw new InvalidOperationException("MaterialFactory.Renderer nicht gesetzt.");
        return r.CreateMaterial(new MaterialDesc
        {
            Kind = MaterialKind.Terrain,
            Name = colorMap != null ? "Terrain_" + colorMap.Name : "Terrain",
            Main = colorMap,
            Splat = splatMap,
            UseSplat = splatMap != null,
            ShadowStrength = ShadowMapStrength,
            Detail1 = detail1,
            Detail2 = detail2,
            DetailTiling = detailTiling,
            DetailStrength = (detail1 != null || detail2 != null) ? detailStrength : 0f,
        });
    }

    public static void Reset()
    {
        _fallback?.Dispose();
        _fallback = null;
        _water?.Dispose(); _water = null; _waterDesc = null;
        _pool?.Dispose(); _pool = null;
        _pending.Clear();
    }
}
