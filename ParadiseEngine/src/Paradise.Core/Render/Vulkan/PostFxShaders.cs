namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// GLSL der Photoshop-Nachbearbeitung (Docs\re\postfx_spec.md §2 und §3). Alle Durchgänge zeichnen ein Vollbild-Dreieck ohne
/// Vertexpuffer und lesen ihre Werte aus einem gemeinsamen Push-Constant-Block (<see cref="Push"/>, 112 Byte):
///   vars      xy = 1 / Größe des Ziels (wie das Original "vars"), zw = je Durchgang (siehe unten)
///   params    LUT: (softlight_opacity, softlight_brightness, overlay_opacity)   – Original c9
///   shadows   LUT: Farbbalance Tiefen   – c10
///   midtones  LUT: Farbbalance Mitten   – c11
///   hightones LUT: Farbbalance Lichter  – c12
///   bloom     LUT: (black, 1/(white-black), intensity)                          – c13
///   gamma     LUT: x = Gamma                                                    – c14
/// Texturen (set 0): binding 0 = Eingang/Szene (sA), 1 = Unschärfe Viertel (sB), 2 = LUT (sLut), 3 = Bloom (sBloom, Zusatz),
/// 4 = AO (sAo, Zusatz). Alle linear, Rand geklemmt.
/// Zusatz der Engine (nicht im Original, siehe <see cref="Paradise.Core.Render.PostFx"/>): BrightFrag, AoFrag, ApplyFrag-Flags.
/// Gerechnet wird auf den gespeicherten 8-Bit-Werten (UNORM-Sicht), also im selben Raum wie das Original (A8R8G8B8, ohne sRGB).
/// </summary>
internal static class PostFxShaders
{
    private const string Head = @"#version 450
layout(push_constant) uniform Push {
    vec4 vars;
    vec4 params;
    vec4 shadows;
    vec4 midtones;
    vec4 hightones;
    vec4 bloom;
    vec4 gamma;
} p;
layout(set = 0, binding = 0) uniform sampler2D sA;
layout(set = 0, binding = 1) uniform sampler2D sB;
layout(set = 0, binding = 2) uniform sampler2D sLut;
layout(set = 0, binding = 3) uniform sampler2D sBloom;
layout(set = 0, binding = 4) uniform sampler2D sAo;
layout(location = 0) out vec4 outColor;
// Pixelmitte des Ziels in Texturkoordinaten
vec2 Uv() { return gl_FragCoord.xy * p.vars.xy; }
";

    /// <summary>Vollbild-Dreieck (3 Ecken, ohne Vertexpuffer).</summary>
    public const string FullscreenVert = @"#version 450
void main() {
    vec2 q = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(q * 2.0 - 1.0, 0.0, 1.0);
}";

    /// <summary>
    /// §2.3 Schritt 1 – Verkleinern auf ein Viertel (PFX_Sampling): 4 bilineare Abtastungen um die Pixelmitte, Mittelwert.
    /// vars.zw = (0.25/Zielbreite, 0.25/Zielhöhe) = genau ein Quellpixel.
    /// </summary>
    public const string DownsampleFrag = Head + @"
void main() {
    vec2 uv = Uv();
    vec3 c = texture(sA, uv + vec2(-p.vars.z, 0.0)).rgb
           + texture(sA, uv + vec2( p.vars.z, 0.0)).rgb
           + texture(sA, uv + vec2(0.0, -p.vars.w)).rgb
           + texture(sA, uv + vec2(0.0,  p.vars.w)).rgb;
    outColor = vec4(c * 0.25, 1.0);
}";

    /// <summary>
    /// §2.3 Schritt 2 / §3 – Gauß 5 Abtastungen (PFX_Blur spec 2), ein Durchgang je Richtung.
    /// vars.zw = Schritt (waagerecht (1/w, 0), senkrecht (0, 1/h)); Gewichte w0..w2 in params.xyz.
    /// </summary>
    public const string BlurFrag = Head + @"
void main() {
    vec2 uv = Uv(), s = p.vars.zw;
    vec3 c = texture(sA, uv).rgb * p.params.x
           + (texture(sA, uv - s).rgb + texture(sA, uv + s).rgb) * p.params.y
           + (texture(sA, uv - 2.0 * s).rgb + texture(sA, uv + 2.0 * s).rgb) * p.params.z;
    outColor = vec4(c, 1.0);
}";

