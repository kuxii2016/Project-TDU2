using Paradise.Core.Formats;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Paradise.Core.Render.Original;

/// <summary>
/// Szenenwerte für die Original-Shader (eines Bildes), in der Konvention des Originals: D3D-Matrizen (Zeilenvektor,
/// linkshändige View, Projektion ohne Vulkan-Y-Spiegelung – die übernimmt <see cref="DxsoToGlsl"/>), Lichtfarben im
/// Gamma-Raum wie in den Wetterdaten (keine Umrechnung nach linear).
/// </summary>
public struct OriginalScene
{
    public Matrix4x4 View, Proj, ViewProj;
    /// <summary>Inverse von View/Proj/ViewProj – einmal je Bild berechnet (vorher je Konstante und je VS/PS-Aufruf).</summary>
    public Matrix4x4 InvView, InvProj, InvViewProj;
    public Vector3 CameraPosition, CameraDirection;
    /// <summary>Richtung des Sonnenlichts (von der Sonne weg, wie "sundirection" der Wetterdaten).</summary>
    public Vector3 SunDirection;
    public Vector3 SunDiffuse, SunSpecular, Ambient, FogColor;
    /// <summary>Emissive des Richtungslichts (+0x40, __DirLight_Emissive_0) = sunflare_core_emissive (exe 0xc98817).</summary>
    public Vector3 SunEmissive;
    /// <summary>(fog_start, fog_end, fog_max, 1 / (end - start)).</summary>
    public Vector4 FogConfig;
    public float Time;
    public Vector4 User0, User1, User2, User3, User4, User5, User6, User7;
    public float Width, Height;
    /// <summary>Render-Lage des Spielursprungs (FrameGlobals.Origin, x/z): SECTROFF gilt im verschobenen Raum wie im Original
    /// (Sektor +0x30/+0x34 = Ursprung − Weltverschiebung, exe 0xc522b4 und 0xc52b70).</summary>
    public Vector2 Origin;
    /// <summary>
    /// Autolicht des Originals (FrameGlobals.CarLight, World/CarLighting = exe 0x744990 GS_CarDisplay, Gamma-Raum): Licht-Instanz der
    /// Autos (car+0x79a0) statt der Sonne – Ambient/Diffus/Glanz × car_sun_*_factor, Sättigung, Tunnel. ENVCOEFF der Lacke je Bild
    /// (exe 0x7175a0): TD_CarPaint2 = CarEnv, TD_CarPaint x = CarEnvFactor.
    /// </summary>
    public bool CarOn;
    public Vector3 CarAmbient, CarDiffuse, CarSpecular;
    public Vector4 CarEnv;
    public float CarEnvFactor;
    /// <summary>TD_DashBoard HeadlightsIntensity.x (FrameGlobals.DashHeadlights).</summary>
    public float DashHeadlights;
    /// <summary>TD_Road-Regen (WFX_WetGrd, exe 0xa63440): RainSpecs (PS) und RainRippleSpecs (VS), je Bild aus dem Wetter.</summary>
    public Vector4 RainSpecs, RainRipple;
    /// <summary>spot_exterior_lightmap_day_night.xy: NIGHTLMAP der Spot-Exterior-Gebäude (s309).</summary>
    public Vector2 SpotExteriorDayNight;
}

/// <summary>
/// Füllt die Konstantenregister eines Original-Shaders pro Zeichenaufruf 
/// EdenRenderer::fillConstants (IsFixedConstantName / ClassifyConstant / WriteMatrix). Jede CTAB-Variable wird einmal je
/// Shader klassifiziert; alles ohne festen Namen ist ein Material-Parameter der .2DM (Name ohne "__"-Suffix).
/// Nicht belegt: zweites Licht, Spot-Lichter außerhalb der Avatare, Echtzeit-Schatten (Shadow_*).
///
/// Abgleich mit der exe (s284): Das Original klassifiziert nicht über Namen, sondern über die Kennungen der .sob-Einträge
/// ("matID" = Objekt-/Matrixwerte, "dynID" = Lichtfarben, "symID" = Material-Symbole). Kennungstabelle 0x15570f8 (0x56 Einträge,
/// Größe 64 für Matrizen 0..8 und 0x0e..0x15, sonst 16). Aus allen .sob abgeleitete Zuordnung (eindeutig, re/original_renderer_exe.md):
/// 0 WVP, 1 WorldView, 2 World, 4 InvWorldView, 5 InvWorld, 7 TInvWorldView, 9 CamPos, 0xa CamDir, 0xb Lod_Dist, 0xd Instance_Color,
/// 0xe..0x10 UVA_0..2, 0x18 Material_Specular, 0x19 Material_Emissive, 0x1a AmbLight_Ambient_0, 0x1b RawAmbient_0, 0x1d Ambient_1,
/// 0x26 DirLight_Dir_0, 0x28 Diffuse_0, 0x29 Diffuse_NoInstance_0, 0x2a RawDiffuse_0, 0x2b Specular_0, 0x2c Specular_NoInstance_0,
/// 0x2e DirLight_Emissive_0, 0x2f Dir_1, 0x34 Specular_1, 0x4a Alpha_Ref, 0x4c SpotLight_Pos_0, 0x4e/0x4f/0x52 SpotLight_Amb/Diff/Spec_0.
/// Objektwerte (exe 0xfb3460, VS ab c60 / PS ab c9 wie Var_VS/Var_PS): Lichtrichtungen und Spot-Lage über die inverse Weltmatrix
/// (0xfb1f00 Richtung normiert, 0xfb2a40 Punkt) – wie hier.
/// </summary>
public static class OriginalConstants
{
    public enum Kind : byte
    {
        Material, None,
        WorldViewProj, World, WorldView, InvWorld, InvWorldView, TInvWorldView,
        View, Proj, ViewProj, InvView, InvProj, InvViewProj, Uva, Skin,
        CamPos, CamDir, DirLightDir0, DirLightDir1, DirLightDiffuse0, DirLightRawDiffuse0,
        DirLightSpecular0, Emissive, Ambient0, RawAmbient0, MaterialSpecular, InstanceColor,
        AlphaRef, LodDist, UserVector, Time, FogColor, FogConfig, RenderTargetSize, StereoUvMadd,
        SpotPos0, SpotAmbient0, SpotDiffuse0, SpotSpecular0, DirEmissive0,
    }

