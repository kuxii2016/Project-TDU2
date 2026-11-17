using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;

namespace Paradise.Core.Audio;

/// <summary>
/// Ambient sounds of the island (islands/&lt;island&gt;/sound/ambience/ambiences.xmb + miscellaneous *.wav)
/// (Sector-*.psa, see <see cref="SoundZonesFile"/>; MapStreamer.AmbienceZoneAt)—the zone type is the
/// Environment.TypeID. Day/night is selected by Name_Resource_Day/Night (if no night file, the day file is used),
/// Transition with a 2.5-second fade, rain as a second layer (Rain[0], volume controlled by the curve).
/// WAV name = &lt;Name_Resource&gt; in lowercase + “.wav” in the same folder (spec 1, FUN_00cbf3e0).
/// Thunder: Lightning[] (eclair_*), a random entry upon impact (0x657340, see <cref=“PlayLightning”/>).
/// </summary>
public sealed class TDU2Ambience : IDisposable
{
    sealed class Env
    {
        public int Type;
        public string Name = "", Day = "", Night = "";
        public float Volume = .5f, Pitch = 1f, Priority = 1f;
    }

    public const float CrossFade = 2.5f;
    /// <summary>Overall ambient volume (Game/Config/AudioConfig.Ambience).</summary>
    public float Volume = 0.6f;

    readonly string _dir;
    readonly Dictionary<int, Env> _envs = new();
    readonly Dictionary<string, AudioClip?> _clips = new(StringComparer.OrdinalIgnoreCase);
    string? _rainName;
    readonly List<(string name, float volume)> _thunder = new();
    AudioVoice? _thunderVoice;
    bool _active;
    readonly Random _rnd = new();
    float _rainVolume = 1f;
    (float x, float y)[] _rainCurve = { (0f, 0f), (1f, 1f) };

    readonly AudioVoice?[] _slot = new AudioVoice?[2];
    readonly float[] _stopIn = new float[2];
    string? _curKey;
    string? _pendingKey;
    float _pendingTime;
    AudioVoice? _rain;

    public string Island { get; }
    public int CurrentType { get; private set; } = -1;

    TDU2Ambience(string dir, string island) { _dir = dir; Island = island; }

    /// <summary>Load the island's ambiences.xmb (null if none exists).</summary>
    public static TDU2Ambience? Load(string? bnkDir, string island)
    {
        if (string.IsNullOrEmpty(bnkDir)) return null;
        var dir = Path.Combine(bnkDir, "islands", island, "sound", "ambience");
        var file = Path.Combine(dir, "ambiences.xmb");
        if (!GameFiles.Exists(file)) { Log.Info($"Umgebung: {file} fehlt – keine Umgebungsgeräusche."); return null; }
        try
        {
            var root = XmbFile.Parse(GameFiles.ReadAllBytes(file));
            var a = new TDU2Ambience(dir, island);
            var envs = root["Environments"];
            if (envs != null)
                foreach (var e in envs.Items)
                {
                    var env = new Env
                    {
                        Type = e.I("TypeID", -1),
                        Name = e.S("TypeName", "") ?? "",
                        Day = e.S("Name_Resource_Day", "") ?? "",
                        Night = e.S("Name_Resource_Night", "") ?? "",
                        Volume = e.F("Volume", .5f),
                        Pitch = e.F("Pitch", 1f),
                        Priority = e.F("PriorityFactor", 1f),
                    };
                    if (env.Type >= 0) a._envs[env.Type] = env;
                }
            var rain = root["Rain"];
            if (rain != null && rain.Items.Count > 0)
            {
                var r = rain.Items[0];
                a._rainName = r.S("Name");
                a._rainVolume = r.F("Volume", 1f);
                var pts = r["Curve"]?["Points"];
                if (pts != null && pts.Items.Count >= 2)
                    a._rainCurve = pts.Items.Select(p => (p.F("X"), p.F("Y"))).OrderBy(p => p.Item1).ToArray();
            }
            var lightning = root["Lightning"];
            if (lightning != null)
                foreach (var l in lightning.Items)
                {
                    var n = l.S("Name", "") ?? "";
                    if (n.Length > 0) a._thunder.Add((n, l.F("Volume", 1f)));
                }
            Log.Info($"Umgebung: {island}: {a._thunder.Count} Donner ({string.Join(", ", a._thunder.Select(t => t.name))})");
            Log.Info($"Umgebung: {island}: {a._envs.Count} Einträge ({string.Join(", ", a._envs.Values.Select(v => v.Type + "=" + v.Name))}), Regen '{a._rainName}'");
            return a;
        }
        catch (Exception ex) { Log.Warn($"Umgebung: {file}: {ex.Message}"); return null; }
    }

