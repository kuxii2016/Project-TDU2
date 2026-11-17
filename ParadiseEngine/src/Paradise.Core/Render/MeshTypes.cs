using System.Numerics;

namespace Paradise.Core.Render;

/// <summary>Einfaches Vertex-Format (Terrain, einfache Meshes): Position + Normale + UV (32 Bytes).</summary>
public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;

    public MeshVertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        Position = position;
        Normal = normal;
        Uv = uv;
    }

    public const uint SizeInBytes = 32;
}

/// <summary>
/// Objekt-Vertex (TDU2/Object, TDU2/ObjectTransparent): wie MeshData im Unity-Projekt –
/// Position, Normale, Vertexfarbe (RGBA8), UV0 (COLOR), UV1 (DETAIL/SHADOW/MARKS),
/// UV2 (letzter UV-Satz: Straßen-Detail, Farbpalette), UV3 (MARKS2). 60 Bytes.
/// </summary>
public struct ObjectVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public uint Color;          // R | G<<8 | B<<16 | A<<24
    public Vector2 Uv0, Uv1, Uv2, Uv3;

    public const uint SizeInBytes = 60;

    public static uint PackColor(byte r, byte g, byte b, byte a) => (uint)(r | (g << 8) | (b << 16) | (a << 24));
}

/// <summary>GPU-Texturformate. BC1/BC3 werden direkt hochgeladen, Rgba8 z. B. für CPU-dekodiertes DXT3.</summary>
public enum GpuTextureFormat { Bc1, Bc3, Rgba8 }

/// <summary>Adressmodus (entspricht Unity TextureWrapMode).</summary>
public enum TextureWrap { Repeat, Clamp, Mirror }

/// <summary>
/// Zum Hochladen vorbereitete Textur: komplette Mip-Kette (Mip 0 zuerst, wie in der .2db),
/// plus Adressmodus – entspricht Unity Texture2D (+ wrapModeU/V).
/// </summary>
public sealed class TextureUpload
{
    public int Width;
    public int Height;
    public GpuTextureFormat Format;
    public int Mips = 1;
    public byte[] Blocks = Array.Empty<byte>();
    public TextureWrap WrapU = TextureWrap.Repeat, WrapV = TextureWrap.Repeat;
    public string? Name;
    /// <summary>Kopie auf die GPU erst im nächsten Bild vor dem Zeichnen (kein Warten auf die GPU) – für viele kleine Texturen (Flash).</summary>
    public bool Deferred;
    /// <summary>Linear statt sRGB anlegen (Unity: new Texture2D(..., linear: true)) – z.B. Schmutzkarte der Autos. Standard sRGB wie in Unity.</summary>
    public bool Linear;
}

/// <summary>Eine GPU-Textur (in Unity: Texture2D). Wird von Materialien referenziert.</summary>
public interface IGpuTexture : IDisposable
{
    int Width { get; }
    int Height { get; }
    string? Name { get; }
    GpuTextureFormat Format { get; }
}

