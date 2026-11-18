using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Paradise.Core.World;

/// <summary>
/// Tageszeit und Wetter wie im Original (game_weathers.xmb), für alle Spieler gleich: Uhrzeit und Wetterfolge werden aus
/// der (Server-)Zeit berechnet. Portiert aus dem Unity-Projekt (TDU2Weather + TDU2WorldLighting). Steuert: Sonne (Richtung,
/// Farbe, Schatten), Umgebungslicht, Nebel, Himmel-Helligkeit, Nachtleuchten (Straßenlampen, Neon, Fenster, Lampenlicht der
/// Masten), nasse Straßen, Regen, Gewitter und das Meer. Ohne Interieur (folgt mit dem Interieur-Port).
/// </summary>
public sealed class WorldWeather : IDisposable
{
    public float DayMinutes = 15f;           // Echtzeit-Minuten für einen Spieltag
    public float HourOffset = 0f;
    public float HawaiiHourOffset = -11f;
    public float ForceHour = -1f;             // >= 0: feste Uhrzeit
    // ---------------- Wetter
    public float WeatherMinutes = 4f;
    public float TransitionSeconds = 120f;
    public int ForceKind = -1;                // >= 0: feste Wetterart
    // ---------------- Umsetzung
    public float DiffuseScale = 0.65f, AmbientScale = 0.75f;
    public bool ApplyFog = true;
    public float FogStrength = 0.05f;
    public int LampLights = 32;               // Shader-Grenze hier 32
    public float LampRadius = 140f;
    public float LampIntensity = 1f;
    public float StreetLightStrength = 0.5f;
    public int RainDrops = 1400;


    public static float Hour { get; private set; } = 12f;
    public static WeatherKind Kind { get; private set; } = WeatherKind.Sunny;
    public static float Night { get; private set; }
    public static bool NewsPending;
    static WeatherKind _lastKind = (WeatherKind)(-1);

    /// <summary>Radio: passende Wettermeldung (Regen / Gewitter / Sonne) oder null bei Wolken/vor dem Regen (Unity TDU2Weather.RadioWeather).</summary>
    public static Audio.RadioMessageType? RadioWeather()
    {
        switch (Kind)
        {
            case WeatherKind.Rainy: return Audio.RadioMessageType.WeatherRain;
            case WeatherKind.Tempest: return Audio.RadioMessageType.WeatherStorm;
            case WeatherKind.Sunny:
            case WeatherKind.AfterRain: return Night < 0.5f ? Audio.RadioMessageType.WeatherSun : null;
            default: return null;
        }
    }

    /// <summary>Radio: Tageszeit-Moderation (Sonnenaufgang 5-9, Mittag 11-14:30, Abend 18:30-22) oder null (Unity TDU2Weather.RadioTime).</summary>
    public static Audio.RadioMessageType? RadioTime()
    {
        float h = Hour;
        if (h >= 5f && h < 9f) return Audio.RadioMessageType.TimeSunrise;
        if (h >= 11f && h < 14.5f) return Audio.RadioMessageType.TimeMidday;
        if (h >= 18.5f && h < 22f) return Audio.RadioMessageType.TimeNightfall;
        return null;
    }
    public static float Rain { get; private set; }
    /// <summary>Unity TDU2Weather.InsideInterior: im Interieur kein Nebel/Regen/Nässe/Straßenlampen, Sonne wie vor dem Wetter.</summary>
    public static bool Inside;
    /// <summary>Unity TDU2Interiors.SetInteriorAmbient: Umgebungslicht im Interieur (InteriorAmbient bzw. GarageAmbient).</summary>
    public static Vector3 InteriorAmbient = new(0.42f, 0.4f, 0.37f);
    public static float GroundWet { get; private set; }
    public bool Loaded => _data != null && _data.Keys.Count > 0;
    /// <summary>Wetter läuft (Unity: TDU2Weather.Instance != null) – Radio wählt dann passende Wetter-/Tageszeit-Moderationen.</summary>
    public static bool Active { get; private set; }

    /// <summary>Kino-Wetter (Unity TDU2Weather.Override): Schlüssel aus fx/cinematic_weathers.xmb statt Uhrzeit/Wetterart. null = normal.</summary>
    public static WeatherData.Key? Override;
    /// <summary>
    /// Unity TDU2Weather.StoryLock: während der Geschichte festes Wetter – sonniger, trockener Nachmittag statt der laufenden
    /// Server-Uhr. Gesetzt von der Geschichte (Gamemodes/Utils/Story), solange ein Schritt aktiv ist. ForceHour/ForceKind haben Vorrang.
    /// </summary>
    public static bool StoryLock;
    public static float StoryHour = 14f;
    public static WeatherKind StoryKind = WeatherKind.Sunny;
    WeatherData.Key? _ovKey;
    float _ovHour;

