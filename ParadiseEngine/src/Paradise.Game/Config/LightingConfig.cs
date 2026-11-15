using Paradise.Core.Render;
using Paradise.Core.World;
using System.Numerics;

namespace Paradise.Game.Config;

/// <summary>
/// Licht-Einstellungen der Welt. Werte nur hier im Code ändern (keine Datei, kein Speichern/Laden).
/// Rechenweg wie im Unity-Projekt (TDU2WorldLighting + TDU2Weather):
///   Ambient = SunAmbient x AmbientScale x Exposure
///   Sonne   = SunDiffuse x DiffuseScale x Exposure − Ambient          (Unity _LightColor0)
///   Farbe   = Albedo x (Ambient + Sonne x N.L)  ==  Albedo x lerp(Ambient, SunDiffuse, N.L)
/// Standard = sonniger Mittag aus den Wetterdaten (sun_diffuse 2.01/1.74/1.36, sun_ambient 0.58/0.67/0.82,
/// sundirection 0/-0.856/-0.423) mit den Dämpfungen aus TDU2Weather (DiffuseScale 0.65, AmbientScale 0.75).
/// </summary>
public sealed class LightingConfig
{
    // ---------------- Sonne / Umgebung (live)
    public float[] SunDiffuse { get; set; } = { 2.01f, 1.74f, 1.36f };
    public float DiffuseScale { get; set; } = 0.65f;
    public float[] SunAmbient { get; set; } = { 0.58f, 0.67f, 0.82f };
    public float AmbientScale { get; set; } = 0.75f;
    public float Exposure { get; set; } = 1f;
    /// <summary>Richtung, in die das Sonnenlicht fällt (Spielkoordinaten, wie "sundirection" der Wetterdaten).</summary>
    public float[] SunDirection { get; set; } = { 0f, -0.856f, -0.423f };

    // ---------------- Tag/Nacht + Wetter (TDU2Weather, game_weathers.xmb). Aus = feste Werte oben.
    public bool UseWeather { get; set; } = true;
    /// <summary>Echtzeit-Minuten für einen Spieltag.</summary>
    public float DayMinutes { get; set; } = 120f;
    /// <summary>&gt;= 0: feste Uhrzeit (z.B. 22 für Nacht).</summary>
    public float ForceHour { get; set; } = -1f;
    public float HourOffset { get; set; } = 0f;
    /// <summary>Echtzeit-Minuten je Wetterabschnitt.</summary>
    public float WeatherMinutes { get; set; } = 14f;
    public float TransitionSeconds { get; set; } = 120f;
    /// <summary>&gt;= 0: feste Wetterart (0 Sonne, 1 Wolken, 2 Regen, 3 vor Regen, 4 nach Regen, 5 Gewitter).</summary>
    public int ForceKind { get; set; } = -1;
    public bool ApplyFog { get; set; } = true;
    public float FogStrength { get; set; } = 0.05f;
    public float StreetLightStrength { get; set; } = 0.8f;
    /// <summary>Straßenlampen-Lichter an den nächsten Masten (max. 32).</summary>
    public int LampLights { get; set; } = 32;
    public float LampRadius { get; set; } = 140f;
    public float LampIntensity { get; set; } = 1f;
    public int RainDrops { get; set; } = 1400;

    // ---------------- Schatten (Unity: Directional Light Soft Shadows, 4 Kaskaden)
    /// <summary>Schattenstärke </summary>
    public float ShadowStrength { get; set; } = 0.5f;
    /// <summary>Schattendistanz in Metern (Unity QualitySettings.shadowDistance, Standard 150).</summary>
    public float ShadowDistance { get; set; } = 150f;

    // ---------------- Wetter / Tageszeit (live)
    public float Wet { get; set; } = 0f;
    /// <summary>1 = Tag (Nachtleuchten aus), 0 = Nacht.</summary>
    public float DayDim { get; set; } = 1f;
    public float NightK { get; set; } = 0f;
    public float StreetLight { get; set; } = 0f;

    // ---------------- Terrain (live)
    public float TerrainLower { get; set; } = 0.1f;
    public float TerrainDepthPush { get; set; } = 0.0015f;