/// <summary>Shader eines Materials.</summary>
public enum MaterialKind
{
    /// <summary>Einfach: eine Textur x Farbe, beleuchtet (MeshVertex). Fallback/Platzhalter.</summary>
    Simple,
    /// <summary>TDU2/Terrain (MeshVertex).</summary>
    Terrain,
    /// <summary>TDU2/Object – opak/Cutout (ObjectVertex).</summary>
    Object,
    /// <summary>TDU2/ObjectTransparent – Alpha-Blend oder additiv, ohne Tiefenschreiben (ObjectVertex).</summary>
    Transparent,
    /// <summary>TDU2/Foliage – Laub (V_*-Cutout ohne DETAIL), Wind + weiche Beleuchtung (ObjectVertex).</summary>
    Foliage,
    /// <summary>TDU2/Sprite – Vegetations-Billboards (*_Spr), Eckoffset in UV1 (ObjectVertex).</summary>
    Sprite,
    /// <summary>TDU2/SpriteGlow – additive/alpha Leucht-Billboards (Halos) (ObjectVertex, transparent).</summary>
    SpriteGlow,
    /// <summary>Bildschirm-Overlay (Debug-UI): Pixelkoordinaten, ohne Tiefentest (ObjectVertex).</summary>
    Overlay,
    /// <summary>Flash-Oberfläche (TDU2/FlashQuad): Pixelkoordinaten, Farbtransformation, Masken.</summary>
    Flash,
    /// <summary>TDU2/Grass – Gras/Büsche (Atlas, Bodenfarbe aus der Colormap über UV1) (ObjectVertex).</summary>
    Grass,
    /// <summary>TDU2/Water – Meer/Pools: Fresnel, Wellen-Normalen, Sonnenglitzern; vormultipliziert, ohne Tiefenschreiben (ObjectVertex).</summary>
    Water,
    /// <summary>TDU2/Car – Fahrzeuglack/Chrom/Interieur (Standard-Licht, Klarlack, Flakes, Lichter, Beklebung, Schmutz) (ObjectVertex).</summary>
    Car,
    /// <summary>TDU2/CarGlass – Fahrzeugglas, transparent vormultipliziert, ohne Tiefenschreiben (ObjectVertex).</summary>
    CarGlass,
    /// <summary>TDU2/CarShadow – Bodenschatten unter dem Fahrzeug, multiplikativ (Blend DstColor Zero) (ObjectVertex).</summary>
    CarShadow,
    /// <summary>TDU2/Skidmark – Reifenspuren: Alpha aus Vertexfarbe x Textur, nachts dunkler (ObjectVertex).</summary>
    Skidmark,
    /// <summary>Partikel (Unity: Sprites/Default) – Textur x Vertexfarbe, alpha-geblendet (ObjectVertex).</summary>
    Particle,
    /// <summary>TDU2/Flare: additives, unbeleuchtetes Quad (Blend One One, Offset -1), Farbe x Intensität in der Vertexfarbe.</summary>
    Flare,
    /// <summary>TDU2/MapTerrain – Geländekacheln der großen Karte: Colormap unbeleuchtet + Relief aus den Normalen (MeshVertex).</summary>
    MapTerrain,
    /// <summary>TDU2/Minimap (große Karte): Straßennetz, Vertexfarbe x Textur, Alpha-Blend, ZTest Always, zuletzt gezeichnet (ObjectVertex).</summary>
    MapRoad,
    /// <summary>TD_CloudMesh – Wolkenkuppel des Originals (fx/clouds.bnk): Streulicht im VS, Alpha-Blend, hinter allem (ObjectVertex).</summary>
    Cloud,
    /// <summary>td_dome mit Sternen (ps @0x1af0): Dom-Mesh der exe, Sternkanal im Normal (u, v, Helligkeit), hinter allem (ObjectVertex).</summary>
    SkyStars,
    /// <summary>td_moon (ps @0xfe0): Mond-Billboard, Farbe = Textur + (0,3922; 0,4118; 0,5098), Alpha = Textur.a · moonPercent (ObjectVertex).</summary>
    SkyMoon,
    /// <summary>TD_Lightning (World/Lightning): Blitzstreifen, Farbe aus u/v, Alpha = Vertexalpha, additiv SrcAlpha/One (ObjectVertex).</summary>
    Lightning,
    /// <summary>TDU2/Avatar bzw. TDU2/AvatarBlend (AvBlend) – Avatare: Haut/Kleidung/Textur/Haare/Auge (AvMode), gehäutet auf der CPU (ObjectVertex).</summary>
    Avatar,
}

/// <summary>Material-Beschreibung (in Unity: Material + Shader-Properties). Defaults = Shader-Defaults.</summary>
public sealed class MaterialDesc
{
    public MaterialKind Kind = MaterialKind.Simple;
    public string? Name;
    /// <summary>
    /// Cull-Modus des Originals (.2DM MAT. +0x43, im Exe D3DRS_CULLMODE 0x16 über Tabelle {2,3,2,1}: 3 = D3DCULL_NONE).
    /// true = beidseitig, false = Rückseiten (Seite gegen die Normalen) werden nicht gezeichnet. Default beidseitig für
    /// alle Materialien ohne .2DM-Flag (Autos, Avatare, Effekte, Himmel ...).
    /// </summary>
    public bool TwoSided = true;
    /// <summary>
    /// Leuchtet (Bloom-Maske, Zusatz PostFx): Material schreibt seinen Alpha-Wert in den Alpha-Kanal des Weltbilds. Additive
    /// Transparente, Glow-Sprites, Flares, Blitze und Auto-/Objekt-Licht tun das ohnehin; hier für alpha-gemischte Transparente
    /// (Spot-Hologramme, transparente TD_Gouraud-Symbole im Interieur).
    /// </summary>
    public bool Glow;

