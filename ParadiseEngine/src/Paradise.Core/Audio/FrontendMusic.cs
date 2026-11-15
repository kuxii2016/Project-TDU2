using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;

namespace Paradise.Core.Audio;

/// <summary>
/// Menu music as in the game
///   LOGIN -> login.wav (1.0), MAP -> map.wav (0.5), LOBBY -> lobby.wav (1.0), REPLAY -> replay.wav, TDMEDIA -> TDUMedia.wav.
/// Files: Bnk/sound/musics/name.wav (loop, fade in/out). Decode in the background,
/// fade in the mixer (no update call required).
/// </summary>
public static class FrontendMusic
{
    public static float Volume = 0.7f;
    public static float FadeSeconds = 1.2f;

    static readonly Dictionary<string, (string file, float vol)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        { "LOGIN", ("login", 0.5f) },
        { "MAP", ("map", 0.5f) },
        { "LOBBY", ("lobby", 0.5f) },
        { "REPLAY", ("replay", 0.5f) },
        { "TDMEDIA", ("tdumedia", 0.5f) },
    };

    static readonly AudioVoice _src = new() { Loop = true, IgnorePause = true };
    static readonly Dictionary<string, AudioClip> _cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();
    static string? _current;
    static float _vol = 1f;
    static int _request;   // jüngste Anforderung (ältere Dekodier-Ergebnisse verwerfen)

    /// <summary>Currently playing music (name from the table) or null.</summary>
    public static string? Current => _current;

    /// <summary>Start a menu's music (LOGIN, MAP, LOBBY, etc.). If the same music is already playing, nothing happens.</summary>
    public static void Play(string mode, string? bnkDir = null)
    {
        bnkDir ??= GameData.BnkDir;
        if (string.IsNullOrEmpty(bnkDir) || string.IsNullOrEmpty(mode) || !Table.TryGetValue(mode, out var e)) return;
        lock (_lock)
        {
            if (string.Equals(_current, mode, StringComparison.OrdinalIgnoreCase) && _src.IsPlaying) { _src.FadeTo(_vol * Volume, FadeSeconds); return; }
            _current = mode; _vol = e.vol;
            int req = ++_request;
            if (_cache.TryGetValue(e.file, out var clip)) { Begin(clip); return; }
            string path = Path.Combine(bnkDir, "sound", "musics", e.file + ".wav");
            if (!GameFiles.Exists(path))
            {
                var dir = Path.Combine(bnkDir, "sound", "musics");
                if (GameFiles.DirectoryExists(dir)) foreach (var f in GameFiles.GetFiles(dir, "*.wav")) if (string.Equals(Path.GetFileNameWithoutExtension(f), e.file, StringComparison.OrdinalIgnoreCase)) { path = f; break; }
            }
            if (!GameFiles.Exists(path))
            {
                Log.Warn("Missing Menue Audio: " + path);
                return;
            }
            string name = e.file;
            Task.Run(() =>
            {
                var c = AudioClip.FromWav("FrontendMusic_" + name, GameFiles.ReadAllBytes(path));
                if (c == null) return;
                lock (_lock)
                {
                    _cache[name] = c;
                    if (req != _request || _current == null)
                        return;
                    Begin(c);
                }
            });
        }
    }

    public static void Stop()
    {
        lock (_lock)
        {
            _current = null; _request++;
            _src.FadeTo(0f, FadeSeconds);
            Task.Delay(TimeSpan.FromSeconds(FadeSeconds + 0.1)).ContinueWith(_ => { lock (_lock) if (_current == null) _src.Stop(); });
        }
    }

    static void Begin(AudioClip clip)
    {
        if (_src.Clip != clip || !_src.IsPlaying) { _src.Clip = clip; _src.Volume = 0f; _src.Play(); }
        _src.FadeTo(_vol * Volume, FadeSeconds);
    }
}