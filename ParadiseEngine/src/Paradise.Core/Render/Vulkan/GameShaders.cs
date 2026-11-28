namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// GLSL-Ports der Unity-Shader aus TDU2Map/Shaders. Unterschiede zur Unity-Version sind nur
/// die Engine-Anbindung: Kamera/Modell als Push-Constants, Material-Properties als Uniform-Buffer
/// (set 0), Shader.SetGlobal*/Licht als Frame-Buffer (set 1).
/// Lichtmodell wie Unity-Surface-Shader: Albedo x (Lichtfarbe x Diffus + Ambient) + Emission.
/// </summary>
internal static class GameShaders
{
    private const string Common = @"#version 450
layout(push_constant) uniform PC { mat4 viewProj; mat4 model; } pc;
layout(set = 1, binding = 0) uniform Frame {
    vec4 camPos;    // xyz Kamera, w _TDU2TerrainLower
    vec4 sunDir;    // xyz zur Sonne, w _TDU2TerrainPush
    vec4 sunColor;  // rgb _LightColor0, w _TDU2Wet
    vec4 ambient;   // rgb Ambient, w _TDU2DayDim
    vec4 misc;      // x _TDU2NightK, y _TDU2StreetLight
    vec4 viewRight; // xyz UNITY_MATRIX_V[0] (Kamera rechts, Welt), w _Time.y
    vec4 viewUp;    // xyz UNITY_MATRIX_V[1] (Kamera oben, Welt)
    vec4 shadow;    // x Stärke, y Distanz, z Normal-Bias (Texel), w Texelgröße (UV)
    vec4 splits;    // Ende der Kaskaden 0..3 (m)
    vec4 texelWorld;// Texelgröße der Kaskaden (m)
    mat4 shadowVP[4];
    vec4 env;       // x _TDU2NightWindows, y Nebel-Start, z Nebel-Ende, w Nebel an
    vec4 fogColor;  // rgb Nebelfarbe, w Himmel-Exposure
    vec4 oceanFresnel; // xyz, w _TDU2OceanSet
    vec4 oceanSpec;    // rgb, Schwelle
    vec4 oceanRefl;    // rgb, w SpecPower
    vec4 oceanMisc;    // x NMap fein, y NMap grob, z Unschärfe, w Lampenzahl
    vec4 lampPos[32];  // xyz, w Reichweite
    vec4 lampCol[32];  // rgb x Intensität, w Richtung.y
    vec4 lampCone[32]; // x cos(außen), y cos(innen), zw Richtung.xz
    vec4 planar;       // Bodenspiegelung: x 1 an / -1 Spiegel-Durchgang, y Höhe der Ebene, zw 1/Bildgröße (Pixel)
    vec4 probe;        // Reflexionssonde: x 1 = gültig
    vec4 carAmbient;   // Autolicht des Originals (Gamma-Raum, World/TDU2CarLighting): rgb Ambient (c13), w 1 = an
    vec4 carDiffuse;   // rgb DirLight diffuse (c14), w ENVCOEFF.x für TD_CarPaint
    vec4 carSpecular;  // rgb DirLight specular (c15)
    vec4 carEnv;       // ENVCOEFF für TD_CarPaint2 (c25)
    vec4 oceanParam;   // TD_Ocean: x 2*NMapLR.y, y 2*NMapHR.y, z attenuationBluriness, w attenuationFresnel
    vec4 oceanParam2;  // x specularPower/sunIntensity^2, y 1 = planarRefl ist das Meer-Spiegelbild
    // Original-Himmel td_dome (World/TDU2SkyDome): Var_PS BetaRPlusBetaM, HGg_Saturation, BetaDashR, BetaDashM,
    // OneOverBetaRPlusBetaM, Multipliers (x; y 1 = an), SunColorAndIntensity, SpectrumToRGB (Zeilen), Streu-Sonne (Welt),
    // __DirLight_Diffuse_0, __AmbLight_Ambient_0 (Gamma-Raum)
    vec4 skyBetaRM, skyHG, skyBetaDashR, skyBetaDashM, skyOneOver, skyMul, skySun;
    vec4 skyS2R0, skyS2R1, skyS2R2, skyS2R3;
    vec4 skyScatter, skyDiffuse, skyAmbient;
    // TD_CloudMesh: Instance_Color, ColorDown, AlphaBlend (Gamma-Raum), x Haze, y DirLight_Emissive.x
    vec4 cloudColor, cloudDown, cloudAlpha, cloudMisc;
    vec4 skyStars;     // td_dome: x StarsIntensity, y StarsFade.x, z StarsFade.y; td_moon: w moonPercent
    vec4 rainSpecs;    // TD_Road RainSpecs: GI, wet, 1, puddle_height (x = 0 -> trocken)
    vec4 rainRipple;   // TD_Road RainRippleSpecs: scroll_u, scroll_v, ripple_intensity, ripple_scale
} fr;
layout(set = 1, binding = 1) uniform sampler2DArrayShadow shadowMap;
layout(set = 1, binding = 2) uniform sampler2D planarRefl;   // Unity _TDU2PlanarRefl
layout(set = 1, binding = 3) uniform samplerCube envCube;    // Unity unity_SpecCube0 (Echtzeit-Sonde am Auto)
layout(set = 1, binding = 5) uniform sampler2D rainPuddleTex;  // WFX_WetGrd PUDDLES (weatherfx puddles.2db)
layout(set = 1, binding = 6) uniform sampler2D rainRippleTex;  // WFX_WetGrd aktuelles RIPPLE_01..16 (alle 0,025 s)

// Echtzeitschatten der Sonne (Unity: Directional Light, Soft Shadows, 4 Kaskaden) -> atten
float TDU2Shadow(vec3 wp, vec3 n) {
    if (fr.shadow.x <= 0.0) return 1.0;
    float d = length(wp - fr.camPos.xyz);
    if (d >= fr.shadow.y) return 1.0;
    int ci = d < fr.splits.x ? 0 : (d < fr.splits.y ? 1 : (d < fr.splits.z ? 2 : 3));
    float tw = ci == 0 ? fr.texelWorld.x : (ci == 1 ? fr.texelWorld.y : (ci == 2 ? fr.texelWorld.z : fr.texelWorld.w));
    vec3 nn = dot(n, n) > 1e-8 ? normalize(n) : vec3(0.0, 1.0, 0.0);
    if (dot(nn, fr.sunDir.xyz) < 0.0) nn = -nn;            // zweiseitige Flächen
    vec4 c = fr.shadowVP[ci] * vec4(wp + nn * tw * fr.shadow.z, 1.0);
    vec3 p = c.xyz / c.w;
    vec2 uv = p.xy * 0.5 + 0.5;
    float z = min(p.z, 1.0);
    float s = 0.0;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            s += texture(shadowMap, vec4(uv + vec2(x, y) * fr.shadow.w, float(ci), z));
    s /= 9.0;
    float fade = clamp((fr.shadow.y - d) / (0.1 * fr.shadow.y), 0.0, 1.0);
    return mix(1.0, s, fr.shadow.x * fade);
}

// Linearer Nebel (Unity: RenderSettings.fog, FogMode.Linear, UNITY_APPLY_FOG)
vec3 TDU2Fog(vec3 col, vec3 wp) {
    if (fr.env.w < 0.5) return col;
    float d = length(wp - fr.camPos.xyz);
    float f = clamp((fr.env.z - d) / max(fr.env.z - fr.env.y, 1.0), 0.0, 1.0);
    return mix(fr.fogColor.rgb, col, f);
}

// Straßenlampen (Unity: Spot-Lights nach unten an den nächsten Masten, Forward-Pixel-Licht)
vec3 TDU2Lamps(vec3 wp, vec3 n) {
    int cnt = int(fr.oceanMisc.w);
    vec3 sum = vec3(0.0);
    vec3 nn = dot(n, n) > 1e-8 ? normalize(n) : vec3(0.0, 1.0, 0.0);
    for (int i = 0; i < cnt; i++) {
        vec3 L = fr.lampPos[i].xyz - wp;
        float r = fr.lampPos[i].w;
        float d2 = dot(L, L);
        if (d2 > r * r) continue;
        float d = sqrt(d2);
        L /= max(d, 1e-4);
        float q = d2 / (r * r);
        float att = 1.0 / (1.0 + 25.0 * q) * clamp((1.0 - q) * 5.0, 0.0, 1.0);   // Unity-Abfall, zum Rand auf 0
        vec3 beam = vec3(fr.lampCone[i].z, fr.lampCol[i].w, fr.lampCone[i].w);    // Leuchtrichtung (Straßenlampe: nach unten)
        float spot = smoothstep(fr.lampCone[i].x, fr.lampCone[i].y, dot(-L, beam));
        float ndl = max(dot(nn, L), 0.0);
        sum += fr.lampCol[i].rgb * att * spot * ndl;
    }
    return sum;
}

// Original-Himmel td_dome (td_dome.sob vs @0x5c0 + ps @0x1570, selbst disassembliert): Streuung nach Hoffman/Preetham in vier
// Spektralkanälen. v1 = Weglänge sqrt((y·3e6)² + 1,525e11) − y·3e6, Phase Rayleigh cos²·0,5 + 2, Mie (1−g²)·(1+g²−2g·cos)^−1,5,
// Inscatter = (β'R·ΦR + β'M·ΦM)/(βR+βM)·(1 − exp(−(βR+βM)·s))·Multipliers.x·SunColor, SpectrumToRGB, ln(1+x), Sättigung um die
// Helligkeit (0,299/0,587/0,114). Unter dem Horizont Übergang zu GroundColor (0,7/0,6/0,5) · Licht, Faktor
// sat((y·5e4 + Kamerahöhe + 700)·0,005). Licht = Ambient + (Diffus − Ambient)·max(Sonne.y in Kameraraum, 0).
// Das Original rechnet im Gamma-Raum -> Ergebnis nach linear (2,2).
vec3 TDU2DomeSkyGamma(vec3 dir, vec3 stars) {
    dir = normalize(dir);
    vec3 S = normalize(fr.skyScatter.xyz);
    float y = dir.y;
    float s = sqrt((y * 3e6) * (y * 3e6) + 1.525e11) - y * 3e6;
    float c = max(dot(dir, S), 0.0);
    float hx = fr.skyHG.y - fr.skyHG.z * c;
    float phM = fr.skyHG.x * (inversesqrt(abs(hx)) / hx);      // rsq (Betrag) · rcp wie im Original
    float phR = c * c * 0.5 + 2.0;
    vec4 ins = (fr.skyBetaDashR * phR + fr.skyBetaDashM * phM) * fr.skyOneOver;
    ins *= vec4(1.0) - exp2(-fr.skyBetaRM * s * 1.442695);
    ins *= fr.skyMul.x * fr.skySun;
    vec3 rgb = fr.skyS2R0.xyz * ins.x + fr.skyS2R1.xyz * ins.y + fr.skyS2R2.xyz * ins.z + fr.skyS2R3.xyz * ins.w;
    rgb = log2(max(rgb, vec3(0.0)) + vec3(1.0)) * 0.6931;
    float lum = dot(rgb, vec3(0.299, 0.587, 0.114));
    rgb = rgb - fr.skyHG.w * (vec3(lum) - rgb);
    rgb += stars;                                          // ps @0x1af0: Sterne vor dem Bodenübergang
    float sunUpView = dot(S, fr.viewUp.xyz);                 // v2.y: Streu-Sonne im Kameraraum
    vec3 light = fr.skyAmbient.rgb + (fr.skyDiffuse.rgb - fr.skyAmbient.rgb) * max(sunUpView, 0.0);
    vec3 ground = clamp(vec3(0.7, 0.6, 0.5), 0.0, 1.0) * light;
    float f = clamp((y * 5e4 + fr.camPos.y + 700.0) * 0.005, 0.0, 1.0);
    return f * (rgb - ground) + ground;
}
vec3 TDU2DomeSky(vec3 dir) { return pow(max(TDU2DomeSkyGamma(dir, vec3(0.0)), vec3(0.0)), vec3(2.2)); }

// Himmel: Original td_dome, solange das Wetter die Werte liefert; sonst Ersatz für Unitys Procedural Skybox
vec3 TDU2Sky(vec3 dir) {
    if (fr.skyMul.y > 0.5) return TDU2DomeSky(dir);
    dir = normalize(dir);
    vec3 sun = normalize(fr.sunDir.xyz);
    float y = dir.y;
    vec3 zenith = vec3(0.22, 0.40, 0.75), horizon = vec3(0.68, 0.79, 0.92), ground = vec3(0.36, 0.36, 0.38);
    vec3 col = y >= 0.0 ? mix(horizon, zenith, pow(clamp(y, 0.0, 1.0), 0.45)) : mix(horizon, ground, pow(clamp(-y * 3.0, 0.0, 1.0), 0.5));
    float low = 1.0 - clamp(sun.y * 3.0, 0.0, 1.0);
    col = mix(col, col * vec3(1.15, 0.85, 0.65), low * (1.0 - clamp(y * 2.0, 0.0, 1.0)) * 0.6);
    float sd = max(dot(dir, sun), 0.0);
    vec3 sunCol = vec3(1.0, 0.96, 0.88);
    col += sunCol * (pow(sd, 8.0) * 0.12 + pow(sd, 120.0) * 0.35);
    col = mix(col, sunCol * 3.0, smoothstep(0.99955, 0.99975, sd));
    return col * fr.fogColor.w;                             // Skybox-Exposure (Tag/Nacht, Wolken, Blitz – TDU2Weather)
}
";

    // Simple (Fallback)
    public const string SimpleVert = Common + @"
layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
void main() {
    vec4 world = pc.model * vec4(inPos, 1.0);
    gl_Position = pc.viewProj * world;
    vNormal = mat3(pc.model) * inNormal;
    vUv = inUv;
}";

    public const string SimpleFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(set = 0, binding = 1) uniform Mat { vec4 color; vec4 p0; } m;   // p0.w Cutoff
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 c = texture(mainTex, vUv) * m.color;
    if (m.p0.w > 0.0 && c.a < m.p0.w) discard;
    float ndl = max(dot(normalize(vNormal), normalize(fr.sunDir.xyz)), 0.0);
    outColor = vec4(c.rgb * (fr.sunColor.rgb * ndl + fr.ambient.rgb), 1.0);
}";

    // MapTerrain – Vertex wie Simple
    public const string MapTerrainFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;   // _MainTex Colormap
layout(set = 0, binding = 1) uniform Mat { vec4 color; vec4 p0; } m;   // p0.x _Brightness, p0.y _Relief
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;
void main() {
    vec3 c = texture(mainTex, vUv).rgb;
    vec3 n = normalize(vNormal);
    float shade = clamp(dot(n, normalize(vec3(-0.45, 0.8, 0.4))), 0.0, 1.0);
    c *= mix(1.0, 0.55 + 0.6 * shade, m.p0.y) * m.p0.x;
    outColor = vec4(c, 1.0);
}";

    // Terrain 
    public const string TerrainVert = Common + @"
layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorld;
void main() {
    vec3 wp = (pc.model * vec4(inPos, 1.0)).xyz;
    wp.y -= fr.camPos.w;                                   // _TDU2TerrainLower
    vec3 d = wp - fr.camPos.xyz;
    float dist = length(d);
    wp += d * (fr.sunDir.w * dist / max(dist, 0.001));     // _TDU2TerrainPush
    gl_Position = pc.viewProj * vec4(wp, 1.0);
    vNormal = mat3(pc.model) * inNormal;
    vUv = inUv;
    vWorld = wp;
}";

    public const string TerrainFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;   // _MainTex Colormap
