using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Car lights as in the original GS_CarDisplay.cpp:
///
/// 0x744990 – Light values per frame for the car (output: Ambient, DirLight diffuse, DirLight specular, direction):
///   1. Outside: Ambient = sun_ambient, Diffuse = sun_diffuse, Specular = sun_specular (weather, raw – gamma space, no “d − a”).
///      In a tunnel (factor [+0x2ad0] > 0, World/TunnelLight), the three colors are
///      blended into tunnel_light_ambient / _diffuse / _specular (0x40b80c lerp, factor = tunnel factor).
///      In interiors (game modes 3, 0x1d, 0x1e, 8, 0x16, 0x23), the light comes from the scene’s ambient object (not yet ported)
///   2. For cars (car+0x79a0): Ambient ×= car_sun_ambient_factor, Diffuse ×= car_sun_diffuse_factor,
///      Specular ×= car_sun_specular_factor (values read from 0x7461f0 -> 0x1661954/58/5c).
///   3. Light beams from street lamps (outside_lightbeam_intensity × 1.5, 0x431264) are added to ambient and diffuse
///      – not here; our lamps are added in the shader via TDU2Lamps.
///   4. Saturation (all three colors): c = lerp(Luma(c), c, 1 + car_lighting_saturation), Luma = (0.299, 0.587, 0.114)
///      (0x14aba84..8c; Hawaii: −0.6 at all times -> 40% saturation).
///
/// 0x7175a0 – ENVCOEFF for paint materials per frame:
///   t = (1 − dirt) · car_envmap_factor   (0x757f70: 1 + car[+0x3b4] · (−1); dirt here 0 = clean car)
///   TD_CarPaint2: ENVCOEFF = (t · F.z, F.w, F.x, F.y), F = car_fresnel_coeff_power_boost_specular
///   TD_CarPaint:  ENVCOEFF.x = t (y remains the material value)
/// </summary>
public static class CarLighting
{    /// <summary>Switch from Game/Config (GraphicsConfig.OriginalCarLight). Off = the car's original Unity light.</summary>
    public static bool Enabled;

    /// <summary>
    /// Weather-related light values for lighting control (exe FUN_00c4e710, Docs OpenTDU2 car_lights_spec §2.2), read from each frame:
    /// car_headlight_flare_intensity (headlights, 1 at night), car_poslight_intensity (parking lights), car_caustics_intensity
    /// (light cone on the ground), car_headlights_intensity. Read from Vehicles/TDU2VehicleLights.
    /// </summary>
    public static float HeadlightFlare, PosLight, Caustics, HeadlightsIntensity;

    static readonly Vector3 Luma = new(0.299f, 0.587f, 0.114f);

    /// <summary>
    /// Calculate car headlights based on weather data. S = weather data by name (with default), as read by WorldWeather.Update.
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

        // 1b. Tunnel: lerp(Sonne, tunnel_light_*, Tunnelfaktor) – 0x744b03..0x744d2e, nur wenn der Faktor > 0
        float tf = TunnelLight.Factor;
        if (tf > 0f)
        {
            amb = Vector3.Lerp(amb, Xyz(S("tunnel_light_ambient", new Vector4(amb, 0f))), tf);
            dif = Vector3.Lerp(dif, Xyz(S("tunnel_light_diffuse", new Vector4(dif, 0f))), tf);
            spc = Vector3.Lerp(spc, Xyz(S("tunnel_light_specular", new Vector4(spc, 0f))), tf);
        }

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

    /// <summary>lerp(Luma(c), c, s) as 0x40f835, using the grayscale value as a vector.</summary>
    static Vector3 Saturate(Vector3 c, float s)
    {
        float l = Vector3.Dot(c, Luma);
        return new Vector3(l) + (c - new Vector3(l)) * s;
    }

    static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);
}