    /// <summary>
    /// LUT-Texel (PFX_LutCreate, 256×256). u = Kanalwert (Spalte), v = Helligkeit (Zeile); Texel i wird bei u = i/256
    /// ausgewertet wie im Original (D3D9 ohne Halbpixel-Versatz).
    /// Abschrift von Euro/Shaders/edrender_pfx_lut_creation.sob ps_3_0 (selbst disassembliert). ACHTUNG: postfx_spec §2.5 lässt
    /// die Begrenzungen (_sat) weg – das Original begrenzt b, jeden Farbbalance-Schritt, die Bloom-Rampe und das Ergebnis vor dem
    /// Gamma auf [0,1]. Ohne sie kippt die Kurve in den dunklen Tönen (Schwarz wird grau, 0,2 wird schwarz -> fleckige Schatten).
    /// </summary>
    public const string LutFrag = Head + @"
void main() {
    float u = (gl_FragCoord.x - 0.5) * p.vars.x;
    float v = (gl_FragCoord.y - 0.5) * p.vars.y;
    vec3 S = p.shadows.rgb, M = p.midtones.rgb, H = p.hightones.rgb;

    // Farbbalance (Übertragungskurven wie GIMP, 15.9375 = 255/16)
    vec3 b = vec3(clamp(p.params.y * v, 0.0, 1.0));                                  // mul_sat
    vec3 fSneg = 1.075 - 1.0 / (1.0 + 15.9375 * (1.0 - b));
    vec3 fSpos = 1.075 - 1.0 / (1.0 + 15.9375 * b);
    vec3 C = clamp(b + S * mix(fSpos, fSneg, lessThanEqual(S, vec3(0.0))), 0.0, 1.0);   // mad_sat
    vec3 m = (C * 255.0 - 127.0) * 0.007874015718698502;
    C = clamp(C + M * 0.667 * (1.0 - m * m), 0.0, 1.0);                                  // mad_sat
    vec3 fHneg = 1.075 - 1.0 / (1.0 + 15.9375 * C);
    vec3 fHpos = 1.075 - 1.0 / (1.0 + 15.9375 * (1.0 - C));
    C = clamp(C + H * mix(fHpos, fHneg, lessThanEqual(H, vec3(0.0))), 0.0, 1.0);        // mad_sat

    // Weiches Licht: Kanalwert u mit Ebene C (Auswahl über C.g für alle Kanäle – so kompiliert im Original)
    vec3 hi = sqrt(u) * (2.0 * C - 1.0) + 2.0 * u * (1.0 - C);
    vec3 lo = 2.0 * u * C + u * u * (1.0 - 2.0 * C);
    vec3 SL = (C.g - 0.5 >= 0.0) ? hi : lo;
    vec3 X = mix(vec3(u), SL, p.params.x);

    // Ineinanderkopieren: Basis X, Ebene v
    vec3 O = mix(2.0 * v * X, 1.0 - 2.0 * (1.0 - X) * (1.0 - v), greaterThanEqual(X - 0.5, vec3(0.0)));
    vec3 Y = mix(X, O, p.params.z);

    // Bloom-Rampe: nur aufhellen (mul_sat), Ergebnis begrenzt (mad_sat)
    float ramp = clamp((v - p.bloom.x) * p.bloom.y, 0.0, 1.0);
    Y = clamp(Y + p.bloom.z * ramp, 0.0, 1.0);

    // Gamma: exp2(G * log2(Y)), Y in [0,1] (log2(0) -> -inf -> 0)
    vec3 g = pow(Y, vec3(p.gamma.x));
    outColor = vec4(mix(g, vec3(0.0), equal(Y, vec3(0.0))), 1.0);
}";