layout(set = 0, binding = 1) uniform sampler2D splatTex;  // _SplatTex: R Schatten, G Straßenlicht, B Mischung DETAIL1/DETAIL2
layout(set = 0, binding = 2) uniform sampler2D detail1;
layout(set = 0, binding = 3) uniform sampler2D detail2;
layout(set = 0, binding = 4) uniform Mat { vec4 p0; } m;  // _DetailTiling, _DetailStrength, _ShadowStrength, _UseSplat
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorld;
layout(location = 0) out vec4 outColor;
void main() {
    if (fr.planar.x < -0.5 && vWorld.y < fr.planar.y + 0.06) discard;   // Spiegel-Durchgang: alles bis knapp über den Boden weg (Unity Clip-Ebene +2 cm; hier +6 cm = Toleranz der Ebenenhöhe wie beim Einmischen) – sonst verdeckt der Boden selbst das Spiegelbild
    float useSplat = m.p0.w;
    vec3 col = texture(mainTex, vUv).rgb;
    vec4 splat = useSplat > 0.5 ? texture(splatTex, vUv) : vec4(1.0, 0.0, 0.0, 1.0);
    vec2 duv = vWorld.xz * m.p0.x;
    // Detailtexturen (d1_cliff_rocks, d2_earth) sind gepackt: nur R = Helligkeitsdetail
    float d1 = texture(detail1, duv).r;
    float d2 = texture(detail2, duv).r;
    float det = mix(d2, d1, useSplat > 0.5 ? splat.b : 0.5) * 1.8;
    col *= mix(1.0, det, m.p0.y);
    col *= mix(1.0, splat.r, m.p0.z * useSplat);
    col *= 1.0 - 0.45 * fr.sunColor.w;                    // Regen: nasser Boden dunkler
    vec3 emission = col * splat.g * useSplat * fr.misc.y * vec3(0.97, 0.84, 0.65);
    float ndl = max(dot(normalize(vNormal), normalize(fr.sunDir.xyz)), 0.0);   // Lambert
    vec3 lit = col * (fr.sunColor.rgb * ndl * TDU2Shadow(vWorld, vNormal) + fr.ambient.rgb + TDU2Lamps(vWorld, vNormal)) + emission;
    outColor = vec4(TDU2Fog(lit, vWorld), 1.0);
}";

    // Object
    // Instancing (nur Vertex-Shader): gleiche Mesh+Material-Läufe in einem Aufruf; Modellmatrizen im Instanzpuffer (set 1, binding 4).
    // pc.model[3][3] == 0 kennzeichnet einen Instanz-Aufruf (normale Modellmatrizen haben dort 1).
    private const string Instancing = @"
layout(std430, set = 1, binding = 4) readonly buffer InstBuf { mat4 m[]; } inst;
mat4 TDU2Model() { return pc.model[3][3] == 0.0 ? inst.m[gl_InstanceIndex] : pc.model; }
";

    private const string ObjectInputs = @"
layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inColor;
layout(location = 3) in vec2 inUv0;
layout(location = 4) in vec2 inUv1;
layout(location = 5) in vec2 inUv2;
layout(location = 6) in vec2 inUv3;
";

    // Material-Properties: color = _Color
    //  p0: _ShadowStrength, _VertexColorStrength, _VcScale, _Cutoff
    //  p1: _DetailMode, _DetailUV, _ColorStrength, _DetailBoost
    //  p2: _DetailGray, _TintMode, _TintScale, _MarksMode
    //  p3: _Marks2Mode, _Brightness, _Wrap, _PlantMode
    //  p4: _DetailAlpha, _Unlit, _MarksTex_TexelSize.xy
    //  p5: _WetSurface, _NightEmit, _WinMode, _NightMode (NIGHTLIGHTMAP auf Binding 1)
    //  p6: _AlphaSource, _Lit, _Intensity, _NightOnly   (Transparent)
    //  p7: _VcMul (Transparent) bzw. Interieur-Lightmap (Object), nightlmap.xy
    private const string ObjectMat = @"
layout(set = 0, binding = 6) uniform Mat { vec4 color; vec4 p0; vec4 p1; vec4 p2; vec4 p3; vec4 p4; vec4 p5; vec4 p6; vec4 p7; vec4 p8; vec4 p9; vec4 p10; vec4 p11; } m;   // p8 _MainTex_ST, p9 _DetailTex_ST, p10 _WinParams, p11.x _ReflMix
";

    public const string ObjectVert = Common + ObjectInputs + Instancing + ObjectMat + @"
layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vColor;
layout(location = 2) out vec2 vUv0;
layout(location = 3) out vec2 vUv1;
layout(location = 4) out vec2 vUv2;
layout(location = 5) out vec2 vUv3;
layout(location = 6) out vec3 vWorld;
void main() {
    mat4 M = TDU2Model();
    vec4 world = M * vec4(inPos, 1.0);
    gl_Position = pc.viewProj * world;
    // Vegetation (_PlantMode): Normale = Welt-oben wie Gras und Boden
    vNormal = m.p3.w > 0.5 ? vec3(0.0, 1.0, 0.0) : mat3(M) * inNormal;
    vColor = inColor;
    vUv0 = inUv0; vUv1 = inUv1; vUv2 = inUv2; vUv3 = inUv3;
    vWorld = world.xyz;
}";

    public const string ObjectFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;    // _MainTex (COLOR, UV0)
layout(set = 0, binding = 1) uniform sampler2D shadowTex;  // _ShadowTex (SHADOW, UV-Satz 2)
layout(set = 0, binding = 2) uniform sampler2D detailTex;  // _DetailTex (DETAIL)
layout(set = 0, binding = 3) uniform sampler2D tintTex;    // _TintTex (DETAIL2, letzter UV-Satz)
layout(set = 0, binding = 4) uniform sampler2D marksTex;   // _MarksTex (MARKS, UV-Satz 2)
layout(set = 0, binding = 5) uniform sampler2D marks2Tex;  // _Marks2Tex (MARKS2, UV-Satz 3)
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vColor;
layout(location = 2) in vec2 vUv0;
layout(location = 3) in vec2 vUv1;
layout(location = 4) in vec2 vUv2;
layout(location = 5) in vec2 vUv3;
layout(location = 6) in vec3 vWorld;
layout(location = 0) out vec4 outColor;

float TDU2Hash(vec2 p) { p = fract(p * vec2(123.34, 345.45)); p += dot(p, p + 34.345); return fract(p.x * p.y); }
float TDU2Noise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = TDU2Hash(i), b = TDU2Hash(i + vec2(1, 0)), c = TDU2Hash(i + vec2(0, 1)), d = TDU2Hash(i + vec2(1, 1));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
vec3 TDU2LinToSrgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
float TDU2Puddle(vec2 wp) { float n = TDU2Noise(wp * 0.10) * 0.65 + TDU2Noise(wp * 0.37) * 0.35; return smoothstep(0.55, 0.72, n); }

// Unity TDU2Object: _ReflMix > 0 und Fläche auf der Spiegelebene -> Oberfläche = lerp(Farbe, Spiegelbild, Fresnel) (Bildschirm-UV)
vec3 TDU2Planar(vec3 col, vec3 wp) {
    if (m.p11.x <= 0.001 || fr.planar.x < 0.5) return col;
    vec2 suv = gl_FragCoord.xy * fr.planar.zw;
    if (fr.planar.x > 1.5) return texture(planarRefl, suv).rgb;   // Diagnose F11: nur das Spiegelbild
    if (abs(wp.y - fr.planar.y) >= 0.06) return col;
    float ndv = clamp(normalize(fr.camPos.xyz - wp).y, 0.0, 1.0);
    //Reflections Stärke des Bodens
    float w = clamp(m.p11.x + (1.0 - m.p11.x) * pow(1.0 - ndv, 5.0), 0.0, 1.0) * 0.35;
    return col * (1.0 - w) + texture(planarRefl, suv).rgb * w;
}

void main() {
    if (fr.planar.x < -0.5 && vWorld.y < fr.planar.y + 0.06) discard;   // Spiegel-Durchgang: alles bis knapp über den Boden weg (Unity Clip-Ebene +2 cm; hier +6 cm = Toleranz der Ebenenhöhe wie beim Einmischen) – sonst verdeckt der Boden selbst das Spiegelbild
    // Unity-Surface-Shader: uv_MainTex = UV0 x _MainTex_ST, uv2/uv3_DetailTex = UV x _DetailTex_ST (.uva-Animation)
    vec2 uvM = vUv0 * m.p8.xy + m.p8.zw;
    vec2 uvD1 = vUv1 * m.p9.xy + m.p9.zw;
    vec2 uvD2 = vUv2 * m.p9.xy + m.p9.zw;
    vec4 c = texture(mainTex, uvM) * m.color;
    if (m.p1.x > 0.5)
    {
        vec2 duv = m.p1.y > 0.5 ? uvD2 : uvD1;
        vec4 det4 = texture(detailTex, duv);
        vec3 det = det4.rgb;
        if (m.p4.x > 0.5 && m.p2.x < 0.5) c.a = min(c.a, det4.a);
        if (m.p2.x > 0.5) det = det.rrr * 1.8;
        c.rgb = clamp(mix(vec3(0.5), c.rgb, m.p1.z) * det * m.p1.w, 0.0, 1.0);
    }
    if (m.p2.y > 0.5) c.rgb = clamp(c.rgb * texture(tintTex, uvD2).rgb * m.p2.z, 0.0, 1.0);
    if (m.p2.w > 0.5)
    {
        vec2 t = m.p4.zw * 1.5;
        vec4 mk = vec4(0.0);
        for (int ix = -1; ix <= 1; ix++)
            for (int iy = -1; iy <= 1; iy++)
                mk += texture(marksTex, uvD1 + vec2(ix, iy) * t);
        mk /= 9.0;
        float a = smoothstep(0.12, 0.38, mk.a);
        vec3 mcol = mk.a > 0.001 ? clamp(mk.rgb / max(mk.a, 0.2), 0.0, 1.0) : vec3(1.0);
        c.rgb = mix(c.rgb, mcol * 0.85, a);
    }
    if (m.p3.x > 0.5)
    {
        vec4 m2 = texture(marks2Tex, vUv3);
        c.rgb = mix(c.rgb, m2.rgb * 0.85, m2.a);
    }
    if (m.p0.w > 0.0 && c.a < m.p0.w) discard;            // alphatest:_Cutoff

    vec3 vc = mix(vec3(1.0), clamp(vColor.rgb * m.p0.z, 0.0, 1.0), m.p0.y);
    float sh = mix(1.0, texture(shadowTex, uvD1).r, m.p0.x);
    vec3 albedo = c.rgb * vc * sh * m.p3.y;
    // Regen auf der Straße wie das Original TD_Road (UT9-Variante, rain_shadow_spec A.4), nur Straßen (m.p5.x):
    // Pfützen = 0,5·(PUDDLES(1,7213541·Pfützen-UV.yx) + PUDDLES(Pfützen-UV)) − N.y + puddle_height ≥ 0, Pfützen-UV = Pos.xz·0,0703125
    // (beide Abfragen fest am Ort – TD_Road.sob ps 39: mul r2.zw, c0.w(1,721), v5.xywz = vertauschtes oT7.zw, NICHT die
    // laufende Ripple-UV wie in rain_shadow_spec A.4; sonst wandern die nassen Stellen); nass = (Pfütze ? 1 : wet)·(N.y ≥ 0,2);
    // Farbe ·(1 − 0,5·nass); Spiegelung = Würfelkarte(R)·(1 − V·n)·GI·nass; in Pfützen Normal = Ripple
    // ((RIPPLE.xy − 0,5)·2·saturate((25 − Abstand)/15)·ripple_intensity – vs 02: mul r0_sat, geklemmt; ungeklemmt würden ferne
    // Pfützen riesige Normalen bekommen und leuchten). Ripple-UV = Pos.xz·ripple_scale − scroll.
    // Tangentenrahmen hier Welt-X/Z (Straßentangenten liegen nicht vor); Gloss-Bump außerhalb der Pfützen fehlt (flach).
    float rainWet = 0.0;
    vec3 rainN = vec3(0.0, 1.0, 0.0);
    if (m.p5.x > 0.5 && fr.rainSpecs.x > 0.0)
    {
        vec3 gp = vWorld - vec3(fr.misc.z, 0.0, fr.misc.w);   // Spielkoordinaten
        vec3 N = normalize(vNormal);
        float flatM = N.y >= 0.2 ? 1.0 : 0.0;
        vec2 ruv = gp.xz * fr.rainRipple.w - fr.rainRipple.xy;
        float rf = clamp((25.0 - length(fr.camPos.xyz - vWorld)) * (1.0 / 15.0), 0.0, 1.0) * fr.rainRipple.z;
        vec2 rip = (TDU2LinToSrgb(texture(rainRippleTex, ruv).rgb).xy - 0.5) * 2.0 * rf;
        vec2 puv = gp.xz * 0.0703125;
        float p = 0.5 * (TDU2LinToSrgb(texture(rainPuddleTex, 1.7213541 * puv.yx).rgb).x + TDU2LinToSrgb(texture(rainPuddleTex, puv).rgb).x);
        bool pud = (p - N.y + fr.rainSpecs.w) >= 0.0;
        rainWet = (pud ? 1.0 : fr.rainSpecs.y) * flatM;
        vec2 b2 = pud ? rip : vec2(0.0);
        rainN = normalize(vec3(b2.x, 0.0, b2.y) + N / max(N.y, 1e-3));
        albedo *= 1.0 - 0.5 * rainWet;
    }

    vec3 emission = albedo * m.p4.y;                       // _Unlit (Lightmap mit eingebackenem Licht)
    albedo *= (1.0 - m.p4.y);
    emission += c.rgb * m.p5.y * fr.misc.x;                // _NightEmit x _TDU2NightK
    vec3 glowC = c.rgb * m.p5.y * fr.misc.x;               // Bloom-Maske (Zusatz PostFx): selbstleuchtende Teile (Neon, Fenster)
    if (m.p5.z > 0.5 && fr.env.x > 0.01)
    {
        // Original TD_Building (gWindowMapTexSampler): Licht = Fensterrauschen(Fensterkachel) x Helligkeit(COLOR) x
        // luminance_power.x, nur wo GLOSS.b über luminance_power.y liegt (Fensterscheiben). Kachel aus 2.6-m-Raster.
        float g = texture(marksTex, uvD1).b;                   // _GlossTex
        float mask = g > m.p10.y ? g : 0.0;
        vec3 wpos = vWorld - vec3(fr.misc.z, 0.0, fr.misc.w);  // Spielkoordinaten (Raster bleibt am Ort)
        vec3 cell = floor(wpos / 2.6);
        vec2 nuv = (cell.xz + cell.y * vec2(17.0, 31.0) + 0.5) / 64.0;
        vec3 win = texture(marks2Tex, nuv).rgb;                // _WinTex
        float lum = dot(texture(mainTex, uvM).rgb, vec3(0.3, 0.59, 0.11));
        emission += win * lum * m.p10.x * mask * fr.env.x * 0.5;
        glowC += win * lum * m.p10.x * mask * fr.env.x * 0.5;
    }
    float glowA = clamp(max(glowC.r, max(glowC.g, glowC.b)) * 2.0, 0.0, 1.0);
    if (m.p5.w > 0.5)
    {
        // Original TD_Building (NIGHTLIGHTMAP s8):
        //   Farbe = COLOR x DETAIL x lerp(night.rgb, Licht, nightlmap.x) + night.rgb x night.a x nightlmap.y  (ohne Detail-Verstärkung)
        // nightlmap (0.44, 1) gilt nachts; tagsüber (_TDU2NightK = 0) nur Echtzeitlicht, gebackene Lampen aus.
        vec4 nl = texture(shadowTex, uvM);
        if (m.p7.x > 1.5)
        {
            // TD_Furnitures (TD_Furnitures.sob ps_3_0, Slots s0 gOcclusionTexSampler, s1 gDetailTexSampler, s7 gNightLightMapSampler):
            //   Licht = lerp(Ambient x (0,5 + 0,5 N.L), Sonne, N.L x Schatten) – im Haus wie bisher fest 0,25 [vermutet]
            //   Farbe = DETAIL x baseColor x lerp(night.rgb, OCCLUSION x Licht, nightlmap.x) + night.rgb x night.a x nightlmap.y
            // OCCLUSION wirkt nur auf den Licht-Anteil, nicht auf die Nacht-Lightmap; keine Vertexfarbe (vs ohne COLOR-Eingang).
            vec3 occ = texture(mainTex, uvM).rgb;
            vec3 fdet = vec3(1.0);
            if (m.p1.x > 0.5)
            {
                fdet = texture(detailTex, m.p1.y > 0.5 ? uvD2 : uvD1).rgb;
                if (m.p2.x > 0.5) fdet = fdet.rrr * 1.8;
            }
            vec3 fcol = fdet * m.color.rgb * mix(nl.rgb, occ * vec3(0.25), m.p7.y) * m.p3.y + nl.rgb * nl.a * m.p7.z;
            outColor = vec4(TDU2Fog(TDU2Planar(fcol, vWorld), vWorld), glowA);
            return;
        }
        if (m.p7.x > 0.5)
        {
            // Interieur (Unity TDU2Object _NightMode mit InteriorFx): Licht ist eingebacken, keine Sonne/Ambient
            //   Emission = Albedo x lerp(night.rgb, _NightAmbient 0.9, nightlmap.x) + night.rgb x night.a x nightlmap.y
            //   Albedo  *= nightlmap.x x _NightLitScale (0)
            outColor = vec4(TDU2Fog(TDU2Planar(emission + albedo * mix(nl.rgb, vec3(0.25), m.p7.y) + nl.rgb * nl.a * m.p7.z, vWorld), vWorld), glowA);
            return;
        }
        float k = fr.misc.x;
        float nx = mix(1.0, m.p7.y, k), ny = m.p7.z * k;
        float boost = m.p1.x > 0.5 ? m.p1.w : 1;          // nur Detail-Verstärkung herausrechnen (_Brightness gilt wie bei allen Objekten)
        albedo /= max(boost, 1e-3);
        emission += albedo * nl.rgb * (1.0 - nx) + nl.rgb * nl.a * ny;
        albedo *= nx * mix(m.p7.w, 1.0, k);                 // Tageshelligkeit (LightingConfig.NightLightmapDayBoost)
    }

    // LightingTDU2Wrap
    float ndl = dot(normalize(vNormal), normalize(fr.sunDir.xyz));
    float diff = clamp(ndl * (1.0 - m.p3.z) + m.p3.z, 0.0, 1.0);
    vec3 lit = albedo * (fr.sunColor.rgb * diff * TDU2Shadow(vWorld, vNormal) + fr.ambient.rgb + TDU2Lamps(vWorld, vNormal)) + emission;
    if (rainWet > 0.0)
    {
        vec3 V = normalize(fr.camPos.xyz - vWorld);
        float fres = (1.0 - dot(V, rainN)) * fr.rainSpecs.x;
        vec3 R = reflect(-V, rainN);
        vec3 env = fr.probe.x > 0.5 ? texture(envCube, R).rgb : TDU2Sky(R);   // Original: Umgebungswürfel des Spielerautos
        // TD_Road rechnet im Gamma-Raum: out = Farbe + Würfel·fres·nass (beides Gamma) -> hier ebenso zusammensetzen,
        // sonst wirkt die Spiegelung (linear addiert) viel zu stark, besonders bei flachem Blick in der Ferne
        vec3 litG = TDU2LinToSrgb(max(lit, vec3(0.0))) + TDU2LinToSrgb(max(env, vec3(0.0))) * (fres * rainWet);
        lit = pow(litG, vec3(2.2));
    }
    outColor = vec4(TDU2Fog(TDU2Planar(lit, vWorld), vWorld), glowA);
}";

    // ObjectTransparent
    public const string TransparentVert = Common + ObjectInputs + ObjectMat + @"
