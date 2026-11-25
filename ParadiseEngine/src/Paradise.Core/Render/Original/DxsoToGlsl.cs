using System.Globalization;
using System.Text;

namespace Paradise.Core.Render.Original;

public static class DxsoToGlsl
{
    public sealed class Result
    {
        public string Glsl = "";
        public readonly List<(int Usage, int Index, int Location)> Inputs = new();   // nur VS
        public uint UsedSamplers;
        public readonly Dictionary<int, int> SamplerTypes = new();                    // N -> 2/3/4
    }

    public static int RegType(uint t) => (int)(((t >> 28) & 7) | ((t >> 8) & 0x18));

    const int T_TEMP = 0, T_INPUT = 1, T_CONST = 2, T_ADDR = 3, T_RASTOUT = 4, T_ATTROUT = 5, T_OUTPUT = 6, T_CONSTINT = 7,
              T_COLOROUT = 8, T_DEPTHOUT = 9, T_SAMPLER = 10, T_CONSTBOOL = 14, T_LOOP = 15, T_MISC = 17, T_PRED = 19;
    const int U_POSITION = 0, U_TEXCOORD = 5, U_COLOR = 10, U_FOG = 11, U_PSIZE = 4;

    public static Result Translate(DxsoShader s)
    {
        var res = new Result();
        var tok = s.Tokens;
        var defs = new Dictionary<int, string>();
        var defis = new Dictionary<int, int[]>();
        var defbs = new Dictionary<int, bool>();
        var inDecl = new Dictionary<int, DxsoShader.Decl>();
        var outDecl = new Dictionary<int, DxsoShader.Decl>();
        int maxTemp = -1;
        bool usesA0 = false, usesAL = false;
        var body = new StringBuilder();
        int indent = 1, loop = 0;

        foreach (var d in s.Decls)
        {
            if (d.RegType == T_SAMPLER) { res.UsedSamplers |= 1u << d.Register; res.SamplerTypes[d.Register] = d.SamplerType == 0 ? 2 : d.SamplerType; }
            else if (d.RegType == T_INPUT) inDecl[d.Register] = d;
            else if (d.RegType == T_OUTPUT) outDecl[d.Register] = d;
        }

        // ---------------------------------------------------------------- Operanden
        string Comp(int c) => "xyzw"[c & 3].ToString();
        string F(float v)
        {
            if (float.IsNaN(v)) return "0.0";
            if (float.IsPositiveInfinity(v)) return "3.402823e38";
            if (float.IsNegativeInfinity(v)) return "-3.402823e38";
            string r = v.ToString("R", CultureInfo.InvariantCulture);
            if (!r.Contains('.') && !r.Contains('E') && !r.Contains('e')) r += ".0";
            return r;
        }

        string RegName(uint t, uint? rel, bool isDst)
        {
            int type = RegType(t), n = (int)(t & 0x7FF);
            switch (type)
            {
                case T_TEMP: maxTemp = System.Math.Max(maxTemp, n); return "r" + n;
                case T_INPUT: return "v" + n;
                case T_CONST:
                    if (rel.HasValue)
                    {
                        usesA0 = true;
                        int rc = (int)((rel.Value >> 16) & 3);   // Swizzle-Komponente des Adressregisters
                        string ar = RegType(rel.Value) == T_LOOP ? "aL" : "a0";
                        if (ar == "aL") usesAL = true;
                        return (s.IsPixel ? "PC.c" : "VC.c") + "[clamp(" + n + " + int(" + ar + "." + Comp(rc) + "), 0, " + (s.IsPixel ? 223 : 255) + ")]";
                    }
                    if (defs.TryGetValue(n, out var dv)) return dv;
                    return (s.IsPixel ? "PC.c[" : "VC.c[") + n + "]";
                case T_ADDR: if (s.IsPixel) return "t" + n; usesA0 = true; return "a0";
                case T_RASTOUT: return n == 0 ? "oPos" : n == 1 ? "oFog" : "oPts";
                case T_ATTROUT: return "oD" + n;
                case T_OUTPUT: return "o" + n;
                case T_CONSTINT: return defis.TryGetValue(n, out var iv) ? "vec4(" + iv[0] + ", " + iv[1] + ", " + iv[2] + ", " + iv[3] + ")" : "vec4(0.0)";
                case T_COLOROUT: return "oC" + n;
                case T_DEPTHOUT: return "oDepth";
                case T_SAMPLER: return "s" + n;
                case T_CONSTBOOL: return defbs.TryGetValue(n, out var bv) && bv ? "vec4(1.0)" : "vec4(0.0)";
                case T_LOOP: usesAL = true; return "aL";
                case T_MISC: return n == 0 ? "vPos" : "vFace";
                case T_PRED: return "p0";
                default: throw new NotSupportedException("Registertyp " + type);
            }
        }

        // Quelle als vec4 (Swizzle + Modifikator)
        string Src(uint t, uint? rel)
        {
            string r = RegName(t, rel, false);
            int sw = (int)((t >> 16) & 0xFF);
            string swz = "." + Comp(sw) + Comp(sw >> 2) + Comp(sw >> 4) + Comp(sw >> 6);
            string v = r.StartsWith("vec4(") ? "(" + r + ")" + swz : r + swz;
            int mod = (int)((t >> 24) & 0xF);
            return mod switch
            {
                0 => v,
                1 => "(-" + v + ")",
                2 => "(" + v + " - 0.5)",
                3 => "(0.5 - " + v + ")",
                4 => "(" + v + " * 2.0 - 1.0)",
                5 => "(1.0 - " + v + " * 2.0)",
                6 => "(1.0 - " + v + ")",
                7 => "(" + v + " * 2.0)",
                8 => "(" + v + " * -2.0)",
                11 => "abs(" + v + ")",
                12 => "(-abs(" + v + "))",
                _ => throw new NotSupportedException("Quellmodifikator " + mod),
            };
        }

        void Emit(string line) { body.Append(' ', indent * 4).Append(line).Append('\n'); }

        void Assign(uint dt, string vec4Expr)
        {
            string r = RegName(dt, null, true);
            int mask = (int)((dt >> 16) & 0xF);
            bool sat = ((dt >> 20) & 1) != 0;
            string e = sat ? "clamp(" + vec4Expr + ", 0.0, 1.0)" : vec4Expr;
            if (r == "oDepth") { Emit("gl_FragDepth = (" + e + ").x;"); return; }
            if (mask == 0xF) { Emit(r + " = " + e + ";"); return; }
            var m = new StringBuilder();
            for (int c = 0; c < 4; c++) if ((mask & (1 << c)) != 0) m.Append("xyzw"[c]);
            Emit(r + "." + m + " = (" + e + ")." + m + ";");
        }

        // ---------------------------------------------------------------- Befehle
        int i = 1;
        var pending = new List<string>();
        while (i < tok.Length)
        {
            uint t = tok[i];
            if (t == 0x0000FFFF) break;
            int op = (int)(t & 0xFFFF);
            if (op == 0xFFFE) { i += 1 + (int)((t >> 16) & 0x7FFF); continue; }
            int len = (int)((t >> 24) & 0xF);
            int ctl = (int)((t >> 16) & 0xFF);
            var a = new uint[len];
            Array.Copy(tok, i + 1, a, 0, System.Math.Min(len, tok.Length - i - 1));
            i += 1 + len;

            // Operanden mit relativer Adressierung (Bit 13 -> Folgetoken)
            var ops = new List<(uint T, uint? Rel)>();
            for (int k = 0; k < a.Length; k++)
            {
                uint x = a[k]; uint? rel = null;
                if ((x & 0x2000) != 0 && k + 1 < a.Length && op != 81 && op != 48 && op != 47 && op != 31) { rel = a[k + 1]; k++; }
                ops.Add((x, rel));
            }
            string S(int k) => Src(ops[k].T, ops[k].Rel);
            string Sx(int k) => S(k) + ".x";

            switch (op)
            {
                case 0: break;                                                         // nop
                case 31: break;                                                        // dcl (vorab gelesen)
                case 81:                                                               // def
                    {
                        int n = (int)(a[0] & 0x7FF);
                        defs[n] = "vec4(" + F(BitConverter.Int32BitsToSingle((int)a[1])) + ", " + F(BitConverter.Int32BitsToSingle((int)a[2])) + ", " +
                                  F(BitConverter.Int32BitsToSingle((int)a[3])) + ", " + F(BitConverter.Int32BitsToSingle((int)a[4])) + ")";
                        break;
                    }
                case 48: defis[(int)(a[0] & 0x7FF)] = new[] { (int)a[1], (int)a[2], (int)a[3], (int)a[4] }; break;   // defi
                case 47: defbs[(int)(a[0] & 0x7FF)] = a[1] != 0; break;                                                // defb
                case 1: Assign(ops[0].T, S(1)); break;                                 // mov
                case 2: Assign(ops[0].T, S(1) + " + " + S(2)); break;                  // add
                case 3: Assign(ops[0].T, S(1) + " - " + S(2)); break;                  // sub
                case 4: Assign(ops[0].T, S(1) + " * " + S(2) + " + " + S(3)); break;   // mad
                case 5: Assign(ops[0].T, S(1) + " * " + S(2)); break;                  // mul
                case 6: Assign(ops[0].T, "vec4(1.0 / " + Sx(1) + ")"); break;          // rcp
                case 7: Assign(ops[0].T, "vec4(inversesqrt(abs(" + Sx(1) + ")))"); break;   // rsq
                case 8: Assign(ops[0].T, "vec4(dot((" + S(1) + ").xyz, (" + S(2) + ").xyz))"); break;   // dp3
                case 9: Assign(ops[0].T, "vec4(dot(" + S(1) + ", " + S(2) + "))"); break;              // dp4
                case 10: Assign(ops[0].T, "min(" + S(1) + ", " + S(2) + ")"); break;
                case 11: Assign(ops[0].T, "max(" + S(1) + ", " + S(2) + ")"); break;
                case 12: Assign(ops[0].T, "vec4(lessThan(" + S(1) + ", " + S(2) + "))"); break;            // slt
                case 13: Assign(ops[0].T, "vec4(greaterThanEqual(" + S(1) + ", " + S(2) + "))"); break;    // sge
                case 14: Assign(ops[0].T, "vec4(exp2(" + Sx(1) + "))"); break;                             // exp
                case 15: Assign(ops[0].T, "vec4(DxLog(" + Sx(1) + "))"); break;                            // log
                case 16: Assign(ops[0].T, "DxLit(" + S(1) + ")"); break;                                   // lit
                case 17: Assign(ops[0].T, "vec4(1.0, (" + S(1) + ").y * (" + S(2) + ").y, (" + S(1) + ").z, (" + S(2) + ").w)"); break;   // dst
                case 18: Assign(ops[0].T, "mix(" + S(3) + ", " + S(2) + ", " + S(1) + ")"); break;                 // lrp: s0*s1 + (1-s0)*s2
                case 19: Assign(ops[0].T, "fract(" + S(1) + ")"); break;                                           // frc
                case 20: case 21: case 22: case 23: case 24:                                                        // m4x4 m4x3 m3x4 m3x3 m3x2
                    {
                        int rows = op == 20 || op == 22 ? 4 : op == 21 || op == 23 ? 3 : 2;
                        bool v3 = op >= 22;
                        uint baseT = ops[2].T; int baseN = (int)(baseT & 0x7FF);
                        var parts = new List<string>();
                        for (int k = 0; k < 4; k++)
                        {
                            if (k >= rows) { parts.Add("0.0"); continue; }
                            uint rt = (baseT & ~0x7FFu) | (uint)(baseN + k);
                            string m = Src(rt, ops[2].Rel);
                            parts.Add(v3 ? "dot((" + S(1) + ").xyz, (" + m + ").xyz)" : "dot(" + S(1) + ", " + m + ")");
                        }
                        Assign(ops[0].T, "vec4(" + string.Join(", ", parts) + ")");
                        break;
                    }
                case 32: Assign(ops[0].T, "vec4(pow(abs(" + Sx(1) + "), " + Sx(2) + "))"); break;          // pow
                case 33: Assign(ops[0].T, "vec4(cross((" + S(1) + ").xyz, (" + S(2) + ").xyz), 0.0)"); break;   // crs
                case 34: Assign(ops[0].T, "sign(" + S(1) + ")"); break;                                    // sgn
                case 35: Assign(ops[0].T, "abs(" + S(1) + ")"); break;                                     // abs
                case 36: Assign(ops[0].T, "vec4(DxNrm((" + S(1) + ").xyz), 0.0)"); break;                  // nrm (w laut Maske)
                case 37: Assign(ops[0].T, "vec4(cos(" + Sx(1) + "), sin(" + Sx(1) + "), 0.0, 0.0)"); break;   // sincos (SM3: x = cos, y = sin)
                case 38:                                                                                    // rep iN
                    Emit("for (int L" + loop + " = 0; L" + loop + " < int(" + Sx(0) + "); L" + loop + "++) {"); loop++; indent++; break;
                case 39: indent--; Emit("}"); break;                                                        // endrep
                case 27:                                                                                    // loop aL, iN
                    usesAL = true;
                    Emit("aL = vec4(" + S(1) + ".y); for (int L" + loop + " = 0; L" + loop + " < int(" + S(1) + ".x); L" + loop + "++, aL += vec4(" + S(1) + ".z)) {"); loop++; indent++; break;
                case 29: indent--; Emit("}"); break;                                                        // endloop
                case 40: Emit("if (" + Sx(0) + " != 0.0) {"); indent++; break;                              // if b
                case 41: Emit("if (" + Sx(0) + Cmp(ctl) + Sx(1) + ") {"); indent++; break;                  // if_cmp
                case 42: indent--; Emit("} else {"); indent++; break;                                       // else
                case 43: indent--; Emit("}"); break;                                                        // endif
                case 44: Emit("break;"); break;
                case 45: Emit("if (" + Sx(0) + Cmp(ctl) + Sx(1) + ") break;"); break;                      // breakc
                case 46: usesA0 = true; Emit("a0 = vec4(round(" + S(1) + "));"); break;                     // mova
                case 65:                                                                                    // texkill
                    {
                        string r = RegName(ops[0].T, null, true);
                        int mask = (int)((ops[0].T >> 16) & 0xF); if (mask == 0) mask = 0xF;
                        var c = new List<string>();
                        for (int k = 0; k < 4; k++) if ((mask & (1 << k)) != 0) c.Add(r + "." + "xyzw"[k] + " < 0.0");
                        Emit("if (" + string.Join(" || ", c) + ") discard;");
                        break;
                    }
                case 66:                                                                                    // texld / texldp / texldb
                    {
                        int sn = (int)(ops[2].T & 0x7FF);
                        string co = S(1);
                        string coord = TexCoord(res, sn, co, ctl == 1);
                        string e = ctl == 2 ? "texture(s" + sn + ", " + coord + ", (" + co + ").w)" : "texture(s" + sn + ", " + coord + ")";
                        Assign(ops[0].T, Swz("TexIn(" + sn + ", " + e + ")", ops[2].T));
                        break;
                    }
                case 95:                                                                                    // texldl
                    {
                        int sn = (int)(ops[2].T & 0x7FF);
                        string co = S(1);
                        Assign(ops[0].T, Swz("TexIn(" + sn + ", textureLod(s" + sn + ", " + TexCoord(res, sn, co, false) + ", (" + co + ").w))", ops[2].T));
                        break;
                    }
                case 93:                                                                                    // texldd
                    {
                        int sn = (int)(ops[2].T & 0x7FF);
                        int ty = res.SamplerTypes.TryGetValue(sn, out var st) ? st : 2;
                        string gsw = ty == 2 ? ".xy" : ".xyz";
                        Assign(ops[0].T, Swz("TexIn(" + sn + ", textureGrad(s" + sn + ", " + TexCoord(res, sn, S(1), false) + ", (" + S(3) + ")" + gsw + ", (" + S(4) + ")" + gsw + "))", ops[2].T));
                        break;
                    }
                case 88: Assign(ops[0].T, "mix(" + S(3) + ", " + S(2) + ", greaterThanEqual(" + S(1) + ", vec4(0.0)))"); break;   // cmp (Auswahl, keine Arithmetik)
                case 90: Assign(ops[0].T, "vec4(dot((" + S(1) + ").xy, (" + S(2) + ").xy) + (" + S(3) + ").x)"); break;               // dp2add
                case 91: Assign(ops[0].T, "dFdx(" + S(1) + ")"); break;                                     // dsx
                case 92: Assign(ops[0].T, "dFdy(" + S(1) + ")"); break;                                     // dsy
                case 28: Emit("return;"); break;                                                            // ret
                default: throw new NotSupportedException("Opcode " + op);
            }
        }

        // ---------------------------------------------------------------- Rahmen
        var g = new StringBuilder();
        g.Append("#version 450\n// Übersetzt aus D3D9 ").Append(s.IsPixel ? "ps" : "vs").Append('_').Append(s.Major).Append('_').Append(s.Minor).Append(" (Paradise.Core.Render.Original.DxsoToGlsl)\n");
        // ext.x: Sampler-Maske "sRGB-Textur" (Vulkan liefert linear, D3D9 lieferte die gespeicherten Gammawerte -> zurückrechnen),
        // ext.y: 1 = Ergebnis des Pixelshaders (Gamma) in den linearen sRGB-Rahmen umrechnen (Übergang, solange der Rest linear rechnet)
        if (s.IsPixel) g.Append("layout(set = 0, binding = 1, std140) uniform PsConst { vec4 c[224]; uvec4 ext; } PC;\n");
        else g.Append("layout(set = 0, binding = 0, std140) uniform VsConst { vec4 c[256]; uvec4 ext; mat4 world; } VC;\n");
        // Lichtpuffer s15 (gTexSamplerShadowRealTime, x = Sonnensichtbarkeit): statt des Bildschirmpuffers des Originals rechnet der PS die
        // Sonnensichtbarkeit selbst aus den Schattenkaskaden der Engine (Set 1) – dafür gibt der VS Weltposition/-normale aus (location 12/13)
        bool lightBuf = s.IsPixel && res.SamplerTypes.ContainsKey(15);
        if (lightBuf)
        {
            // Aufbau wie VulkanRenderer.FrameUbo (std140: vec4-Felder/-Arrays ohne Lücken); frPad = 29 vec4 von Planar bis RainRipple,
            // danach die Scheinwerfer-Projektoren
            g.Append("layout(set = 1, binding = 0, std140) uniform Frame { vec4 camPos, sunDir, sunColor, ambient, misc, viewRight, viewUp, shadow, splits, texelWorld; mat4 shadowVP[4]; vec4 env, fogColor, oceanFresnel, oceanSpec, oceanRefl, oceanMisc; vec4 lampPos[32]; vec4 lampCol[32]; vec4 lampCone[32]; vec4 frPad[29]; vec4 proj[40]; vec4 projMisc; } fr;\n");
            g.Append("layout(set = 1, binding = 1) uniform sampler2DArrayShadow shadowMap;\n");
            g.Append("layout(set = 1, binding = 7) uniform sampler2D headlightCookie;\n");
            g.Append("layout(location = 12) in vec4 xWp;\nlayout(location = 13) in vec4 xWn;\n");
            g.Append(SunShadowGlsl);
            g.Append(LightBufGlsl);
        }
        if (!s.IsPixel) g.Append("layout(location = 12) out vec4 xWp;\nlayout(location = 13) out vec4 xWn;\n");
        foreach (var kv in res.SamplerTypes.OrderBy(k => k.Key))
            g.Append("layout(set = 0, binding = ").Append(2 + kv.Key).Append(") uniform ").Append(kv.Value == 3 ? "samplerCube" : kv.Value == 4 ? "sampler3D" : "sampler2D").Append(" s").Append(kv.Key).Append(";\n");

        if (s.IsPixel)
        {
            foreach (var d in inDecl.Values)
            {
                int loc = VaryingLocation(d.Usage, d.UsageIndex);
                if (loc < 0) throw new NotSupportedException("PS-Eingang Usage " + d.Usage);
                g.Append("layout(location = ").Append(loc).Append(") in vec4 v").Append(d.Register).Append(";\n");
            }
            g.Append("layout(location = 0) out vec4 oC0;\n");
        }
        else
        {
            foreach (var d in inDecl.Values)
            {
                // COLOR1 der Auto-Shader (TD_CarPaint2/_NM, TD_CarMat_NM): Dreckfarbe rgb + Dreck-Alpha a – im Original ein Datenstrom je
                // Auto (Dreck je Viertel), hier je Draw aus VC.ext.z (RGBA8, OriginalMaterial.Color1) [s315]
                if (d.Usage == U_COLOR && d.UsageIndex == 1)
                {
                    g.Append("#define v").Append(d.Register).Append(" unpackUnorm4x8(VC.ext.z)\n");
                    continue;
                }
                g.Append("layout(location = ").Append(d.Register).Append(") in vec4 v").Append(d.Register).Append(";\n");
                res.Inputs.Add((d.Usage, d.UsageIndex, d.Register));
            }
            foreach (var d in outDecl.Values)
            {
                if (d.Usage == U_POSITION) continue;
                int loc = VaryingLocation(d.Usage, d.UsageIndex);
                if (loc < 0) continue;   // PSIZE/FOG: im Original-Spiel nicht an den PS weitergereicht
                g.Append("layout(location = ").Append(loc).Append(") out vec4 o").Append(d.Register).Append(";\n");
            }
        }
        string ext = s.IsPixel ? "PC.ext" : "VC.ext";
        g.Append("float DxLog(float x) { float a = abs(x); return a == 0.0 ? -3.402823e38 : log2(a); }\n");
        g.Append("vec3 DxNrm(vec3 v) { float l = dot(v, v); return l == 0.0 ? vec3(0.0) : v * inversesqrt(l); }\n");
        g.Append("vec4 DxLit(vec4 s) { float d = max(s.x, 0.0); float sp = s.x > 0.0 ? pow(max(s.y, 0.0), clamp(s.w, -128.0, 128.0)) : 0.0; return vec4(1.0, d, sp, 1.0); }\n");
        g.Append("vec3 LinToSrgb(vec3 c) { c = clamp(c, 0.0, 1.0); return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }\n");
        g.Append("vec3 SrgbToLin(vec3 c) { c = max(c, 0.0); return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }\n");
        g.Append("vec4 TexIn(int n, vec4 t) { ");
        if (lightBuf) g.Append("if (n == 15) return vec4(OrigSunVis(xWp.xyz, xWn.xyz), OrigLightYZ(xWp.xyz), 1.0); ");   // y/z Lampenlicht (edrender_pfx_lightness), w 1
        g.Append("if (((").Append(ext).Append(".x >> uint(n)) & 1u) != 0u) t.rgb = LinToSrgb(t.rgb); return t; }\n");
        // Register als Modulvariablen: "ret" beendet die Shader-Funktion, die Nachbearbeitung in main() läuft trotzdem
        for (int k = 0; k <= maxTemp; k++) g.Append("vec4 r").Append(k).Append(" = vec4(0.0);\n");
        if (usesA0) g.Append("vec4 a0 = vec4(0.0);\n");
        if (usesAL) g.Append("vec4 aL = vec4(0.0);\n");
        if (s.IsPixel) g.Append("vec4 vPos = vec4(0.0), vFace = vec4(0.0);\n");
        else
        {
            g.Append("vec4 oPos = vec4(0.0), oFog = vec4(0.0), oPts = vec4(0.0);\n");
            foreach (var d in outDecl.Values)
                if (d.Usage == U_POSITION || VaryingLocation(d.Usage, d.UsageIndex) < 0) g.Append("vec4 o").Append(d.Register).Append(" = vec4(0.0);\n");
        }
        g.Append("void ShaderMain() {\n");
        g.Append(body);
        g.Append("}\n");
        g.Append("void main() {\n");
        if (s.IsPixel)
        {
            g.Append("    vPos = vec4(gl_FragCoord.xy - 0.5, 0.0, 0.0);\n");
            g.Append("    vFace = vec4(gl_FrontFacing ? 1.0 : -1.0);\n");
            g.Append("    oC0 = vec4(0.0);\n");
            g.Append("    ShaderMain();\n");
            // D3D9 Alpha-Test (Render-State, nicht im Shader): ext.w an, ext.z Referenz. Das Original setzt D3DRS_ALPHAFUNC immer auf
            // D3DCMP_GREATER (unpacked.exe 0xfae0d4: SetRenderState(0x19, 5)) -> bestanden bei Alpha > Referenz, verglichen in 8 Bit
            g.Append("    if (PC.ext.w != 0u && uint(clamp(oC0.a, 0.0, 1.0) * 255.0 + 0.5) <= uint(uintBitsToFloat(PC.ext.z) * 255.0 + 0.5)) discard;\n");
            g.Append("    if (PC.ext.y != 0u) oC0.rgb = SrgbToLin(oC0.rgb);\n");
        }
        else
        {
            foreach (var d in outDecl.Values)
                if (d.Usage != U_POSITION && VaryingLocation(d.Usage, d.UsageIndex) >= 0) g.Append("    o").Append(d.Register).Append(" = vec4(0.0);\n");
            g.Append("    ShaderMain();\n");
            var inPos = inDecl.Values.FirstOrDefault(d => d.Usage == U_POSITION && d.UsageIndex == 0);
            var inNrm = inDecl.Values.FirstOrDefault(d => d.Usage == 3 && d.UsageIndex == 0);
            // ext.y = 1: TD_HeightMap-Datenstrom (POSITION = Höhe, Index x, Index z; OriginalMaterial.HeightMapInput) -> Lage (x·3, h, z·3)
            string vp = inPos != null ? "v" + inPos.Register : "vec4(0.0)";
            g.Append("    xWp = VC.world * vec4(VC.ext.y == 1u ? vec3(").Append(vp).Append(".y * 3.0, ").Append(vp).Append(".x, ").Append(vp).Append(".z * 3.0) : ").Append(vp).Append(".xyz, 1.0);\n");
            g.Append("    xWn = vec4(mat3(VC.world) * ").Append(inNrm != null ? "v" + inNrm.Register + ".xyz" : "vec3(0.0, 1.0, 0.0)").Append(", 0.0);\n");
            var pos = outDecl.Values.FirstOrDefault(d => d.Usage == U_POSITION && d.UsageIndex == 0);
            g.Append("    gl_Position = ").Append(pos != null ? "o" + pos.Register : "oPos").Append(";\n");
            g.Append("    gl_Position.y = -gl_Position.y;\n");
            // Projektion der Original-Konstanten hat D3D-Tiefe (nah 0, fern 1, VulkanRenderer.BuildOriginalScene) -> Reversed-Z der Engine
            g.Append("    gl_Position.z = gl_Position.w - gl_Position.z;\n");
        }
        g.Append("}\n");
        res.Glsl = g.ToString();
        return res;
    }