    /// <summary>
    /// §2.5 Anwenden (PFX_ApplyLut ps_0): Zeile = Helligkeit aus Bild und Unschärfe gemischt (vars.z = BlurredLumFactor),
    /// jeder Kanal einzeln über die Tabelle. Gezeichnet als erstes im UI-Durchgang über das ganze Bild.
    /// Zusatz (Engine): vars.w = Flags (1 Photoshop, 2 Bloom, 4 AO); params.x = Bloom-Stärke, params.y = AO-Stärke.
    /// AO dunkelt das Bild vor der Tabelle ab, Bloom kommt danach per "Negativ multiplizieren" dazu. Nur Photoshop = Original.
    /// </summary>
    public const string ApplyFrag = Head + @"
void main() {
    vec2 uv = Uv();
    int flags = int(p.vars.w + 0.5);
    vec3 a = texture(sA, uv).rgb;
    if ((flags & 4) != 0) a *= mix(1.0, texture(sAo, uv).r, p.params.y);
    vec3 c = a;
    if ((flags & 1) != 0) {
        vec3 bq = texture(sB, uv).rgb;
        float L = dot(mix(a, bq, p.vars.z), vec3(0.299, 0.587, 0.114));
        c = vec3(texture(sLut, vec2(a.r, L)).r,
                 texture(sLut, vec2(a.g, L)).g,
                 texture(sLut, vec2(a.b, L)).b);
    }
    if ((flags & 2) != 0) {
        vec3 b = clamp(texture(sBloom, uv).rgb * p.params.x, 0.0, 1.0);
        c = 1.0 - (1.0 - c) * (1.0 - b);
    }
    if ((flags & 8) != 0) c = vec3(texture(sAo, uv).r);                                   // Diagnose: AO-Bild
    if ((flags & 16) != 0) c = clamp(texture(sBloom, uv).rgb * p.params.x, 0.0, 1.0);     // Diagnose: Bloom-Bild
    if ((flags & 32) != 0) c = vec3(texture(sA, uv).a);                                   // Diagnose: Bloom-Maske (Alpha)
    outColor = vec4(c, 1.0);
}";

