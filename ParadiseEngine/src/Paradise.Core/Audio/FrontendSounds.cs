using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using System.Text;
using System.Text.RegularExpressions;

namespace Paradise.Core.Audio;

/// <summary>
/// Interface sounds as in the game: the Flash movies report “FSCommand:sndp:id”
/// (HUD: chptpass at the checkpoint, three/two/one/go during the countdown, time/time2; Menus: move/valid/back ...).
/// Mapping of ID to file + volume from frontend/sounds/fe_snd.bnk/sounds.ini (“AddFESound ‘id’ ”file.wav“ vol”),
/// the WAVs (Microsoft ADPCM) are located in the same bank.
/// </summary>
public static class FrontendSounds
{
    public static bool Enabled = true;
    public static float Volume = 0.8f;

    static bool _loadTried;
    static BnkArchive? _bank;
    static readonly Dictionary<string, (string file, float vol)> _map = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, AudioClip?> _clips = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, double> _lastPlay = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _suppressed = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Ein Film-Sound darf vom Spiel selbst gesteuert werden (z. B. der Wreck-Detektor:
    /// der Film meldet wrckwarn in einem festen Loop, aber die Distanz den Takt vorgibt).
    /// Nur FSCommands werden unterdrueckt; direkte FrontendSounds.Play()-Aufrufe laufen weiter.
    /// </summary>
    public static void SetSuppressed(string id, bool suppressed)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (suppressed) _suppressed.Add(id); else _suppressed.Remove(id);
    }

    static bool EnsureLoaded()
    {
        if (_bank != null) return true;
        if (_loadTried) return false;
        _loadTried = true;
        try
        {
            string? dir = GameData.BnkDir;
            if (string.IsNullOrEmpty(dir))
            {
                Log.Warn("UI Sounds: Game data folder unknown");
                return false;
            }
            string path = Path.Combine(dir, "frontend", "sounds", "fe_snd.bnk");
            if (!GameFiles.Exists(path))
            {
                Log.Warn("UI sounds: " + path + " is missing");
                return false;
            }
            _bank = BnkArchive.Open(path);
            var ini = _bank.Find("sounds.ini");
            if (ini != null)
            {
                var raw = _bank.Read(ini); var sb = new StringBuilder(raw.Length); foreach (var b in raw) sb.Append((char)b); var text = sb.ToString();
                foreach (Match m in Regex.Matches(text, "AddFESound\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"\\s+([0-9.]+)", RegexOptions.IgnoreCase))
                {
                    float.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v);
                    _map[m.Groups[1].Value.Trim()] = (m.Groups[2].Value.Trim(), v <= 0f ? 1f : v);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("UI sounds: " + ex.Message);
            _bank = null;
            return false;
        }
    }

    static AudioClip? Clip(string id, out float vol)
    {
        vol = 1f;
        if (!EnsureLoaded() || !_map.TryGetValue(id, out var m)) return null;
        vol = m.vol;
        if (_clips.TryGetValue(m.file, out var c)) return c;
        c = null;
        try
        {
            var e = _bank!.Find(m.file) ?? _bank.Find(m.file + ".wav") ?? _bank.Find(m.file.Replace(" ", ""));
            if (e != null) c = AudioClip.FromWav("fe_" + id, _bank.Read(e));
        }
        catch (Exception) { c = null; }
        _clips[m.file] = c;
        return c;
    }

    /// <summary>Decode clips in advance (e.g., before the race: countdown/checkpoint) so the first sound doesn't stutter.</summary>
    public static void Preload(params string[] ids)
    {
        if (!Enabled) return;
        foreach (var id in ids) Clip(id, out _);
    }

    /// <summary>Play the sound with the ID specified in sounds.ini (e.g., “chptpass”).</summary>
    public static void Play(string id)
    {
        if (!Enabled || string.IsNullOrEmpty(id))
            return;
        double now = _clock.Elapsed.TotalSeconds;
        if (_lastPlay.TryGetValue(id, out var last) && now - last < 0.06)
            return;
        _lastPlay[id] = now;
        var c = Clip(id, out float vol);
        if (c == null)
            return;
        AudioEngine.PlayOneShot(c, System.Math.Clamp(vol * Volume, 0f, 1f), ignorePause: true);
    }

    /// <summary>Evaluate FSCommand: “sndp:id[:...]” plays the sound. Returns true = it was a sound command.</summary>
    public static bool Command(string? cmd)
    {
        if (cmd == null) return false;
        var c = cmd.Trim();
        if (!c.StartsWith("sndp:", StringComparison.OrdinalIgnoreCase)) return false;
        var id = c.Substring(5);
        int colon = id.IndexOf(':');
        if (colon >= 0) id = id.Substring(0, colon);
        Play(id);
        return true;
    }

    static readonly HashSet<Flash.VmfPlayer> _attached = new();

    /// <summary>Play a movie's audio commands (multiple Attach calls are ignored).</summary>
    public static void Attach(Flash.VmfPlayer? film)
    {
        if (film == null || !_attached.Add(film)) return;
        film.FSCommand += OnFilmCommand;
    }

    public static void Detach(Flash.VmfPlayer? film)
    {
        if (film == null || !_attached.Remove(film)) return;
        film.FSCommand -= OnFilmCommand;
    }

    static void OnFilmCommand(string cmd, string args)
    {
        if (cmd != null && cmd.StartsWith("sndp:", StringComparison.OrdinalIgnoreCase))
        {
            var id = cmd.Substring(5);
            int colon = id.IndexOf(':');
            if (colon >= 0) id = id.Substring(0, colon);
            if (_suppressed.Contains(id.Trim())) return;
        }
        Command(cmd);
    }
}