    // gemeinsam
    public IGpuTexture? Main;                      // _MainTex (Objekt: COLOR, Terrain: Colormap)
    public Vector4 Color = Vector4.One;            // _Color
    public float Cutoff;                           // _Cutoff (0 = kein Alpha-Test)

    // TDU2/Terrain
    public IGpuTexture? Splat, Detail1, Detail2;
    public float DetailTiling = 0.098f;
    public float DetailStrength = 1f;
    public bool UseSplat;
    public float ShadowStrength;                   // auch TDU2/Object (_ShadowStrength)

    // TDU2/Object
    public IGpuTexture? Shadow, Detail, Tint, Marks, Marks2;
    public float VertexColorStrength = 0.5f;
    public float VcScale = 2f;
    public bool DetailMode;
    public bool DetailUvLast;                      // _DetailUV: 0 = UV-Satz 2 (UV1), 1 = letzter UV-Satz (UV2)
    public float ColorStrength = 1f;
    public float DetailBoost = 1.2f;
    public bool DetailGray;
    public bool TintMode;
    public float TintScale = 2f;
    public bool MarksMode, Marks2Mode;
    public float Brightness = 1.15f;
    public float Wrap = 0.35f;
    public bool PlantMode;
    public bool DetailAlpha;
    public float Unlit;
    public bool WetSurface;                        // _WetSurface (Straßen/Plätze werden bei Regen dunkler)
    public float NightEmit;                        // _NightEmit (Lampen/Neon leuchten nachts)
    // Nachtfenster (TD_Building: GLOSS + WINDOWMAP, luminance_power)
    public bool WinMode;
    public IGpuTexture? GlossTex, WinTex;
    public Vector4 WinParams;
    // Nacht-Lightmap (TD_Building NIGHTLIGHTMAP, lm_*_night, UV0) und nightlmap des Originals (in allen TD_Building-Materialien 0.44, 1)
    public IGpuTexture? NightLm;
    public Vector2 NightLmap = new(0.44f, 1f);
    public float NightLmDayBoost = 1f;             // Tageshelligkeit (Game/Config)
    /// <summary>Interieur (Unity InteriorFx _NightMode, _NightLitScale 0, _NightAmbient 0.9): immer die Nacht-Lightmap, keine Sonne.</summary>
    public bool InteriorLm;
    /// <summary>Unity TDU2Object _ReflMix: Anteil der Bodenspiegelung (TD_Furnitures: 1 - baseColor.a bzw. biasCoeff x 0,8).</summary>
    public float ReflMix;
    // TDU2/MapTerrain
    public float Relief = 0.35f;                   // _Relief (_Brightness = Brightness, hier 1 setzen)

    // TDU2/ObjectTransparent
    public bool Additive;                          // Blend SrcAlpha/One statt SrcAlpha/OneMinusSrcAlpha
    public bool AlphaFromDetail;                   // _AlphaSource
    public float Lit = 1f;
    public float Intensity = 1f;
    public bool NightOnly;
    public bool VcMul;

    // TDU2/Foliage
    public float NormalUp = 1f;
    public float FoliageWrap;
    public float Translucency;
    public float SelfShadow = 1f;
    public float UseVertexColor;
    public float WindStrength = 1f;
    public float WindSpeed = 1f;
    public float FlutterStrength = 1f;

    // TDU2/Sprite
    public float CrownHeight;
    public bool CrownOn;
    public float SpriteVertexColor = 1f;           // _UseVertexColor (Sprite: Original ja)

    // Textur-Animation (.uva): _MainTex_ST / _DetailTex_ST (xy = Skalierung, zw = Versatz)
    public Vector4 MainST = new(1, 1, 0, 0);
    public Vector4 DetailST = new(1, 1, 0, 0);

    // TDU2/Grass
    public IGpuTexture? Ground;                    // _GroundTex (Colormap des Sektors), _SplatTex = Splat
    public float GrassWind = 0.08f;
    public float GroundBoost = 1f;
    public float GroundTipMix = 0.3f;

