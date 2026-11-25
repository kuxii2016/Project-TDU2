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
            var en = new Entry { Kind = Classify(c.Name), Register = c.Register, Count = c.Count, Class = c.Class, Name = c.Name };
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
    /// ATTEFACT = (1 − p3, 1 − p4, 1 − p5, Envmap) – auch TD_Clothe2Bike (0x67f4fc, eigener Setter); TD_Pedestrian SHADOWATTENUATION =
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
            if (key == s_kAtteFact) { W(o, new Vector4(1f - p[3], 1f - p[4], 1f - p[5], av.Envmap)); return true; }
        }
        else if (fam == OriginalMaterial.AvatarFamily.Pedestrian && key == s_kShadowAtt)
        {
            W(o, new Vector4(av.Shadow, av.Shadow, av.Shadow, 1f));
            return true;
        }
        return false;
    }

    /// <summary>Register eines Shaders füllen (<paramref name="regs"/> = Anzahl vec4 des Konstantenblocks).</summary>
    public static unsafe void Fill(float* dst, int regs, Entry[] entries, MaterialDef def, in Matrix4x4 world, in OriginalScene sc, MaterialDef? over = null,
                                   Paradise.Core.Avatars.AvatarLightState? av = null, OriginalMaterial.AvatarFamily fam = OriginalMaterial.AvatarFamily.None,
                                   Vector4? glossFct = null, OriginalMaterial? mat = null)
    {
        bool avOn = av != null && av.Valid;
        // Auto-Materialien (CarModel: OriginalMaterial.Car) bekommen das Autolicht des Originals statt der Sonne
        bool carOn = !avOn && mat != null && mat.Car && sc.CarOn;
        Vector3 lAmb = carOn ? sc.CarAmbient : sc.Ambient, lDif = carOn ? sc.CarDiffuse : sc.SunDiffuse, lSpc = carOn ? sc.CarSpecular : sc.SunSpecular;
        if (!Matrix4x4.Invert(world, out var inv)) inv = Matrix4x4.Identity;
        foreach (ref readonly var e in entries.AsSpan())
        {
            if (e.Register + e.Count > regs) continue;
            float* o = dst + e.Register * 4;
            switch (e.Kind)
            {
                case Kind.Material:
                    // Autodreck (TD_CarPaint2/_NM, TD_CarMat_NM VS): DirtColor0..3 = Dreckfarbe rgb + Dreck-Alpha je Viertel, der VS
                    // mischt sie bilinear über TEXCOORD0 zu COLOR1 (s318). Hier ein Wert für alle vier Viertel (Vehicles/CarDirt).
                    if (mat != null && mat.Car && e.Name.StartsWith("DirtColor", StringComparison.Ordinal)) { W(o, mat.Dirt); break; }
                    if (avOn && AvatarSymbol(o, e.Key0, av!, fam)) break;
                    if (glossFct.HasValue && e.Key0 == s_kGlossFct) { W(o, glossFct.Value); break; }   // Autolichter (Lichtgläser)
                    if (carOn && e.Key0 == s_kEnvCoeff && (def.ShaderSet == SetCarPaint2 || def.ShaderSet == SetCarPaint))
                    {
                        // exe 0x7175a0: TD_CarPaint2 ganz aus dem Autolicht, TD_CarPaint nur x (y bleibt Materialwert)
                        if (def.ShaderSet == SetCarPaint2) { W(o, sc.CarEnv); break; }
                        Vector4 ev = Vector4.Zero;
                        if (!((over != null && (over.Params.TryGetValue(e.PKey0, out ev) || over.Params.TryGetValue(e.PKey1, out ev)))
                              || def.Params.TryGetValue(e.PKey0, out ev) || def.Params.TryGetValue(e.PKey1, out ev))) ev = Vector4.Zero;
                        ev.X = sc.CarEnvFactor;
                        W(o, ev);
                        break;
                    }
                    // Instrumente (exe 0x71a6e7: Sätze caca000000000010 / 7dad16b47eb51337, Symbol HEADLIGHTSINTENSITY; 0x717ee0 schreibt je
                    // Bild nur .x = 0x70eaf0 = car_headlights_intensity aus dem Wetter, y..w bleiben Materialwerte)
                    if (e.PKey0 == s_kHeadlightsInt && (def.ShaderSet == SetDashBoard || def.ShaderSet == SetDashBoard2))
                    {
                        Vector4 hv = Vector4.Zero;
                        if (!(def.Params.TryGetValue(e.PKey0, out hv) || def.Params.TryGetValue(e.PKey1, out hv))) hv = Vector4.Zero;
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
                    // over: Lack-Material der Autos (<car>_color.2dm) vor dem Karosserie-Material
                    if ((over != null && (over.Params.TryGetValue(e.PKey0, out var pv) || over.Params.TryGetValue(e.PKey1, out pv)))
                        || def.Params.TryGetValue(e.PKey0, out pv) || def.Params.TryGetValue(e.PKey1, out pv))
                    {
                        // SECTROFF (World.SectorMaterials, Spielkoordinaten) in den Render-Raum der Weltmatrix
                        if (e.Key0 == s_kSectrOff) pv += new Vector4(sc.Origin.X, 0f, sc.Origin.Y, 0f);
                        W(o, pv);
                    }
                    else
                    {
                        // Ring enthält sonst Werte eines anderen Aufrufs -> 0 wie ein frischer D3D9-Konstantenpuffer; Name für die Diagnose merken
                        new Span<float>(o, e.Count * 4).Clear();
                        NoteMissing(e.Name);
                    }
                    break;
                case Kind.WorldViewProj: WriteMatrix(o, e.Count, e.Class, world * sc.ViewProj); break;
                case Kind.World: WriteMatrix(o, e.Count, e.Class, world); break;
                case Kind.WorldView: WriteMatrix(o, e.Count, e.Class, world * sc.View); break;
                case Kind.InvWorld: WriteMatrix(o, e.Count, e.Class, inv); break;
                case Kind.InvWorldView:
                case Kind.TInvWorldView:
                    {
                        if (!Matrix4x4.Invert(world * sc.View, out var iwv)) iwv = Matrix4x4.Identity;
                        WriteMatrix(o, e.Count, e.Class, e.Kind == Kind.TInvWorldView ? Matrix4x4.Transpose(iwv) : iwv);
                        break;
                    }
                case Kind.View: WriteMatrix(o, e.Count, e.Class, sc.View); break;
                case Kind.Proj: WriteMatrix(o, e.Count, e.Class, sc.Proj); break;
                case Kind.ViewProj: WriteMatrix(o, e.Count, e.Class, sc.ViewProj); break;
                case Kind.InvView: { Matrix4x4.Invert(sc.View, out var m); WriteMatrix(o, e.Count, e.Class, m); break; }
                case Kind.InvProj: { Matrix4x4.Invert(sc.Proj, out var m); WriteMatrix(o, e.Count, e.Class, m); break; }
                case Kind.InvViewProj: { Matrix4x4.Invert(sc.ViewProj, out var m); WriteMatrix(o, e.Count, e.Class, m); break; }
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
                case Kind.CamPos: W(o, Vector3.Transform(sc.CameraPosition, inv), 1f); break;
                case Kind.CamDir: W(o, Dir(inv, sc.CameraDirection), 0f); break;
                case Kind.DirLightDir0: W(o, Dir(inv, avOn ? av!.Direction : sc.SunDirection), 0f); break;
                case Kind.DirLightDir1: W(o, Vector3.TransformNormal(new Vector3(0, -1, 0), inv), 0f); break;
                case Kind.DirLightDiffuse0: W(o, Mul(avOn ? av!.Diffuse : lDif, MatDiffuse(def)), MatDiffuse(def).W); break;
                case Kind.DirLightRawDiffuse0: W(o, avOn ? av!.Diffuse : lDif, 1f); break;
                case Kind.DirLightSpecular0: W(o, Mul(avOn ? av!.Specular : lSpc, def.Specular), def.Specular.W); break;
                case Kind.Emissive: W(o, def.Emissive); break;   // Material_Emissive 0xfbd170: Datei +0xF0 unverändert
                // __DirLight_Emissive_0 (Kennung 0x2e, exe 0xfbf600): Emissive des Richtungslichts (+0x40) x Material-Emissive, w = Material.w
                case Kind.DirEmissive0: W(o, Mul(sc.SunEmissive, def.Emissive), def.Emissive.W); break;
                case Kind.Ambient0: W(o, Mul(avOn ? av!.Ambient : lAmb, MatAmbient(def)), MatAmbient(def).W); break;
                case Kind.RawAmbient0: W(o, avOn ? av!.Ambient : lAmb, 1f); break;
                // Kameralicht (nur Avatare; sonst aus = 0 wie ein abgeschaltetes Spotlicht). Materialfarben wie exe 0xfbd9b0/0xfbd450/0xfbd6e0
                case Kind.SpotPos0: W(o, avOn ? Vector3.Transform(av!.SpotPosition(sc.CameraDirection), inv) : Vector3.Zero, 1f); break;
                case Kind.SpotAmbient0: W(o, avOn ? Mul(av!.SpotAmbient, MatAmbient(def)) : Vector3.Zero, MatAmbient(def).W); break;
                case Kind.SpotDiffuse0: W(o, avOn ? Mul(av!.SpotDiffuse, MatDiffuse(def)) : Vector3.Zero, MatDiffuse(def).W); break;
                case Kind.SpotSpecular0: W(o, avOn ? Mul(av!.SpotSpecular, def.Specular) : Vector3.Zero, def.Specular.W); break;
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
                default: new Span<float>(o, e.Count * 4).Clear(); break;   // zweites Licht, Spot-Lichter, Echtzeit-Schatten: abgeschaltet -> 0
            }
        }
    }
}