layout(location = 0) out vec4 vUv;      // xy = COLOR, zw = DETAIL
layout(location = 1) out vec4 vColor;
layout(location = 2) out vec3 vLight;
void main() {
    vec4 world = pc.model * vec4(inPos, 1.0);
    gl_Position = pc.viewProj * world;
    vUv.xy = inUv0 * m.p8.xy + m.p8.zw;                        // _MainTex_ST
    vUv.zw = (m.p1.y > 0.5 ? inUv2 : inUv1) * m.p9.xy + m.p9.zw; // _DetailTex_ST
    vColor = inColor;
    vec3 n = normalize(mat3(pc.model) * inNormal);
    float ndl = abs(dot(n, normalize(fr.sunDir.xyz)));     // zweiseitig
    vec3 lit = fr.ambient.rgb + fr.sunColor.rgb * ndl;
    vLight = mix(vec3(1.0), lit, m.p6.y);                  // _Lit
}";

    public const string TransparentFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(set = 0, binding = 2) uniform sampler2D detailTex;
layout(location = 0) in vec4 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 2) in vec3 vLight;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 c = texture(mainTex, vUv.xy) * m.color;
    if (m.p1.x > 0.5)
    {
        vec4 d = texture(detailTex, vUv.zw);
        c.rgb = clamp(c.rgb * (m.p2.x > 0.5 ? d.rrr : d.rgb) * m.p1.w, 0.0, 1.0);
        c.a = mix(c.a, d.a * m.color.a, m.p6.x);          // _AlphaSource
    }
    c *= mix(vec4(1.0), vColor, m.p7.x);                   // _VcMul
    c.rgb *= vLight * m.p6.z;                              // _Intensity
    float nightK = 1.0 - fr.ambient.w * m.p6.w;            // 1 - _TDU2DayDim x _NightOnly
    outColor = c * nightK;
}";

    // Foliage
    //  color = _Color
    //  p0: _VertexColorStrength, _NormalUp, _Wrap, _Translucency
    //  p1: _SelfShadow, _UseVertexColor, _WindStrength, _WindSpeed
    //  p2: _FlutterStrength, _Cutoff
    public const string FoliageVert = Common + ObjectInputs + Instancing + ObjectMat + @"
layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vColor;
layout(location = 2) out vec2 vUv;
layout(location = 3) out vec3 vWorld;
void main() {
    mat4 M = TDU2Model();
    mat3 o2w = mat3(M);
    // Normalen Richtung Objekt-oben mischen (Kronen wirken wie ein Volumen)
    vec3 upObj = normalize(inverse(o2w) * vec3(0.0, 1.0, 0.0));
    vec3 nObj = normalize(mix(inNormal, upObj, m.p0.y));

    // Wind: großes Schwanken (Höhe über dem Stamm) + schnelles Blattflattern
    vec3 wp = (M * vec4(inPos, 1.0)).xyz;
    vec3 origin = (M * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    float h = max(0.0, wp.y - origin.y);
    float phase = dot(origin.xz, vec2(0.071, 0.113));
    float t = fr.viewRight.w * m.p1.w;
    float sway = (sin(t * 0.9 + phase) * 0.6 + sin(t * 2.1 + phase * 1.7) * 0.25) * m.p1.z * 0.012 * h;
    float flutter = sin(t * 7.0 + dot(wp, vec3(1.3, 0.7, 1.1))) * m.p2.x * 0.02 * clamp(h * 0.3, 0.0, 1.0);
    wp += vec3(sway + flutter, flutter * 0.5, sway * 0.6 - flutter);

    gl_Position = pc.viewProj * vec4(wp, 1.0);
    vNormal = o2w * nObj;
    vColor = inColor;
    vUv = inUv0;
    vWorld = wp;
}";

    public const string FoliageFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vColor;
layout(location = 2) in vec2 vUv;
layout(location = 3) in vec3 vWorld;
layout(location = 0) out vec4 outColor;
void main() {
    if (fr.planar.x < -0.5 && vWorld.y < fr.planar.y + 0.06) discard;   // Spiegel-Durchgang: alles bis knapp über den Boden weg (Unity Clip-Ebene +2 cm; hier +6 cm = Toleranz der Ebenenhöhe wie beim Einmischen) – sonst verdeckt der Boden selbst das Spiegelbild
    vec4 c = texture(mainTex, vUv) * m.color;
    if (c.a < m.p2.y) discard;                                  // alphatest:_Cutoff
    vec3 vc = mix(vec3(1.0), clamp(vColor.rgb * 2.0, 0.0, 1.0), m.p0.x * m.p1.y);
    vec3 albedo = c.rgb * vc;
    // LightingFoliage (Original TD_Vegetation): Wrap + Durchleuchtung, Echtzeitschatten folgen
    vec3 n = normalize(vNormal);
    if (!gl_FrontFacing) n = -n;                                // Cull Off: Rückseite
    float ndl = dot(n, normalize(fr.sunDir.xyz));
    float wrap = clamp((ndl + m.p0.z) / (1.0 + m.p0.z), 0.0, 1.0);
    float back = clamp(-ndl, 0.0, 1.0) * m.p0.w;
    float atten = mix(1.0, TDU2Shadow(vWorld, n), m.p1.x);   // _SelfShadow
    outColor = vec4(TDU2Fog(albedo * fr.sunColor.rgb * (wrap + back) * atten + albedo * fr.ambient.rgb, vWorld), 1.0);
}";

    // Sprite
    //  color = _Color
    //  p0: _Cutoff, _CrownHeight, _CrownOn, _UseVertexColor
    public const string SpriteVert = Common + ObjectInputs + Instancing + ObjectMat + @"
layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vColor;
layout(location = 2) out vec2 vUv;
layout(location = 3) out vec3 vWorld;
void main() {
    mat4 M = TDU2Model();
    mat3 o2w = mat3(M);
    mat3 w2o = inverse(o2w);
    // Eckoffset (UV1, Meter) zur Kamera ausrichten (UNITY_MATRIX_V[0]/[1] in den Objektraum)
    vec3 right = normalize(w2o * fr.viewRight.xyz);
    vec3 up    = normalize(w2o * fr.viewUp.xyz);
    vec3 p = inPos + right * inUv1.x + up * inUv1.y;
    vec3 wp = (M * vec4(p, 1.0)).xyz;
    // Kugel-Normale um die Kronenmitte (RAMPCLR1.x)
    vec3 center = (M * vec4(0.0, m.p0.y, 0.0, 1.0)).xyz;
    vec3 nw = wp - center;
    if (m.p0.z < 0.5 || dot(nw, nw) < 1e-6) nw = vec3(0.0, 1.0, 0.0);
    vNormal = normalize(nw);
    // Wind wie TDU2/Foliage (großes Schwanken): Krone bewegt sich mit dem Stamm
    vec3 origin = (M * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    float h = max(0.0, wp.y - origin.y);
    float phase = dot(origin.xz, vec2(0.071, 0.113));
    float t = fr.viewRight.w * m.p1.y;
    float sway = (sin(t * 0.9 + phase) * 0.6 + sin(t * 2.1 + phase * 1.7) * 0.25) * m.p1.x * 0.012 * h;
    wp += vec3(sway, 0.0, sway * 0.6);
    gl_Position = pc.viewProj * vec4(wp, 1.0);
    vWorld = wp;
    vColor = inColor;
    vUv = inUv0;
}";

    public const string SpriteFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vColor;
layout(location = 2) in vec2 vUv;
layout(location = 3) in vec3 vWorld;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 c = texture(mainTex, vUv) * m.color;
    if (c.a < m.p0.x) discard;                                  // alphatest:_Cutoff
    vec3 vc = mix(vec3(1.0), vColor.rgb, m.p0.w);
    vec3 albedo = c.rgb * vc;
    float gloss = m.p0.z > 0.5 ? vColor.a : 0.0;                // Vertexalpha = Anteil N.L
    float ndl = max(0.0, dot(normalize(vNormal), normalize(fr.sunDir.xyz)));
    float x = 1.0 + gloss * (ndl - 1.0);
    outColor = vec4(TDU2Fog(albedo * fr.sunColor.rgb * x * TDU2Shadow(vWorld, vNormal) + albedo * fr.ambient.rgb, vWorld), 1.0);
}";

    // TD_Impostor (V_*_H-I): 4-Vertices-Quad, inPos.y = Höhe, inPos.z = horizontale Ausdehnung.
    // Nur die Horizontale wird zylindrisch zur Kamera gedreht; die Höhe bleibt unverändert (keine Streckung).
    //  color = _Color, p0.x = _Cutoff
    public const string ImpostorVert = Common + ObjectInputs + Instancing + ObjectMat + @"
layout(location = 0) out vec2 vUv;
layout(location = 1) out vec3 vWorld;
void main() {
    mat4 M = TDU2Model();
    vec3 origin = M[3].xyz;
    // lokale Y-Achse in Welt (Höhe, Skalierung aus der Instanzmatrix)
    vec3 sy = M[1].xyz;
    float syLen = length(sy);
    vec3 up = syLen > 1e-6 ? sy / syLen : vec3(0.0, 1.0, 0.0);
    // lokale Z-Achse = Breite der Karte; nur diese kommt auf die Kamera-Rechte (XZ-Projektion)
    float szLen = length(M[2].xyz);
    vec3 right = vec3(fr.viewRight.x, 0.0, fr.viewRight.z);
    float rl = length(right);
    right = rl > 1e-6 ? right / rl : vec3(1.0, 0.0, 0.0);
    vec3 wp = origin + up * (inPos.y * syLen) + right * (inPos.z * szLen);
    gl_Position = pc.viewProj * vec4(wp, 1.0);
    vUv = inUv0;
    vWorld = wp;
}";

    public const string ImpostorFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec3 vWorld;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 c = texture(mainTex, vUv) * m.color;
    if (c.a < m.p0.x) discard;                                  // alphatest
    // TD_Impostor.ps: tex * (__AmbLight_Ambient_1 + __AmbLight_Ambient_0). Der verschwindend kleine
    // zweite Slot (impostor_ambient ~0,2 gamma) ist hier als konstanter linearer Anteil enthalten.
    vec3 amb = fr.ambient.rgb + vec3(0.033);
    outColor = vec4(TDU2Fog(c.rgb * amb, vWorld), 1.0);
}";

    // SpriteGlow
    //  color = _Color
    //  p0: _Intensity, _NightOnly
    public const string SpriteGlowVert = Common + ObjectInputs + ObjectMat + @"
layout(location = 0) out vec2 vUv;
void main() {
    mat3 w2o = inverse(mat3(pc.model));
    vec3 right = normalize(w2o * fr.viewRight.xyz);
    vec3 up    = normalize(w2o * fr.viewUp.xyz);
    vec3 p = inPos + right * inUv1.x + up * inUv1.y;
    gl_Position = pc.viewProj * (pc.model * vec4(p, 1.0));
    vUv = inUv0;
}";

    public const string SpriteGlowFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 c = texture(mainTex, vUv) * m.color;
    c.rgb *= m.p0.x;
    c *= 1.0 - fr.ambient.w * m.p0.y;                           // 1 - _TDU2DayDim x _NightOnly
    outColor = c;
}";

    // Overlay (Debug-UI): Pixel -> NDC über viewProj
    public const string OverlayVert = Common + ObjectInputs + @"
layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;
void main() {
    gl_Position = pc.viewProj * vec4(inPos.xy, 0.0, 1.0);
    vUv = inUv0;
    vColor = inColor;
}";

    public const string OverlayFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 0) out vec4 outColor;
void main() {
    outColor = texture(mainTex, vUv) * vColor;
}";

    // Overlay über allem (IRenderer.DrawOverlayTop, Entwicklermenü) im UI-Durchgang: Ziel ist die UNORM-Sicht, die sRGB-Kodierung
    // des Weltdurchgangs (sRGB-Ziel) wird hier von Hand gemacht -> gleiche Farben wie OverlayFrag.
    public const string OverlayUiFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 0) out vec4 outColor;