    /// <summary>
    /// Zusatz Bloom, Schritt 1: helle Stellen auf ein Viertel (4 Abtastungen wie DownsampleFrag), weiche Schwelle über den größten
    /// Farbkanal, dazu alles mit Bloom-Maske (Alpha). vars.zw wie DownsampleFrag; params = (Schwelle, Knie, Masken-Stärke).
    /// </summary>
    public const string BrightFrag = Head + @"
void main() {
    vec2 uv = Uv();
    vec4 s = (texture(sA, uv + vec2(-p.vars.z, 0.0))
            + texture(sA, uv + vec2( p.vars.z, 0.0))
            + texture(sA, uv + vec2(0.0, -p.vars.w))
            + texture(sA, uv + vec2(0.0,  p.vars.w))) * 0.25;
    vec3 c = s.rgb;
    float br = max(c.r, max(c.g, c.b));
    float knee = max(p.params.y, 1e-4);
    float soft = clamp(br - p.params.x + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float w = max(soft, br - p.params.x) / max(br, 1e-4);
    // Leuchtende Materialien (Alpha des Weltbilds = Bloom-Maske: Lampen, Flares, Glow-Sprites, Neon, Hologramme) leuchten
    // unabhängig von der Schwelle – im 8-Bit-Bild sind sie sonst nicht heller als eine weiße Wand. params.z = Stärke.
    vec3 e = c * clamp(s.a * p.params.z, 0.0, 4.0);
    outColor = vec4(max(c * w, e), 1.0);
}";

    /// <summary>
    /// Zusatz AO (halbe Auflösung): Bildschirmraum-Verdeckung aus der Weltdurchgang-Tiefe (Reversed-Z, bei MSAA Abtastung 0).
    /// Sichtraum aus der Tiefe: z = B / (d + A) mit A = proj.M33, B = proj.M43; x = ndc.x·z/P00, y = ndc.y·z/P11.
    /// Push: vars.xy = 1/Zielgröße, params = (A, B, P00, P11), shadows = (Radius m, Neigung, Ausblend-Entfernung, Stärke 1).
    /// Ergebnis r = Sichtbarkeit (1 = frei). sA ist hier die Tiefe; MSAA-Variante ersetzt sampler2D durch sampler2DMS.
    /// </summary>
    public const string AoFrag = @"#version 450
layout(push_constant) uniform Push {
    vec4 vars;
    vec4 params;
    vec4 shadows;
    vec4 midtones;
    vec4 hightones;
    vec4 bloom;
    vec4 gamma;
} p;
layout(set = 0, binding = 0) uniform sampler2D sA;
layout(location = 0) out vec4 outColor;
ivec2 gSize;
float Depth(ivec2 q) { return texelFetch(sA, clamp(q, ivec2(0), gSize - 1), 0).r; }
vec3 ViewPos(ivec2 q) {
    float d = Depth(q);
    float z = d <= 0.0 ? 1e6 : p.params.y / (d + p.params.x);
    vec2 ndc = (vec2(q) + 0.5) / vec2(gSize) * 2.0 - 1.0;
    return vec3(ndc.x * z / p.params.z, ndc.y * z / p.params.w, z);
}
void main() {
    gSize = textureSize(sA AO_LOD);
    ivec2 px = ivec2(gl_FragCoord.xy * 2.0);
    vec3 P = ViewPos(px);
    if (P.z >= p.shadows.z || Depth(px) <= 0.0) { outColor = vec4(1.0); return; }
    // Normale aus den Nachbarn (jeweils die nähere Seite, damit Kanten keine falschen Normalen bekommen)
    vec3 l = ViewPos(px - ivec2(1, 0)), r = ViewPos(px + ivec2(1, 0));
    vec3 u = ViewPos(px - ivec2(0, 1)), d = ViewPos(px + ivec2(0, 1));
    vec3 dx = abs(r.z - P.z) < abs(P.z - l.z) ? r - P : P - l;
    vec3 dy = abs(d.z - P.z) < abs(P.z - u.z) ? d - P : P - u;
    vec3 n = normalize(cross(dx, dy));
    if (dot(n, -P) < 0.0) n = -n;
    // Radius in Bildpunkten (begrenzt), gedrehte Spirale je Bildpunkt
    float rpx = clamp(p.shadows.x * abs(p.params.z) * 0.5 * float(gSize.x) / P.z, 2.0, 80.0);
    float rot = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))) * 6.2831853;
    const int N = 12;
    float occ = 0.0;
    for (int i = 0; i < N; i++) {
        float t = (float(i) + 0.5) / float(N);
        float ang = rot + float(i) * 2.3999632;          // goldener Winkel
        vec2 off = vec2(cos(ang), sin(ang)) * rpx * sqrt(t);
        vec3 Q = ViewPos(px + ivec2(off));
        vec3 v = Q - P;
        float dist = length(v);
        if (dist < 1e-4) continue;
        float fall = clamp(1.0 - dist / p.shadows.x, 0.0, 1.0);
        occ += max(dot(n, v / dist) - p.shadows.y, 0.0) * fall;
    }
    float ao = 1.0 - occ / float(N) * 2.0;
    ao = mix(ao, 1.0, smoothstep(p.shadows.z * 0.6, p.shadows.z, P.z));
    outColor = vec4(vec3(clamp(ao, 0.0, 1.0)), 1.0);
}";

    /// <summary>AO-Shader passend zur Tiefe: mit MSAA sampler2DMS (texelFetch Abtastung 0), sonst sampler2D (Mip 0).</summary>
    public static string AoSource(bool msaa) => msaa
        ? AoFrag.Replace("uniform sampler2D sA;", "uniform sampler2DMS sA;").Replace("AO_LOD", "")
        : AoFrag.Replace("AO_LOD", ", 0");
}
