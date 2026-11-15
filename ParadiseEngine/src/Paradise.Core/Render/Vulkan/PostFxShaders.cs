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
/// Texturen (set 0): binding 0 = Eingang/Szene (sA), 1 = Unschärfe Viertel (sB), 2 = LUT (sLut). Alle linear, Rand geklemmt.
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
    /// </summary>
    public const string ApplyFrag = Head + @"
void main() {
    vec2 uv = Uv();
    vec3 a = texture(sA, uv).rgb;
    vec3 bq = texture(sB, uv).rgb;
    float L = dot(mix(a, bq, p.vars.z), vec3(0.299, 0.587, 0.114));
    outColor = vec4(texture(sLut, vec2(a.r, L)).r,
                    texture(sLut, vec2(a.g, L)).g,
                    texture(sLut, vec2(a.b, L)).b, 1.0);
}";
}