    // TDU2/Water (Main = _NormalMap)
    public Vector4 PoolTint;                       // _PoolTint (a = Mindest-Deckung, 0 = Meer)
    public Vector2 NMapScale = new(0.15f, 0.015f);
    public Vector2 NMapWeight = new(1f, 1f);
    public float WaveStrength = 0.6f, WaveSpeed = 1f, FlatDistance = 2500f;
    public Vector3 Fresnel = new(0.3f, 30f, 0.5f);  // Bias, Power, Scale
    public Vector3 ReflColor = new(0.498f, 0.498f, 0.498f);
    public float ReflBlur = 0.05f;
    public Vector4 SpecColorCut = new(0.502f, 0.596f, 0.674f, 0f);
    public float SpecPower = 20f;
    /// <summary>
    /// Meer wie das Original TD_Ocean (ps_0/vs_0): Main = NoiseNMap (World/OceanNoise), Ground = Colormap der Ebene,
    /// OceanColorUv = (Ursprung x, Ursprung z, 1/Ausdehnung x, 1/Ausdehnung z) in Spielkoordinaten
    /// (Sektor 768 m, Area 6144 m, Insel 6144·AreaSize), OceanLevel 0 Sektor / 1 Area / 2 Insel (Zeichenreihenfolge grob vor fein).
    /// </summary>
    public bool Ocean;
    /// <summary>TD_CloudMesh: Überblendgewicht beim Wechsel des Wolkenobjekts (CLOUD_A/B/R), 1 = voll.</summary>
    public float CloudWeight = 1f;
    public Vector4 OceanColorUv;
    public int OceanLevel = 2;

    // TDU2/Car (TDU2Car.shader) – Main = _MainTex, Color = _Color
    public Vector4 Color2 = Vector4.One;           // _Color2
    public float Bicolour, RampPow, MainGray, AlphaFromRed;
    /// <summary>
    /// Original-Lack TD_CarPaint (aus TD_CarPaint.sob disassembliert): 1 = Lackformel des Spiels statt Unity-Standard.
    /// TdMatParam = MATPARAM (x Reflexionsfaktor, z Fresnel-Minimum, w Fresnel-Exponent), TdEnvCoeff = ENVCOEFF (x Stärke, y N·V-Versatz),
    /// TdSpecParm = SPECPARM (x Glanz-Exponent, y Glanz-Stärke, z Reflexions-Alpha), TdLayrClr = LAYRCLR1 (Detailebene Farbe/Deckkraft).
    /// </summary>
    public float TdPaint, TdLayer;
    public Vector4 TdMatParam, TdEnvCoeff, TdSpecParm, TdLayrClr = Vector4.One;
    /// <summary>
    /// TD_CarPaint2 mit Mikroflocken (Shader-Variante mit gMicroFlakesTexSampler, z.B. TD_CarPaint2.sob ps @93216): TdFlake = 1 an,
    /// FlakeTex = FLAKE-Ebene, TdRampFlake = RAMPFLAKE (Farbe der Flocken, Gamma-Raum roh), TdFlakeABNS = FLAKEABNS
    /// (x Anteil Flocke an der Normale, y Stärke Glitzer, z Anteil Normale, w Kachelung je Objekteinheit), TdRampPowY = RAMPPOW.y.
    /// </summary>
    public float TdFlake, TdRampPowY;
    public Vector4 TdRampFlake, TdFlakeABNS;
    public IGpuTexture? FlakeTex;
    /// <summary>Original-Glas (TdPaint = 3): DETAIL2-Ebene (G_I, deckt die Grundfarbe ab).</summary>
    public IGpuTexture? Detail2Tex;
    public float Heat;
    public Vector4 HeatColor = new(1f, 0.25f, 0.04f, 1f);
    public float Glossiness = 0.5f, Metallic;
    public IGpuTexture? EmissionTex, LightMask, StickerTex, DirtTex;
    public float Emission, EmissionDiff;
    public Vector4 EmissionTint = Vector4.One;
    public float UseMask, LgtLights, LgtBrake, LgtReverse, LgtBlink;
    public Vector4 TailLightColor = new(1f, 0.015f, 0.01f, 1f), BrakeLightColor = new(1f, 0.01f, 0.005f, 1f);
    public Vector4 ReverseLightColor = Vector4.One, BlinkLightColor = new(1f, 0.25f, 0.01f, 1f);
    public Vector4 LightIntensity = new(1f, 2f, 2f, 2f); // _TailLightIntensity, _BrakeLightIntensity, _ReverseLightIntensity, _BlinkLightIntensity
    public float SelfIllum, Occlusion = 1f;
    public float ClearCoat, ClearCoatPower = 600f, Flake, FlakeScale = 3000f;
    public float UseSticker, StickerUV, StickerAlphaOnly;
    public float DirtLevel, ScratchLevel;
    public Vector4 DirtColor = new(0.42f, 0.35f, 0.26f, 1f);
    // TDU2/CarGlass
    public float MaxAlpha = 0.5f;
    // TDU2/CarShadow (Main = Schattentextur)
    public float Strength = 0.75f;