vec3 ToSrgb(vec3 c) {
    c = clamp(c, 0.0, 1.0);
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}
void main() {
    vec4 c = texture(mainTex, vUv) * vColor;
    outColor = vec4(ToSrgb(c.rgb), c.a);
}";

    // FlashQuad
    //  Pixelkoordinaten (0,0 links oben). Push-Konstanten: viewProj[0][0]/[1][1] Skalierung, [3].xy Versatz,
    //  [2][0] _LumAlpha, [2][1] _MaskOn; model[0] _Mul, [1] _Add, [2] _ClipRect, [3] _MaskRect.
    //  binding 0 _MainTex, 1 _MaskTex. Blend SrcAlpha/OneMinusSrcAlpha, ohne Tiefe.
    public const string FlashVert = Common + ObjectInputs + @"
layout(location = 0) out vec2 vUv;
layout(location = 1) out vec2 vPix;
void main() {
    gl_Position = vec4(inPos.x * pc.viewProj[0][0] + pc.viewProj[3][0], inPos.y * pc.viewProj[1][1] + pc.viewProj[3][1], 0.0, 1.0);
    vUv = inUv0;
    vPix = inPos.xy;
}";

    public const string FlashFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(set = 0, binding = 1) uniform sampler2D maskTex;
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec2 vPix;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 clipR = pc.model[2];
    // SWF-Maske (ClipDepth) als Rechteck in Pixeln
    if (vPix.x < clipR.x || vPix.y < clipR.y || vPix.x > clipR.z || vPix.y > clipR.w) discard;
    if (pc.viewProj[2][1] > 0.5) {
        // Maskenform: gerasterte Shape, oberste Zeile (v = 1) bei _MaskRect.y
        vec4 mr = pc.model[3];
        vec2 muv = vec2((vPix.x - mr.x) / (mr.z - mr.x), 1.0 - (vPix.y - mr.y) / (mr.w - mr.y));
        if (muv.x < 0.0 || muv.x > 1.0 || muv.y < 0.0 || muv.y > 1.0 || texture(maskTex, muv).a < 0.02) discard;
    }
    vec4 t = texture(mainTex, vUv);
    if (pc.viewProj[2][0] > 0.5) t = vec4(1.0, 1.0, 1.0, max(t.r, max(t.g, t.b)) * t.a);
    outColor = clamp(t * pc.model[0] + pc.model[1], 0.0, 1.0);
    // Unity (Color Space Linear): IMGUI/GL in OnGUI schreibt ohne sRGB-Kodierung -> der linear gelesene Wert landet direkt im
    // Bild und wird dort auch so gemischt. [2][3] = so ausgeben (Flash-Filme), [1][3] = Ziel ist die UNORM-Sicht (ohne Kodierung).
    bool unityGui = pc.viewProj[2][3] > 0.5, rawTarget = pc.viewProj[1][3] > 0.5;
    vec3 c = outColor.rgb;
    if (rawTarget && !unityGui)      // Logo/Video: Bildwerte unverändert zeigen -> selbst kodieren
        outColor.rgb = mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
    else if (!rawTarget && unityGui) // ohne UNORM-Sicht (sRGB-Ziel): angenähert, Mischen bleibt linear
        outColor.rgb = mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}";

    // Grass 
    //  binding 0 _MainTex (Atlas), 1 _GroundTex (Colormap, UV1), 2 _SplatTex (UV1)
    //  color = _Color
    //  p0: _Cutoff, _Wind, _GroundOn, _GroundBoost
    //  p1: _GroundTipMix, _UseSplat, _ShadowStrength
    public const string GrassVert = Common + ObjectInputs + ObjectMat + @"
layout(location = 0) out vec2 vUv;
layout(location = 1) out vec2 vUvGround;
layout(location = 2) out vec4 vColor;
layout(location = 3) out vec3 vWorld;
void main() {
    vec3 p = inPos;
    // Windbewegung an der Spitze stärker als an der Basis
    float top = 1.0 - clamp(inUv0.y, 0.0, 1.0);
    vec3 wp0 = (pc.model * vec4(p, 1.0)).xyz;
    float wind = sin(fr.viewRight.w * 1.7 + wp0.x * 0.3 + wp0.z * 0.2) * m.p0.y * top;
    p.x += wind;
    p.z += wind * 0.5;
    vec3 wp = (pc.model * vec4(p, 1.0)).xyz;
    wp.y -= fr.camPos.w;                                   // mit dem Gelände absenken (_TDU2TerrainLower)
    gl_Position = pc.viewProj * vec4(wp, 1.0);
    vUv = inUv0;
    vUvGround = inUv1;
    vColor = inColor;
    vWorld = wp;
}";

    public const string GrassFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(set = 0, binding = 1) uniform sampler2D groundTex;
layout(set = 0, binding = 2) uniform sampler2D splatTex;
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec2 vUvGround;
layout(location = 2) in vec4 vColor;
layout(location = 3) in vec3 vWorld;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 c = texture(mainTex, vUv) * m.color;
    if (c.a < m.p0.x) discard;                             // alphatest:_Cutoff
    // wie Unity TDU2/Grass + TD_TallGrass ps_0: Grastextur x lerp(Bodenfarbe, 1, oT3.x) x gebackener Schatten.
    vec3 ground = m.p0.z > 0.5 ? mix(clamp(texture(groundTex, vUvGround).rgb * m.p0.w, 0.0, 1.0), vec3(1.0), vColor.a) : vec3(1.0);
    float sh = m.p1.y > 0.5 ? mix(1.0, texture(splatTex, vUvGround).r, m.p1.z) : 1.0;
    vec3 albedo = c.rgb * vColor.rgb * ground * sh;
    // LightingPlant: Normale = oben
    float ndl = clamp(normalize(fr.sunDir.xyz).y, 0.0, 1.0);
    outColor = vec4(TDU2Fog(albedo * (fr.sunColor.rgb * ndl * TDU2Shadow(vWorld, vec3(0.0, 1.0, 0.0)) + fr.ambient.rgb + TDU2Lamps(vWorld, vec3(0.0, 1.0, 0.0))), vWorld), 1.0);
}";

    // Water
    //  binding 0 _NormalMap
    //  color = _PoolTint
    //  p0: _NMapScale.xy, _NMapWeight.xy
    //  p1: _WaveStrength, _WaveSpeed, _FlatDistance, _HasNormalMap
    //  p2: _Fresnel (Bias, Power, Scale), _ReflBlur
    //  p3: _ReflColor.rgb, _SpecPower
    //  p4: _SpecColorCut
    public const string WaterVert = Common + ObjectInputs + ObjectMat + @"
layout(location = 0) out vec3 vWorld;
void main() {
    vec4 world = pc.model * vec4(inPos, 1.0);
    gl_Position = pc.viewProj * world;
    vWorld = world.xyz;
}";

    // Meer (m.p6.x = 1): Original TD_Ocean ps_0 (Euro/Shaders/TD_Ocean.sob, selbst disassembliert, mit _sat; ocean_spec §2.7)
    //  binding 0 NoiseNMap (World/OceanNoise), 1 Colormap der Ebene; p5 = Colormap-Ursprung/1:Ausdehnung (Spielkoordinaten);
    //  Spiegelbild = planarRefl (SEAREF_N, ohne Himmel, gelöscht mit ocean_reflectionColor), sonst ocean_reflectionColor.
    //  Werte des Originals sind Gamma-Farben -> Farbkonstanten linear umgerechnet.
    public const string WaterFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D normalMap;
layout(set = 0, binding = 1) uniform sampler2D colorMap;
layout(location = 0) in vec3 vWorld;
layout(location = 0) out vec4 outColor;

vec3 OceanTD() {
    vec3 toCam = fr.camPos.xyz - vWorld;                   // TC1.xyz (Welt, zur Kamera)
    float dist = length(toCam);
    vec2 g = vWorld.xz - fr.misc.zw;                       // Spielkoordinaten (ShiftWorld)
    // Abflachung/Abschwächung in der Ferne
    float h = clamp(0.02 * toCam.y, 0.0, 1.0);
    float a = (1.0 - h * h * (3.0 - 2.0 * h)) * 0.7 + dist / 224.0;   // TC1.w = Sichttiefe / 224
    a = clamp((a - 1.2) * 1.6666667, 0.0, 1.0);
    vec3 fwd = normalize(cross(fr.viewRight.xyz, fr.viewUp.xyz));
    float A = a * a * (3.0 - 2.0 * a) * 0.38 * pow(max((1.0 - fwd.y * fwd.y) * 1.05, 0.0), 5.5);   // TC3.z = Blickrichtung.y
    float flatK = clamp(2.0 * A, 0.0, 1.0), att = clamp(A, 0.0, 1.0);
    vec3 F = fr.oceanFresnel.xyz;                          // ocean_fresnel -> mp1 = (F.x*F.z, (1-F.x)*F.z, F.y)
    float base = fr.oceanParam.w * att + F.x * F.z;
    // Normale aus NoiseNMap: LR- und HR-Kachelung (TC0 = Welt.xz * ocean_NMapLR.x / HR.x)
    vec3 n = (texture(normalMap, g * fr.oceanMisc.y).xyz - 0.5) * (2.0 * fr.oceanParam.x)
           + (texture(normalMap, g * fr.oceanMisc.x).xyz - 0.5) * (2.0 * fr.oceanParam.y);
    n.xz *= exp(-0.001);
    vec3 nn = normalize(n);
    vec3 N = normalize(nn + flatK * (vec3(0.0, 1.0, 0.0) - nn));
    vec3 R;
    if (fr.oceanParam2.y > 0.5 && m.p6.z >= 0.0) {          // Pools (OceanLevel -1): keine Spiegelung der Meeresebene
        vec2 suv = gl_FragCoord.xy * fr.planar.zw;
        suv.x += exp(-fr.oceanParam.z) * fr.oceanMisc.z * N.x;   // Verzerrung nur waagrecht
        R = texture(planarRefl, clamp(suv, 0.0, 1.0)).rgb;
    } else R = pow(max(fr.oceanRefl.rgb, vec3(0.0)), vec3(2.2));
    vec3 C = m.p6.y > 0.5 ? texture(colorMap, clamp((g - m.p5.xy) * m.p5.zw + 0.01 * N.xz, 0.0, 1.0)).rgb : vec3(0.02, 0.12, 0.16);
    vec3 L = normalize(fr.sunDir.xyz);
    vec3 light = fr.ambient.rgb + max(L.y, 0.0) * fr.sunColor.rgb;   // Ambient + max(L.y,0) * (Diffus - Ambient)
    vec3 V = toCam / max(dist, 1e-3);
    float Fr = clamp(base + (1.0 - F.x) * F.z * pow(1.0 - clamp(dot(V, N), 0.0, 1.0), F.y), 0.0, 1.0);
    vec3 col = mix(C * light, R, Fr);
    vec3 r = reflect(-V, N);
    col += pow(max(dot(r, L), 0.0), max(fr.oceanParam2.x, 1.0)) * pow(max(fr.oceanSpec.rgb, vec3(0.0)), vec3(2.2));
    return col;
}

// Ersatz ohne Normalmap: Summe gedrehter Sinuswellen (Ableitungen = Neigung)
vec2 WaveSlope(vec2 p, float t) {
    vec2 s = vec2(0.0);
    const vec2 d0 = vec2(0.80, 0.60), d1 = vec2(-0.45, 0.89), d2 = vec2(0.97, -0.24), d3 = vec2(-0.70, -0.71);
    s += d0 * cos(dot(p, d0) * 6.0 + t * 1.7) * 0.30;
    s += d1 * cos(dot(p, d1) * 9.5 + t * 2.3) * 0.22;
    s += d2 * cos(dot(p, d2) * 15.0 + t * 3.1) * 0.15;
    s += d3 * cos(dot(p, d3) * 23.0 + t * 3.9) * 0.10;
    return s;
}
vec3 SampleN(vec2 uv) { vec3 n = texture(normalMap, uv).rgb * 2.0 - 1.0; return vec3(n.x, n.z, n.y); }

void main() {
    if (m.p6.x > 0.5) { outColor = vec4(TDU2Fog(OceanTD(), vWorld), 1.0); return; }   // TD_Ocean: deckend
    bool w = fr.oceanFresnel.w > 0.5;                      // _TDU2OceanSet (Wetter)
    vec4 fres4 = vec4(w ? fr.oceanFresnel.xyz : m.p2.xyz, 0.0);
    vec3 specC = w ? fr.oceanSpec.rgb : m.p4.rgb; float specCut = w ? fr.oceanSpec.a : m.p4.a;
    float specPow = max(1.0, w ? fr.oceanRefl.w : m.p3.w);
    vec3 reflC = (w ? fr.oceanRefl.rgb : m.p3.rgb) * 2.0;
    vec2 sc = w && fr.oceanMisc.x > 0.0 ? fr.oceanMisc.xy : m.p0.xy;

    float t = fr.viewRight.w * m.p1.y;
    vec2 p = vWorld.xz - fr.misc.zw;                       // Spielkoordinaten (Wellen bleiben am Ort)
    vec3 n;
    if (m.p1.w > 0.5) {
        vec3 n1 = SampleN(p * sc.x + t * vec2(0.021, 0.013));
        vec3 n2 = SampleN(p * sc.y + t * vec2(-0.004, 0.006));
        n = n1 * m.p0.z + n2 * m.p0.w;
        n.xz *= m.p1.x;
    } else {
        vec2 s = WaveSlope(p * sc.x * 0.6, t) + WaveSlope(p * sc.y * 0.6 + 3.7, t * 0.5) * 1.5;
        n = vec3(-s.x * m.p1.x * 0.35, 1.0, -s.y * m.p1.x * 0.35);
    }
    n = normalize(n);
    vec3 toCam = fr.camPos.xyz - vWorld;
    float dist = length(toCam);
    vec3 v = toCam / max(dist, 1e-3);
    n = normalize(mix(n, vec3(0, 1, 0), clamp(dist / max(1.0, m.p1.z), 0.0, 1.0)));

    // Fresnel wie TD_Ocean: Bias + Scale * (1 - N.V)^Power
    float fres = clamp(fres4.x + fres4.z * pow(1.0 - clamp(dot(v, n), 0.0, 1.0), fres4.y), 0.0, 1.0);
    vec3 r = reflect(-v, n);
    r.y = abs(r.y);
    vec3 refl = TDU2Sky(r) * reflC;

    vec3 l = normalize(fr.sunDir.xyz);
    float sunUp = clamp(l.y * 4.0, 0.0, 1.0);
    float spec = pow(clamp(dot(r, l), 0.0, 1.0), specPow);
    spec = max(0.0, spec - specCut);
    vec3 sunTotal = fr.sunColor.rgb;                       // _LightColor0
    float lum = clamp(dot(sunTotal, vec3(0.3, 0.59, 0.11)), 0.0, 1.0);
    float lit = lum;
    if (m.color.a > 0.0) {
        float ambLum = dot(fr.ambient.rgb, vec3(0.3, 0.59, 0.11));
        if (dot(refl, vec3(0.333)) < 0.08) refl = max(refl, fr.ambient.rgb * reflC);
        lit = clamp(max(lum, ambLum * 1.5), 0.0, 1.0);
    }
    vec3 col = refl * fres + specC * spec * lum * sunUp;
    float alpha = fres;
    if (m.color.a > 0.0) {
        vec3 water = m.color.rgb * (0.45 + 0.55 * lit);
        float a0 = m.color.a;
        col += water * (1.0 - fres) * a0;
        alpha = fres + (1.0 - fres) * a0;
    }
    // UNITY_APPLY_FOG_COLOR(fog, o, fogColor x alpha): vormultipliziert
    if (fr.env.w > 0.5) {
        float f = clamp((fr.env.z - dist) / max(fr.env.z - fr.env.y, 1.0), 0.0, 1.0);
        col = mix(fr.fogColor.rgb * alpha, col, f);
    }
    outColor = vec4(col, alpha);                           // Blend One OneMinusSrcAlpha (vormultipliziert)
}";

    //  Himmel (Vollbild-Dreieck, viewProj = inverse Rotations-ViewProj)
    public const string SkyVert = Common + @"
