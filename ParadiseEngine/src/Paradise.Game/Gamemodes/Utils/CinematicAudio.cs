using Paradise.Core.Audio;
using Paradise.Core.Cinematics;
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using System.Numerics;
using System.Text;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Kino-Töne einer Szenen-Bank (Port von Unity TDU2CinematicAudio / OpenTDU2 CinematicSounds), gemeinsam für alle Spot-Kinos
/// (<see cref="SpotCinematics"/>: Flughafen, Waschanlage):
///   Fx__Root{SourceData} -> Fx__SourceData{Fx[0] = .bfx, SoundList[].SoundNode} -> Fx__Sound{SoundFile[0] = .wav}
///   bzw. Fx__Sound__&lt;LANG&gt;{LocalizedSoundFile[0]} in der Sprachbank "&lt;bank&gt;__&lt;sprache&gt;.bnk" (Stimmen).
///   .bfx: SND-Knoten (" DNS" bei Knoten+0xe0, Klasse 0x8540076c bei +0x58): +0x5c Schleifen (-1 = Schleife), +0x78 Name,
///   +0xa0 Ton-rsc, +0xac Lautstärke, +0xb0 Tonhöhe, +0xb4/+0xb8 Zufall, +0xc8/+0xd0 Nah/Fern; "LSGL" bei 0xb4 = Sprachweiche.
///   Auslöser: Fx-Ereignisse der .cin (CinData.FxEvents, z.B. Musik M_WASHING_INTRO/M_WASHING/M_WASHING_OUTRO der Waschanlage) und
///   Fx-Spuren (0x7846) der Darsteller einer .sce.
/// Der Mixer der Engine ist 2D: positionierte Töne werden beim Start nach der Entfernung zur Kino-Kamera abgeschwächt (linear
/// zwischen Nah/Fern des .bfx) – Musik, Stimmen und Töne mit Flag 0x10 voll.
/// </summary>
internal sealed class CinematicAudio
{
    sealed class Sound
    {
        public string Name = "";
        public ulong Rsc;
        public float Volume = 1f, Pitch = 1f, VolumeRandom, PitchRandom, ClipNear = 4f, ClipFar = 40f;
        public bool Loop, Voice;
    }
    sealed class SndNode { public string Name = ""; public ulong Rsc; public Sound Params = null!; }

    /// <summary>Mischung wie im Original (Stimmen vor Musik).</summary>
    public static float VoiceVolume = 1f, EffectsVolume = 0.8f;
    public const uint Flag2D = 0x10;

    readonly BnkArchive _bank;
    BnkArchive? _lang;
    readonly Dictionary<ulong, BnkEntry> _rsc = new(), _langRsc = new();
    readonly Dictionary<ulong, ulong> _rootSources = new();
    readonly Dictionary<ulong, string> _rootNames = new();
    readonly Dictionary<ulong, (ulong bfx, List<ulong> nodes)> _sources = new();
    readonly Dictionary<ulong, ulong> _soundFiles = new();
    readonly Dictionary<ulong, List<Sound>> _resolved = new();
    readonly Dictionary<ulong, AudioClip?> _clips = new();
    readonly List<AudioVoice> _voices = new();
    string _prefix = "US_";
    bool _woman;
    uint _seed = 12345;

    public bool HasSounds => _rootSources.Count > 0;

    CinematicAudio(BnkArchive bank) { _bank = bank; }

    /// <summary>Im Hintergrund laden (Töne werden vorab dekodiert). lang: "ge", "us" …</summary>
    public static CinematicAudio Load(BnkArchive bank, string lang, bool woman)
    {
        var a = new CinematicAudio(bank) { _woman = woman };
        try { a.LoadInternal(lang); }
        catch (Exception ex) { Log.Warn("Kino-Töne " + Path.GetFileName(bank.FilePath) + ": " + ex.Message); }
        return a;
    }

    static string Prefix(string lang) => (lang ?? "us").ToLowerInvariant() switch
    {
        "us" or "en" => "US_",
        "ge" or "de" => "GE_",
        "fr" => "FR_",
        "it" => "IT_",
        "sp" or "es" => "SP_",
        "ru" => "RU_",
        "pl" or "po" => "PL_",
        "ja" or "jp" => "JP_",
        var l => l.ToUpperInvariant() + "_",
    };