    /// <summary>
    /// Unity OverrideHour: Tag oder Nacht entscheidet der Schlüssel selbst (spot_exterior_lightmap_day_night.y), darin die Stunde des
    /// Spieltags mit der passendsten Sonnenhöhe – damit Himmel, Reflexionen und Nachtwerte zur Szene passen.
    /// </summary>
    float OverrideHour(WeatherData.Key ov, float fallback)
    {
        if (ov == _ovKey) return _ovHour;
        var data = _data!;
        _ovKey = ov; _ovHour = fallback;
        float night = System.Math.Clamp(data.Value(ov, 1, "spot_exterior_lightmap_day_night", new Vector4(1, 0, 0, 0)).Y, 0f, 1f);
        var d4 = data.Value(ov, 0, "sundirection", Vector4.Zero);
        var d = new Vector3(d4.X, d4.Y, d4.Z);
        bool hasDir = d.LengthSquared() > 1e-6f;
        if (hasDir) d = Vector3.Normalize(d);
        float best = float.MaxValue;
        for (int i = 0; i < 96; i++)
        {
            float h = i * 0.25f;
            float wn = System.Math.Clamp(data.Sample("spot_exterior_lightmap_day_night", h, 1, 1, 0f, new Vector4(1, 0, 0, 0)).Y, 0f, 1f);
            float cost = MathF.Abs(wn - night) * 10f;
            if (hasDir)
            {
                var s4 = data.Sample("sundirection", h, 0, 0, 0f, Vector4.Zero);
                var sv = new Vector3(s4.X, s4.Y, s4.Z);
                if (sv.LengthSquared() > 1e-6f) cost += MathF.Abs(Vector3.Normalize(sv).Y - d.Y);
            }
            else cost += MathF.Abs(h - (night > 0.5f ? 0f : 13f)) * 0.01f;
            if (cost < best) { best = cost; _ovHour = h; }
        }
        Paradise.Core.Diagnostics.Log.Info($"Kino-Wetter {ov.Name}: Nacht={night:0.00} -> Uhrzeit {_ovHour:0.00} h");
        return _ovHour;
    }
    public string? Error => _error;

    private WeatherData? _data;
    private string? _error;
    private string _island = "ibiza";
    private readonly Dictionary<long, WeatherKind> _kindCache = new();
    private float _wet, _nightK, _nightWin, _wetShown;
    /// <summary>Gewitter wie im Original (WFX_Lightning, World/Lightning): Blitz, Flackern, Überschreibungen der Wetterwerte.</summary>
    private readonly Lightning _lightning = new();
    /// <summary>Blickrichtung/Bodenhöhe für den Blitz (Spielkoordinaten), vor <see cref="Update"/> setzen.</summary>
    public Vector3 CameraForward = Vector3.UnitZ;
    public Func<float, float, float?>? GroundHeight;
    private FrameGlobals _g = FrameGlobals.Default;