layout(location = 0) out vec3 vDir;
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0;
    gl_Position = vec4(p, 0.0, 1.0);
    vec4 w = pc.viewProj * vec4(p, 0.5, 1.0);
    vDir = w.xyz / w.w;
}";

    public const string SkyFrag = Common + @"
layout(location = 0) in vec3 vDir;
layout(location = 0) out vec4 outColor;
void main() { outColor = vec4(TDU2Sky(vDir), 1.0); }";

    //  Schattenkarten (nur Tiefe, Alpha-Test wie im Bild)
    public const string ShadowObjectFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 2) in vec2 vUv0;
void main() {
    if (m.p0.w > 0.0 && texture(mainTex, vUv0 * m.p8.xy + m.p8.zw).a * m.color.a < m.p0.w) discard;
}";

    public const string ShadowFoliageFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 2) in vec2 vUv;
void main() { if (texture(mainTex, vUv).a * m.color.a < m.p2.y) discard; }";

    public const string ShadowSpriteFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 2) in vec2 vUv;
void main() { if (texture(mainTex, vUv).a * m.color.a < m.p0.x) discard; }";

    public const string ShadowTerrainVert = Common + @"
layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
void main() {
    vec3 wp = (pc.model * vec4(inPos, 1.0)).xyz;
    wp.y -= fr.camPos.w;                                   // _TDU2TerrainLower
    gl_Position = pc.viewProj * vec4(wp, 1.0);
}";

    // Car, TDU2/CarGlass, TDU2/CarShadow
    // Material-Properties (binding 6):
    //  c0 _Color, c1 _Color2, c2 _Bicolour,_RampPow,_MainGray,_AlphaFromRed, c3 _Heat,_Glossiness,_Metallic,_Cutoff
    //  c4 _HeatColor, c5 _EmissionTint, c6 _Emission,_EmissionDiff,_SelfIllum,_Occlusion, c7 _ClearCoat,_ClearCoatPower,_Flake,_FlakeScale
    //  c8 _UseMask,_LgtLights,_LgtBrake,_LgtReverse, c9 _LgtBlink,_UseSticker,_StickerUV,_StickerAlphaOnly
    //  c10 _DirtLevel,_ScratchLevel,_MaxAlpha,_Strength, c11 _DirtColor, c12..c15 Tail/Brake/Reverse/Blink-Farbe, c16 Intensitäten
    //  c17 MATPARAM, c18 ENVCOEFF, c19 SPECPARM, c20 LAYRCLR1, c21 (TdPaint, TdLayer, RAMPPOW.y, Flocken an) – Original-Lack TD_CarPaint
    //  c22 RAMPFLAKE (roh, Gamma-Raum), c23 FLAKEABNS – Flocken-Variante TD_CarPaint2
    private const string CarMat = @"
layout(set = 0, binding = 6) uniform CarMat { vec4 c[24]; } cm;
";

    public const string CarVert = Common + ObjectInputs + CarMat + @"
layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv0;
layout(location = 2) out vec2 vUv1;
layout(location = 3) out vec3 vWorld;
layout(location = 4) flat out float vMirror;
layout(location = 5) out vec3 vObj;    // Objektraum-Position (Flocken, Original o7 = v0)
layout(location = 6) out vec3 vObjN;   // Objektraum-Normale (Gewichte der Flocken-Projektion, Original o4)
void main() {
    vObj = inPos; vObjN = inNormal;
    vec4 world = pc.model * vec4(inPos, 1.0);
    gl_Position = pc.viewProj * world;
    vNormal = mat3(pc.model) * inNormal;
    vUv0 = inUv0; vUv1 = inUv1;
    vWorld = world.xyz;
    // gespiegelte Knoten (z.B. rechte Räder als Spiegelkopie, Skalierung -1): Vorder-/Rückseite vertauscht
    vMirror = determinant(mat3(pc.model)) < 0.0 ? -1.0 : 1.0;
}";

    // Standard-Licht (SurfaceOutputStandard): GGX + Schlick, Umgebung = Himmel (unity_SpecCube0 = Skybox)
    // Umgebung der Spiegelungen. Im Unity-Projekt lief eine Echtzeit-Reflexionssonde (TDU2VehicleManager.MirrorReflections):
    // unterhalb des Horizonts spiegeln Boden, Bäume und Häuser (dunkel), nicht der helle Himmelsverlauf.
    private const string CarEnv = @"
