using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Licht der Autos wie im Original (unpacked.exe, GS_CarDisplay.cpp):
///
/// 0x744990 – Lichtwerte je Bild für das Auto (Ausgabe: Ambient, DirLight diffuse, DirLight specular, Richtung):
///   1. Außen: Ambient = sun_ambient, Diffus = sun_diffuse, Glanz = sun_specular (Wetter, roh – Gamma-Raum, kein "d − a"
///      wie im Unity-Projekt). Im Tunnel wird zu den tunnel_light_* übergeblendet (Faktor am Auto, noch nicht portiert).
///      Im Interieur (Spielmodi 3, 0x1d, 0x1e, 8, 0x16, 0x23) kommt das Licht vom Umgebungsobjekt der Szene (noch nicht portiert:
///      dort bleibt <see cref="CarLightGlobals.On"/> aus und die Autos nutzen das bisherige Licht).
///   2. Für Autos (car+0x79a0): Ambient ×= car_sun_ambiant_factor, Diffus ×= car_sun_diffuse_factor,
///      Glanz ×= car_sun_specular_factor (Werte gelesen in 0x7461f0 -> 0x1661954/58/5c).
///   3. Lichtkegel der Straßenlampen (outside_lightbeam_intensity × 1.5, 0x431264) werden zu Ambient und Diffus addiert
///      – hier nicht, unsere Lampen kommen im Shader über TDU2Lamps dazu.
///   4. Sättigung (alle drei Farben): c = lerp(Luma(c), c, 1 + car_lighting_saturation), Luma = (0.299, 0.587, 0.114)
///      (0x14aba84..8c; Hawaii: −0.6 zu jeder Uhrzeit -> 40 % Sättigung).
///
/// 0x7175a0 – ENVCOEFF der Lack-Materialien je Bild:
///   t = (1 − Schmutz) · car_envmap_factor   (0x757f70: 1 + car[+0x3b4] · (−1); Schmutz hier 0 = sauberes Auto)
///   TD_CarPaint2: ENVCOEFF = (t · F.z, F.w, F.x, F.y), F = car_fresnel_coeff_power_boost_specular
///   TD_CarPaint:  ENVCOEFF.x = t (y bleibt der Materialwert)
/// </summary>
public static class CarLighting
{
    /// <summary>Schalter aus Game/Config (GraphicsConfig.OriginalCarLight). Aus = bisheriges Unity-Licht am Auto.</summary>
    public static bool Enabled;

    /// <summary>
    /// Lampen-Werte des Wetters für die Lichtsteuerung (exe FUN_00c4e710, Docs OpenTDU2 car_lights_spec §2.2), jedes Bild gelesen:
    /// car_headlight_flare_intensity (Scheinwerfer, nachts 1), car_poslight_intensity (Positionslicht), car_caustics_intensity
    /// (Lichtkegel auf dem Boden), car_headlights_intensity. Gelesen von Vehicles/TDU2VehicleLights.
    /// </summary>
    public static float HeadlightFlare, PosLight, Caustics, HeadlightsIntensity;

    static readonly Vector3 Luma = new(0.299f, 0.587f, 0.114f);

    /// <summary>
    /// Autolicht aus den Wetterwerten berechnen. S = Wetterwert nach Name (mit Vorgabe), wie WorldWeather.Update ihn liest.
    /// </summary>
    public static CarLightGlobals Compute(Func<string, Vector4, Vector4> S)
    {
        HeadlightFlare = S("car_headlight_flare_intensity", Vector4.Zero).X;
        PosLight = S("car_poslight_intensity", Vector4.Zero).X;
        Caustics = S("car_caustics_intensity", Vector4.Zero).X;
        HeadlightsIntensity = S("car_headlights_intensity", Vector4.Zero).X;
        if (!Enabled) return default;

        // 1. Licht der Sonne (roh aus dem Wetter)
        var amb = Xyz(S("sun_ambient", new Vector4(0.58f, 0.67f, 0.82f, 0)));
        var dif = Xyz(S("sun_diffuse", new Vector4(2.01f, 1.74f, 1.36f, 0)));
        var spc = Xyz(S("sun_specular", new Vector4(1f, 1f, 1f, 0)));

        // 2. Faktoren für Autos
        amb *= S("car_sun_ambiant_factor", Vector4.One).X;
        dif *= S("car_sun_diffuse_factor", Vector4.One).X;
        spc *= S("car_sun_specular_factor", Vector4.One).X;

        // 4. Sättigung
        float sat = 1f + S("car_lighting_saturation", Vector4.Zero).X;
        amb = Saturate(amb, sat);
        dif = Saturate(dif, sat);
        spc = Saturate(spc, sat);

        // ENVCOEFF
        const float dirt = 0f;   // Schmutz am Auto (car[+0x3b4]) – noch ohne Schmutzsystem
        float t = (1f - dirt) * S("car_envmap_factor", Vector4.One).X;
        var f = S("car_fresnel_coeff_power_boost_specular", Vector4.One);

        return new CarLightGlobals
        {
            On = true,
            Ambient = amb,
            Diffuse = dif,
            Specular = spc,
            EnvCoeff = new Vector4(t * f.Z, f.W, f.X, f.Y),
            EnvFactor = t,
        };
    }

    /// <summary>lerp(Luma(c), c, s) wie 0x40f835 mit dem Grauwert als Vektor.</summary>
    static Vector3 Saturate(Vector3 c, float s)
    {
        float l = Vector3.Dot(c, Luma);
        return new Vector3(l) + (c - new Vector3(l)) * s;
    }

    static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);
}