    // TDU2/Avatar, TDU2/AvatarBlend (Core/Avatars/AvatarMaterials) – Main = SKIN/TEX/HAIR, Detail = DETAIL_GRAIN
    public bool AvBlend;
    public float AvMode;                           // _Mode: 0 Haut, 1 Kleidung, 2 Textur, 3 Haare, 4 Auge
    public IGpuTexture? AvGloss, AvRamp, AvBump, AvLayer1, AvLayer2, AvLayer3;
    public float AvBrightness = 1f, AvAlphaLum, AvGlossOn, AvRampOn, AvRampScale = 1.5f, AvBumpOn, AvDetailOn, AvOmbre, AvInCar, AvIndoor;
    public Vector4 AvSkinClr = Vector4.One, AvDclClr = new(0f, 0f, 0f, 1f), AvCoeffs = new(0.5f, 0f, 0.5f, 0f), AvUVScale = new(1f, 1f, 10f, 10f);
    public Vector4 AvBaseClr = new(0.5f, 0.5f, 0.5f, 1f), AvClrL1 = Vector4.One, AvClrL2 = Vector4.One, AvClrL3 = Vector4.One;
    public Vector4 AvLayerOn, AvLayerUV = new(1f, 2f, 2f, 0f), AvUVOff, AvBColor = new(1f, 1f, 1f, 0.5f), AvTColor = new(0f, 0f, 0f, 1f);
    public Vector4 AvPrimClr = new(0f, 0f, 0f, 1f), AvScdClr = Vector4.One, AvSpec = new(0.2f, 8f, 0f, 0f), AvCarInteg = new(0.35f, 0.5f, 1f, 0f), AvAtteFact;
    /// <summary>Innenraum (Unity _TDU2AvatarIndoor/_TDU2IndoorDir/-Light/-AmbTop/-AmbBottom aus TDU2Interiors.SetAvatarIndoor).</summary>
    /// <summary>Feinabstimmung (Unity _TDU2AvRampAdd, _TDU2AvRampRow, _TDU2AvAddMix, _TDU2AvAmbAdd; 0 = Original) und _TDU2AvHairSheen.</summary>
    public Vector4 AvTweak;
    public float AvHairSheen;
    public Vector4 AvIndoorDir = new(0.25f, 1f, 0.35f, 0f), AvIndoorLight = new(0.5f, 0.47f, 0.43f, 1f), AvIndoorAmbTop = new(0.46f, 0.44f, 0.41f, 1f), AvIndoorAmbBottom = new(0.2f, 0.19f, 0.18f, 1f);

    public MaterialDesc Clone() => (MaterialDesc)MemberwiseClone();
}

/// <summary>Spot-Licht (Straßenlampe: nach unten; Fahrzeug: Scheinwerfer/Punktlicht).</summary>
public struct LampLight
{
    public Vector3 Position;   // Render-Raum
    public float Range;
    public Vector3 Color;      // Farbe x Intensität
    public float CosOuter, CosInner;
    /// <summary>Leuchtrichtung (normiert); (0,0,0) = nach unten. Punktlicht: CosOuter/CosInner unter -1.</summary>
    public Vector3 Direction;
}

/// <summary>Zähler des letzten Frames + Gerätedaten (Debug-UI).</summary>
public sealed class RenderStats
{
    public int DrawCalls, Triangles, PipelineBinds, TransparentDraws, OverlayDraws, ShadowDraws;
    public int Textures, Materials, Meshes;
    public long GpuMemoryAllocated;      // vom Allokator belegte Blöcke (Bytes)
    public long VramTotal;               // Device-Local-Heap (Bytes)
    public long VramBudget, VramUsage;   // VK_EXT_memory_budget (0 = nicht verfügbar)
    public string DeviceName = "", DeviceType = "", Driver = "", Api = "";
    public int Width, Height;
    public bool VSync;
}