// Original-Shader rechnen im Gamma-Raum (D3D9 ohne sRGB). Die Engine rechnet linear (wie das Unity-Projekt, Color Space Linear):
// Eingänge nach Gamma, Original-Formel, Ergebnis zurück nach linear.
vec3 TdG(vec3 c) { c = max(c, vec3(0.0)); return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 TdL(vec3 c) { c = max(c, vec3(0.0)); return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
// Licht für die Original-Lacke (Gamma-Raum). Mit Autolicht des Originals (GraphicsConfig.OriginalCarLight): Werte aus
// TDU2CarLighting (sun_* × car_sun_*_factor, entsättigt). Sonst wie bisher aus dem Unity-Licht (Sonne = Diffus − Ambient).
bool CarOrigLight() { return fr.carAmbient.w > 0.5; }
void CarLights(out vec3 amb, out vec3 dif, out vec3 spc) {
    if (CarOrigLight()) { amb = fr.carAmbient.rgb; dif = fr.carDiffuse.rgb; spc = fr.carSpecular.rgb; return; }
    amb = TdG(fr.ambient.rgb); dif = TdG(fr.sunColor.rgb); spc = dif;
}
vec3 CarEnv(vec3 r) {
    r = normalize(r);
    // Unity: Echtzeit-Reflexionssonde am Auto (unity_SpecCube0, 256, Faces einzeln) – sobald alle Seiten gerendert sind
    if (fr.probe.x > 0.5) return texture(envCube, r).rgb;
    vec3 sky = TDU2Sky(r);
    vec3 ground = fr.ambient.rgb * 0.55 + fr.sunColor.rgb * max(fr.sunDir.y, 0.0) * 0.12;   // beleuchteter Boden/Bewuchs
    float up = smoothstep(-0.05, 0.35, r.y);
    return mix(ground, sky, up);
}
";

    private const string CarLighting = CarEnv + @"
vec3 CarStandard(vec3 albedo, float metallic, float smoothness, float occlusion, vec3 n, vec3 v, vec3 wp) {
    vec3 l = normalize(fr.sunDir.xyz);
    vec3 specColor = mix(vec3(0.04), albedo, metallic);
    float oneMinusRefl = 0.96 * (1.0 - metallic);
    vec3 diffColor = albedo * oneMinusRefl;
    float rough = max(1.0 - smoothness, 0.02);
    float a = rough * rough;
    vec3 h = normalize(l + v);
    float nl = max(dot(n, l), 0.0), nv = max(abs(dot(n, v)), 1e-4), nh = max(dot(n, h), 0.0), lh = max(dot(l, h), 0.0);
    float a2 = a * a;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    float D = a2 / (3.14159265 * d * d + 1e-7);
    float V = 0.5 / (nl * (nv * (1.0 - a) + a) + nv * (nl * (1.0 - a) + a) + 1e-5);
    vec3 F = specColor + (1.0 - specColor) * pow(1.0 - lh, 5.0);
    vec3 specTerm = max(V * D * 3.14159265 * nl, 0.0) * F;
    float atten = TDU2Shadow(wp, n);
    vec3 direct = (diffColor * nl + specTerm) * fr.sunColor.rgb * atten;
    vec3 indirectDiff = diffColor * (fr.ambient.rgb + TDU2Lamps(wp, n)) * occlusion;
    vec3 r = reflect(-v, n);
    vec3 env = mix(CarEnv(r), fr.ambient.rgb, clamp(rough * 1.2, 0.0, 1.0));
    float surfaceReduction = 1.0 / (a * a + 1.0);
    float grazing = clamp(smoothness + (1.0 - oneMinusRefl), 0.0, 1.0);
    vec3 fl = mix(specColor, vec3(grazing), pow(1.0 - nv, 5.0));
    vec3 indirectSpec = env * occlusion * surfaceReduction * fl;
    return direct + indirectDiff + indirectSpec;
}
";

    // Original-Lack TD_CarPaint (TD_CarPaint.sob, vs_3_0/ps_3_0 disassembliert):
    //  VS: ndv = dot(normalize(cam - p), N); fres = pow(sat(1 - |ndv|), MATPARAM.w); env = lerp(MATPARAM.z, 1, fres) * ENVCOEFF.x * MATPARAM.x
    //      * min(ceil(ndv + ENVCOEFF.y), 1); r = pow(max(ndv, 0), RAMPPOW.x); Lack = sat(r * RAMPCLR1 + (1 - r) * RAMPCLR2)
    //  PS: Lack = lerp(Lack, Detail.rgb * LAYRCLR1.rgb, Detail.a * LAYRCLR1.w); Licht = lerp(Ambient, Sonne, max(N·L, 0) * Schatten)
    //      Glanz = pow(max(dot(V, reflect(-L, N)) * Schatten, 1e-4), SPECPARM.x) * DirLight specular * SPECPARM.y
    //      Farbe = Lack * Licht + Glanz + Umgebung(reflect(-V, N)) * (1 - Umgebung.a * SPECPARM.z) * env   (kein Metallic: Farbe bleibt)
    private const string CarTdPaint = @"
vec3 CarTdPaint(vec3 base, vec3 n, vec3 v, vec3 wp, out vec3 light) {
    vec3 l = normalize(fr.sunDir.xyz);
    vec4 mp = cm.c[17], ec = cm.c[18], sp = cm.c[19];
    if (CarOrigLight()) ec.x = fr.carDiffuse.w;   // ENVCOEFF.x zur Laufzeit (exe 0x7175a0)
    float ndv = dot(n, v);
    float fres = pow(clamp(1.0 - abs(ndv), 0.0, 1.0), max(mp.w, 0.0));
    float envAmt = mix(mp.z, 1.0, fres) * ec.x * mp.x * min(ceil(ndv + ec.y), 1.0);
    float sh = TDU2Shadow(wp, n);
    vec3 amb, sun, spc;
    CarLights(amb, sun, spc);
    vec3 lg = mix(amb, sun, max(dot(n, l), 0.0) * sh) + TdG(TDU2Lamps(wp, n));
    float spec = pow(max(dot(v, reflect(-l, n)) * sh, 1e-4), max(sp.x, 1.0)) * sp.y;
    vec3 env = TdG(CarEnv(reflect(-v, n)));
    light = TdL(lg);
    return TdL(base * lg + spc * spec + env * max(envAmt, 0.0));
}
";

    // Original-Lack TD_CarPaint2 (TD_CarPaint2.sob ps_3_0, Variante ohne Mikroflocken; N·V/N·L im Tangentenraum = Normale):
    //  Glanz = pow(max(dot(V, reflect(-L,N)) * Schatten, 1e-4), SPECBLINN.x) * DirLight specular * SPECBLINN.y * ENVCOEFF.y
    //  F = lerp(pow(sat(1-|N·V|), ENVCOEFF.w*MATPARAM.w), 1, ENVCOEFF.z*MATPARAM.z); Diffus = 1 - MATPARAM.y * F
    //  Licht = lerp(Ambient, Sonne * Diffus, max(N·L,0) * Schatten); Lack = sat(RAMPCLR2 + pow(max(N·V,0), RAMPPOW.x) * (RAMPCLR1 - RAMPCLR2))
    //  Farbe = Lack * Licht + Glanz + Umgebung(reflect(-V,N)) * (1 - Umgebung.a * RAMPPOW.z) * ENVCOEFF.x * MATPARAM.x * F
    private const string CarTdPaint2 = @"
vec3 CarTdPaint2(vec3 base, vec3 n, vec3 v, vec3 wp, out vec3 light) {
    vec3 l = normalize(fr.sunDir.xyz);
    vec4 mp = cm.c[17], ec = CarOrigLight() ? fr.carEnv : cm.c[18], sb = cm.c[19];   // ENVCOEFF zur Laufzeit (exe 0x7175a0)
    float ndv = dot(n, v);
    float sh = TDU2Shadow(wp, n);
    float spec = pow(max(dot(v, reflect(-l, n)) * sh, 1e-4), max(sb.x, 0.0)) * sb.y * ec.y;
    float F = mix(pow(clamp(1.0 - abs(ndv), 0.0, 1.0), max(ec.w * mp.w, 0.0)), 1.0, ec.z * mp.z);
    float dif = 1.0 - mp.y * F;
    vec3 amb, sun, spc;
    CarLights(amb, sun, spc);
    vec3 lg = mix(amb, sun * dif, max(dot(n, l), 0.0) * sh) + TdG(TDU2Lamps(wp, n));
    vec3 env = TdG(CarEnv(reflect(-v, n)));
    light = TdL(lg);
    return TdL(base * lg + spc * spec + env * ec.x * mp.x * F);
}
";

    public const string CarFrag = Common + CarMat + CarLighting + CarTdPaint + CarTdPaint2 + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;      // _MainTex
layout(set = 0, binding = 1) uniform sampler2D emissionTex;  // _EmissionTex (TEXLIGHT)
layout(set = 0, binding = 2) uniform sampler2D lightMask;    // _LightMask (lgt_g)
layout(set = 0, binding = 3) uniform sampler2D stickerTex;   // _StickerTex
layout(set = 0, binding = 4) uniform sampler2D dirtTex;      // _DirtTex
layout(set = 0, binding = 5) uniform sampler2D flakeTex;     // FLAKE (gMicroFlakesTexSampler)
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv0;
layout(location = 2) in vec2 vUv1;
layout(location = 3) in vec3 vWorld;
layout(location = 4) flat in float vMirror;
layout(location = 5) in vec3 vObj;
layout(location = 6) in vec3 vObjN;
layout(location = 0) out vec4 outColor;

// Original TD_CarPaint2, Variante mit Mikroflocken (TD_CarPaint2.sob ps @93216), Lackfarbe im Gamma-Raum:
//  Flocke f = Dreifach-Projektion der FLAKE-Textur auf die Objektposition × FLAKEABNS.w (Ebenen (-x,-z), (-z,-y), (x,-y), je ×2−1),
//    Gewichte = normalize(|N_obj|^8)² ; f liegt im Tangentenraum (z = Normale)
//  Nf = FLAKEABNS.x·f + FLAKEABNS.z·(0,0,1); G = FLAKEABNS.y·(f + (0,0,1)); H = normalize(L + V)
//  Lack = sat(lerp(RAMPCLR2, RAMPCLR1, pow(max(H·Nf,0), RAMPPOW.x)) + pow(max(H·G,0), RAMPPOW.y) · RAMPFLAKE)
// Anders als die Variante ohne Flocken (Verlauf über N·V) liegt der helle RAMPCLR1 nur im Glanzbereich um die Sonne.
// Tangente/Binormale: beliebige Basis um N (f ist Rauschen, die Drehung um N ändert das Bild nicht).
vec3 CarFlakeBase(vec3 n, vec3 v, vec3 c1g, vec3 c2g) {
    vec4 ab = cm.c[23];
    vec3 an = abs(vObjN);
    vec3 w = an * an; w *= w; w *= w;
    w = normalize(max(w, vec3(1e-12))); w *= w;
    vec3 p = ab.w * vObj;
    vec3 sA = texture(flakeTex, vec2(-p.x, -p.z)).rgb * 2.0 - 1.0;
    vec3 sB = texture(flakeTex, vec2(-p.z, -p.y)).rgb * 2.0 - 1.0;
    vec3 sC = texture(flakeTex, vec2( p.x, -p.y)).rgb * 2.0 - 1.0;
    vec3 f = sB * w.x + sA * w.y + sC * w.z;
    vec3 t = normalize(cross(abs(n.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), n));
    vec3 b = cross(n, t);
    vec3 fw = f.x * t + f.y * b + f.z * n;
    vec3 nf = ab.x * fw + ab.z * n;
    vec3 g = ab.y * (fw + n);
    vec3 h = normalize(normalize(fr.sunDir.xyz) + v);
    float ramp = pow(max(dot(h, nf), 0.0), cm.c[2].y);
    float glint = pow(max(dot(h, g), 0.0), cm.c[21].z);
    return clamp(mix(c2g, c1g, ramp) + glint * cm.c[22].rgb, 0.0, 1.0);
}

float FlakeHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

void main() {
    vec3 n = normalize(vNormal);
    vec3 v = normalize(fr.camPos.xyz - vWorld);
    // Rückseite erkennen über die tatsächliche Dreiecksfläche (unabhängig von Wicklung/Spiegelung des Knotens): die Normale
    // muss zur sichtbaren Seite des Dreiecks zeigen. (Über gl_FrontFacing + Spiegelung hing das Ergebnis an der Wicklung der
    // Datei – gespiegelte und ungespiegelte Räder/Teile wurden je nach Blickseite falsch beleuchtet.)
    vec3 fn = cross(dFdx(vWorld), dFdy(vWorld));
    if (dot(fn, fn) > 1e-20) { fn = normalize(fn); if (dot(fn, v) < 0.0) fn = -fn; if (dot(n, fn) < 0.0) n = -n; }
    vec4 baseCol = cm.c[0];
    float rampPow = cm.c[2].y, bicolour = cm.c[2].x;
    bool td = cm.c[21].x > 0.5;
    if (td) {
        // Original: Verlauf RAMPCLR1 (frontal) -> RAMPCLR2 (streifend), auch wenn RAMPCLR2 schwarz ist
        // (im Gamma-Raum wie das Original; RAMPCLR kommen linear aus dem Material-Puffer)
        vec3 c0g = TdG(cm.c[0].rgb), c1g = TdG(cm.c[1].rgb);
        vec3 bg = c0g;
        if (cm.c[21].w > 0.5) bg = CarFlakeBase(n, v, c0g, c1g);   // Flocken-Variante (GraphicsConfig.OriginalCarFlakes)
        else if (rampPow > 0.001) { float rr = pow(max(dot(n, v), 0.0), rampPow); bg = clamp(rr * c0g + (1.0 - rr) * c1g, 0.0, 1.0); }
        baseCol.rgb = TdL(bg);
    } else if (rampPow > 0.001) {
        float ndvR = clamp(dot(n, v), 0.0, 1.0);
        baseCol.rgb = mix(cm.c[1].rgb, cm.c[0].rgb, pow(ndvR, rampPow));
    } else if (bicolour > 0.001) {
        float ndvB = clamp(dot(n, v), 0.0, 1.0);
        baseCol.rgb = mix(cm.c[0].rgb, cm.c[1].rgb, smoothstep(0.15, 0.95, 1.0 - ndvB) * bicolour);
    }
    vec4 tx = texture(mainTex, vUv0);
    if (cm.c[2].w > 0.5) { tx.a = tx.r; tx.rgb = vec3(1.0); }
    if (cm.c[2].z > 0.5) tx.rgb = tx.rrr;
    vec4 c = tx * baseCol;
    if (td) {
        // Original: Detailebene (DETAIL) über dem Lack, Deckkraft = Detail.a * LAYRCLR1.w
        c = baseCol;
        if (cm.c[21].y > 0.5) c.rgb = TdL(mix(TdG(baseCol.rgb), TdG(tx.rgb) * TdG(cm.c[20].rgb), tx.a * cm.c[20].w));
    }
    if (c.a < cm.c[3].w) discard;

    vec2 suv = cm.c[9].z > 0.5 ? vUv1 : vUv0;
    if (cm.c[9].y > 0.5) {
        vec4 st = texture(stickerTex, suv);
        c.rgb = mix(st.rgb, mix(c.rgb, st.rgb, st.a), cm.c[9].w);
    }
    float dirtK = 0.0;
    float dirtLevel = cm.c[10].x, scratchLevel = cm.c[10].y;
    if (dirtLevel > 0.001 || scratchLevel > 0.001) {
        vec2 dm = texture(dirtTex, suv).rg;
        dirtK = clamp((dm.r - (1.0 - dirtLevel)) * 4.0, 0.0, 1.0) * cm.c[11].a;
        c.rgb = mix(c.rgb, cm.c[11].rgb, dirtK);
        float scr = clamp((dm.g - (1.0 - scratchLevel)) * 4.0, 0.0, 1.0);
        c.rgb = mix(c.rgb, c.rgb * 0.55 + 0.25, scr);
    }
    vec3 albedo = c.rgb;
    float metallic = cm.c[3].z * (1.0 - dirtK);
    float smoothness = cm.c[3].y * (1.0 - dirtK * 0.85);
    float occlusion = cm.c[6].w;
    vec3 emission = c.rgb * cm.c[6].z;

    float lampKill = 1.0;
    float glowA = 0.0;   // Bloom-Maske (Zusatz PostFx): leuchtende Lampen
    vec3 lensTex = vec3(0.0); float lensK = 0.0;   // Original-Lichtglas im Gamma-Raum (nur Lack-Pfad mit Autolicht des Originals)
    if (cm.c[8].x > 0.5) {
        // Original TD_CarPaint_NM ps96 (docs re/car_lights_spec 1): m = GlossLight * (1,1,1,-1) + (0,0,0,1);
        // emis = TexLight * dot(m * GLOSSFCT, 1); Diffus/Glanz *= prod(1 - GLOSSFCT.i * m.i) (leuchtende Lampe ohne Lack darunter).
        // GLOSSFCT = (Bremse, Rückfahr, Positions-/Scheinwerfer, Blinker); Maske linear gefiltert wie im Original.
        vec4 msk = texture(lightMask, vUv0);
        vec4 mm = vec4(msk.rgb, 1.0 - msk.a);
        vec4 gf = clamp(vec4(cm.c[8].z, cm.c[8].w, cm.c[8].y, cm.c[9].x), 0.0, 1.0);
        // Stärke je Funktion (_LightIntensity: Rück/Positions, Bremse, Rückfahr, Blinker) – Engine ohne Bloom
        vec4 w = vec4(cm.c[16].y, cm.c[16].z, cm.c[16].x, cm.c[16].w);
        if (td && CarOrigLight()) { lensTex = texture(emissionTex, vUv0).rgb; lensK = dot(mm * gf, w); }
        else emission += texture(emissionTex, vUv0).rgb * dot(mm * gf, w);
        vec4 kk = vec4(1.0) - gf * mm;
        lampKill = kk.x * kk.y * kk.z * kk.w;
        glowA = clamp(1.0 - lampKill, 0.0, 1.0);
    } else if (cm.c[6].x > 0.001) {
        // Unity TDU2/Car: nur wo die An-Textur heller ist als die Aus-Farbe (sonst leuchtet das ganze Glas mit)
        vec3 on = texture(emissionTex, vUv0).rgb;
        float gain = clamp(dot(clamp(on - c.rgb * 0.9, 0.0, 1.0), vec3(0.3, 0.59, 0.11)) * 3.0, 0.0, 1.0);
        vec3 glow = cm.c[6].y > 0.5 ? on * gain * 1.5 : on;
        emission += glow * cm.c[5].rgb * cm.c[6].x * 2.0;   // x2: Engine ohne Bloom
        glowA = clamp(max(glow.r, max(glow.g, glow.b)) * cm.c[6].x, 0.0, 1.0);
    }
    vec3 l = normalize(fr.sunDir.xyz);
    if (td) {
        float heatT = cm.c[3].x;
        if (heatT > 0.001) { float hh = clamp(heatT, 0.0, 1.0); emission += cm.c[4].rgb * mix(vec3(0.6, 0.12, 0.02), vec3(1.6, 0.7, 0.2), hh * hh) * hh * 2.0; glowA = max(glowA, hh); }
        vec3 lightT;
        vec3 paint = cm.c[21].x > 1.5 ? CarTdPaint2(TdG(albedo), n, v, vWorld, lightT) : CarTdPaint(TdG(albedo), n, v, vWorld, lightT);
        // Schmutz wie im Original: über dem Ergebnis, mit dem Licht beleuchtet, ohne Glanz/Spiegelung
        paint = mix(paint, albedo * lightT, dirtK);
        // Original ps96: Farbe = Lack · prod(1 − GLOSSFCT·Maske) + TEXLIGHT · dot(Maske · GLOSSFCT, 1), im Gamma-Raum addiert
        vec3 litT = CarOrigLight() ? TdL(TdG(paint) * lampKill + TdG(lensTex) * lensK) : paint * lampKill;
        outColor = vec4(TDU2Fog(litT + emission, vWorld), glowA);
        return;
    }
    float clearCoat = cm.c[7].x;
    if (clearCoat * (1.0 - dirtK) > 0.001) {
        float ndv = clamp(dot(n, v), 0.0, 1.0);
        float fres = 0.05 + 0.95 * pow(1.0 - ndv, 5.0);
        vec3 env = CarEnv(reflect(-v, n));
        vec3 h = normalize(l + v);
        float spec = pow(clamp(dot(n, h), 0.0, 1.0), cm.c[7].y) * clamp(dot(n, l), 0.0, 1.0) * 4.0;
        emission += (env * fres + fr.sunColor.rgb * spec) * clearCoat * (1.0 - dirtK);
    }
    float flake = cm.c[7].z;
    if (flake > 0.001 && dirtK < 0.99) {
        float ndv = clamp(dot(n, v), 0.0, 1.0);
        albedo *= mix(1.0, 0.78 + 0.22 * ndv, flake);
        vec2 cell = floor(vUv1 * cm.c[7].w);
        float h1 = FlakeHash(cell), h2 = FlakeHash(cell + 17.31), h3 = FlakeHash(cell + 41.7);
        vec3 fn = normalize(n + (vec3(h1, h2, h3) - 0.5) * 0.3);
        vec3 hv = normalize(l + v);
        float spark = pow(clamp(dot(fn, hv), 0.0, 1.0), 260.0) * step(0.82, h3) * clamp(dot(n, l), 0.0, 1.0);
        vec3 envF = mix(CarEnv(reflect(-v, fn)), fr.ambient.rgb, 0.4);
        emission += (fr.sunColor.rgb * spark * 0.7 + envF * step(0.93, h1) * 0.04) * (albedo * 0.6 + 0.4) * flake * (1.0 - dirtK);
    }
    float heat = cm.c[3].x;
    if (heat > 0.001) {
        float hh = clamp(heat, 0.0, 1.0);
        emission += cm.c[4].rgb * mix(vec3(0.6, 0.12, 0.02), vec3(1.6, 0.7, 0.2), hh * hh) * hh * 2.0;
        glowA = max(glowA, hh);
    }
    vec3 lit = CarStandard(albedo, metallic, smoothness, occlusion, n, v, vWorld) * lampKill + emission;
    outColor = vec4(TDU2Fog(lit, vWorld), glowA);
}";

    // CarGlass: Surface Standard alpha:premul (Diffus x Alpha, Spiegelung bleibt)
    public const string CarGlassFrag = Common + CarMat + CarEnv + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;      // DETAIL
layout(set = 0, binding = 5) uniform sampler2D detail2Tex;   // DETAIL2 (G_I), nur Original-Glas
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv0;
layout(location = 2) in vec2 vUv1;
layout(location = 3) in vec3 vWorld;
layout(location = 4) flat in float vMirror;
layout(location = 0) out vec4 outColor;
void main() {
    vec3 n = normalize(vNormal);
    vec3 v = normalize(fr.camPos.xyz - vWorld);
    // Rückseite erkennen über die tatsächliche Dreiecksfläche (unabhängig von Wicklung/Spiegelung des Knotens): die Normale
    // muss zur sichtbaren Seite des Dreiecks zeigen. (Über gl_FrontFacing + Spiegelung hing das Ergebnis an der Wicklung der
    // Datei – gespiegelte und ungespiegelte Räder/Teile wurden je nach Blickseite falsch beleuchtet.)
    vec3 fn = cross(dFdx(vWorld), dFdy(vWorld));
    if (dot(fn, fn) > 1e-20) { fn = normalize(fn); if (dot(fn, v) < 0.0) fn = -fn; if (dot(n, fn) < 0.0) n = -n; }
    if (cm.c[21].x > 2.5) {
        // Original-Glas der Außenhaut, Familie TD_CarPaint (TD_CarPaint.sob ps @201040), Gamma-Raum:
        //  VS: Rampe = sat(r·RAMPCLR1 + (1−r)·RAMPCLR2), r = pow(max(N·V,0), RAMPPOW.x);
        //      env = lerp(MATPARAM.z, 1, pow(sat(1−|N·V|), MATPARAM.w)) · ENVCOEFF.x · MATPARAM.x · min(ceil(N·V + ENVCOEFF.y), 1)
        //  PS: Basis = lerp(lerp(Rampe, DETAIL·LAYRCLR1, DETAIL.a·LAYRCLR1.w), DETAIL2.rgb, DETAIL2.a)
        //      Licht = lerp(Ambient, Diffus, max(N·L,0)·Schatten); Glanz = pow(max(V·reflect(−L,N)·Schatten, 1e−4), SPECPARM.x)·Specular·SPECPARM.y
        //      Farbe = Basis·Licht + Glanz + Umgebung(reflect(−V,N))·(1 − Umgebung.a·SPECPARM.z)·env;  Deckkraft = DETAIL.a
        vec4 mp = cm.c[17], ec = cm.c[18], sp = cm.c[19], ly = cm.c[20];
        if (CarOrigLight()) ec.x = fr.carDiffuse.w;
        vec3 l = normalize(fr.sunDir.xyz);
        float ndv = dot(n, v);
        vec3 c0g = TdG(cm.c[0].rgb), c1g = TdG(cm.c[1].rgb);
        vec3 ramp = c0g;
        if (cm.c[2].y > 0.001) { float rr = pow(max(ndv, 0.0), cm.c[2].y); ramp = clamp(rr * c0g + (1.0 - rr) * c1g, 0.0, 1.0); }
        vec4 dt = texture(mainTex, vUv0);
        vec3 base = mix(ramp, TdG(dt.rgb) * TdG(ly.rgb), dt.a * ly.w);
        vec4 d2 = texture(detail2Tex, vUv1);
        base = mix(base, TdG(d2.rgb), d2.a);
        float sh = TDU2Shadow(vWorld, n);
        vec3 amb, dif, spc;
        CarLights(amb, dif, spc);
        vec3 lg = mix(amb, dif, max(dot(n, l), 0.0) * sh) + TdG(TDU2Lamps(vWorld, n));
        float spec = pow(max(dot(v, reflect(-l, n)) * sh, 1e-4), max(sp.x, 1.0)) * sp.y;
        float envAmt = mix(mp.z, 1.0, pow(clamp(1.0 - abs(ndv), 0.0, 1.0), max(mp.w, 0.0))) * ec.x * mp.x * min(ceil(ndv + ec.y), 1.0);
        vec3 rgb = base * lg + spc * spec + TdG(CarEnv(reflect(-v, n))) * max(envAmt, 0.0);
        float a = dt.a;
        outColor = vec4(TDU2Fog(TdL(rgb), vWorld) * a, a);   // SrcAlpha/InvSrcAlpha als vormultipliziert (Blend 4)
        return;
    }
    if (cm.c[21].x > 0.5) {
        // Original TD_CarGlassDirt (ps_3_0): Farbe = Umgebung(reflect(-V,N)) * MATPARAM.x * lerp(MATPARAM.z, 1, pow(1-|N·V|, MATPARAM.w))
        //   + Sonne * pow(max(dot(V, reflect(-L,N)), 1e-4), SPECPARM.x) * SPECPARM.y; Deckkraft = max(Textur-Alpha (4 Abtastungen
        //   ±1/1024), Helligkeit der Farbe); Blend SrcAlpha/InvSrcAlpha (hier vormultipliziert). Die Texturfarbe nutzt das Original nicht.
        vec4 mp = cm.c[17], sp = cm.c[19];
        vec3 lg = normalize(fr.sunDir.xyz);
        float ndv = dot(n, v);
        float envAmt = mp.x * mix(mp.z, 1.0, pow(clamp(1.0 - abs(ndv), 0.0, 1.0), max(mp.w, 0.0)));
        // im Gamma-Raum wie das Original (auch die Deckkraft aus der Helligkeit), Ausgabe linear
        vec3 rgb = TdG(CarEnv(reflect(-v, n))) * (1.0 - sp.w) * envAmt;
        vec3 gAmb, gDif, gSpc;
        CarLights(gAmb, gDif, gSpc);   // Glanz mit DirLight specular (c15)
        rgb += gSpc * pow(max(dot(v, reflect(-lg, n)), 1e-4), max(sp.x, 1.0)) * sp.y * TDU2Shadow(vWorld, n);
        const float o = 0.000976562;
        float ta = (texture(mainTex, vUv0 - vec2(o)).a + 2.0 * texture(mainTex, vUv0).a + texture(mainTex, vUv0 + vec2(o)).a) * 0.25;
        float ga = clamp(max(ta, dot(rgb, vec3(0.299, 0.587, 0.114))), 0.0, 1.0);
        outColor = vec4(TDU2Fog(TdL(rgb), vWorld) * ga, ga);
        return;
    }
    vec4 c = texture(mainTex, vUv0) * cm.c[0];
    float alpha = min(c.a, cm.c[10].z);
    float smoothness = cm.c[3].y;
    // Diffusanteil vormultipliziert, Spiegelung (Standard: Fresnel über die Deckkraft hinaus) bleibt sichtbar
    vec3 ld = normalize(fr.sunDir.xyz);
    vec3 diffuse = c.rgb * 0.96 * (fr.sunColor.rgb * max(dot(n, ld), 0.0) * TDU2Shadow(vWorld, n) + fr.ambient.rgb + TDU2Lamps(vWorld, n)) * alpha;
    float rough = max(1.0 - smoothness, 0.02);
    float nv = max(abs(dot(n, v)), 1e-4);
    float grazing = clamp(smoothness + 0.04, 0.0, 1.0);
    float fl = mix(0.04, grazing, pow(1.0 - nv, 5.0));
    vec3 l = normalize(fr.sunDir.xyz), h = normalize(l + v);
    float spec = pow(max(dot(n, h), 0.0), mix(16.0, 2048.0, smoothness)) * max(dot(n, l), 0.0);
    vec3 refl = CarEnv(reflect(-v, n)) * fl / (rough * rough * rough * rough + 1.0) + fr.sunColor.rgb * spec * TDU2Shadow(vWorld, n);
    float a = alpha + (1.0 - alpha) * fl;   // Unity: Deckkraft wächst mit der Reflektivität (premultiply)
    vec3 col = diffuse + refl;
    outColor = vec4(TDU2Fog(col, vWorld) , a);
}";

    // CarShadow: Blend DstColor Zero -> (1-a) dunkelt den Untergrund ab
    public const string CarShadowFrag = Common + CarMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 1) in vec2 vUv0;
layout(location = 0) out vec4 outColor;
void main() {
    float a = texture(mainTex, vUv0).a * cm.c[10].w;
    outColor = vec4(1.0 - a, 1.0 - a, 1.0 - a, 1.0);
}";

    // Schattenwurf Car (addshadow: Alpha-Test mit _Cutoff)
    public const string ShadowCarFrag = Common + CarMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 1) in vec2 vUv0;