    public struct Entry
    {
        public Kind Kind;
        public int Register, Count, Class, Index;
        public ulong Key0, Key1;   // Material-Parameter: Großschreibung / Schreibweise wie im Shader (erste 8 Zeichen, Avatar-Symbole)
        // Schlüssel der .2DM-Parameter: Zeichen i auf Byte i % 8 aufaddiert (NIGHTLMAP -> 0x9E "IGHTLMA", SPECBLINN -> 0xA1 "PECBLIN";
        // in den Banken nachgezählt, wie CarMaterials.ParamKey). Mit den abgeschnittenen Schlüsseln fehlten alle Namen > 8 Zeichen -> 0.
        public ulong PKey0, PKey1;
        public string Name;
        /// <summary>Vorausgewertet: Autodreck-Symbol (wurde je Draw per Stringvergleich geprüft).</summary>
        public bool IsDirtColor;
        /// <summary>Register*4 bzw. Count*4 – einmal je Shader statt je Draw neu multiplizieren.</summary>
        public int Offset, Floats;
    }

    /// <summary>
    /// Materialwerte eines Original-Shaders, die nicht von Weltmatrix/Szene abhängen: einmal je (<see cref="OriginalMaterial"/>,
    /// Entry-Array) auflösen statt bei jedem Draw erneut zwei Dictionary-Lookups und Sonderfallprüfungen zu machen. Szenenwerte
    /// (CarEnv, Regen, NIGHTLMAP, Avatarlicht, Dirt, Gloss …) bleiben bewusst im langsamen Pfad.
    /// </summary>
    public sealed class CachedMaterialConstants
    {
        public readonly Vector4[] Values;
        public readonly bool[] Pure;
        /// <summary>Statische Materialwerte auch für dynamische Einträge – ersetzt die Dictionary-Lookups je Draw.</summary>
        public readonly Vector4[] MaterialValues;
        /// <summary>Nur Def-Werte (HEADLIGHTSINTENSITY liest bewusst nicht das Over-Material).</summary>
        public readonly Vector4[] DefValues;
        /// <summary>Ob der Wert tatsächlich in over/def stand (für die Diagnose fehlender Parameter).</summary>
        public readonly bool[] MaterialKnown;
        public CachedMaterialConstants(Vector4[] values, bool[] pure, Vector4[] materialValues, Vector4[] defValues, bool[] materialKnown)
        { Values = values; Pure = pure; MaterialValues = materialValues; DefValues = defValues; MaterialKnown = materialKnown; }
    }

    /// <summary>VS- und PS-Cache eines Programms für dasselbe Material (ein Dictionary-Lookup statt zwei je Draw).</summary>
    internal sealed class CachedProgramConstants
    {
        public readonly CachedMaterialConstants Vs, Ps;
        public CachedProgramConstants(CachedMaterialConstants vs, CachedMaterialConstants ps) { Vs = vs; Ps = ps; }
    }

    static readonly ConditionalWeakTable<DxsoShader, Entry[]> s_cache = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_missing = new();

    /// <summary>Material-Parameter, die ein Shader liest, das Material aber nicht hat (DevMenu > Rendering, Diagnose).</summary>
    public static string Missing => s_missing.IsEmpty ? "" : string.Join(", ", s_missing.Keys.OrderBy(k => k).Take(12)) + (s_missing.Count > 12 ? " ..." : "");
    static void NoteMissing(string name) { if (s_missing.Count < 256) s_missing.TryAdd(name, 0); }