    // Straßenlampen (streetlamps.xml)
    struct LampDef { public Vector3 Color; public float Intensity, Near, Far, Open, Falloff; }
    private readonly Dictionary<string, LampDef> _lampDefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Matrix4x4 m, Vector3 bmin, Vector3 bmax, string name)> _lampHits = new();
    private LampLight[] _lamps = Array.Empty<LampLight>();
    private int _lampCount;
    private double _nextLampScan;
    static readonly string[] LampPrefixes = { "SF_LIGH" };

    public RainField? Rainfall { get; private set; }

    public void Init(IslandIndex index)
    {
        _island = index.IslandName ?? "ibiza";
        string fx = index.FxDir ?? "";
        string weatherFile = Path.Combine(fx, "game_weathers.xmb"), lampsFile = Path.Combine(fx, "streetlamps.xml");
        try
        {
            if (GameFiles.Exists(weatherFile)) { _data = WeatherData.Load(weatherFile); Log.Info($"Wetter: {_data.Keys.Count} Zeitpunkte aus {Path.GetFileName(weatherFile)}"); }
            else _error = "game_weathers.xmb fehlt: " + weatherFile;
        }
        catch (Exception ex) { _error = ex.Message; }
        if (_error != null) Log.Warn("Wetter: " + _error);
        try { if (GameFiles.Exists(lampsFile)) LoadLamps(GameFiles.ReadAllText(lampsFile)); } catch (Exception) { }
    }

    void LoadLamps(string xml)
    {
        foreach (Match m in Regex.Matches(xml, "<Lamp\\s+([^>]*)/>"))
        {
            string a = m.Groups[1].Value;
            string? Attr(string n) { var mm = Regex.Match(a, n + "\\s*=\\s*\"([^\"]*)\""); return mm.Success ? mm.Groups[1].Value : null; }
            string? name = Attr("name"); string? col = Attr("color");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(col)) continue;
            col = col.TrimStart('#');
            if (col.Length < 8) continue;
            byte r = Convert.ToByte(col.Substring(0, 2), 16), g = Convert.ToByte(col.Substring(2, 2), 16), b = Convert.ToByte(col.Substring(4, 2), 16), al = Convert.ToByte(col.Substring(6, 2), 16);
            float P(string n, float d) => float.TryParse(Attr(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
            _lampDefs[name] = new LampDef
            {
                Color = new Vector3(r / 255f, g / 255f, b / 255f),
                Intensity = al / 255f,
                Near = P("near", 8f),
                Far = P("far", 10f),
                Open = P("open_angle", 30f),
                Falloff = P("falloff_angle", 50f),
            };
        }
    }

    // ------------------------------------------------------------------ Zeit und Wetterfolge (deterministisch)
    static uint Hash(long x)
    {
        unchecked
        {
            ulong h = (ulong)x * 0x9E3779B97F4A7C15UL;
            h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 27;
            return (uint)(h ^ (h >> 32));
        }
    }

    static WorldWeather? _instance;
    double _now;

    /// <summary>
    /// Unity TDU2Weather.Forecast: Wetterart und Tag/Nacht in hoursAhead Spielstunden für eine Insel (null = die geladene) –
    /// eigene Wetterfolge und Ortszeit der Insel (TDU Media, Wetter-Kanal).
    /// </summary>
    public static void Forecast(float hoursAhead, string? island, out WeatherKind kind, out bool night)
    {
        kind = Kind; night = Night > 0.5f;
        var w = _instance;
        if (w == null || w._data == null || w._data.Keys.Count == 0) return;
        if (string.IsNullOrEmpty(island)) island = w._island;
        double dayLen = MathF.Max(1f, w.DayMinutes) * 60.0;
        double t = w._now + hoursAhead / 24.0 * dayLen;
        double segLen = MathF.Max(1f, w.WeatherMinutes) * 60.0;
        kind = w.ForceKind >= 0 ? (WeatherKind)System.Math.Clamp(w.ForceKind, 0, 5) : w.KindAt((long)System.Math.Floor(t / segLen), IslandSalt(island));
        float hour = w.ForceHour >= 0f ? w.ForceHour + hoursAhead : (float)(((t / dayLen) * 24.0 + w.HourOffset + w.ZoneOffset(island)) % 24.0);
        hour %= 24f;
        if (hour < 0f) hour += 24f;
        int b = (int)kind + 1;
        night = System.Math.Clamp(w._data.Sample("spot_exterior_lightmap_day_night", hour, b, b, 0f, new Vector4(1, 0, 0, 0)).Y, 0f, 1f) > 0.5f;
    }

    public static string IslandKey(string? island)
    {
        if (string.IsNullOrEmpty(island)) return "ibiza";
        if (island.StartsWith("h", StringComparison.OrdinalIgnoreCase)) return "hawai";
        if (island.StartsWith("cas", StringComparison.OrdinalIgnoreCase)) return "casino";
        return "ibiza";
    }

    float ZoneOffset(string island) => IslandKey(island) == "hawai" ? HawaiiHourOffset : 0f;
    static long IslandSalt(string island) { string k = IslandKey(island); return k == "hawai" ? 1 : k == "casino" ? 2 : 0; }

    WeatherKind KindAt(long seg) => KindAt(seg, IslandSalt(_island));

    /// <summary>Wetterart im Abschnitt seg einer Insel: Markow-Kette, von einem festen Startpunkt 48 Abschnitte vorher gerechnet.</summary>
    WeatherKind KindAt(long seg, long salt)
    {
        long key = (seg << 2) | salt;
        if (_kindCache.TryGetValue(key, out var k)) return k;
        var s = WeatherKind.Sunny;
        for (long i = seg - 48; i <= seg; i++) s = Next(s, Hash(salt == 0 ? i : i ^ (salt * 0x5DEECE66DL)) % 1000 / 1000f);
        if (_kindCache.Count > 128) _kindCache.Clear();
        _kindCache[key] = s;
        return s;
    }

    static WeatherKind Next(WeatherKind s, float r) => s switch
    {
        WeatherKind.Sunny => r < 0.62f ? WeatherKind.Sunny : r < 0.9f ? WeatherKind.Cloudy : WeatherKind.BeforeRain,
        WeatherKind.Cloudy => r < 0.35f ? WeatherKind.Sunny : r < 0.65f ? WeatherKind.Cloudy : WeatherKind.BeforeRain,
        WeatherKind.BeforeRain => r < 0.7f ? WeatherKind.Rainy : r < 0.88f ? WeatherKind.Tempest : WeatherKind.Cloudy,
        WeatherKind.Rainy => r < 0.4f ? WeatherKind.Rainy : r < 0.5f ? WeatherKind.Tempest : WeatherKind.AfterRain,
        WeatherKind.Tempest => r < 0.3f ? WeatherKind.Tempest : r < 0.6f ? WeatherKind.Rainy : WeatherKind.AfterRain,
        _ => r < 0.7f ? WeatherKind.Sunny : WeatherKind.Cloudy,   // AfterRain
    };

    /// <summary>
    /// Jedes Bild (Unity: Update). now = Serverzeit in Sekunden (synchron für alle), time = Spielzeit für Effekte.
    /// findLamps: Lampen-Instanzen um die Kamera (Unity: VegetationRenderer.FindInstances).
    /// </summary>
    public void Update(double now, double time, float dt, Vector3 cameraGame, Vector3 rootOffset,
                       Action<string[], Vector3, float, List<(Matrix4x4 m, Vector3 bmin, Vector3 bmax, string name)>>? findLamps)
    {
        if (_data == null || _data.Keys.Count == 0) return;
        _now = now; _instance = this;
        float dayLen = MathF.Max(1f, DayMinutes) * 60f;
        float hour = ForceHour >= 0f ? ForceHour : StoryLock ? StoryHour : (float)(((now / dayLen) * 24.0 + HourOffset + ZoneOffset(_island)) % 24.0);
        if (hour < 0f) hour += 24f;
        // Kino-Wetter (Titel-/Story-Szenen): Uhrzeit aus der Sonnenrichtung des Schlüssels (Unity TDU2Weather.OverrideHour)
        var ov = Override;
        if (ov != null) hour = OverrideHour(ov, hour);
        Hour = hour;

        // Wetter: Abschnitt und Übergang vom vorigen
        double segLen = MathF.Max(1f, WeatherMinutes) * 60.0;
        long seg = (long)System.Math.Floor(now / segLen);
        WeatherKind cur = ForceKind >= 0 ? (WeatherKind)System.Math.Clamp(ForceKind, 0, 5) : StoryLock ? StoryKind : KindAt(seg);
        WeatherKind prev = ForceKind >= 0 || StoryLock ? cur : KindAt(seg - 1);
        float since = (float)(now - seg * segLen);
        float t = TransitionSeconds > 0f ? System.Math.Clamp(since / TransitionSeconds, 0f, 1f) : 1f;
        t = t * t * (3f - 2f * t);
        Kind = t >= 0.5f ? cur : prev;
        Active = true;
        if (Kind != _lastKind) { if ((int)_lastKind >= 0) NewsPending = true; _lastKind = Kind; }
        int ba = (int)prev + 1, bb = (int)cur + 1;
        var data = _data;
        Vector4 Raw(string n, Vector4 d = default) => ov != null ? data.Value(ov, 1, n, d) : data.Sample(n, hour, ba, bb, t, d);
        var dirRaw = ov != null ? data.Value(ov, 0, "sundirection", new Vector4(0, -0.856f, -0.423f, 0))
                                : data.Sample("sundirection", hour, 0, 0, 0f, new Vector4(0, -0.856f, -0.423f, 0));
        bool inside = Inside;

        // ---------------- Gewitter (WFX_Lightning): Zustände, Blitz, danach überschreibt es Wetterwerte dieses Bildes (0xa5d310)
        _lightning.Update(dt, !inside && Raw("thunder_off_on").X > 0.5f, (n, dv) => Raw(n, dv), dirRaw,
                          cameraGame, CameraForward, GroundHeight ?? ((_, _) => null));
        Vector4 S(string n, Vector4 d = default) => _lightning.Value(n, Raw(n, d));

        var g = FrameGlobals.Default;

        // ---------------- Sonne / Licht
        var dir4 = _lightning.Value("sundirection", dirRaw);
        var dir = new Vector3(dir4.X, dir4.Y, dir4.Z);
        var diff = S("sun_diffuse", new Vector4(2.01f, 1.74f, 1.36f, 0));
        var amb = S("sun_ambient", new Vector4(0.58f, 0.67f, 0.82f, 0));
        float shadowOp = S("sun_shadow_opacity", Vector4.One).X;
        if (dir.LengthSquared() > 1e-6f) g.SunDirection = Vector3.Normalize(-dir);   // Shader: Richtung zur Sonne
        g.ShadowStrength = System.Math.Clamp(shadowOp, 0f, 1f);
        // TDU2WorldLighting.Apply (Exposure 1): Ambient = a, Licht = d − a
        var d = new Vector3(diff.X, diff.Y, diff.Z) * DiffuseScale;
        var a = new Vector3(amb.X, amb.Y, amb.Z) * AmbientScale;
        g.Ambient = a;
        g.SunColor = Vector3.Max(Vector3.Zero, d - a);
        if (inside)
        {
            // Unity: Sonne wie vor dem Wetter (TDU2WorldLighting.Apply mit SunDiffuse 1.30/1.24/1.12, Ambient 0.46/0.49/0.56 ->
            // Licht = Sonne - Ambient), Umgebungslicht = InteriorAmbient (AmbientMode.Flat), Richtung der Szenensonne
            g.SunDirection = FrameGlobals.Default.SunDirection;
            g.SunColor = new Vector3(1.30f - 0.46f, 1.24f - 0.49f, 1.12f - 0.56f);
            g.Ambient = InteriorAmbient;
            g.ShadowStrength = FrameGlobals.Default.ShadowStrength;
            g.FogStart = 0f; g.FogEnd = 0f;   // Unity InsideInterior: RenderSettings.fog = false
        }

        // ---------------- Nebel
        if (ApplyFog && !inside)
        {
            var fc = S("fog_color", new Vector4(0.7f, 0.62f, 0.55f, 0));
            float fs = S("fog_start").X, fe = MathF.Max(fs + 10f, S("fog_end", new Vector4(1500, 0, 0, 0)).X);
            float fm = System.Math.Clamp(S("fog_max", new Vector4(0.33f, 0, 0, 0)).X * FogStrength, 0.05f, 1f);
            g.FogColor = new Vector3(fc.X, fc.Y, fc.Z);
            g.FogStart = fs;
            g.FogEnd = fs + (fe - fs) / fm;   // bei fog_end ist der Nebel bei fog_max
        }

        // ---------------- Meer (TD_Ocean)
        Vector4 O(string n, Vector4 dv) => ov != null ? data.Value(ov, 1, n, dv) : data.Sample(n, hour, ba, bb, t, data.Sample(n, hour, 1, 1, 0f, dv));
        var of = O("ocean_fresnel", new Vector4(0.3f, 30f, 0.5f, 0f));
        g.OceanSet = true;
        g.OceanFresnel = new Vector3(of.X, of.Y, of.Z);
        g.OceanSpec = O("ocean_specularColorAndCutOff", new Vector4(0.502f, 0.596f, 0.674f, 0f));
        var orf = O("ocean_reflectionColor", new Vector4(0.498f, 0.498f, 0.498f, 0f));
        g.OceanRefl = new Vector3(orf.X, orf.Y, orf.Z);
        g.OceanSpecPower = O("ocean_specularPower", new Vector4(20f, 0, 0, 0)).X;
        g.OceanBlur = O("ocean_reflectionBluriness", new Vector4(0.05f, 0, 0, 0)).X;
        var nHR = O("ocean_NMapHR", new Vector4(0.15f, 1f, 0, 0)); var nLR = O("ocean_NMapLR", new Vector4(0.015f, 1f, 0, 0));
        g.OceanNMap = new Vector2(nHR.X, nLR.X);
        // TD_Ocean (FUN_00961710, ocean_spec §2.2): mp0 = (specularPower / Skydome_sunIntensity², 2·LR.y, 2·HR.y),
        // mp3 = (attenuationBluriness, attenuationFresnel, 0.001, reflectionBluriness)
        float sunInt = S("Skydome_sunIntensity", new Vector4(1f, 0, 0, 0)).X;
        g.OceanSpecPow = g.OceanSpecPower / MathF.Max(1e-3f, sunInt * sunInt);
        g.OceanParam = new Vector4(2f * nLR.Y, 2f * nHR.Y, O("ocean_attenuationBluriness", Vector4.Zero).X, O("ocean_attenuationFresnel", Vector4.Zero).X);
        Render.RenderFx.OceanClear = g.OceanRefl;   // SEAREF_N wird mit ocean_reflectionColor gelöscht (kein Himmel im Spiegelbild)
        OceanNoise.Scaler = O("ocean_NMapScaler", new Vector4(0.13f, 0, 0, 0)).X;

        // ---------------- Autolicht wie im Original (TDU2CarLighting, GS_CarDisplay 0x744990). Interieur: Licht der Szene, noch offen.
        g.CarLight = inside ? default : CarLighting.Compute((n, dv) => S(n, dv));

        // ---------------- Himmel wie im Original (td_dome, World/TDU2SkyDome): Streuung aus Skydome_*, Sonne = sundirection,
        // Licht des Doms = sun_diffuse / sun_ambient (Gamma-Raum, ohne die Engine-Skalierung oben)
        g.Sky = SkyDome.Compute((n, dv) => S(n, dv), dir, new Vector3(diff.X, diff.Y, diff.Z), new Vector3(amb.X, amb.Y, amb.Z));

        // ---------------- Wolkenkuppel (TD_CloudMesh, World/TDU2Clouds): Objekt je Wetterart, Farben/Deckkraft/Drehung aus clouds_*
        g.CloudColor = S("clouds_cloud3dcolor", new Vector4(1f, 1f, 1f, 0f));
        g.CloudDown = S("clouds_cloud3dcolor2", Vector4.Zero);
        g.CloudAlpha = S("clouds_cloud3dAlphaBlend", new Vector4(0f, 0f, 1f, 0f));
        g.CloudMisc = new Vector2(S("Skydome_haze").X, S("sunflare_core_emissive").X);
        var spd = S("clouds_cloud3dSpeed");
        Render.RenderFx.CloudSpeed = new Vector2(spd.X, spd.Y);
        string CloudName(int block)
        {
            WeatherData.Key k;
            if (ov != null) { k = ov; block = 1; }
            else { data.Bracket(hour, out var ka, out var kb, out float hf); k = hf < 0.5f ? ka : kb; }
            return block >= 0 && block < k.Strings.Length && k.Strings[block].TryGetValue("clouds_cloud3dname", out var cn) ? cn : "";
        }
        Render.RenderFx.CloudName = CloudName(bb);
        Render.RenderFx.CloudNamePrev = CloudName(ba);
        Render.RenderFx.CloudBlend = t;

        // ---------------- Photoshop-Nachbearbeitung (Original FUN_00d769e0, Docs\re\postfx_spec.md §2.1)
        // Vorgaben = Grundwerte der exe, falls ein Schlüssel fehlt. Gamma/BlurredLumFactor kommen nicht vom Wetter.
        PhotoshopFx.SoftlightOpacity = S("photoshop_softlight_opacity", new Vector4(0.55f, 0, 0, 0)).X;
        PhotoshopFx.SoftlightBrightness = S("photoshop_softlight_brightness", new Vector4(1.25f, 0, 0, 0)).X;
        PhotoshopFx.OverlayOpacity = S("photoshop_overlay_opacity", new Vector4(0.30f, 0, 0, 0)).X;
        PhotoshopFx.BalanceShadows = Xyz(S("photoshop_color_balance_shadows"));
        PhotoshopFx.BalanceMidtones = Xyz(S("photoshop_color_balance_midtones"));
        PhotoshopFx.BalanceHightones = Xyz(S("photoshop_color_balance_hightones"));
        PhotoshopFx.BloomBlack = S("photoshop_bloom_black_level", new Vector4(0.5f, 0, 0, 0)).X;
        PhotoshopFx.BloomWhite = S("photoshop_bloom_white_level", new Vector4(1.0f, 0, 0, 0)).X;
        PhotoshopFx.BloomIntensity = S("photoshop_bloom_intensity", new Vector4(0.25f, 0, 0, 0)).X;
        PhotoshopFx.CrappyMode = S("photoshop_crappymode_enable").X == 1f;   // exe: (v == 1.0f)
        static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);

        // ---------------- Tag/Nacht-Leuchten
        Night = System.Math.Clamp(S("spot_exterior_lightmap_day_night", new Vector4(1, 0, 0, 0)).Y, 0f, 1f);
        float street = System.Math.Clamp(S("outside_street_lightmap").X / 1.5f, 0f, 1f);
        g.DayDim = 1f - MathF.Max(street, Night);
        _nightK = MathF.Max(street, Night);
        g.NightK = _nightK;
        g.StreetLight = street * 1.5f * StreetLightStrength;
        _nightWin = MathF.Max(S("building_nightwindow_frequency").X, Night);
        g.NightWindows = _nightWin;
        // Himmel: Skybox-Exposure x lerp(1, 0.06, Nacht) x Wolken (Blitz: über die Wetterwerte, World/Lightning)
        // Wolkenfaktor zwischen alter und neuer Wetterart überblenden (vorher Sprung bei der Hälfte des Übergangs)
        static float CloudOf(WeatherKind k) => k == WeatherKind.Rainy || k == WeatherKind.Tempest ? 0.55f : k == WeatherKind.Cloudy || k == WeatherKind.BeforeRain ? 0.8f : 1f;
        float cloud = CloudOf(prev) + (CloudOf(cur) - CloudOf(prev)) * t;
        g.SkyExposure = (1f + (0.06f - 1f) * Night) * cloud;

        // ---------------- Regen, Nässe
        Rain = System.Math.Clamp(S("rain_alpha").X / 120f, 0f, 1f);
        float wetTarget = System.Math.Clamp(S("rain_ground_wet").X / 0.7f, 0f, 1f) * System.Math.Clamp(S("rain_ground_global_intensity", Vector4.One).X, 0f, 1f);
        float rate = dt * (wetTarget > _wet ? 1f / 60f : 1f / 360f);   // nass in ~1 min, trocken in ~6 min
        _wet = _wet < wetTarget ? MathF.Min(wetTarget, _wet + rate) : MathF.Max(wetTarget, _wet - rate);
        if (ForceKind >= 0 && MathF.Abs(_wet - wetTarget) > 0.001f && _forceWas != ForceKind) _wet = wetTarget;
        _forceWas = ForceKind;
        // Unity: Kino-Schnitt bzw. Ende des Kinos oder Beginn/Ende der Geschichte: Wetter wechselt hart -> Nässe sofort übernehmen
        if (ov != _wetOv || StoryLock != _wetLock) { _wetOv = ov; _wetLock = StoryLock; _wet = wetTarget; }
        GroundWet = _wet;
        _wetShown = inside ? 0f : MathF.Max(_wet, Rain);
        g.Wet = _wetShown;
        // TD_Road-Regen wie das Original: Werte jedes Bild direkt aus dem Wetter (FUN_00aa6ce0 -> WFX_WetGrd), aus bei GI <= 0
        float gi = inside ? 0f : S("rain_ground_global_intensity").X;
        g.RainSpecs = new Vector4(gi, S("rain_ground_wet").X, 1f, S("rain_ground_puddle_height").X);
        g.RainRipple = new Vector4(0f, 0f, S("rain_ground_ripple_intensity").X, S("rain_ground_ripple_scale", new Vector4(0.6f, 0, 0, 0)).X);
        Render.RenderFx.RainScrollSpeed = S("rain_ground_ripple_scroll_speed").X;
        if (RainDrops > 0 && Rain > 0.01f && Rainfall == null) Rainfall = new RainField(RainDrops);
        Rainfall?.Update(inside ? 0f : Rain, dt, 1f + (0.35f - 1f) * Night, cameraGame + rootOffset);

        // ---------------- Straßenlampen
        UpdateLamps(inside ? 0f : street, time, cameraGame, rootOffset, findLamps);
        g.Lamps = _lamps;
        g.LampCount = _lampCount;

        _g = g;
    }
    private int _forceWas = int.MinValue;
    private WeatherData.Key? _wetOv;
    private bool _wetLock;

    /// <summary>Werte des letzten Updates (Sonne, Licht, Nebel, Nacht, Meer, Lampen).</summary>
    public FrameGlobals Globals => _g;

    void UpdateLamps(float on, double time, Vector3 cameraGame, Vector3 rootOffset,
                     Action<string[], Vector3, float, List<(Matrix4x4 m, Vector3 bmin, Vector3 bmax, string name)>>? findLamps)
    {
        float k = on;
        if (LampLights <= 0 || k <= 0.01f || findLamps == null) { _lampCount = 0; return; }
        if (time < _nextLampScan)
        {
            // nur Intensität nachführen
            return;
        }
        _nextLampScan = time + 0.7;
        _lampHits.Clear();
        findLamps(LampPrefixes, cameraGame, LampRadius, _lampHits);
        // Lampenköpfe sammeln, nächste zuerst
        var heads = new List<(float d, Vector3 p, LampDef def)>();
        foreach (var (m, bmin, bmax, name) in _lampHits)
        {
            var def = DefFor(name);
            var c = (bmin + bmax) * 0.5f; var e = (bmax - bmin) * 0.5f;
            float top = bmax.Y - 0.35f;
            if (e.X > 1.2f || e.Z > 1.2f)
            {
                // Doppelausleger: zwei Köpfe an den Enden der langen Achse
                var ax = e.X >= e.Z ? new Vector3(e.X * 0.85f, 0, 0) : new Vector3(0, 0, e.Z * 0.85f);
                AddHead(heads, Vector3.Transform(new Vector3(c.X, top, c.Z) + ax, m), cameraGame, def);
                AddHead(heads, Vector3.Transform(new Vector3(c.X, top, c.Z) - ax, m), cameraGame, def);
            }
            else AddHead(heads, Vector3.Transform(new Vector3(c.X, top, c.Z), m), cameraGame, def);
        }
        heads.Sort((x, y) => x.d.CompareTo(y.d));
        int n = System.Math.Min(LampLights, heads.Count);
        if (_lamps.Length < n) _lamps = new LampLight[System.Math.Max(n, 32)];
        for (int i = 0; i < n; i++)
        {
            var h = heads[i];
            float spot = System.Math.Clamp(h.def.Falloff * 2f, 30f, 150f);
            float inner = System.Math.Clamp(h.def.Open * 2f, 1f, spot - 1f);
            float baseI = 2.2f + h.def.Intensity * 4f;
            _lamps[i] = new LampLight
            {
                Position = h.p + rootOffset,
                Range = MathF.Max(6f, h.def.Far * 1.6f),
                Color = h.def.Color * (baseI * k * LampIntensity),
                CosOuter = MathF.Cos(spot * 0.5f * MathF.PI / 180f),
                CosInner = MathF.Cos(inner * 0.5f * MathF.PI / 180f),
            };
        }
        _lampCount = n;
    }

    static void AddHead(List<(float d, Vector3 p, LampDef def)> heads, Vector3 p, Vector3 cam, LampDef def) => heads.Add(((p - cam).LengthSquared(), p, def));

    LampDef DefFor(string objName)
    {
        // Library-Name z.B. "SF_LIGHF" -> streetlamps.xml "SF_LighF"
        if (objName != null && _lampDefs.TryGetValue(objName, out var d)) return d;
        if (objName != null && objName.Length >= 8 && _lampDefs.TryGetValue(objName.Substring(0, 8), out d)) return d;
        return new LampDef { Color = new Vector3(0.97f, 0.84f, 0.65f), Intensity = 0.3f, Near = 8f, Far = 10f, Open = 28f, Falloff = 48f };
    }

    // ------------------------------------------------------------------ Regen zeichnen
    private IGpuTexture? _rainTex;
    private IGpuMaterial? _rainMat;

    /// <summary>Regenstreifen zeichnen (zwischen BeginFrame/EndFrame). cameraRight = Kamera rechts (Welt).</summary>
    public void DrawRain(IRenderer r, Vector3 cameraRight)
    {
        var rf = Rainfall;
        if (rf == null) return;
        rf.CameraRight = cameraRight;
        if (!rf.Visible || rf.Active <= 0) return;
        if (_rainMat == null)
        {
            // Streifen-Textur 4x32 wie im Unity-Projekt (Alpha = sin-Verlauf, Rand schwächer)
            var px = new byte[4 * 32 * 4];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 4; x++)
                {
                    float al = MathF.Sin(y / 31f * MathF.PI) * (x == 1 || x == 2 ? 1f : 0.35f);
                    int o = (y * 4 + x) * 4;
                    px[o] = 255; px[o + 1] = 255; px[o + 2] = 255; px[o + 3] = (byte)(al * 255);
                }
            _rainTex = r.CreateTexture(new TextureUpload { Width = 4, Height = 32, Format = GpuTextureFormat.Rgba8, Mips = 1, Blocks = px, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp, Name = "RainStreak" });
            // Sprites/Default: Textur x Vertexfarbe, unbeleuchtet, Alpha-Blend
            _rainMat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Transparent, Name = "Rain", Main = _rainTex, Lit = 0f, VcMul = true });
        }
        var mesh = r.CreateMesh(rf.Vertices.AsSpan(0, rf.Active * 4), rf.Indices.AsSpan(0, rf.Active * 6));
        r.DrawMesh(mesh, _rainMat, Matrix4x4.Identity);
        mesh.Dispose();   // Freigabe verzögert (nach den laufenden Frames)
    }

    /// <summary>Blitz zeichnen (TD_Lightning, nur beim Wachsen/Stehen). viewForward = Blickrichtung der Kamera (Welt).</summary>
    public void DrawLightning(IRenderer r, Vector3 rootOffset, Vector3 viewForward)
    {
        if (Inside) return;
        _lightning.Draw(r, rootOffset, viewForward);
    }

    public void Dispose()
    {
        _rainMat?.Dispose(); _rainMat = null;
        _rainTex?.Dispose(); _rainTex = null;
        _lightning.Dispose();
    }

    // ------------------------------------------------------------------ Regen
    /// <summary>Regenstreifen um die Kamera (eigenes Mesh). Portiert aus TDU2Weather.RainField.</summary>
    public sealed class RainField
    {
        readonly Vector3[] _p; readonly float[] _speed;
        const float Box = 28f, Height = 18f;
        public ObjectVertex[] Vertices;
        public uint[] Indices;
        public int Active;
        public bool Visible;

        public RainField(int n)
        {
            _p = new Vector3[n]; _speed = new float[n];
            var rnd = new Random(1234);
            for (int i = 0; i < n; i++)
            {
                _p[i] = new Vector3((float)rnd.NextDouble() * 2f * Box - Box, (float)rnd.NextDouble() * Height, (float)rnd.NextDouble() * 2f * Box - Box);
                _speed[i] = 14f + (float)rnd.NextDouble() * 6f;
            }
            Vertices = new ObjectVertex[n * 4];
            Indices = new uint[n * 6];
            for (int i = 0; i < n; i++)
            {
                uint b = (uint)(i * 4);
                Vertices[b].Uv0 = new Vector2(0, 0); Vertices[b + 1].Uv0 = new Vector2(0, 1); Vertices[b + 2].Uv0 = new Vector2(1, 1); Vertices[b + 3].Uv0 = new Vector2(1, 0);
                Indices[i * 6] = b; Indices[i * 6 + 1] = b + 1; Indices[i * 6 + 2] = b + 2; Indices[i * 6 + 3] = b; Indices[i * 6 + 4] = b + 2; Indices[i * 6 + 5] = b + 3;
            }
        }

        /// <summary>Tropfen bewegen. camRender = Kamera im Render-Raum; right wird beim Zeichnen gesetzt.</summary>
        public Vector3 CameraRight = Vector3.UnitX;
        public void Update(float amount, float dt, float brightness, Vector3 camRender)
        {
            Visible = amount > 0.005f;
            if (!Visible) return;
            var cp = camRender;
            var right = CameraRight;
            int n = _p.Length;
            Active = (int)MathF.Round(n * System.Math.Clamp(amount, 0f, 1f));
            byte alpha = (byte)((25f + (85f - 25f) * amount) * brightness);
            var fall = Vector3.Normalize(new Vector3(0.6f, -1f, 0.25f));
            for (int i = 0; i < Active; i++)
            {
                int b = i * 4;
                var p = _p[i] + fall * (_speed[i] * dt);
                if (p.Y < 0f) p.Y += Height;
                _p[i] = p;
                float x = Repeat(p.X - cp.X + Box, 2f * Box) - Box, z = Repeat(p.Z - cp.Z + Box, 2f * Box) - Box;
                var wp = new Vector3(cp.X + x, cp.Y - Height * 0.35f + p.Y, cp.Z + z);
                var len = fall * (0.9f + _speed[i] * 0.04f);
                var w = right * 0.012f;
                uint c = ObjectVertex.PackColor((byte)(150f * brightness), (byte)(162f * brightness), (byte)(180f * brightness), alpha);
                Vertices[b].Position = wp - w; Vertices[b + 1].Position = wp - w - len; Vertices[b + 2].Position = wp + w - len; Vertices[b + 3].Position = wp + w;
                for (int k = 0; k < 4; k++) { Vertices[b + k].Color = c; Vertices[b + k].Normal = Vector3.UnitY; }
            }
        }

        static float Repeat(float t, float len) => t - MathF.Floor(t / len) * len;
    }
}