/// <summary>Ein GPU-Material (in Unity: Material).</summary>
public interface IGpuMaterial : IDisposable
{
    MaterialKind Kind { get; }
}

/// <summary>Ein zur GPU hochgeladenes Mesh.</summary>
public interface IGpuMesh : IDisposable
{
    int IndexCount { get; }
}

/// <summary>Globale Frame-Werte (in Unity: Shader.SetGlobal* + RenderSettings/Licht). Vom Streamer/Wetter gesetzt.</summary>
public struct FrameGlobals
{
    public Vector3 CameraPosition;
    public Vector3 SunDirection;        // zur Sonne zeigend
    public Vector3 SunColor;            // _LightColor0
    public Vector3 Ambient;             // Umgebungslicht (ShadeSH9)
    public float TerrainLower;          // _TDU2TerrainLower (m)
    public float TerrainPush;           // _TDU2TerrainPush
    public float Wet;                   // _TDU2Wet
    public float DayDim;                // _TDU2DayDim (1 = Tag)
    public float NightK;                // _TDU2NightK
    public float StreetLight;           // _TDU2StreetLight
    public float NightWindows;          // _TDU2NightWindows (Fensterlicht der Gebäude)
    public Vector3 FogColor;            // RenderSettings.fogColor
    public float FogStart, FogEnd;      // linearer Nebel (m); FogEnd <= FogStart = aus
    public float SkyExposure;           // Skybox _Exposure-Faktor (0 = Standard 1)
    public bool NoSky;                  // Kamera ohne Skybox: Hintergrund = BackgroundColor (Unity CameraClearFlags.SolidColor, große Karte)
    public Vector3 BackgroundColor;     // sRGB
    // Meer (TD_Ocean, TDU2Weather: _TDU2Ocean*), OceanSet = false -> Materialwerte
    public bool OceanSet;
    public Vector3 OceanFresnel;         // Bias, Power, Scale
    public Vector4 OceanSpec;           // rgb, Schwelle
    public Vector3 OceanRefl;
    public float OceanSpecPower, OceanBlur;
    public Vector2 OceanNMap;           // Kacheln je Meter fein/grob
    /// <summary>TD_Ocean: x 2·ocean_NMapLR.y, y 2·ocean_NMapHR.y, z ocean_attenuationBluriness, w ocean_attenuationFresnel.</summary>
    public Vector4 OceanParam;
    /// <summary>TD_Ocean mp0.x = ocean_specularPower / Skydome_sunIntensity².</summary>
    public float OceanSpecPow;
    /// <summary>Straßenlampen (Unity: Spot-Lights an den nächsten Masten, nachts). Render-Raum.</summary>
    public LampLight[]? Lamps;
    public int LampCount;
    public float ShadowStrength;        // Light.shadowStrength (0 = keine Schatten)
    public float ShadowDistance;        // QualitySettings.shadowDistance (m)
    public Vector2 Origin;              // _TDU2Origin.xz: Render-Position des Spielursprungs (Wasserwellen)
    public float Time;                  // _Time.y (Sekunden, Wind/Animation) – setzt der Renderer, wenn 0
    /// <summary>Licht der Autos wie im Original (World/TDU2CarLighting, GS_CarDisplay). On = false -> Autos nutzen Sonne/Ambient oben.</summary>
    public CarLightGlobals CarLight;
    /// <summary>Original-Himmel td_dome (World/TDU2SkyDome). On = false -> Ersatzhimmel.</summary>
    public SkyDomeGlobals Sky;
    /// <summary>TD_CloudMesh (Gamma-Raum): Instance_Color = clouds_cloud3dcolor, ColorDown = clouds_cloud3dcolor2,
    /// AlphaBlend = clouds_cloud3dAlphaBlend, x Skydome_haze, y __DirLight_Emissive_0.x = sunflare_core_emissive.x.</summary>
    public Vector4 CloudColor, CloudDown, CloudAlpha;
    public Vector2 CloudMisc;
    /// <summary>TD_Road Regen (WFX_WetGrd, rain_shadow_spec A.2/A.4): RainSpecs = (rain_ground_global_intensity, rain_ground_wet, 1,
    /// rain_ground_puddle_height); RainRipple = (scroll_u, scroll_v, rain_ground_ripple_intensity, rain_ground_ripple_scale).
    /// RainSpecs.x = 0 -> trocken (Material ohne UT9).</summary>
    public Vector4 RainSpecs, RainRipple;