    static readonly Dictionary<string, Kind> s_names = new()
    {
        ["__World_View_Proj__float4x4"] = Kind.WorldViewProj, ["__World__float4x4"] = Kind.World,
        ["LastWorld__float4x4"] = Kind.World, ["__World_View__float4x4"] = Kind.WorldView,
        ["__Inv_World__float4x4"] = Kind.InvWorld, ["__Inv_World_View__float4x4"] = Kind.InvWorldView,
        ["__T_Inv_World_View__float4x4"] = Kind.TInvWorldView, ["View"] = Kind.View, ["LastView"] = Kind.View,
        ["Proj"] = Kind.Proj, ["ViewProj"] = Kind.ViewProj, ["InvView"] = Kind.InvView, ["InvProj"] = Kind.InvProj,
        ["InvViewProj"] = Kind.InvViewProj, ["__Skin_Matrix__array96"] = Kind.Skin, ["__CamPos_ObjSpace"] = Kind.CamPos,
        ["__CamDir_ObjSpace"] = Kind.CamDir, ["__DirLight_Dir_0"] = Kind.DirLightDir0, ["__DirLight_Dir_1"] = Kind.DirLightDir1,
        ["__DirLight_Diffuse_0"] = Kind.DirLightDiffuse0, ["__DirLight_Diffuse_NoInstance_0"] = Kind.DirLightDiffuse0,
        ["__DirLight_RawDiffuse_0"] = Kind.DirLightRawDiffuse0, ["__DirLight_Specular_0"] = Kind.DirLightSpecular0,
        ["__DirLight_Specular_NoInstance_0"] = Kind.DirLightSpecular0, ["__DirLight_Emissive_0"] = Kind.DirEmissive0,
        ["__Material_Emissive"] = Kind.Emissive, ["__AmbLight_Ambient_0"] = Kind.Ambient0,
        ["__AmbLight_RawAmbient_0"] = Kind.RawAmbient0, ["__Material_Specular"] = Kind.MaterialSpecular,
        ["__Instance_Color"] = Kind.InstanceColor, ["__Alpha_Ref"] = Kind.AlphaRef, ["__Lod_Dist"] = Kind.LodDist,
        ["Time_"] = Kind.Time, ["FogColor"] = Kind.FogColor, ["FogConfig"] = Kind.FogConfig,
        ["RenderTargetSize"] = Kind.RenderTargetSize, ["StereoUvMadd"] = Kind.StereoUvMadd,
        // Kameralicht der Avatare (Instanz-Spotlicht +0x2ab0, Ids 0x4c/0x4e/0x4f/0x52)
        ["__SpotLight_Pos_0"] = Kind.SpotPos0, ["__SpotLight_Ambient_0"] = Kind.SpotAmbient0,
        ["__SpotLight_Diffuse_0"] = Kind.SpotDiffuse0, ["__SpotLight_Specular_0"] = Kind.SpotSpecular0,
    };

    // Material-Symbole, die das Spiel bei Avatar-Materialien überschreibt (exe 0x67f31b/0x67f525: Schlüssel = erste 8 Zeichen)
    static readonly ulong s_kCarInteg = Bin.KeyFromString("CARINTEG"), s_kAtteFact = Bin.KeyFromString("ATTEFACT"),
                          s_kShadowAtt = Bin.KeyFromString("SHADOWAT"), s_kGlossFct = Bin.KeyFromString("GLOSSFCT"),
                          s_kSectrOff = Bin.KeyFromString("SECTROFF"),
                          s_kEnvCoeff = Bin.KeyFromString("ENVCOEFF");
    const ulong SetCarPaint = 0x183157b81c6c7357UL, SetCarPaint2 = 0x183157b81c6c5377UL;
    const ulong SetDashBoard = 0xcaca000000000010UL, SetDashBoard2 = 0x7dad16b47eb51337UL;
    static readonly ulong s_kHeadlightsInt = FoldKey("HEADLIGHTSINTENSITY");
    // WFX_WetGrd (exe 0xa634a9/0xa634ba): Symbole RainSpecs (0x4e4941a5'43455053) und RainRippleSpecs (0x9e9c869e'50a38c97) im Satz TD_Road
    static readonly ulong s_kRainSpecs = FoldKey("RAINSPECS"), s_kRainRipple = FoldKey("RAINRIPPLESPECS");
    // Spot-Exteriors (s309): exe 0xc60bb0 sammelt NIGHTLMAP (0x4847499e'414d4c54) aller Materialien mit Satz-GUID TD_Building
    // (0x183157b8'1c6cff57) und Schalter +0x335 = S_USERTYPE6 (Spec[37]) == 1; 0xc60dc0 schreibt je Bild (x, y, 0, 0) mit
    // (x, y) = SPOT_EXTERIOR_LIGHTMAP_DAY_NIGHT (0xc61320, Schlüssel 0x3e4b4320'354b3b3b)
    static readonly ulong s_kNightLmap = FoldKey("NIGHTLMAP");
    const ulong SetBuilding = 0x183157b81c6cff57UL;
    const int SpecUserType6 = 37;
    static readonly string[] s_fixedPrefixes = { "__UVA_", "__SpotLight_", "__AmbLight_", "__DirLight_", "Shadow_", "UserVector" };

    static Kind Classify(string name)
    {
        if (s_names.TryGetValue(name, out var k)) return k;
        bool fixedName = false;
        foreach (var p in s_fixedPrefixes) if (name.StartsWith(p, StringComparison.Ordinal)) { fixedName = true; break; }
        if (!fixedName) return Kind.Material;
        if (name.StartsWith("__UVA_", StringComparison.Ordinal)) return Kind.Uva;
        if (name.StartsWith("UserVector", StringComparison.Ordinal)) return Kind.UserVector;
        return Kind.None;
    }

    static ulong FoldKey(string s)
    {
        ulong k = 0;
        for (int i = 0; i < s.Length; i++)
        {
            int sh = 8 * (i % 8);
            byte cur = (byte)(k >> sh);
            k = (k & ~(0xFFUL << sh)) | ((ulong)(byte)(cur + (byte)s[i]) << sh);
        }
        return k;
    }

    static string StripSuffix(string name)
    {
        int p = name.IndexOf("__", StringComparison.Ordinal);
        return p <= 0 ? name : name.Substring(0, p);
    }