void main() {
    if (cm.c[3].w > 0.0) {
        vec4 tx = texture(mainTex, vUv0);
        float a = cm.c[2].w > 0.5 ? tx.r : tx.a;
        if (a * cm.c[0].a < cm.c[3].w) discard;
    }
}";

    // Avatar, AvatarBlend
    // Original-Shader-Sets TD_Skin2/TD_Skinface (Modus 0), TD_Clothe2 (1), TD_Avatar (2), TD_Hair (3), TD_Eye (4).
    // Texturen je Modus (binding 0..5): Haut: SKIN, GLOSSDETAILCUT, SPECTRALSCATTERING, NORMAL; Kleidung: OCCLUSION, DETAIL_GRAIN,
    // LAYER1, LAYER2, LAYER3; sonst TEX/HAIR auf 0. Die gehäuteten Vertices kommen im Avatar-Raum (CPU-Häutung, Core/Avatars/AvatarRig).
    // Material-Properties (binding 6):
    //  c0 _Color, c1 _Mode,_Cutoff,_Brightness,_AlphaLum, c2 _SkinClr, c3 _DclClr, c4 _Coeffs, c5 _UVScale, c6 _BaseClr,
    //  c7..c9 _ClrL1..3, c10 _LayerOn (xyz), w _GlossOn, c11 _LayerUV (xyz), w _RampOn, c12 _UVOff (xy), z _Ombre, w _BumpOn,
    //  c13 _BColor, c14 _TColor, c15 _PrimClr, c16 _ScdClr, c17 _Spec, c18 _CarInteg, c19 _AtteFact,
    //  c20 _InCar,_DetailOn,_RampScale,_TDU2AvatarIndoor, c21 _TDU2IndoorDir, c22 _TDU2IndoorLight, c23 _TDU2IndoorAmbTop, c24 _TDU2IndoorAmbBottom
    //  c25 _TDU2AvRampAdd,_TDU2AvRampRow,_TDU2AvAddMix,_TDU2AvAmbAdd, c26.x _TDU2AvHairSheen (Feinabstimmung, 0 = Original)
    private const string AvatarMat = @"
layout(set = 0, binding = 6) uniform AvatarMat { vec4 c[28]; } am;
";

    public const string AvatarVert = Common + ObjectInputs + AvatarMat + @"