    static void ReadHashcodes(BnkArchive? bank, BnkEntry? hc, Dictionary<ulong, BnkEntry> into)
    {
        if (bank == null || hc == null) return;
        var groups = XmbFile.Parse(bank.Read(hc)).Get("___CmdSaveFileInfoArray");
        if (groups == null) return;
        var byIndex = new Dictionary<int, BnkEntry>();
        foreach (var e in bank.Entries) byIndex.TryAdd(e.Index, e);
        foreach (var g in groups.Items)
        {
            var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
            if (idx == null || refs == null) continue;
            int n = System.Math.Min(idx.Items.Count, refs.Items.Count);
            for (int i = 0; i < n; i++)
            {
                int fi = idx.Items[i].AsInt(-1);
                if (fi < 0) continue;
                if (!byIndex.TryGetValue(fi, out var be)) be = fi < bank.Entries.Count ? bank.Entries[fi] : null;
                if (be != null) into[refs.Items[i].AsULong()] = be;
            }
        }
    }

    void LoadInternal(string lang)
    {
        ReadHashcodes(_bank, _bank.Find("hashcodes.xmb"), _rsc);
        // Sprachbank "<bank>__<lang>.bnk", Rückfall "__us", dann jede vorhandene
        lang = string.IsNullOrEmpty(lang) ? "us" : lang.ToLowerInvariant();
        string path = _bank.FilePath ?? "";
        if (path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
        {
            string stem = path[..^4];
            var tries = new List<string> { lang, "us" };
            try
            {
                string? dir = Path.GetDirectoryName(path); string name = Path.GetFileName(stem) + "__";
                if (dir != null)
                    foreach (var f in GameFiles.GetFiles(dir, name + "*.bnk"))
                    {
                        string l = Path.GetFileNameWithoutExtension(f)[name.Length..];
                        if (!tries.Contains(l)) tries.Add(l);
                    }
            }
            catch (Exception) { }
            foreach (var l in tries)
            {
                string p = stem + "__" + l + ".bnk";
                if (!GameFiles.Exists(p)) continue;
                try
                {
                    _lang = BnkArchive.Open(p);
                    BnkEntry? hc = null;
                    foreach (var e in _lang.Entries) if (e.Extension == ".xmb" && e.Name.StartsWith("hashcodes", StringComparison.OrdinalIgnoreCase)) { hc = e; break; }
                    ReadHashcodes(_lang, hc, _langRsc);
                    lang = l;
                    break;
                }
                catch (Exception ex) { Log.Warn("Kino-Töne " + p + ": " + ex.Message); _lang = null; }
            }
        }
        _prefix = Prefix(lang);

        var ent = _bank.Find("entities.xmb");
        if (ent == null) return;
        var root = XmbFile.Parse(_bank.Read(ent));
        var fx = root.Get("ExportType_Fx")?.Get("ArrayOf_Fx__Root");
        if (fx != null) foreach (var it in fx.Items) { ulong hc = it.U("__Hc"); _rootSources[hc] = it.U("SourceData"); _rootNames[hc] = it.S("__Name", "") ?? ""; }
        var sd = root.Get("ExportType_FxSourceData")?.Get("ArrayOf_Fx__SourceData");
        if (sd != null)
            foreach (var it in sd.Items)
            {
                ulong bfx = 0;
                var f = it.Get("Fx");
                if (f != null && f.Items.Count > 0) bfx = f.Items[0].U("rsc");
                var nodes = new List<ulong>();
                var sl = it.Get("SoundList");
                if (sl != null) foreach (var s in sl.Items) nodes.Add(s.U("SoundNode"));
                _sources[it.U("__Hc")] = (bfx, nodes);
            }
        var snd = root.Get("ExportType_FxSound");
        if (snd != null)
            foreach (var arr in snd.Children)
            {
                if (!arr.IsArray || !arr.Name.StartsWith("ArrayOf_Fx__Sound", StringComparison.Ordinal)) continue;
                foreach (var it in arr.Items)
                    foreach (var field in new[] { "SoundFile", "LocalizedSoundFile" })
                    {
                        var l = it.Get(field);
                        if (l != null && l.Items.Count > 0) { _soundFiles[it.U("__Hc")] = l.Items[0].U("rsc"); break; }
                    }
            }
        // alle Wurzeln auflösen und die Töne vorab dekodieren
        int waves = 0;
        foreach (var hc in new List<ulong>(_rootSources.Keys))
            foreach (var s in Resolve(hc))
                if (!_clips.ContainsKey(s.Rsc)) { _clips[s.Rsc] = Decode(s.Rsc); if (_clips[s.Rsc] != null) waves++; }
        Log.Info($"Kino-Töne {Path.GetFileName(path)}: {_rootSources.Count} Fx, {waves} Töne" + (_lang != null ? ", Stimmen " + Path.GetFileName(_lang.FilePath) : ""));
    }

    AudioClip? Decode(ulong rsc)
    {
        var e = FindWav(rsc, out var b);
        return e != null && b != null ? AudioClip.FromWav(e.Name, b.Read(e)) : null;
    }

    BnkEntry? FindWav(ulong rsc, out BnkArchive? bank)
    {
        bank = _bank;
        if (_rsc.TryGetValue(rsc, out var e) && e.Extension == ".wav") return e;
        bank = _lang;
        if (_lang != null && _langRsc.TryGetValue(rsc, out e) && e.Extension == ".wav") return e;
        bank = null;
        return null;
    }

    static List<SndNode> ParseBfx(byte[]? d, out bool languageSwitch)
    {
        var list = new List<SndNode>();
        languageSwitch = d != null && d.Length >= 0xb8 && d[0xb4] == 'L' && d[0xb5] == 'S' && d[0xb6] == 'G' && d[0xb7] == 'L';
        if (d == null || d.Length < 0xe4) return list;
        for (int tag = 0xe0; tag + 4 <= d.Length; tag += 4)
        {
            if (d[tag] != ' ' || d[tag + 1] != 'D' || d[tag + 2] != 'N' || d[tag + 3] != 'S') continue;
            int n = tag - 0xe0;
            if (BitConverter.ToUInt32(d, n + 0x58) != 0x8540076cu) continue;
            int len = 0; while (len < 32 && d[n + 0x78 + len] != 0) len++;
            var s = new Sound
            {
                Name = Encoding.ASCII.GetString(d, n + 0x78, len),
                Rsc = BitConverter.ToUInt64(d, n + 0xa0),
                Volume = BitConverter.ToSingle(d, n + 0xac),
                Pitch = BitConverter.ToSingle(d, n + 0xb0),
                VolumeRandom = BitConverter.ToSingle(d, n + 0xb4),
                PitchRandom = BitConverter.ToSingle(d, n + 0xb8),
                ClipNear = BitConverter.ToSingle(d, n + 0xc8),
                ClipFar = BitConverter.ToSingle(d, n + 0xd0),
                Loop = BitConverter.ToUInt32(d, n + 0x5c) == 0xffffffffu,
            };
            if (float.IsNaN(s.Pitch) || s.Pitch <= 0f) s.Pitch = 1f;
            if (float.IsNaN(s.Volume) || s.Volume < 0f) s.Volume = 1f;
            if (!(s.ClipNear > 0f)) s.ClipNear = 4f;
            if (!(s.ClipFar > s.ClipNear)) s.ClipFar = s.ClipNear + 36f;
            list.Add(new SndNode { Name = s.Name, Rsc = s.Rsc, Params = s });
            tag += 0xe0 - 4;
        }
        return list;
    }

    List<Sound> Resolve(ulong rootHc)
    {
        lock (_resolved)
        {
            if (_resolved.TryGetValue(rootHc, out var cached)) return cached;
            var list = new List<Sound>();
            _resolved[rootHc] = list;
            if (!_rootSources.TryGetValue(rootHc, out var src) || !_sources.TryGetValue(src, out var source)) return list;
            List<SndNode>? nodes = null; bool ls = false;
            if (source.bfx != 0 && _rsc.TryGetValue(source.bfx, out var be)) nodes = ParseBfx(_bank.Read(be), out ls);
            nodes ??= new List<SndNode>();
            ulong langRsc = 0;
            if (ls)
            {
                SndNode? chosen = null;
                foreach (var prefix in new[] { _prefix, "US_", "" })
                {
                    // Spielerin: zuerst die "_W"-Aufnahme, sonst die normale (und umgekehrt)
                    for (int pass = 0; pass < 2 && chosen == null; pass++)
                        foreach (var n in nodes)
                            if (n.Name.StartsWith(prefix, StringComparison.Ordinal) && n.Name.EndsWith("_W", StringComparison.Ordinal) == ((pass == 0) == _woman)
                                && FindWav(n.Rsc, out _) != null) { chosen = n; break; }
                    if (chosen != null) break;
                }
                if (chosen == null) return list;
                langRsc = chosen.Rsc;
            }
            foreach (var sn in source.nodes)
            {
                if (!_soundFiles.TryGetValue(sn, out ulong rsc)) continue;
                if (ls && rsc != langRsc) continue;
                var e = FindWav(rsc, out var wb);
                if (e == null) continue;
                Sound? p = null;
                foreach (var n in nodes) if (n.Rsc == rsc) { p = n.Params; break; }
                var s = p != null ? new Sound { Volume = p.Volume, Pitch = p.Pitch, VolumeRandom = p.VolumeRandom, PitchRandom = p.PitchRandom, ClipNear = p.ClipNear, ClipFar = p.ClipFar, Loop = p.Loop } : new Sound();
                s.Rsc = rsc; s.Name = e.Name; s.Voice = wb != null && wb == _lang;
                list.Add(s);
            }
            return list;
        }
    }

    float Rand() { _seed = _seed * 0x7ff8a3edu + 0x2aa01d31u; return (float)(_seed / 4294967296.0); }
    float RandVolume(float r, float v) => r == 0f ? v : System.Math.Clamp(v + r * Rand() - 0.5f * r, 0f, 1f);
    float RandPitch(float r, float p)
    {
        if (r == 0f) return p;
        float x = 2f * Rand() - 1f;
        return x >= 0f ? p * (1f + x * r) : (1f + x) * p - x * p / (1f + r);
    }

    /// <summary>Töne einer Fx-Wurzel. pos/eye: Spielraum (null = 2D). Gestartete Stimmen kommen in <paramref name="into"/>.</summary>
    void PlayRoot(ulong rootHc, float offset, Vector3? pos, Vector3? eye, uint flags, List<AudioVoice>? into)
    {
        foreach (var s in Resolve(rootHc))
        {
            if (!_clips.TryGetValue(s.Rsc, out var clip)) _clips[s.Rsc] = clip = Decode(s.Rsc);
            if (clip == null) continue;
            float vol = RandVolume(s.VolumeRandom, s.Volume) * (s.Voice ? VoiceVolume : EffectsVolume);
            bool spatial = pos.HasValue && eye.HasValue && (flags & Flag2D) == 0 && !s.Voice;
            if (spatial)
            {
                float d = Vector3.Distance(pos!.Value, eye!.Value);
                float near = System.Math.Max(0.1f, s.ClipNear), far = System.Math.Max(near + 1f, s.ClipFar);
                vol *= d <= near ? 1f : d >= far ? 0f : 1f - (d - near) / (far - near);
            }
            if (vol <= 0.001f) continue;
            float len = clip.Frames / (float)clip.Rate;
            float pitch = System.Math.Clamp(RandPitch(s.PitchRandom, s.Pitch), 0.1f, 3f);
            float start = offset * pitch;
            if (s.Loop && len > 0f) start %= len;
            if (start >= len) continue;
            var v = new AudioVoice { Clip = clip, Loop = s.Loop, Pitch = pitch };
            v.Volume = System.Math.Clamp(vol, 0f, 1f);
            v.Play();
            if (start > 0f) v.Time = start;
            _voices.Add(v);
            into?.Add(v);
        }
    }

    /// <summary>Fx-Ereignisse des .cin im Intervall (from, to] (from &lt; 0: Start). Position: Zeile 3 der Ereignismatrix (Bankrahmen).</summary>
    public void UpdateCin(CinData cin, float from, float to, Matrix4x4 frame, Vector3? eye, List<AudioVoice>? into)
    {
        if (_rootSources.Count == 0) return;
        foreach (var e in cin.FxEvents)
        {
            bool hit = (e.Time > from && e.Time <= to) || (from < 0f && e.Time <= 0f && to >= 0f);
            if (!hit || e.Reference < 0 || e.Reference >= cin.References.Count) continue;
            var m = e.World;
            var world = Vector3.Transform(new Vector3(m[12], m[13], m[14]), frame);
            PlayRoot(cin.References[e.Reference].Hashcode, System.Math.Max(to - e.Time, 0f), world, eye, e.Flags, into);
        }
    }

    /// <summary>Fx-Spuren (0x7846) der Darsteller einer .sce im Intervall (from, to] der Szenenzeit.</summary>
    public void UpdateScene(CinScene sc, float from, float to, Matrix4x4 frame, Vector3? eye, List<AudioVoice>? into)
    {
        if (_rootSources.Count == 0) return;
        foreach (var o in sc.Objects)
            foreach (var tr in o.Tracks)
            {
                if (tr.Id != CinIds.TrackFx || tr.Components < 21) continue;
                for (int k = 0; k < tr.Times.Length; k++)
                {
                    float t = tr.Times[k];
                    bool hit = (t > from && t <= to) || (from < 0f && t <= 0f && to >= 0f);
                    if (!hit) continue;
                    int b = k * tr.Components;
                    uint reference = tr.Raw.Length > b ? tr.Raw[b] : 0xffffffffu;
                    uint flags = tr.Raw.Length > b + 1 ? tr.Raw[b + 1] : 0;
                    if (reference >= sc.References.Count || sc.References[(int)reference].Type != 0x010d) continue;
                    var off = new Vector3(tr.Values[b + 17], tr.Values[b + 18], tr.Values[b + 19]);
                    var world = Vector3.Transform(sc.ToGame(o.SamplePosition(t) + off), frame);
                    PlayRoot(sc.References[(int)reference].Hashcode, System.Math.Max(to - t, 0f), world, eye, flags, into);
                }
            }
    }

    public static void Stop(List<AudioVoice>? voices)
    {
        if (voices == null) return;
        foreach (var v in voices) v.Stop();
        voices.Clear();
    }

    public void StopAll()
    {
        foreach (var v in _voices) v.Stop();
        _voices.Clear();
    }
}