    /// <summary>Klassifizierte Float-Konstanten eines Shaders (einmal je Shader berechnet).</summary>
    public static Entry[] EntriesOf(DxsoShader s)
    {
        if (s_cache.TryGetValue(s, out var e)) return e;
        var list = new List<Entry>();
        foreach (var c in s.Constants)
        {
            if (c.RegisterSet != 2) continue;
            var en = new Entry { Kind = Classify(c.Name), Register = c.Register, Count = c.Count, Class = c.Class, Name = c.Name,
                                 IsDirtColor = c.Name.StartsWith("DirtColor", StringComparison.Ordinal),
                                 Offset = c.Register * 4, Floats = c.Count * 4 };
            if (en.Kind == Kind.Material)
            {
                string b = StripSuffix(c.Name);
                en.Key0 = Bin.KeyFromString(b.ToUpperInvariant());
                en.Key1 = Bin.KeyFromString(b);
                en.PKey0 = FoldKey(b.ToUpperInvariant());
                en.PKey1 = FoldKey(b);
            }
            else if (en.Kind == Kind.Uva) en.Index = c.Name.Length > 6 ? c.Name[6] - '0' : 0;
            else if (en.Kind == Kind.UserVector) en.Index = c.Name.Length > 10 ? c.Name[10] - '0' : 0;
            list.Add(en);
        }
        e = list.ToArray();
        s_cache.AddOrUpdate(s, e);
        return e;
    }

    /// <summary>D3DXPC_MATRIX_ROWS (2): Register k = Zeile k; MATRIX_COLUMNS (3): Register k = Spalte k (Zeilenvektor-Matrix).</summary>
    static unsafe void WriteMatrix(float* o, int regs, int cls, Matrix4x4 m)
    {
        float* p = (float*)&m;
        for (int k = 0; k < regs && k < 4; k++)
            for (int i = 0; i < 4; i++)
                o[k * 4 + i] = cls == 2 ? p[k * 4 + i] : p[i * 4 + k];
    }

    static unsafe void W(float* o, Vector4 v) { o[0] = v.X; o[1] = v.Y; o[2] = v.Z; o[3] = v.W; }
    static unsafe void W(float* o, Vector3 v, float w) { o[0] = v.X; o[1] = v.Y; o[2] = v.Z; o[3] = w; }
    static Vector3 Mul(Vector3 a, Vector4 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    static Vector3 Dir(in Matrix4x4 inv, Vector3 v) { var r = Vector3.TransformNormal(v, inv); float l = r.Length(); return l > 1e-12f ? r / l : r; }

    /// <summary>Übersichtliche Sonderfälle, die pro Draw von Szene/Material abhängen. Alles andere lässt sich je Shader+Material cachen.</summary>
    static bool IsDynamic(Entry e, MaterialDef def, OriginalMaterial? mat)
        => e.IsDirtColor
        || (mat != null && mat.Avatar != null && (e.Key0 == s_kCarInteg || e.Key0 == s_kAtteFact || e.Key0 == s_kShadowAtt))
        || (mat != null && mat.GlossFct.HasValue && e.Key0 == s_kGlossFct)
        || (mat != null && mat.Car && e.Key0 == s_kEnvCoeff && (def.ShaderSet == SetCarPaint2 || def.ShaderSet == SetCarPaint))
        || (e.PKey0 == s_kHeadlightsInt && (def.ShaderSet == SetDashBoard || def.ShaderSet == SetDashBoard2))
        || (mat != null && mat.SpotExterior && e.PKey0 == s_kNightLmap && def.ShaderSet == SetBuilding
            && def.Spec != null && def.Spec.Length > SpecUserType6 && def.Spec[SpecUserType6] == 1)
        || (def.ShaderSet == OriginalShaderSystem.SetRoad && (e.PKey0 == s_kRainSpecs || e.PKey0 == s_kRainRipple))
        || e.Key0 == s_kSectrOff;

    static Vector4 ResolveStaticEntry(Entry e, MaterialDef def, MaterialDef? over)
    {
        if ((over != null && (over.Params.TryGetValue(e.PKey0, out var pv) || over.Params.TryGetValue(e.PKey1, out pv)))
            || def.Params.TryGetValue(e.PKey0, out pv) || def.Params.TryGetValue(e.PKey1, out pv)) return pv;
        return Vector4.Zero;   // frischer D3D9-Konstantenpuffer
    }

    static readonly CachedMaterialConstants s_noCache = new(Array.Empty<Vector4>(), Array.Empty<bool>(), Array.Empty<Vector4>(), Array.Empty<Vector4>(), Array.Empty<bool>());
    static readonly CachedProgramConstants s_noProgramCache = new(s_noCache, s_noCache);

    /// <summary>Einmal je OriginalMaterial+Entry-Array: statische Materialwerte, dynamische Einträge bleiben unscharf.</summary>
    static CachedMaterialConstants BuildCache(Entry[] entries, MaterialDef def, MaterialDef? over, OriginalMaterial? mat)
    {
        if (mat == null) return s_noCache;
        var vals = new Vector4[entries.Length];
        var pure = new bool[entries.Length];
        var mats = new Vector4[entries.Length];
        var defVals = new Vector4[entries.Length];
        var known = new bool[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Kind == Kind.Material)
            {
                known[i] = (over != null && (over.Params.ContainsKey(e.PKey0) || over.Params.ContainsKey(e.PKey1)))
                           || def.Params.ContainsKey(e.PKey0) || def.Params.ContainsKey(e.PKey1);
                if (known[i]) mats[i] = ResolveStaticEntry(e, def, over);
                if (def.Params.TryGetValue(e.PKey0, out var dv) || def.Params.TryGetValue(e.PKey1, out dv)) defVals[i] = dv;
            }
            pure[i] = e.Kind == Kind.Material && e.Count == 1 && !IsDynamic(e, def, mat);
            if (pure[i]) vals[i] = mats[i];
        }
        return new CachedMaterialConstants(vals, pure, mats, defVals, known);
    }