    // ---------------- Materialien (wirken auf neu geladene Objekte – Bereich verlassen oder neu starten)
    /// <summary>Original-Beleuchtung: kein Wrap (überschreibt WrapLighting).</summary>
    public bool OriginalLighting { get; set; } = true;
    /// <summary>Helligkeit der Gebäude/Objekte (_Brightness), 1 = unverändert.</summary>
    public float ObjectBrightness { get; set; } = 0.5f;
    public float WrapLighting { get; set; } = 0.35f;
    public float VertexColorStrength { get; set; } = 0.5f;
    public float ShadowMapStrength { get; set; } = 0.6f;
    public float GlowIntensity { get; set; } = 0.55f;
    /// <summary>Gebäude mit Nacht-Lightmap (TD_Building NIGHTLIGHTMAP, Spot-Exteriors): Helligkeit am Tag, passend zu den übrigen
    /// Lightmap-Objekten (die laufen mit Detail-Verstärkung 2). 1 = Original-Formel ohne Verstärkung, 2 = wie die übrigen.</summary>
    public float NightLightmapDayBoost { get; set; } = 2f;   // = Unity _DetailBoost der Lightmap-Materialien
    public float HaloIntensity { get; set; } = 1f;
    public float WindStrength { get; set; } = 1f;
    public float DetailTiling { get; set; } = 0.098f;
    public float DetailStrength { get; set; } = 1f;

    static Vector3 V(float[]? a, Vector3 def) => a is { Length: >= 3 } ? new Vector3(a[0], a[1], a[2]) : def;

    /// <summary>Frame-Globals wie TDU2WorldLighting.Apply().</summary>
    public FrameGlobals ToFrameGlobals()
    {
        var amb = V(SunAmbient, new Vector3(0.58f, 0.67f, 0.82f)) * AmbientScale * Exposure;
        var diff = V(SunDiffuse, new Vector3(2.01f, 1.74f, 1.36f)) * DiffuseScale * Exposure;
        var sun = Vector3.Max(Vector3.Zero, diff - amb);
        var dir = V(SunDirection, new Vector3(0f, -0.856f, -0.423f));
        if (dir.LengthSquared() < 1e-6f) dir = -Vector3.UnitY;
        return new FrameGlobals
        {
            SunDirection = Vector3.Normalize(-dir),   // Shader: Richtung zur Sonne
            SunColor = sun,
            Ambient = amb,
            Wet = Wet,
            DayDim = DayDim,
            NightK = NightK,
            StreetLight = StreetLight,
            ShadowStrength = ShadowStrength,
            ShadowDistance = ShadowDistance,
        };
    }

    /// <summary>Wetter-Einstellungen übertragen (DiffuseScale/AmbientScale gelten auch für die Wetterwerte).</summary>
    public void ApplyTo(WorldWeather w)
    {
        w.DayMinutes = DayMinutes; w.ForceHour = ForceHour; w.HourOffset = HourOffset;
        w.WeatherMinutes = WeatherMinutes; w.TransitionSeconds = TransitionSeconds; w.ForceKind = ForceKind;
        w.DiffuseScale = DiffuseScale; w.AmbientScale = AmbientScale;
        w.ApplyFog = ApplyFog; w.FogStrength = FogStrength; w.StreetLightStrength = StreetLightStrength;
        w.LampLights = LampLights; w.LampRadius = LampRadius; w.LampIntensity = LampIntensity; w.RainDrops = RainDrops;
    }

    /// <summary>Material-/Terrain-Einstellungen auf den Streamer übertragen (vor Initialize; danach nur Terrain live).</summary>
    public void ApplyTo(MapStreamer s)
    {
        s.TerrainLower = TerrainLower;
        s.TerrainDepthPush = TerrainDepthPush;
        s.OriginalLighting = OriginalLighting;
        s.ObjectBrightness = ObjectBrightness;
        s.WrapLighting = WrapLighting;
        s.VertexColorStrength = VertexColorStrength;
        s.ShadowMapStrength = ShadowMapStrength;
        s.WindStrength = WindStrength;
        s.DetailTiling = DetailTiling;
        s.DetailStrength = DetailStrength;
        MaterialFactory.GlowIntensity = GlowIntensity;
        MaterialFactory.NightLightmapDayBoost = NightLightmapDayBoost;
        MaterialFactory.HaloIntensity = HaloIntensity;
    }
}
