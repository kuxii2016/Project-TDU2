using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Konstanten des Original-Himmels td_dome (td_dome.sob vs @0x5c0 / ps @0x1570, selbst disassembliert), berechnet wie
/// die exe (Dome-Objekt, Konstanten-Handles +0x11ac..+0x11cc):
/// <list type="bullet">
/// <item>Grundwerte 0xcfef58 (Ctor): βR, β'R, βM, β'M für die vier Spektralkanäle 0,70/0,65/0,57/0,475 µm (Tabelle 0x150cff8).</item>
/// <item>0xd03580: Skydome_betaRayMultiplier · 0,1 und Skydome_betaMieMultiplier · 0,01 auf βR/β'R bzw. βM/β'M,
///   BetaRPlusBetaM = Summe, OneOverBetaRPlusBetaM = 1/Summe, HGg_Saturation = (1−g², 1+g², 2g, Skydome_saturation) mit
///   g = Skydome_HGg · 0,001 (HGg ≥ 1000 → 1000, 0x9c09ad), Multipliers = (1, 0,0138, 0,0113, 0,008).</item>
/// <item>0xd00ef0/0xd00f80: Sonnenfarbe nach Preetham – θ = acos(clamp(−L.y)), höchstens Skydome_sunAngleAttenuation (Grad),
///   Luftmasse m = 1/(cos θ + 0,15·(93,885 − θ°)^−1,253), Trübung β = 0,0460836582205·T − 0,04586025928522,
///   τ = exp(−m·0,008735·λ^−4,08)·exp(−m·β·λ^−1,3), Intensität = Skydome_sunIntensity·5·(9,2 − 5,5·betaMieMultiplier),
///   unter dem Horizont · exp(20·(π/2 − θ)); SunColorAndIntensity = Intensität · τ.</item>
/// <item>0xd03e00: SpectrumToRGB = diag(1−0,2k, 1−0,1k, 1+0,1k, 1+0,2k) · Mᵀ · XYZ→RGB, k = Skydome_colorTemperature,
///   M = CIE-Farbwerte der vier Kanäle (0x150cef8), XYZ→RGB-Matrix 0x150d17c.</item>
/// <item>GroundColor = (0,7; 0,6; 0,5) (0xd0354b).</item>
/// </list>
/// Die Sonnenrichtung für die Streuung dreht der Vertex-Shader zusätzlich (bis 31° Richtung Horizont, abhängig von der
/// Sonnenhöhe); das steht hier 1:1 als <see cref="ScatterSun"/>.
/// </summary>
public static class TDU2SkyDome
{
    // Ctor 0xcfef58: Tabelle 0x150cff8 rückwärts gelesen
    static readonly Vector4 BetaR = new(5.17077660333598e-06f, 6.954958280402934e-06f, 1.1761115274566691e-05f, 2.4387847588513978e-05f);
    static readonly Vector4 BetaDashR = new(3.086079800596053e-07f, 4.1509346715429274e-07f, 7.019398253760301e-07f, 1.455542360417894e-06f);
    static readonly Vector4 BetaM = new(0.004965062253177166f, 0.005740597378462553f, 0.007399686146527529f, 0.01051431056112051f);
    static readonly Vector4 BetaDashM = new(0.001150051481090486f, 0.0013337875716388226f, 0.0017344574443995953f, 0.0024976187851279974f);
    static readonly float[] Lambda = { 0.7f, 0.65f, 0.57f, 0.475f };   // 0x150ced8