    /// <summary>Sonnensichtbarkeit aus den Schattenkaskaden der Engine (wie GameShaders.TDU2Shadow, Stärke = sun_shadow_opacity).</summary>
    const string SunShadowGlsl = @"float OrigSunVis(vec3 wp, vec3 n) {
    if (fr.shadow.x <= 0.0) return 1.0;
    float d = length(wp - fr.camPos.xyz);
    if (d >= fr.shadow.y) return 1.0;
    int ci = d < fr.splits.x ? 0 : (d < fr.splits.y ? 1 : (d < fr.splits.z ? 2 : 3));
    float tw = ci == 0 ? fr.texelWorld.x : (ci == 1 ? fr.texelWorld.y : (ci == 2 ? fr.texelWorld.z : fr.texelWorld.w));
    vec3 nn = dot(n, n) > 1e-8 ? normalize(n) : vec3(0.0, 1.0, 0.0);
    if (dot(nn, fr.sunDir.xyz) < 0.0) nn = -nn;
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
";

    /// <summary>
    /// y/z des Lichtpuffers wie das Original: edrender_pfx_lightness PS1/PS2 summiert je Scheinwerfer-Projektor (Lichtvolumen,
    /// additiv) in einen Puffer
    ///   t = min((cone · dist)² · Spot_Color.x · Cookie.y · vars.z · near, 32), cone = sat(sat(dot(normalize(P − Pos), Dir)) · Att.w + Att.z),
    ///   dist = sat(Att.x − |P − Pos| · Att.y), near = 1 ab |P − Pos| ≥ Near, linear 0..1 über Near − 0,5 .. Near (cmp-Kette PS1),
    ///   Cookie = texldp(gProjTexture, ProjMatrix · P): ProjMatrix = LookAt(Pos, Dir, Up; x = Dir × Up) · Perspektive(Sichtfeld
    ///   E+0x88 für Breite und Höhe) · Bias (0,5, −0,5, 0,5, 0,5; exe 0x168bd00) -> u = 0,5 + 0,5·cot·x/z, v = 0,5 − 0,5·cot·y/z,
    /// danach PS0: L' = (1 − exp(−0,28854 · L)) · 0,15625, kodiert als y = (8e + ⌊8(m − 1)⌋)/255, z = frac(8(m − 1)) mit
    /// L'·32768 + 0,01 = 2^e · m (e = ⌊log2⌋); die Material-Shader dekodieren das zu 32 · L' (z. B. TD_Ground/TD_CarPaint: + Albedo · Wert).
    /// Projektoren (Vehicles/HeadlightProjectors, exe 0xd1c340/0xd1cc80/0xd1d380): fr.proj[5·i..] = (Pos, Near), (Dir, vars.z),
    /// (x-Achse, cot), (Oben, 0), Att; fr.projMisc.x Anzahl. Spot_Color.x = 1 (exe 0xd1bf42). Cookie (Frame-Binding 7) ist eine
    /// sRGB-Textur der Engine: Rohwert wie D3D9 (ohne SRGBTEXTURE) durch Rückrechnen.
    /// </summary>
    const string LightBufGlsl = @"float OrigCookieY(vec2 uv) {
    float c = texture(headlightCookie, uv).y;
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}
vec2 OrigLightYZ(vec3 wp) {
    int np = int(fr.projMisc.x);
    float L = 0.0;
    for (int i = 0; i < 8; i++) {
        if (i >= np) break;
        vec4 pa = fr.proj[i * 5], pd = fr.proj[i * 5 + 1], px = fr.proj[i * 5 + 2], pu = fr.proj[i * 5 + 3], at = fr.proj[i * 5 + 4];
        vec3 v = wp - pa.xyz;
        float d = length(v);
        if (d <= 1e-6) continue;
        float cone = clamp(clamp(dot(v / d, pd.xyz), 0.0, 1.0) * at.w + at.z, 0.0, 1.0);
        float dist = clamp(at.x - d * at.y, 0.0, 1.0);
        float vz = dot(v, pd.xyz);
        if (cone <= 0.0 || dist <= 0.0 || vz <= 0.0) continue;
        float t = cone * dist;
        t = t * t;
        vec2 uv = vec2(0.5 + 0.5 * px.w * dot(v, px.xyz) / vz, 0.5 - 0.5 * px.w * dot(v, pu.xyz) / vz);
        t *= OrigCookieY(uv) * pd.w;
        float nr = d - (pa.w - 0.5);
        float nf = d - pa.w >= 0.0 ? 1.0 : nr + nr;
        nf = nr >= 0.0 ? nf : 0.0;
        L += min(t * nf, 32.0);
    }
    float lq = clamp((1.0 - exp(-0.288539 * L)) * 0.15625, 0.0, 1.0);
    float e = floor(log2(lq * 32768.0 + 0.01));
    float m8 = (lq * 32768.0 * exp2(-e) - 1.0) * 8.0;
    float y = clamp((e * 8.0 + floor(m8)) / 255.0, 0.0, 1.0);
    float z = clamp(fract(m8), 0.0, 1.0);
    if (e < 0.0) { y = 0.0; z = 0.0; }
    return vec2(y, z);
}
";

    static string Cmp(int ctl) => ctl switch { 1 => " > ", 2 => " == ", 3 => " >= ", 4 => " < ", 5 => " != ", 6 => " <= ", _ => throw new NotSupportedException("Vergleich " + ctl) };

    /// <summary>COLOR0/1 -> 0/1, TEXCOORD0..9 -> 2..11; sonst -1.</summary>
    public static int VaryingLocation(int usage, int index) => usage == U_COLOR ? (index <= 1 ? index : -1) : usage == U_TEXCOORD ? (index <= 9 ? 2 + index : -1) : -1;

    static string TexCoord(Result res, int sn, string co, bool proj)
    {
        int ty = res.SamplerTypes.TryGetValue(sn, out var st) ? st : 2;
        if (proj) return ty == 2 ? "(" + co + ").xy / (" + co + ").w" : "(" + co + ").xyz / (" + co + ").w";
        return ty == 2 ? "(" + co + ").xy" : "(" + co + ").xyz";
    }

    /// <summary>Swizzle des Sampler-Operanden auf das Textur-Ergebnis anwenden (meist .xyzw).</summary>
    static string Swz(string e, uint samplerTok)
    {
        int sw = (int)((samplerTok >> 16) & 0xFF);
        if (sw == 0xE4) return e;
        string C(int c) => "xyzw"[c & 3].ToString();
        return "(" + e + ")." + C(sw) + C(sw >> 2) + C(sw >> 4) + C(sw >> 6);
    }
}