using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;

namespace Paradise.Core.Audio;

/// <summary>
/// Umgebungsgeräusche der Insel (islands/&lt;insel&gt;/sound/ambience/ambiences.xmb + lose *.wav), nach
/// OpenTDU2 docs/re/ambience_audio_spec.md. Welcher Eintrag spielt, kommt aus den Klangzonen der Sektoren
/// (Sector-*.psa, siehe <see cref="SoundZonesFile"/>; MapStreamer.AmbienceZoneAt) – der Zonentyp ist die
/// Environment.TypeID. Tag/Nacht wählt Name_Resource_Day/Night (ohne Nacht-Datei die Tag-Datei),
/// Wechsel mit 2,5 s Überblendung, Regen als zweite Schicht (Rain[0], Lautstärke über die Kurve).
/// Wav-Name = &lt;Name_Resource&gt; klein + ".wav" im selben Ordner (spec 1, FUN_00cbf3e0).
/// Donner: Lightning[] (eclair_*), beim Einschlag ein zufälliger Eintrag (0x657340, siehe <see cref="PlayLightning"/>).
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
    /// <summary>Gesamtlautstärke der Umgebung (Game/Config/AudioConfig.Ambience).</summary>
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

    /// <summary>ambiences.xmb der Insel laden (null, wenn es keine gibt).</summary>
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
    /// Jedes Bild. zoneType = MapStreamer.AmbienceZoneAt (-1 = unbekannt, der laufende Eintrag bleibt),
    /// active = false (Interieur, keine Welt) blendet alles aus.
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

        // kurze Zonenwechsel an Grenzen nicht sofort umschalten (0,5 s gleiche Zone)
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
    /// Donner beim Einschlag (Original 0x657340 auf dem Umgebungs-Manager, aufgerufen von WFX_Lightning beim Übergang Stehen →
    /// Ausblenden): nichts, solange der vorige Donner noch spielt oder die Umgebung aus ist; sonst Eintrag rand() % Anzahl,
    /// Clip-Abstände 999999/1000000 (also ohne Abschwächung), Lautstärke = Umgebungslautstärke · Eintrag.Volume.
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