    AudioClip? Clip(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_clips.TryGetValue(name, out var c)) return c;
        var path = Path.Combine(_dir, name.ToLowerInvariant() + ".wav");
        c = GameFiles.Exists(path) ? AudioClip.FromWav(name, GameFiles.ReadAllBytes(path)) : null;
        if (c == null) Log.Warn($"Umgebung: {path} fehlt oder ist kein lesbares WAV.");
        _clips[name] = c;
        return c;
    }

    float RainCurve(float x)
    {
        var c = _rainCurve;
        if (x <= c[0].x) return c[0].y;
        for (int i = 1; i < c.Length; i++)
            if (x <= c[i].x) { float t = (x - c[i - 1].x) / MathF.Max(1e-5f, c[i].x - c[i - 1].x); return c[i - 1].y + (c[i].y - c[i - 1].y) * t; }
        return c[^1].y;
    }
    /// <summary>
    /// Any image. zoneType = MapStreamer.AmbienceZoneAt (-1 = unknown; the current entry remains),
    /// active = false (interior, no world) hides everything.
    /// </summary>
    public void Update(int zoneType, bool night, float rain, bool active, float dt)
    {
        _active = active;
        for (int i = 0; i < 2; i++)
            if (_stopIn[i] > 0f && (_stopIn[i] -= dt) <= 0f) { _slot[i]?.Stop(); _slot[i] = null; }

        string? key = _curKey;
        Env? env = null;
        if (!active) key = null;
        else if (zoneType >= 0)
        {
            key = null;
            if (_envs.TryGetValue(zoneType, out env) && env.Priority > 0f)
            {
                var n = night && !string.IsNullOrEmpty(env.Night) ? env.Night : env.Day;
                if (!string.IsNullOrEmpty(n)) key = env.Type + ":" + n;
            }
        }

        if (key != _curKey && active && _curKey != null && key != null)
        {
            if (key != _pendingKey) { _pendingKey = key; _pendingTime = 0f; }
            _pendingTime += dt;
            if (_pendingTime < 0.5f) key = _curKey;
        }
        if (key != _curKey) SwitchTo(key, env);
        else if (key != null && env != null && _slot[0] != null) _slot[0]!.Pitch = env.Pitch;

        // Regen-Schicht
        float rv = active && rain > 0.001f ? Volume * _rainVolume * RainCurve(System.Math.Clamp(rain, 0f, 1f)) : 0f;
        if (rv > 0f && _rain == null && _rainName != null)
        {
            var c = Clip(_rainName);
            if (c != null) { _rain = new AudioVoice { Clip = c, Loop = true }; _rain.Volume = 0f; _rain.Play(); }
        }
        if (_rain != null)
        {
            _rain.FadeTo(rv, 1f);
            if (rv <= 0f && _rain.Volume <= 0.0001f) { _rain.Stop(); _rain = null; }
        }
    }

    /// <summary>
    /// Thunder upon impact (original 0x657340 in the Environment Manager, called by WFX_Lightning during the transition from Standing →
    /// Fade Out): nothing as long as the previous thunder is still playing or the environment is off; otherwise, entry rand() % count,
    /// clip intervals 999999/1000000 (i.e., no attenuation), volume = environment volume · entry.Volume.
    /// </summary>
    public void PlayLightning()
    {
        if (!_active || _thunder.Count == 0) return;
        if (_thunderVoice != null && _thunderVoice.IsPlaying) return;
        var (name, vol) = _thunder[_rnd.Next(0, 32768) % _thunder.Count];
        var clip = Clip(name);
        if (clip == null) return;
        _thunderVoice = new AudioVoice { Clip = clip, Loop = false };
        _thunderVoice.Volume = Volume * vol;
        _thunderVoice.Play();
    }

    void SwitchTo(string? key, Env? env)
    {
        // alter Eintrag: ausblenden, danach anhalten
        if (_slot[0] != null) { _slot[0]!.FadeTo(0f, CrossFade); _slot[1]?.Stop(); _slot[1] = _slot[0]; _stopIn[1] = CrossFade + 0.1f; }
        _slot[0] = null; _stopIn[0] = 0f;
        _curKey = key; _pendingKey = null;
        CurrentType = env?.Type ?? -1;
        if (key == null || env == null) return;
        var name = key[(key.IndexOf(':') + 1)..];
        var clip = Clip(name);
        if (clip == null) return;
        var v = new AudioVoice { Clip = clip, Loop = true, Pitch = env.Pitch };
        v.Volume = 0f;
        v.Play();
        v.FadeTo(Volume * env.Volume, CrossFade);
        _slot[0] = v;
        Log.Info($"Umgebung: {env.Name} ({name})");
    }

    public void Dispose()
    {
        for (int i = 0; i < 2; i++) { _slot[i]?.Stop(); _slot[i] = null; }
        _rain?.Stop(); _rain = null;
        _thunderVoice?.Stop(); _thunderVoice = null;
        _curKey = null;
    }
}
