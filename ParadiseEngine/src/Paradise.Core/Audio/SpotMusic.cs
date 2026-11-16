using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Vehicles;

namespace Paradise.Core.Audio;

/// <summary>
/// Musik in Interieurs wie im Spiel (portiert aus Unity TDU2SpotAudio): Autoradio stumm, stattdessen die Spot-Musik aus
/// Bnk/sound/musics (MngMusic, "%sSound/Musics/%s"). Zuerst wird im Exterior-Bank des Spots (entities.xmb) nach einem Verweis
/// auf eine der Musikdateien gesucht, sonst gilt die Tabelle (Spot-Code-Präfix → Datei). Varianten (playerhouse01..03,
/// clothesshop01/02, club…) werden je Spot fest über den Spot-Index gewählt. Beim Verlassen ausblenden, Radio wieder an.
/// </summary>
public static class SpotMusic
{
    public static float Volume = 0.6f;
    public static float FadeSeconds = 1.0f;

    /// <summary>Spot-Code-Präfix → Musikdatei (ohne .wav). Längere Präfixe zuerst (Unity TDU2SpotAudio.Table).</summary>
    static readonly (string prefix, string music)[] Table =
    {
        ("ERS_A", "schoolasphalt"), ("ERS_B", "schooldirt"), ("ERS_C", "schoolclassic"),
        ("ESTOL", "stolencardealer"), ("ESTICK", "stickershop"), ("EPHOT", "photograph"),
        ("ECDC", "cardealer"), ("ECD", "cardealer"), ("EBD", "cardealer"),
        ("EPH", "playerhouse"), ("ETUN", "tunershop"), ("ESUR", "surgeryclinic"),
        ("EHD", "hairdresser"), ("EGAM", "gamblinghall"), ("EAIR", "airport"),
        ("ERE", "realtor"), ("ECH", "clothesshop"), ("EAS", "clothesshop"), ("EUC", "club"),
    };

    static readonly AudioVoice _src = new() { Loop = true, IgnorePause = true };
    static readonly object _lock = new();
    static int _request;
    static bool _inside, _ducked;
    static string? _current;

    /// <summary>Gerade laufende Spot-Musik (Dateiname) oder null.</summary>
    public static string? Current => _current;

    /// <summary>Interieur betreten: Radio stumm, Musik des Spots laden (Hintergrund) und einblenden.</summary>
    public static void Enter(string? code, int spotIndex, string? bnkDir, string? exteriorDir)
    {
        lock (_lock)
        {
            _inside = true;
            int req = ++_request;
            if (TDU2Radio.Instance != null) TDU2Radio.Instance.Muted = true;
            if (string.IsNullOrEmpty(bnkDir) || string.IsNullOrEmpty(code)) return;
            Task.Run(() =>
            {
                var (clip, name) = Load(bnkDir!, exteriorDir, code!, spotIndex);
                lock (_lock)
                {
                    if (req != _request || !_inside) return;
                    if (clip == null) { if (name != null) Log.Warn("Spot-Musik " + name + ".wav nicht dekodierbar"); else Log.Info("Spot-Musik: keine für " + code); return; }
                    _current = name;
                    _src.Clip = clip; _src.Volume = 0f; _src.Play();
                    _src.FadeTo(_ducked ? 0f : Volume, FadeSeconds);
                    Log.Info("Spot-Musik: " + name + ".wav (" + code + ")");
                }
            });
        }
    }

    /// <summary>Interieur verlassen: Musik ausblenden, Radio wieder an.</summary>
    public static void Exit()
    {
        lock (_lock)
        {
            _inside = false; _request++; _current = null;
            if (TDU2Radio.Instance != null) TDU2Radio.Instance.Muted = false;
            _src.FadeTo(0f, FadeSeconds);
            Task.Delay(TimeSpan.FromSeconds(FadeSeconds + 0.1)).ContinueWith(_ => { lock (_lock) if (!_inside) _src.Stop(); });
        }
    }

    /// <summary>Ausblenden, solange eine andere Musik läuft (große Karte im Interieur), danach wieder einblenden.</summary>
    public static void Duck(bool on)
    {
        lock (_lock)
        {
            if (_ducked == on) return;
            _ducked = on;
            if (_inside && _src.Clip != null) _src.FadeTo(on ? 0f : Volume, FadeSeconds);
        }
    }

    static (AudioClip? clip, string? name) Load(string bnkDir, string? extDir, string code, int salt)
    {
        string dir = Path.Combine(bnkDir, "sound", "musics");
        if (!GameFiles.DirectoryExists(dir)) return (null, null);
        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in GameFiles.GetFiles(dir, "*.wav")) available[Path.GetFileNameWithoutExtension(f)] = f;

        string? pick = FromExterior(extDir, code, available);
        if (pick == null)
        {
            string up = code.ToUpperInvariant();
            foreach (var (prefix, music) in Table)
            {
                if (!up.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (available.ContainsKey(music)) { pick = music; break; }
                var vars = new List<string>();
                foreach (var k in available.Keys) if (k.StartsWith(music, StringComparison.OrdinalIgnoreCase)) vars.Add(k);
                vars.Sort(StringComparer.OrdinalIgnoreCase);
                if (vars.Count > 0) { pick = vars[System.Math.Abs(salt) % vars.Count]; break; }
            }
        }
        if (pick == null || !available.TryGetValue(pick, out var path)) return (null, null);
        return (AudioClip.FromWav("SpotMusic_" + pick, GameFiles.ReadAllBytes(path)), pick);
    }

    /// <summary>Exterior-Bank des Spots (entities.xmb) nach einem String durchsuchen, der einer Musikdatei entspricht.</summary>
    static string? FromExterior(string? extDir, string code, Dictionary<string, string> available)
    {
        try
        {
            if (string.IsNullOrEmpty(extDir) || string.IsNullOrEmpty(code)) return null;
            var bankPath = CarDatabase.FindFile(extDir, code.ToLowerInvariant() + ".bnk");
            if (bankPath == null) return null;
            var bank = BnkArchive.Open(bankPath);
            var e = bank.Find("entities.xmb");
            if (e == null) return null;
            var root = XmbFile.Parse(bank.Read(e));
            string? found = null;
            void Walk(XmbNode? n, int depth)
            {
                if (found != null || n == null || depth > 40) return;
                if (n.Value is string sv && sv.Length > 2)
                {
                    var stem = Path.GetFileNameWithoutExtension(sv.Replace('\\', '/'));
                    if (available.ContainsKey(stem)) { found = stem; return; }
                }
                foreach (var c in n.Children) Walk(c, depth + 1);
                foreach (var c in n.Items) Walk(c, depth + 1);
            }
            Walk(root, 0);
            return found;
        }
        catch (Exception) { return null; }
    }
}