layout(location = 0) out vec3 vNormal;
layout(location = 7) out vec4 vTangent;   // xyz Welt, w Händigkeit; 0 = keine (Ableitungen im Fragment-Shader)
vec3 OctDecode(vec2 e) {
    vec3 v = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (v.z < 0.0) v.xy = (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    return normalize(v);
}
layout(location = 1) out vec2 vUv0;
layout(location = 2) out vec2 vUv1;
layout(location = 3) out vec2 vUv2;
layout(location = 4) out vec3 vWorld;
layout(location = 5) out float vFoot;
layout(location = 6) out float vHgt;
void main() {
    vec4 world = pc.model * vec4(inPos, 1.0);
    gl_Position = pc.viewProj * world;
    vNormal = mat3(pc.model) * inNormal;
    // AvatarRig.Skin: Tangente in Uv3 (Oktaeder), Farbe B = 0 als Kennung, G = Händigkeit
    vTangent = inColor.b < 0.5 ? vec4(mat3(pc.model) * OctDecode(inUv3), inColor.g > 0.5 ? 1.0 : -1.0) : vec4(0.0);
    vUv0 = inUv0; vUv1 = inUv1; vUv2 = inUv2;
    vWorld = world.xyz;
    // gehäutete Höhe im Avatar-Raum
    float h = inPos.y;
    float d = max(0.001, am.c[18].y - am.c[18].x);
    vFoot = am.c[20].x > 0.5 ? clamp((h - am.c[18].x) / d, 0.0, 1.0) : 1.0;
    vHgt = h;
}";

    private const string AvatarFragBody = @"
layout(set = 0, binding = 0) uniform sampler2D tex0;
layout(set = 0, binding = 1) uniform sampler2D tex1;
layout(set = 0, binding = 2) uniform sampler2D tex2;
layout(set = 0, binding = 3) uniform sampler2D tex3;
layout(set = 0, binding = 4) uniform sampler2D tex4;
layout(set = 0, binding = 5) uniform sampler2D tex5;
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv0;
layout(location = 2) in vec2 vUv1;
layout(location = 3) in vec2 vUv2;
layout(location = 4) in vec3 vWorld;
layout(location = 5) in float vFoot;
layout(location = 6) in float vHgt;
layout(location = 0) out vec4 outColor;
const vec3 LUM = vec3(0.299, 0.587, 0.114);

vec2 SampleUV(float which) { return which < 0.5 ? vUv0 : (which < 1.5 ? vUv1 : vUv2); }

layout(location = 7) in vec4 vTangent;
// Tangentenraum: gemittelte Vertex-Tangenten (stetig über die Dreiecke); ohne sie Basis aus den Bildschirmableitungen (UV0),
// die je Dreieck konstant sind (Kanten sichtbar)
vec3 TangentToWorld(vec3 n, vec3 N) {
    if (dot(vTangent.xyz, vTangent.xyz) > 1e-8) {
        vec3 T = vTangent.xyz - N * dot(N, vTangent.xyz);
        if (dot(T, T) > 1e-10) {
            T = normalize(T);
            vec3 B = cross(N, T) * vTangent.w;
            return normalize(mat3(T, B, N) * n);
        }
    }
    vec3 dp1 = dFdx(vWorld), dp2 = dFdy(vWorld);
    vec2 du1 = dFdx(vUv0), du2 = dFdy(vUv0);
    vec3 dp2perp = cross(dp2, N), dp1perp = cross(N, dp1);
    vec3 T = dp2perp * du1.x + dp1perp * du2.x;
    vec3 B = dp2perp * du1.y + dp1perp * du2.y;
    float invmax = inversesqrt(max(max(dot(T, T), dot(B, B)), 1e-20));
    return normalize(mat3(T * invmax, B * invmax, N) * n);
}

void main() {
    float mode = am.c[1].x;
    bool indoor = am.c[20].w > 0.5;
    vec2 uv = vUv0;
    vec4 c = vec4(1.0);
    vec3 n = vec3(0.0, 0.0, 1.0);
    float scatter = 0.0, ao = 1.0, aux = 1.0;
    float occl = indoor ? mix(0.55, 1.0, clamp(vHgt / 1.0, 0.0, 1.0)) : 1.0;
    float specK = am.c[17].x, specP = am.c[17].y;
    bool bump = false;
    if (mode < 0.5)
    {
        // Haut: Farbe = SKIN (Dekal DCLCLR über GLOSSDETAILCUT.b x COEFFS.y), Glanz = .r, Durchscheinen = .g
        c = texture(tex0, uv) * am.c[0];
        c.a = 1.0;
        if (am.c[10].w > 0.5)
        {
            vec4 g = texture(tex1, uv);
            c.rgb = mix(c.rgb, am.c[3].rgb, clamp(g.b * am.c[4].y, 0.0, 1.0));
            specK *= g.r;
            scatter = clamp(g.g, 0.0, 1.0);
        }
        if (am.c[12].w > 0.5) { vec4 nt = texture(tex3, uv); vec2 xy = nt.ag * 2.0 - 1.0; n = vec3(xy, sqrt(clamp(1.0 - dot(xy, xy), 0.0, 1.0))); bump = true; }   // DXT5nm (AG)
    }
    else if (mode < 1.5)
    {
        // TD_Clothe2: BASECLR, LAYER1/LAYER2 über Alpha, Stoffkorn DETAIL.b dunkelt ab, LAYER3 darüber
        c = am.c[6];
        float alpha = am.c[6].a;
        vec4 det = am.c[20].y > 0.5 ? texture(tex1, uv * am.c[5].zw) : vec4(0.5, 0.5, 1.0, 0.5);
        if (am.c[10].x > 0.5) { vec4 l = texture(tex2, SampleUV(am.c[11].x) * am.c[5].xy); float a = l.a * am.c[7].a; c.rgb = mix(c.rgb, am.c[7].rgb * l.rgb, a); alpha = max(alpha, a); }
        if (am.c[10].y > 0.5) { vec4 l = texture(tex3, SampleUV(am.c[11].y)); float a = l.a * am.c[8].a; c.rgb = mix(c.rgb, am.c[8].rgb * l.rgb, a); alpha = max(alpha, a); }
        c.rgb *= det.b;
        if (am.c[10].z > 0.5) { vec4 l = texture(tex4, SampleUV(am.c[11].z)); float a = l.a * am.c[9].a; c.rgb = mix(c.rgb, am.c[9].rgb * l.rgb, a); alpha = max(alpha, a); }
        c.a = AVATAR_BLEND ? alpha : 1.0;
        specK *= det.b;
        if (am.c[12].w > 0.5)
        {
            // OCCLUSION: RG = Normale, B = eingebackene Verdeckung; Stoffkorn-Normale (DETAIL.ag) addiert, z = 1
            vec4 occ = texture(tex0, uv);
            vec2 xy = occ.rg * 2.0 - 1.0;
            if (am.c[20].y > 0.5) xy += vec2(det.a, det.g) * 2.0 - 1.0;
            n = normalize(vec3(xy, 1.0));
            ao = occ.b;
            bump = true;
        }
    }
    else if (mode < 2.5)
    {
        // TD_Avatar: Farbe = TEX x COLOR, Deckkraft = TEX.a
        vec4 t = texture(tex0, uv + am.c[12].xy);
        c = t * am.c[0];
        if (am.c[1].w > 0.5) c = vec4(am.c[0].rgb, max(t.r, max(t.g, t.b)) * am.c[0].a);
        if (am.c[12].z > 0.5) { c = mix(am.c[13], am.c[14], t.a); specK = max(specK, 0.5); specP = max(specP, 40.0); }
    }
    else if (mode < 3.5)
    {
        // TD_Hair: Farbe lerp(PRIMCLR, SCDCLR, HAIR.g), Glanz x HAIR.b, Helligkeit HAIR.r, Deckkraft HAIR.a
        vec4 h = texture(tex0, uv);
        c.rgb = mix(am.c[15].rgb, am.c[16].rgb, h.g);
        c.a = h.a;
        specK *= h.b;
        aux = h.r;
    }
    else
    {
        // Augen: Iris-Zelle des 5x5-Atlas über UVOFF
        c = texture(tex0, uv + am.c[12].xy) * am.c[0];
        c.a = 1.0;
        specK = 0.6; specP = 60.0;
    }
    vec3 albedo = c.rgb * am.c[1].z;
    float alphaOut = c.a;
    if (alphaOut < am.c[1].y) discard;
    // beidseitige Flächen: Rückseite mit umgedrehter Normale
    if (!gl_FrontFacing) n.z = -n.z;
    vec3 Ng = normalize(vNormal);   // Unity: Tangentenraum der Mesh-Normale, Rückseite über n.z
    vec3 N = bump ? TangentToWorld(n, Ng) : normalize(Ng * n.z);
    vec3 V = normalize(fr.camPos.xyz - vWorld);
    float gloss = specK * (indoor ? 0.5 : 1.0);

    // Lichtmodell der Original-Shader: Licht = lerp(Umgebung x atteFact.x, Sonne x atteFact.y, t)
    vec3 att = mix(am.c[19].xyz, vec3(1.0), vFoot);
    vec3 L, lc, amb;
    float atten;
    if (indoor)
    {
        L = normalize(am.c[21].xyz);
        lc = am.c[22].rgb;
        atten = 1.0;
        amb = mix(am.c[24].rgb, am.c[23].rgb, clamp(N.y * 0.5 + 0.5, 0.0, 1.0));
    }
    else
    {
        L = normalize(fr.sunDir.xyz);
        lc = fr.sunColor.rgb;
        atten = TDU2Shadow(vWorld, N);
        amb = max(fr.ambient.rgb, vec3(0.0));
    }
    float nl = dot(N, L);
    float rv = max(dot(reflect(-L, N), V), 1e-4);
    vec4 tw = am.c[25];
    amb = (amb + tw.w) * att.x;
    vec3 dif = lc * att.y, spl = lc * att.z;
    vec3 light, spec, col;
    if (mode < 0.5)
    {
        float w = am.c[4].x;
        float u = clamp((nl + w) / (1.0 + w), 0.0, 1.0);
        u = clamp(clamp(dot(V, -L), 0.0, 1.0) * am.c[17].z * (1.0 - clamp(dot(N, V), 0.0, 1.0)) + u, 0.0, 1.0) * atten;
        amb = mix(amb, dot(amb, LUM) * am.c[2].rgb, scatter);
        dif = mix(dif, dot(dif, LUM) * am.c[2].rgb, scatter);
        spl = mix(spl, dot(spl, LUM) * am.c[2].rgb, scatter);
        float rv2 = tw.y > 0.001 ? tw.y : am.c[4].z;
        vec3 ramp = am.c[11].w > 0.5 ? texture(tex2, vec2(u, rv2)).rgb * (am.c[20].z + tw.x) : vec3(1.0);
        light = (mix(amb, dif, u) + amb * u * tw.z) * ramp;
        spec = spl * pow(max(rv * atten, 1e-4), max(1.0, specP)) * gloss;
        col = albedo * light + spec;
    }
    else if (mode > 2.5 && mode < 3.5)
    {
        float u = clamp(0.75 * nl + 0.25, 0.0, 1.0) * atten;
        light = (mix(amb, dif, u) + amb * u * tw.z) * ao;
        vec3 h = normalize(L + V);
        spec = spl * pow(clamp(dot(N, h), 0.0, 1.0), max(1.0, specP)) * gloss * atten * clamp(1.75 * nl + 0.25, 0.0, 1.0);
        vec3 dark = light * albedo + spec;
        vec3 lit = albedo * (1.0 + dot(light, LUM) * 0.33) + spec * (0.33 + am.c[26].x);
        col = mix(dark, lit, clamp(dot(light * aux + spec, LUM), 0.0, 1.0));
    }
    else
    {
        float u = clamp(nl, 0.0, 1.0) * atten;
        light = (mix(amb, dif, u) + amb * u * tw.z) * ao;
        spec = spl * pow(max(rv * atten, 1e-4), max(1.0, specP)) * gloss * ao;
        col = albedo * light + spec;
    }
    col *= occl;
    // Zusatzlichter (Unity Forward-Add: Straßenlampen, Scheinwerfer): einfach diffus
    if (!indoor) col += albedo * TDU2Lamps(vWorld, N) * occl * ao;
    outColor = vec4(TDU2Fog(col, vWorld), alphaOut);
}";

    public const string AvatarFrag = Common + AvatarMat + "\n#define AVATAR_BLEND false\n" + AvatarFragBody;
    public const string AvatarBlendFrag = Common + AvatarMat + "\n#define AVATAR_BLEND true\n" + AvatarFragBody;

    // Schattenwurf TDU2/Avatar (addshadow: Alpha-Test mit _Cutoff auf TEX/HAIR)
    public const string ShadowAvatarFrag = Common + AvatarMat + @"
layout(set = 0, binding = 0) uniform sampler2D tex0;
layout(location = 1) in vec2 vUv0;
void main() {
    float mode = am.c[1].x;
    if (am.c[1].y > 0.0 && mode > 1.5 && mode < 3.5) {
        vec4 t = texture(tex0, vUv0 + (mode < 2.5 ? am.c[12].xy : vec2(0.0)));
        float a = mode < 2.5 ? (am.c[1].w > 0.5 ? max(t.r, max(t.g, t.b)) : t.a) * am.c[0].a : t.a;
        if (a < am.c[1].y) discard;
    }
}";

    // Skidmark, Partikel (Sprites/Default)
    public const string FxVert = Common + ObjectInputs + @"
layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv0;
layout(location = 2) out vec3 vWorld;
layout(location = 3) out vec3 vNormal;
void main() {
    vec4 world = pc.model * vec4(inPos, 1.0);
    gl_Position = pc.viewProj * world;
    vColor = inColor; vUv0 = inUv0; vWorld = world.xyz;
    vNormal = mat3(pc.model) * inNormal;
}";

    // Sterne (Original td_dome vs @0xc50 / ps @0x1af0)
    // Dom-Mesh der exe (World/Stars) um die Kamera; Normal = (u, v, Helligkeit). oT3.w = (normalize(v0).y − Fade.x)·Fade.y,
    // oT4.w = |v0|; Sterne = sat(oT3.w)·StarsIntensity·|v0| · Textur(uv).rgb · Helligkeit, zum Himmel addiert (Gamma-Raum).
    public const string SkyStarsVert = Common + ObjectInputs + @"
layout(location = 0) out vec3 vPos;    // Objektraum (Richtung + Länge)
layout(location = 1) out vec3 vStar;   // u, v, Helligkeit
void main() {
    vec4 world = pc.model * vec4(inPos, 1.0);
    vec4 clip = pc.viewProj * world;
    clip.z = 1e-6 * clip.w;
    gl_Position = clip;
    vPos = inPos;
    vStar = inNormal;
}";

    public const string SkyStarsFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec3 vPos;
layout(location = 1) in vec3 vStar;
layout(location = 0) out vec4 outColor;
vec3 LinToSrgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
void main() {
    float len = length(vPos);
    float fade = clamp((vPos.y / len - fr.skyStars.y) * fr.skyStars.z, 0.0, 1.0);
    float k = fade * fr.skyStars.x * len;
    vec3 tex = LinToSrgb(max(texture(mainTex, vStar.xy).rgb, vec3(0.0))) * vStar.z;
    vec3 col = TDU2DomeSkyGamma(vPos, k * tex);
    outColor = vec4(pow(max(col, vec3(0.0)), vec3(2.2)), 1.0);
}";

    // Mond (Original td_moon vs @0x4c0 / ps @0xfe0)
    // Billboard aus World/Stars.DrawMoon (Modellmatrix = rechts/oben/Achse · Größe), UV = (1 − u, v), Tiefe auf die Fernebene.
    public const string MoonVert = Common + ObjectInputs + @"
layout(location = 0) out vec2 vUv;
void main() {
    vec4 clip = pc.viewProj * (pc.model * vec4(inPos, 1.0));
    clip.z = 1e-6 * clip.w;
    gl_Position = clip;
    vUv = vec2(1.0 - inUv0.x, inUv0.y);
}";

    public const string MoonFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;
vec3 LinToSrgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
void main() {
    vec4 t = texture(mainTex, vUv);
    vec3 col = LinToSrgb(max(t.rgb, vec3(0.0))) + vec3(0.3922, 0.4118, 0.5098);   // Gamma-Raum wie das Original
    outColor = vec4(pow(col, vec3(2.2)), t.a * fr.skyStars.w);
}";

    // Wolken (Original TD_CloudMesh)
    // vs @0x760: Höhe · AlphaBlend.z; Sonnenseite = min(sat(−L·n_xz)·1,75; 1)·sat(1 − |L.y|·1,5); Streulicht wie td_dome, aber nur
    // Rayleigh (Phase cos²·0,5 + 2), Weglänge aus der Richtung der Weltposition (Spielkoordinaten), ln(1+x), Sättigung;
    // oT3.w = 0,5·Haze. Die Kuppel (fx/clouds.bnk, Radius ~200 km) liegt jenseits der Fernebene -> Tiefe auf die Fernebene
    // (Reversed-Z: knapp über 0), gezeichnet hinter allem wie der Himmel.
    public const string CloudVert = Common + ObjectInputs + @"
layout(location = 0) out vec4 vInst;   // oT1 Instance_Color
layout(location = 1) out vec4 vT2;     // oT2: uv, AlphaBlend.x, Sonnenseite
layout(location = 2) out vec4 vScat;   // oT3: Streulicht (Gamma), 0,5·Haze
void main() {
    vec3 p = vec3(inPos.x, fr.cloudAlpha.z * inPos.y, inPos.z);
    vec4 world = pc.model * vec4(p, 1.0);
    vec4 clip = pc.viewProj * world;
    clip.z = 1e-6 * clip.w;
    gl_Position = clip;
    vec3 L = -normalize(fr.sunDir.xyz);                     // __DirLight_Dir_0 (Lichtrichtung)
    vec3 nxz = normalize(vec3(inNormal.x, 0.0, inNormal.z));
    float side = min(clamp(dot(-L, nxz), 0.0, 1.0) * 1.75, 1.0);
    float low = clamp(abs(L.y) * -1.5 + 1.0, 0.0, 1.0);
    vT2 = vec4(inUv0, fr.cloudAlpha.x, side * low);
    vec3 S = normalize(fr.skyScatter.xyz);
    float c = max(dot(normalize(world.xyz - fr.camPos.xyz), S), 0.0);
    float phR = c * c * 0.5 + 2.0;
    vec4 ins = phR * fr.skyBetaDashR * fr.skyOneOver;
    vec3 gp = world.xyz - vec3(fr.misc.z, 0.0, fr.misc.w);   // Spielkoordinaten (r2 = World · Pos)
    float y = gp.y * inversesqrt(dot(gp, gp));
    float s = sqrt((y * 3e6) * (y * 3e6) + 1.525e11) - y * 3e6;
    ins *= vec4(1.0) - exp2(-fr.skyBetaRM * s * 1.442695);
    ins *= fr.skyMul.x * fr.skySun;
    vec3 rgb = fr.skyS2R0.xyz * ins.x + fr.skyS2R1.xyz * ins.y + fr.skyS2R2.xyz * ins.z + fr.skyS2R3.xyz * ins.w;
    rgb = log2(max(rgb, vec3(0.0)) + vec3(1.0)) * 0.6931;
    float lum = dot(rgb, vec3(0.299, 0.587, 0.114));
    vScat = vec4(rgb - fr.skyHG.w * (vec3(lum) - rgb), 0.5 * fr.cloudMisc.x);
    vInst = fr.cloudColor;
}";

    // ps @0x3170 (Gamma-Raum): Textur R = Licht, B = Schattenseite, A = Deckkraft (Datenkanäle -> sRGB-Dekodierung rückgängig)
    public const string CloudFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec4 vInst;
layout(location = 1) in vec4 vT2;
layout(location = 2) in vec4 vScat;
layout(location = 0) out vec4 outColor;
vec3 LinToSrgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
void main() {
    vec4 t = texture(mainTex, vT2.xy);
    t.rgb = LinToSrgb(max(t.rgb, vec3(0.0)));
    const vec3 lc = vec3(0.299, 0.587, 0.114);
    float hz = dot(vScat.rgb, lc) * vScat.w;
    vec3 e = fr.cloudMisc.y * vInst.rgb * t.x;
    vec3 e2 = e * t.x;
    vec3 col = hz * (vScat.rgb - e2) + e2;
    float sh = mix(1.0 - t.x, t.z, vInst.w);
    float k = sh * (1.0 - vT2.w);
    col += fr.cloudMisc.y * fr.cloudDown.rgb * k * fr.cloudDown.w;
    float l = dot(col, lc);
    col += vT2.w * (vec3(l) - col);
    outColor = vec4(pow(max(col, vec3(0.0)), vec3(2.2)), t.w * vT2.z * m.color.x);   // m.color.x Überblendgewicht
}";

    // Reifenspuren: dunkle, halbtransparente Streifen (Alpha aus Vertexfarbe x Textur); Vertexfarbe schwarz = Gummi, braun = Erde
    public const string SkidmarkFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv0;
layout(location = 2) in vec3 vWorld;
layout(location = 3) in vec3 vNormal;
layout(location = 0) out vec4 outColor;
// Wie das Original TD_TireSkid (ps_3_0): die Spur ist eine beleuchtete Fläche –
//   Farbe = Spurfarbe · lerp(Ambient, Diffus, max(N·L,0)·Schatten), Deckkraft = Maske · Vertex-Alpha.
// Vorher unbeleuchtet (Farbe direkt ausgegeben): auf Erde/Schotter im Schatten und bei Nacht leuchteten die hellen Spuren.
// Die Vertexfarbe ist eine Bildschirmfarbe (sRGB) und wird linear umgerechnet.
void main() {
    vec4 t = texture(mainTex, vUv0);
    vec3 n = dot(vNormal, vNormal) > 1e-8 ? normalize(vNormal) : vec3(0.0, 1.0, 0.0);
    if (n.y < 0.0) n = -n;
    float ndl = max(dot(n, normalize(fr.sunDir.xyz)), 0.0) * TDU2Shadow(vWorld, n);
    vec3 light = fr.ambient.rgb + fr.sunColor.rgb * ndl + TDU2Lamps(vWorld, n);
    vec3 col = pow(vColor.rgb, vec3(2.2)) * light;
    // TD_TireSkid ps_0: Deckkraft = Maske (Rot-Kanal von tarmac.2db / ground.2db) x Vertex-Alpha
    outColor = vec4(TDU2Fog(col, vWorld), t.r * vColor.a);
}";

    // Partikel (Unity Sprites/Default): Textur x Vertexfarbe
    public const string ParticleFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv0;
layout(location = 2) in vec3 vWorld;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 c = texture(mainTex, vUv0) * vColor;
    outColor = vec4(TDU2Fog(c.rgb, vWorld), c.a);
}";

    // Partikel (td_particle.sob): ps = Textur x Vertexfarbe, ohne Nebel. Beleuchtete Variante (vs 04/05): Farbe x
    // lerp(__AmbLight_Ambient_0, __DirLight_Diffuse_0, 0,75) im Gamma-Raum -> hier linear (pow 2,2). m.color.x = beleuchtet (.2DM).
    public const string TdParticleFrag = Common + ObjectMat + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv0;
layout(location = 2) in vec3 vWorld;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 c = texture(mainTex, vUv0) * vColor;
    if (m.color.x > 0.5 && fr.skyMul.y > 0.5)
        c.rgb *= pow(max(mix(fr.skyAmbient.rgb, fr.skyDiffuse.rgb, 0.75), vec3(0.0)), vec3(2.2));
    outColor = c;
}";

    // Flare: tex x _Color x _Intensity (in der Vertexfarbe), Nebel nach Schwarz (UNITY_APPLY_FOG_COLOR(.., 0))
    public const string FlareFrag = Common + @"
layout(set = 0, binding = 0) uniform sampler2D mainTex;
layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv0;
layout(location = 2) in vec3 vWorld;
layout(location = 0) out vec4 outColor;
void main() {
    vec3 c = texture(mainTex, vUv0).rgb * vColor.rgb * (vColor.a * 4.0);
    if (fr.env.w > 0.5) {
        float d = length(vWorld - fr.camPos.xyz);
        c *= clamp((fr.env.z - d) / max(fr.env.z - fr.env.y, 1.0), 0.0, 1.0);
    }
    outColor = vec4(c, clamp(max(c.r, max(c.g, c.b)), 0.0, 1.0));   // Alpha = Bloom-Maske (Blend One/One: Farbe unabhängig vom Alpha)
}";

    // Blitz (Original TD_Lightning, selbst disassembliert)
    // vs: Drehung um Y mit atan2(CamDir_ObjSpace.x, .z) – macht World/Lightning schon in der Modellmatrix, daher FxVert.
    // ps (keine Textur): A = (1 − u²)²·v, B = (1 − clamp(5u, −1, 1)²)·v, Farbe = B·0,1 + A·0,9 (Gamma-Raum), Alpha = Vertexalpha;
    // Blend SRCALPHA/ONE.
    public const string LightningFrag = Common + @"
layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv0;
layout(location = 0) out vec4 outColor;
void main() {
    float u = vUv0.x, v = vUv0.y;
    float c = clamp(5.0 * u, -1.0, 1.0);
    float a = 1.0 - u * u;
    a = a * a * v;
    float b = (1.0 - c * c) * v - a;
    vec3 col = vec3(b * 0.1 + a);
    outColor = vec4(pow(max(col, vec3(0.0)), vec3(2.2)), vColor.a);
}";

    public const string ShadowEmptyFrag = Common + @"
void main() { }";
}