    /// <summary>Einmal je OriginalMaterial+Programm: VS und PS gemeinsam, inklusive der Material-Fallbackwerte.</summary>
    internal static CachedProgramConstants GetProgramCached(OriginalProgram prog, MaterialDef def, MaterialDef? over, OriginalMaterial? mat)
    {
        if (mat == null || prog.VsEntries == null || prog.PsEntries == null) return s_noProgramCache;
        if (mat.ProgramConstants.TryGetValue(prog, out var cached)) return cached;
        cached = new CachedProgramConstants(
            BuildCache(prog.VsEntries, def, over, mat),
            BuildCache(prog.PsEntries, def, over, mat));
        mat.ProgramConstants.Add(prog, cached);
        return cached;
    }

    /// <summary>
    /// Materialfarben der Lichtkonstanten wie die exe (dyn-Block 0xfc0120, Kennung 0x1a -> 0xfbf150, 0x28 -> 0xfbe510, 0x2b -> 0xfbe790):
    /// Licht.rgb x Material.rgb, w = Material.w. Laufzeit-Material +0xb0 Ambient, +0xc0 Diffuse, +0xd0 Specular (auch Material_Specular
    /// 0xfbd100), +0xe0 Emissive (0xfbd170); Laufzeit = .2DM MAT. − 0x10 (GUID +0x300 / Datei +0x310). Damit ist Datei +0xC0 = Ambient und
    /// +0xD0 = Diffuse – in <see cref="MaterialDef"/> heißen die beiden Felder umgekehrt (MaterialDef.Diffuse = +0xC0), daher hier getauscht.
    /// </summary>
    static Vector4 MatAmbient(MaterialDef d) => d.Diffuse;   // Datei MAT.+0xC0
    static Vector4 MatDiffuse(MaterialDef d) => d.Ambient;   // Datei MAT.+0xD0