    /// <summary>Werte für den Shader (alle wie in den Konstanten des Originals).</summary>
    public static SkyDomeGlobals Compute(Func<string, Vector4, Vector4> S, Vector3 lightDir, Vector3 dirLightDiffuse, Vector3 ambient)
    {
        var o = new SkyDomeGlobals { On = true };
        float ray = S("Skydome_betaRayMultiplier", new Vector4(1f, 0, 0, 0)).X * 0.1f;
        float mieRaw = S("Skydome_betaMieMultiplier", Vector4.Zero).X;
        float mie = mieRaw * 0.01f;
        var bR = BetaR * ray; var bM = BetaM * mie;
        o.BetaRPlusBetaM = bR + bM;
        o.BetaDashR = BetaDashR * ray;
        o.BetaDashM = BetaDashM * mie;
        o.OneOverBetaRPlusBetaM = new Vector4(1f / o.BetaRPlusBetaM.X, 1f / o.BetaRPlusBetaM.Y, 1f / o.BetaRPlusBetaM.Z, 1f / o.BetaRPlusBetaM.W);
        float hgg = S("Skydome_HGg", Vector4.Zero).X;
        if (hgg > 1000f) hgg = 1000f;
        float g = hgg * 0.001f;
        o.HGgSaturation = new Vector4(1f - g * g, 1f + g * g, g + g, S("Skydome_saturation", Vector4.Zero).X);
        o.Multipliers = new Vector4(1f, 0.0138f, 0.0113f, 0.008f);

        // Sonnenfarbe (0xd00ef0 Winkel, 0xd00f80 Farbe)
        var L = lightDir.LengthSquared() > 1e-12f ? Vector3.Normalize(lightDir) : new Vector3(0f, -1f, 0f);
        float theta = MathF.Acos(System.Math.Clamp(-L.Y, 0f, 1f));
        float lim = S("Skydome_sunAngleAttenuation", Vector4.Zero).X * MathF.PI / 180f;
        theta = lim > theta ? theta : lim;
        if (theta < 0f) theta = 0f;
        float T = S("Skydome_turbitity", Vector4.Zero).X;
        float beta = 0.04608365822050f * T - 0.04586025928522f;
        float m = 1f / (MathF.Cos(theta) + 0.15f * MathF.Pow(93.885f - theta / MathF.PI * 180f, -1.253f));
        Span<float> tau = stackalloc float[4];
        for (int i = 0; i < 4; i++)
            tau[i] = MathF.Exp(-m * 0.008735f * MathF.Pow(Lambda[i], -4.08f)) * MathF.Exp(-m * beta * MathF.Pow(Lambda[i], -1.3f));
        float I = S("Skydome_sunIntensity", new Vector4(1f, 0, 0, 0)).X * 5f * (9.2f - 5.5f * mieRaw);
        if (theta > MathF.PI / 2f) I *= MathF.Exp((MathF.PI / 2f - theta) * 20f);
        o.SunColorAndIntensity = new Vector4(I * tau[0], I * tau[1], I * tau[2], I * tau[3]);

        // SpectrumToRGB (0xd03e00): Zeile i = RGB des Spektralkanals i
        float k = S("Skydome_colorTemperature", Vector4.Zero).X;
        var M = new Matrix4x4(
            0.01136f, 0.0041f, 0f, 0f,
            0.2835f, 0.107f, 0f, 0f,
            0.7621f, 0.952f, 0.0021f, 0f,
            0.1421f, 0.1126f, 1.0419f, 0f);                     // Mᵀ (0xf37450 transponiert die Tabelle 0x150cef8)
        var tint = new Matrix4x4(1f - 0.2f * k, 0, 0, 0, 0, 1f - 0.1f * k, 0, 0, 0, 0, 1f + 0.1f * k, 0, 0, 0, 0, 1f + 0.2f * k);
        var xyzToRgb = new Matrix4x4(
            2.3706743717193604f, -0.5138850212097168f, 0.005298200063407421f, 0f,
            -0.9000405073165894f, 1.42530357837677f, -0.014694900251924992f, 0f,
            -0.4706338047981262f, 0.0885813981294632f, 1.0093967914581299f, 0f,
            0f, 0f, 0f, 0f);
        var s2r = tint * M * xyzToRgb;
        o.SpectrumToRgb0 = new Vector4(s2r.M11, s2r.M12, s2r.M13, s2r.M14);
        o.SpectrumToRgb1 = new Vector4(s2r.M21, s2r.M22, s2r.M23, s2r.M24);
        o.SpectrumToRgb2 = new Vector4(s2r.M31, s2r.M32, s2r.M33, s2r.M34);
        o.SpectrumToRgb3 = new Vector4(s2r.M41, s2r.M42, s2r.M43, s2r.M44);

        // Sterne (0xd01864/0xd0195e): StarsFade = (Start, 1/(End − Start)), StarsIntensity = Skydome_starsPercent
        float fs = S("Skydome_starsFadeStart", Vector4.Zero).X, fe = S("Skydome_starsFadeEnd", Vector4.Zero).X;
        o.StarsIntensity = S("Skydome_starsPercent", Vector4.Zero).X;
        o.StarsFadeStart = fs;
        o.StarsFadeScale = fe != fs ? 1f / (fe - fs) : 0f;
        o.MoonPercent = S("Skydome_moonPercent", Vector4.Zero).X;
        o.MoonSize = S("Skydome_moonSize", Vector4.Zero).X * 10f;

        o.ScatterSun = ScatterSunDir(L);
        o.DirLightDiffuse = dirLightDiffuse;
        o.AmbLight = ambient;
        return o;
    }

    /// <summary>
    /// td_dome vs (Anweisungen 1004–1608) 1:1: Achse a = normalize(0, −2·Lx·Ly, 2·Ly·Lz), Winkel φ = 2π·frac(0,5 − 0,08584·(1 −
    /// sat(−L.y))) − π, Rodrigues-Zeilen wie im Shader zusammengesetzt; Ergebnis negiert = Richtung zur Streu-Sonne (Welt).
    /// </summary>
    public static Vector3 ScatterSunDir(Vector3 L)
    {
        float Lx = L.X, Ly = L.Y, Lz = L.Z;
        var a = new Vector3(0f, -2f * Lx * Ly, 2f * Ly * Lz);
        if (a.LengthSquared() < 1e-20f) return -L;   // Achse 0 (Sonne genau im Horizont/Zenit): keine Drehung
        a = Vector3.Normalize(a);
        float ax = a.X, ay = a.Y, az = a.Z;
        float x = 1f - System.Math.Clamp(-Ly, 0f, 1f);
        x = x * -0.08584f + 0.5f;
        x -= MathF.Floor(x);
        float phi = x * 6.283f - 3.142f;
        float c = MathF.Cos(phi), s = MathF.Sin(phi), kk = 1f - c;
        float X = Lx * ((1f - az * az) * c + az * az) + Ly * (ax * az * kk + ay * s) + Lz * (ay * az * kk - ax * s);
        float Y = Ly * ((1f - ax * ax) * c + ax * ax) + Lx * (ax * az * kk - ay * s) + Lz * (ax * ay * kk + az * s);
        float Z = Lx * (ay * az * kk + ax * s) + Ly * (ax * ay * kk - az * s) + Lz * ((1f - ay * ay) * c + ay * ay);
        return new Vector3(-X, -Y, -Z);
    }
}