    /// <summary>Tageslicht-Standard bis TDU2WorldLighting/TDU2Weather portiert sind.</summary>
    public static FrameGlobals Default => new()
    {
        SunDirection = Vector3.Normalize(new Vector3(0.4f, 0.85f, 0.35f)),
        SunColor = new Vector3(0.75f),
        Ambient = new Vector3(0.25f),
        DayDim = 1f,
        ShadowStrength = 0.424f,   // Unity-Szene: Directional Light m_Shadows.m_Strength
        ShadowDistance = 150f,     // Unity-Standard (QualitySettings / Dev-Menü)
    };
}

/// <summary>
/// Autolicht des Originals (exe 0x744990 GS_CarDisplay, berechnet in World/TDU2CarLighting). Alle Farben im Gamma-Raum wie im
/// Original (D3D9 ohne sRGB) – die Lack-Shader rechnen damit direkt (TD_CarPaint/TD_CarPaint2: c13 Ambient, c14 DirLight
/// diffuse, c15 DirLight specular, c25 EnvCoeff).
/// </summary>
public struct CarLightGlobals
{
    public bool On;
    public Vector3 Ambient, Diffuse, Specular;
    /// <summary>ENVCOEFF von TD_CarPaint2 (exe 0x7175a0): (car_envmap_factor · Schmutzfaktor · F.z, F.w, F.x, F.y),
    /// F = car_fresnel_coeff_power_boost_specular.</summary>
    public Vector4 EnvCoeff;
    /// <summary>ENVCOEFF.x von TD_CarPaint (ohne F.z): car_envmap_factor · Schmutzfaktor.</summary>
    public float EnvFactor;
}

/// <summary>
/// Zustand eines Flash-Quad-Stapels wie im Unity-Shader TDU2/FlashQuad: Farbe = saturate(Textur × Mul + Add),
/// LumAlpha (Fonts: Luminanz als Alpha), ClipRect (Pixel xMin yMin xMax yMax, außerhalb verworfen),
/// MaskRect + Maskentextur (Alpha &lt; 0.02 verworfen).
/// </summary>
public struct FlashQuadParams
{
    public Vector4 Mul, Add, Clip, MaskRect;
    public bool LumAlpha;
    /// <summary>
    /// Wie Unity-IMGUI im linearen Farbraum (Flash-Filme über GL in OnGUI): der linear gelesene Texturwert wird ohne sRGB-Kodierung
    /// ausgegeben (dunkler/kontrastreicher als das Bild selbst). Für VmfPlayer an, für Logo/Video aus.
    /// </summary>
    public bool UnityGui;
}

/// <summary>Frame-Werte des Original-Himmels (td_dome ps @0x1570, Var_PS c9..c23). Farben im Gamma-Raum wie im Original.</summary>
public struct SkyDomeGlobals
{
    public bool On;
    public Vector4 BetaRPlusBetaM, HGgSaturation, BetaDashR, BetaDashM, OneOverBetaRPlusBetaM, Multipliers, SunColorAndIntensity;
    public Vector4 SpectrumToRgb0, SpectrumToRgb1, SpectrumToRgb2, SpectrumToRgb3;
    /// <summary>StarsIntensity = Skydome_starsPercent; StarsFade = (Skydome_starsFadeStart, 1/(End − Start)) (0xd01864/0xd0195e).</summary>
    public float StarsIntensity, StarsFadeStart, StarsFadeScale;
    /// <summary>PosMoon.w = Skydome_moonPercent, SizeMoon.x = Skydome_moonSize · 10 (0xd01712).</summary>
    public float MoonPercent, MoonSize;
    /// <summary>Richtung zur Streu-Sonne (Welt, vom VS gedreht).</summary>
    public Vector3 ScatterSun;
    /// <summary>__DirLight_Diffuse_0 / __AmbLight_Ambient_0 (Gamma-Raum, sun_diffuse / sun_ambient).</summary>
    public Vector3 DirLightDiffuse, AmbLight;
}