    /// <summary>
    /// CARINTEG / ATTEFACT / SHADOWATTENUATION wie das Spiel je Bild (exe 0x67f360ff., Spec §2.6): CARINTEG = (p1, p2, Schatten, 1),
    /// ATTEFACT = (1 − p3, 1 − p4, 1 − p5, 0) – envmap bewusst aus (siehe <see cref="AvatarSymbol"/>), sonst chromige Kleidung;
    /// auch TD_Clothe2Bike (0x67f4fc, eigener Setter); TD_Pedestrian SHADOWATTENUATION =
    /// (s, s, s, 1); TD_Avatar: Werte der .2DM (nicht in der Liste).
    /// Außerhalb des Autos ist (p1, p2) = (0, 0): D3D9 rechnet h = y·rcp(0) -> k = 1 + inf·0 = 1; SPIR-V gäbe NaN, daher (0, 1)
    /// (ergibt k = 1 genau, Spec §3). TD_Hair: CARINTEG.w = 10 (exe 0x67f2d7, im Auto Knochenhöhe – offen).
    /// </summary>
    static unsafe bool AvatarSymbol(float* o, ulong key, Paradise.Core.Avatars.AvatarLightState av, OriginalMaterial.AvatarFamily fam)
    {
        var p = av.Factors;
        if (fam is OriginalMaterial.AvatarFamily.Standard or OriginalMaterial.AvatarFamily.Hair)
        {
            if (key == s_kCarInteg)
            {
                float x = p[1], y = p[2];
                if (x == y) { x = 0f; y = 1f; }
                W(o, new Vector4(x, y, av.Shadow, fam == OriginalMaterial.AvatarFamily.Hair ? 10f : 1f));
                return true;
            }
            // ATTEFACT.w = envmap. Das Original mischt damit TD_Accessories2/TD_Clothe2Bike auf den Umgebungswürfel; mit dem
            // verfügbaren Probe-Würfel wirken Kleider/Accessoires chromig, während der eigene Avatar-Shader ganz ohne envmap
            // auskam. Deshalb hier 0 (nur Avatare; Auto-Lacke nutzen ENVCOEFF unverändert).
            if (key == s_kAtteFact) { W(o, new Vector4(1f - p[3], 1f - p[4], 1f - p[5], 0f)); return true; }
        }
        else if (fam == OriginalMaterial.AvatarFamily.Pedestrian && key == s_kShadowAtt)
        {
            W(o, new Vector4(av.Shadow, av.Shadow, av.Shadow, 1f));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Werte, die innerhalb eines Draws für VS und PS gleich sind und vorher in <see cref="Fill"/> je Konstante neu berechnet
    /// wurden (Matrixprodukte, normalisierte Richtungen mit sqrt, Licht x Material). Einmal je Draw statt mehrfach je Block.
    /// </summary>
    public struct OriginalDrawCache
    {
        public bool CarOn;
        public Matrix4x4 WorldViewProj, WorldView, TInvWorldView;
        public Vector3 CamPos, CamDir, DirLightDir0, DirLightDir1, SpotPos;
        public Vector4 Ambient, RawAmbient, Diffuse, RawDiffuse, Specular, DirEmissive;
        public Vector4 SpotAmbient, SpotDiffuse, SpotSpecular;

        /// <summary>Bitmaske der von VS+PS tatsächlich gelesenen Konstanten (Kind &lt; 64).</summary>
        public static ulong MaskOf(Entry[] vs, Entry[] ps)
        {
            ulong m = 0;
            for (int i = 0; i < vs.Length; i++) m |= 1UL << (int)vs[i].Kind;
            for (int i = 0; i < ps.Length; i++) m |= 1UL << (int)ps[i].Kind;
            return m;
        }

        static bool Has(ulong m, Kind k) => (m & (1UL << (int)k)) != 0;

        /// <summary>Inverse Weltmatrix nur berechnen, wenn ein Shader sie oder eines der objektraum-Lichtwerte liest.</summary>
        public static bool NeedsInverse(ulong m)
            => Has(m, Kind.InvWorld) || Has(m, Kind.InvWorldView) || Has(m, Kind.TInvWorldView)
            || Has(m, Kind.CamPos) || Has(m, Kind.CamDir) || Has(m, Kind.DirLightDir0)
            || Has(m, Kind.DirLightDir1) || Has(m, Kind.SpotPos0);

        public static bool NeedsInvWorldView(ulong m) => Has(m, Kind.InvWorldView) || Has(m, Kind.TInvWorldView);

        /// <summary>
        /// Nur die Werte berechnen, die VS/PS dieses Draws wirklich lesen. Vorher wurden hier bei jedem Draw drei Matrixprodukte,
        /// eine Transposition, zwei Normalisierungen (sqrt) und zwölf Licht-Farbmultiplikationen ausgeführt – auch für Shaders,
        /// die nur WVP und Materialfarben brauchen.
        /// </summary>
        public static OriginalDrawCache Create(in Matrix4x4 world, in OriginalScene sc, in Matrix4x4 inv, in Matrix4x4 invWorldView,
                                               MaterialDef def, Paradise.Core.Avatars.AvatarLightState? av, OriginalMaterial? mat, ulong mask)
        {
            bool avOn = av != null && av.Valid;
            bool carOn = !avOn && mat != null && mat.Car && sc.CarOn;
            var c = new OriginalDrawCache { CarOn = carOn };
            if (Has(mask, Kind.WorldViewProj)) c.WorldViewProj = world * sc.ViewProj;
            if (Has(mask, Kind.WorldView)) c.WorldView = world * sc.View;
            if (Has(mask, Kind.TInvWorldView)) c.TInvWorldView = Matrix4x4.Transpose(invWorldView);
            if (Has(mask, Kind.CamPos)) c.CamPos = Vector3.Transform(sc.CameraPosition, inv);
            if (Has(mask, Kind.CamDir)) c.CamDir = Dir(inv, sc.CameraDirection);
            if (Has(mask, Kind.DirLightDir0)) c.DirLightDir0 = Dir(inv, avOn ? av!.Direction : sc.SunDirection);
            if (Has(mask, Kind.DirLightDir1)) c.DirLightDir1 = Vector3.TransformNormal(new Vector3(0f, -1f, 0f), inv);
            if (Has(mask, Kind.SpotPos0) && avOn) c.SpotPos = Vector3.Transform(av!.SpotPosition(sc.CameraDirection), inv);
            if (Has(mask, Kind.Ambient0) || Has(mask, Kind.RawAmbient0) || Has(mask, Kind.SpotAmbient0))
            {
                Vector3 la = carOn ? sc.CarAmbient : sc.Ambient;
                var ma = MatAmbient(def);
                if (Has(mask, Kind.Ambient0)) c.Ambient = new Vector4(Mul(avOn ? av!.Ambient : la, ma), ma.W);
                if (Has(mask, Kind.RawAmbient0)) c.RawAmbient = new Vector4(avOn ? av!.Ambient : la, 1f);
                if (Has(mask, Kind.SpotAmbient0)) c.SpotAmbient = new Vector4(avOn ? Mul(av!.SpotAmbient, ma) : Vector3.Zero, ma.W);
            }
            if (Has(mask, Kind.DirLightDiffuse0) || Has(mask, Kind.DirLightRawDiffuse0) || Has(mask, Kind.SpotDiffuse0))
            {
                Vector3 ld = carOn ? sc.CarDiffuse : sc.SunDiffuse;
                var md = MatDiffuse(def);
                if (Has(mask, Kind.DirLightDiffuse0)) c.Diffuse = new Vector4(Mul(avOn ? av!.Diffuse : ld, md), md.W);
                if (Has(mask, Kind.DirLightRawDiffuse0)) c.RawDiffuse = new Vector4(avOn ? av!.Diffuse : ld, 1f);
                if (Has(mask, Kind.SpotDiffuse0)) c.SpotDiffuse = new Vector4(avOn ? Mul(av!.SpotDiffuse, md) : Vector3.Zero, md.W);
            }
            if (Has(mask, Kind.DirLightSpecular0) || Has(mask, Kind.SpotSpecular0))
            {
                Vector3 ls = carOn ? sc.CarSpecular : sc.SunSpecular;
                var ms = def.Specular;
                if (Has(mask, Kind.DirLightSpecular0)) c.Specular = new Vector4(Mul(avOn ? av!.Specular : ls, ms), ms.W);
                if (Has(mask, Kind.SpotSpecular0)) c.SpotSpecular = new Vector4(avOn ? Mul(av!.SpotSpecular, ms) : Vector3.Zero, ms.W);
            }
            if (Has(mask, Kind.DirEmissive0)) c.DirEmissive = new Vector4(Mul(sc.SunEmissive, def.Emissive), def.Emissive.W);
            return c;
        }
    }

    /// <summary>Register eines Shaders füllen (<paramref name="regs"/> = Anzahl vec4 des Konstantenblocks).</summary>
    public static unsafe void Fill(float* dst, int regs, Entry[] entries, MaterialDef def, in Matrix4x4 world, in OriginalScene sc,
                                   in OriginalDrawCache cache, in Matrix4x4 inv, in Matrix4x4 invWorldView,
                                   CachedMaterialConstants cached,
                                   Paradise.Core.Avatars.AvatarLightState? av = null, OriginalMaterial.AvatarFamily fam = OriginalMaterial.AvatarFamily.None,
                                   Vector4? glossFct = null, OriginalMaterial? mat = null)
    {
        bool avOn = av != null && av.Valid;
        bool carMat = mat != null && mat.Car;
        var mats = cached.MaterialValues;
        var defVals = cached.DefValues;
        for (int idx = 0; idx < entries.Length; idx++)
        {
            ref readonly var e = ref entries[idx];
            if (e.Register + e.Count > regs) continue;
            float* o = dst + e.Offset;
            if (cached.Pure[idx] && e.Count == 1) { W(o, cached.Values[idx]); continue; }
            switch (e.Kind)
            {
                case Kind.Material:
                    // Autodreck (TD_CarPaint2/_NM, TD_CarMat_NM VS): DirtColor0..3 = Dreckfarbe rgb + Dreck-Alpha je Viertel, der VS
                    // mischt sie bilinear über TEXCOORD0 zu COLOR1 (s318). Hier ein Wert für alle vier Viertel (Vehicles/CarDirt).
                    if (carMat && e.IsDirtColor) { W(o, mat!.Dirt); break; }
                    if (avOn && AvatarSymbol(o, e.Key0, av!, fam)) break;
                    if (glossFct.HasValue && e.Key0 == s_kGlossFct) { W(o, glossFct.Value); break; }   // Autolichter (Lichtgläser)
                    if (cache.CarOn && e.Key0 == s_kEnvCoeff && (def.ShaderSet == SetCarPaint2 || def.ShaderSet == SetCarPaint))
                    {
                        // exe 0x7175a0: TD_CarPaint2 ganz aus dem Autolicht, TD_CarPaint nur x (y bleibt Materialwert)
                        if (def.ShaderSet == SetCarPaint2) { W(o, sc.CarEnv); break; }
                        Vector4 ev = (uint)idx < (uint)mats.Length ? mats[idx] : Vector4.Zero;
                        ev.X = sc.CarEnvFactor;
                        W(o, ev);
                        break;
                    }
                    // Instrumente (exe 0x71a6e7: Sätze caca000000000010 / 7dad16b47eb51337, Symbol HEADLIGHTSINTENSITY; 0x717ee0 schreibt je
                    // Bild nur .x = 0x70eaf0 = car_headlights_intensity aus dem Wetter, y..w bleiben Materialwerte)
                    if (e.PKey0 == s_kHeadlightsInt && (def.ShaderSet == SetDashBoard || def.ShaderSet == SetDashBoard2))
                    {
                        Vector4 hv = (uint)idx < (uint)defVals.Length ? defVals[idx] : Vector4.Zero;
                        hv.X = sc.DashHeadlights;
                        W(o, hv);
                        break;
                    }
                    if (mat != null && mat.SpotExterior && e.PKey0 == s_kNightLmap && def.ShaderSet == SetBuilding
                        && def.Spec != null && def.Spec.Length > SpecUserType6 && def.Spec[SpecUserType6] == 1)
                    {
                        W(o, new Vector4(sc.SpotExteriorDayNight.X, sc.SpotExteriorDayNight.Y, 0f, 0f));
                        break;
                    }
                    if (def.ShaderSet == OriginalShaderSystem.SetRoad && (e.PKey0 == s_kRainSpecs || e.PKey0 == s_kRainRipple))
                    {
                        W(o, e.PKey0 == s_kRainSpecs ? sc.RainSpecs : sc.RainRipple);
                        break;
                    }
                    // Fallback (over zuerst, dann def) einmal je Programm+Material aufgelöst – keine Dictionary-Suche je Draw.
                    if ((uint)idx < (uint)cached.MaterialKnown.Length && cached.MaterialKnown[idx])
                    {
                        // SECTROFF (World.SectorMaterials, Spielkoordinaten) in den Render-Raum der Weltmatrix.
                        Vector4 mv = mats[idx];
                        if (e.Key0 == s_kSectrOff) mv += new Vector4(sc.Origin.X, 0f, sc.Origin.Y, 0f);
                        W(o, mv);
                    }
                    else
                    {
                        new Span<float>(o, e.Floats).Clear();
                        NoteMissing(e.Name);
                    }
                    break;
                case Kind.WorldViewProj: WriteMatrix(o, e.Count, e.Class, cache.WorldViewProj); break;
                case Kind.World: WriteMatrix(o, e.Count, e.Class, world); break;
                case Kind.WorldView: WriteMatrix(o, e.Count, e.Class, cache.WorldView); break;
                case Kind.InvWorld: WriteMatrix(o, e.Count, e.Class, inv); break;
                case Kind.InvWorldView:
                case Kind.TInvWorldView:
                    WriteMatrix(o, e.Count, e.Class, e.Kind == Kind.TInvWorldView ? cache.TInvWorldView : invWorldView);
                    break;
                case Kind.View: WriteMatrix(o, e.Count, e.Class, sc.View); break;
                case Kind.Proj: WriteMatrix(o, e.Count, e.Class, sc.Proj); break;
                case Kind.ViewProj: WriteMatrix(o, e.Count, e.Class, sc.ViewProj); break;
                case Kind.InvView: WriteMatrix(o, e.Count, e.Class, sc.InvView); break;
                case Kind.InvProj: WriteMatrix(o, e.Count, e.Class, sc.InvProj); break;
                case Kind.InvViewProj: WriteMatrix(o, e.Count, e.Class, sc.InvViewProj); break;
                case Kind.Uva:
                    {
                        // Textur-Matrix: VS uv' = (u, v, 1) · Zeile 0/1 (.xyw), z. B. TD_Building dp3 o1.x, r0, c65.xyww
                        var st = mat == null ? new Vector4(1, 1, 0, 0) : e.Index == 0 ? mat.MainST : e.Index == 1 ? mat.DetailST : new Vector4(1, 1, 0, 0);
                        // Alle __UVA_n der .sob sind D3DXPC_MATRIX_COLUMNS (Klasse 3, Scan 24.11.: TD_Building, TD_Gouraud, td_pubneon,
                        // TD_Water …): Register k = Spalte k -> u' = u·M11 + v·M21 + M41, v' = u·M12 + v·M22 + M42 (Zeilenvektor-Matrix
                        // wie D3D, Versatz in Zeile 4). Vorher stand der Versatz in M14/M24 -> ging verloren, Animationen liefen nicht.
                        var um = Matrix4x4.Identity;
                        um.M11 = st.X; um.M22 = st.Y;
                        if (e.Class == 2) { um.M14 = st.Z; um.M24 = st.W; }
                        else { um.M41 = st.Z; um.M42 = st.W; }
                        WriteMatrix(o, e.Count, e.Class, um);
                        break;
                    }
                case Kind.Skin:
                    for (int b = 0; b < e.Count / 3; b++) WriteMatrix(o + b * 12, 3, 3, Matrix4x4.Identity);
                    break;
                case Kind.CamPos: W(o, cache.CamPos, 1f); break;
                case Kind.CamDir: W(o, cache.CamDir, 0f); break;
                case Kind.DirLightDir0: W(o, cache.DirLightDir0, 0f); break;
                case Kind.DirLightDir1: W(o, cache.DirLightDir1, 0f); break;
                case Kind.DirLightDiffuse0: W(o, cache.Diffuse); break;
                case Kind.DirLightRawDiffuse0: W(o, cache.RawDiffuse); break;
                case Kind.DirLightSpecular0: W(o, cache.Specular); break;
                case Kind.Emissive: W(o, def.Emissive); break;   // Material_Emissive 0xfbd170: Datei +0xF0 unverändert
                // __DirLight_Emissive_0 (Kennung 0x2e, exe 0xfbf600): Emissive des Richtungslichts (+0x40) x Material-Emissive, w = Material.w
                case Kind.DirEmissive0: W(o, cache.DirEmissive); break;
                case Kind.Ambient0: W(o, cache.Ambient); break;
                case Kind.RawAmbient0: W(o, cache.RawAmbient); break;
                // Kameralicht (nur Avatare; sonst aus = 0 wie ein abgeschaltetes Spotlicht). Materialfarben wie exe 0xfbd9b0/0xfbd450/0xfbd6e0
                case Kind.SpotPos0: W(o, cache.SpotPos, 1f); break;
                case Kind.SpotAmbient0: W(o, cache.SpotAmbient); break;
                case Kind.SpotDiffuse0: W(o, cache.SpotDiffuse); break;
                case Kind.SpotSpecular0: W(o, cache.SpotSpecular); break;
                case Kind.MaterialSpecular: W(o, def.Specular); break;
                case Kind.InstanceColor: W(o, Vector4.One); break;
                case Kind.AlphaRef: { float r = def.Flags[7] / 255f; W(o, new Vector4(r)); break; }
                case Kind.LodDist: W(o, new Vector4(0, 1, 0, 1)); break;
                case Kind.UserVector:
                    W(o, e.Index switch { 0 => sc.User0, 1 => sc.User1, 2 => sc.User2, 3 => sc.User3, 4 => sc.User4, 5 => sc.User5, 6 => sc.User6, 7 => sc.User7, _ => Vector4.Zero });
                    break;
                case Kind.Time: W(o, new Vector4(sc.Time, sc.Time * 0.5f, sc.Time * 0.25f, 1f)); break;
                case Kind.FogColor: W(o, sc.FogColor, 1f); break;
                case Kind.FogConfig: W(o, sc.FogConfig); break;
                case Kind.RenderTargetSize: W(o, new Vector4(1f / System.Math.Max(sc.Width, 1f), 1f / System.Math.Max(sc.Height, 1f), sc.Width, sc.Height)); break;
                case Kind.StereoUvMadd: W(o, new Vector4(1, 1, 0, 0)); break;
                default: new Span<float>(o, e.Floats).Clear(); break;   // zweites Licht, Spot-Lichter, Echtzeit-Schatten: abgeschaltet -> 0
            }
        }
    }
}