using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.World;
using System.Runtime.InteropServices;
using System.Text;

namespace Paradise.Core.Audio;

public enum RadioStation { off = 0, hariba, roadtrack, kalua }

/// <summary>
/// Autoradio:
///  - Hariba / RoadRock: Original-Sender aus Euro/Bnk/sound/radio (db_data.cpr: DBRadioStation/DBRadioMessage, setup.xmb).
///    Online (Sendezeit vom Server) läuft die deterministische Wiedergabeliste für alle Spieler synchron; offline wird zufällig abgespielt.
///  - Kalua: Benutzerdefinierte MP3-Dateien aus dem Ordner „SharedAssets/Radio“ neben der EXE-Datei.
///  - Anzeige im HUD (hud.bnk /HUD/radio): Logo, Sendername, Interpret/Songtitel als Lauftext, Lautstärke.
/// Schaltflächen steuern den Spielbildschirm (Paradise.Game): <see cref="Toggle"/>, <see cref="ChangeStation"/>, <see cref="ChangeVolume"/>.
/// Der Sender und die Lautstärke werden in der Spielspeicherdatei gespeichert (ursprüngliche DATA RadioOptions, <see cref="ApplySaved"/>/<see cref="SavedStation"/>);
/// Wenn keine Spielspeicherdatei vorhanden ist, gelten die Standardwerte aus Game/Config/AudioConfig.
/// </summary>
public sealed class TDU2Radio
{
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    static extern bool mciGetErrorString(int errorCode, StringBuilder errorText, int errorTextSize);

    public static TDU2Radio? Instance { get; private set; }
    public static bool IsOpen => Instance != null && Instance._isOpen;

    VmfPlayer? _hud;
    bool _isOpen;

    public RadioStation CurrentStation = RadioStation.off;
    public int CurrentVolume = 1;
    public DateTime LastStationChange { get; private set; } = DateTime.MinValue;

    public string SharedAssetsFolderName = "SharedAssets";
    public string RadioFolderName = "Radio";
    public bool ShuffleTracks = false;
    public int MarqueeLength = 15;
    public float MarqueeSpeed = 0.3f;
    public string MarqueeSeparator = "   -   ";
    public bool PlayOriginalRadio = true;
    public float JingleChance = 0.3f, CommentChance = 0.2f, AdChance = 0.1f;
    const string MciAlias = "UserRadio";
    string _radioFolderPath = "";
    readonly List<RadioTrack> _tracks = new();
    int _currentTrackIndex = -1;
    bool _tracksLoaded, _mciOpened;
    float _nextPlaybackCheck, _nextMarqueeUpdate;
    float _time;
    string? _bnkDir;
    TDU2RadioData? _radioData;
    Task<TDU2RadioData>? _radioDataTask;
    readonly AudioVoice _radioSource = new() { Loop = false };
    Task<AudioClip?>? _decodeTask;
    RadioStation _decodeStation;
    RadioMessage? _currentMessage;
    readonly List<RadioMessage> _stationQueue = new();
    int _queueIndex;
    bool _originalPlaying, _segmentPlaying, _pendingRestart, _playedAnySegment, _waitRetry;
    float _retryAt;
    int _tracksSinceJingle = 99;
    bool _lastWasMusic;
    static readonly Random Rng = new();
    TDU2RadioData.RadioPlaylist? _syncPlaylist;
    int _syncCycleStation, _syncCycleSeed;
    RadioMessage? _syncMessage, _decodeMessage;
    float _seekBase;
    double _seekBaseAt = double.NaN;

    string _currentTitle = "", _currentArtist = "";
    int _titleScrollOffset, _artistScrollOffset;
    public string CurrentTitle => _currentTitle;
    public string CurrentArtist => _currentArtist;

    sealed class RadioTrack { public string FilePath = "", Title = "", Artist = ""; }

    public TDU2Radio(string? bnkDir)
    {
        Instance = this;
        _radioFolderPath = Path.Combine(AppContext.BaseDirectory, SharedAssetsFolderName, RadioFolderName);
        try { Directory.CreateDirectory(_radioFolderPath); LoadRadioTracks(); }
        catch (Exception) { }
        _bnkDir = bnkDir;
        EnsureRadioData();
    }

    void EnsureRadioData()
    {
        if (!PlayOriginalRadio || _radioData != null || _radioDataTask != null) return;
        if (string.IsNullOrEmpty(_bnkDir) || !GameFiles.DirectoryExists(Path.Combine(_bnkDir, "sound", "radio"))) return;
        string dir = _bnkDir;
        _radioDataTask = Task.Run(() => TDU2RadioData.Load(dir));
    }

    public void SetHudPlayer(VmfPlayer? hud)
    {
        if (ReferenceEquals(_hud, hud)) return;
        _hud = hud;
        if (_hud == null) return;
        _hud.SetVisible("/HUD/radio", true);
        _hud.ForceVisible("/HUD/radio", true);
        _hud.ForceVisible("/HUD/radio/#2", false);
        _hud.SetFrame("/HUD/radio/volume", CurrentVolume);
        UpdateHudText();
        ChangeStation(0);
    }

    void LoadRadioTracks()
    {
        _tracks.Clear();
        var files = Directory.GetFiles(_radioFolderPath, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            if (!string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)) continue;
            ReadMetadata(path, out string title, out string artist);
            _tracks.Add(new RadioTrack { FilePath = path, Title = title, Artist = artist });
        }
        _tracksLoaded = true;
    }

    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; ApplyMute(); }
    }
    bool _muted;
    float _nextMuteCheck;

    void ApplyMute()
    {
        if (_mciOpened) MciCommand("setaudio " + MciAlias + " volume to " + (_muted ? 0 : CurrentVolume * 100));
        _radioSource.Volume = _muted ? 0f : System.Math.Clamp(CurrentVolume / 10f, 0f, 1f);
    }

    public void Update(float dt)
    {
        _time += dt;
        if (_muted && _time >= _nextMuteCheck) { _nextMuteCheck = _time + 0.25f; ApplyMute(); }
        UpdateMarquee();
        PollRadioData();
        PollOriginalRadio();
        if (CurrentStation == RadioStation.kalua && _tracksLoaded && _mciOpened && _time >= _nextPlaybackCheck)
        {
            _nextPlaybackCheck = _time + 0.5f;
            string mode = MciQuery("status " + MciAlias + " mode");
            if (mode == "stopped") PlayNextTrack();
            else if (string.IsNullOrEmpty(mode)) StopTrack();
        }
        if (_hud != null && _isOpen && (DateTime.Now - LastStationChange).TotalSeconds > 5) { _isOpen = false; ChangeRadio(); }
    }

    public void Toggle()
    {
        _isOpen = !_isOpen;
        LastStationChange = DateTime.Now;
        ChangeRadio();
    }

    public void ChangeVolume(int delta)
    {
        if (!_isOpen) { _isOpen = true; LastStationChange = DateTime.Now; ChangeRadio(); }
        CurrentVolume = System.Math.Clamp(CurrentVolume + delta, 0, 11);
        if (_mciOpened) MciCommand("setaudio " + MciAlias + " volume to " + (CurrentVolume * 100));
        _radioSource.Volume = _muted ? 0f : System.Math.Clamp(CurrentVolume / 10f, 0f, 1f);
        _hud?.SetFrame("/HUD/radio/volume", CurrentVolume);
    }

    public void ChangeStation(int delta)
    {
        int count = Enum.GetValues<RadioStation>().Length;
        if (delta != 0) CurrentStation = (RadioStation)(((int)CurrentStation + (delta > 0 ? 1 : -1) + count) % count);
        _isOpen = true;
        LastStationChange = DateTime.Now;
        _hud?.GotoLabel("/HUD/radio/radio_logo", CurrentStation.ToString(), false);
        StopTrack();
        StopOriginalAudio();
        if (CurrentStation == RadioStation.kalua && _tracksLoaded) PlayNextTrack();
        else if (IsGameStation(CurrentStation))
        {
            _pendingRestart = true;
            if (_radioData != null) { _pendingRestart = false; StartOriginalStation(); }
        }
        else
        {
            _currentTitle = ""; _currentArtist = "";
            _titleScrollOffset = 0; _artistScrollOffset = 0;
            UpdateHudText();
        }
        ChangeRadio();
    }

    void PlayNextTrack()
    {
        if (!_tracksLoaded || _tracks.Count == 0 || CurrentStation != RadioStation.kalua) return;
        int nextIndex;
        if (ShuffleTracks && _tracks.Count > 1)
        {
            nextIndex = Rng.Next(0, _tracks.Count);
            if (nextIndex == _currentTrackIndex) nextIndex = (nextIndex + 1) % _tracks.Count;
        }
        else nextIndex = (_currentTrackIndex + 1) % _tracks.Count;
        PlayTrack(nextIndex);
    }

    void PlayTrack(int index)
    {
        if (index < 0 || index >= _tracks.Count || CurrentStation != RadioStation.kalua) return;
        StopTrack();
        var track = _tracks[index];
        string safePath = Path.GetFullPath(track.FilePath).Replace("\"", "");
        int result = MciCommand("open \"" + safePath + "\" type mpegvideo alias " + MciAlias);
        if (result != 0)
        {
            Log.Error("Radio: Title unreadable: " + track.FilePath);
            _currentTrackIndex = index;
            return;
        }
        _mciOpened = true;
        MciCommand("setaudio " + MciAlias + " volume to " + (_muted ? 0 : CurrentVolume * 100));
        result = MciCommand("play " + MciAlias);
        if (result != 0)
        {
            Log.Error("Radio: Playback failed: " + track.Title);
            _currentTrackIndex = index;
            StopTrack();
            return;
        }
        _currentTrackIndex = index;
        _nextPlaybackCheck = _time + 0.5f;
        _currentTitle = track.Title; _currentArtist = track.Artist;
        _titleScrollOffset = 0; _artistScrollOffset = 0;
        _nextMarqueeUpdate = _time + MarqueeSpeed;
        UpdateHudText();
    }

    void StopTrack()
    {
        if (!_mciOpened) return;
        MciCommand("stop " + MciAlias);
        MciCommand("close " + MciAlias);
        _mciOpened = false;
    }

    static int MciCommand(string command)
    {
        int result;
        try { result = mciSendString(command, null, 0, IntPtr.Zero); }
        catch (Exception ex)
        {
            Log.Warn("Radio: MCI not available: " + ex.Message);
            return -1;
        }
        if (result != 0)
        {
            var error = new StringBuilder(256);
            try { mciGetErrorString(result, error, error.Capacity); } catch (Exception) { }
            Log.Warn("Radio MCI error: " + command + " -> " + error);
        }
        return result;
    }

    static string MciQuery(string command)
    {
        var result = new StringBuilder(256);
        int error;
        try { error = mciSendString(command, result, result.Capacity, IntPtr.Zero); }
        catch (Exception) { return string.Empty; }
        return error == 0 ? result.ToString().Trim() : string.Empty;
    }

    void UpdateMarquee()
    {
        if (CurrentStation == RadioStation.off || _hud == null) return;
        if (_time < _nextMarqueeUpdate) return;
        _nextMarqueeUpdate = _time + System.Math.Max(0.05f, MarqueeSpeed);
        int visibleLength = System.Math.Max(1, MarqueeLength);
        _titleScrollOffset = _currentTitle.Length > visibleLength ? _titleScrollOffset + 1 : 0;
        _artistScrollOffset = _currentArtist.Length > visibleLength ? _artistScrollOffset + 1 : 0;
        UpdateHudText();
    }

    string GetMarqueeText(string text, int offset)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int length = System.Math.Max(1, MarqueeLength);
        if (text.Length <= length) return text;
        string loopText = text + (MarqueeSeparator ?? "   -   ");
        var output = new StringBuilder(length);
        for (int i = 0; i < length; i++) output.Append(loopText[(offset + i) % loopText.Length]);
        return output.ToString();
    }

    void UpdateHudText()
    {
        if (_hud == null) return;
        _hud.SetVariable("/:RadioTitle", StationDisplayName(CurrentStation));
        _hud.SetVariable("/:RadioSinger", GetMarqueeText(_currentArtist, _artistScrollOffset));
        _hud.SetVariable("/:RadioTrack", GetMarqueeText(_currentTitle, _titleScrollOffset));
    }

    void ChangeRadio() => _hud?.GotoLabel("/HUD/radio", _isOpen ? "open" : "close", true);

    static bool IsGameStation(RadioStation st) => st == RadioStation.hariba || st == RadioStation.roadtrack;

    RadioStationInfo? StationInfo(RadioStation st) => _radioData?.StationByLabel(st.ToString());

    string StationDisplayName(RadioStation st)
    {
        var info = StationInfo(st);
        return info != null && !string.IsNullOrEmpty(info.Name) ? info.Name : st.ToString();
    }

    void PollRadioData()
    {
        if (_radioDataTask == null || !_radioDataTask.IsCompleted) return;
        var t = _radioDataTask;
        _radioDataTask = null;
        if (t.IsFaulted)
            Log.Error("Radio data cannot be read: " + t.Exception?.GetBaseException().Message);
        else
        {
            _radioData = t.Result;
            if (!string.IsNullOrEmpty(_radioData.Error))
                Log.Warn("Radio: " + _radioData.Error);
            else
                Log.Info($"Radio: {_radioData.Stations.Count} Sender, {_radioData.Messages.Count} Messages, {_radioData.BankPaths.Count} Banks.");
        }
        if (_pendingHash != 0 && _radioData != null)
        {
            if (ResolveHash(_pendingHash)) { _pendingHash = 0; if (_hud != null) ChangeStation(0); return; }
            _pendingHash = 0;
        }
        if (IsGameStation(CurrentStation) && _pendingRestart) { _pendingRestart = false; StartOriginalStation(); }
    }

    bool UseSync => PlayOriginalRadio && TDU2Session.LoggedIn && TDU2Session.HasRadioClock && _radioData != null && IsGameStation(CurrentStation);

    void PollOriginalRadio()
    {
        if (_decodeTask != null && _decodeTask.IsCompleted)
        {
            var t = _decodeTask;
            _decodeTask = null;
            var clip = t.IsFaulted ? null : t.Result;
            bool current = _originalPlaying && !_segmentPlaying && _decodeStation == CurrentStation && IsGameStation(CurrentStation)
                           && (!UseSync || ReferenceEquals(_decodeMessage, _syncMessage));
            if (current && clip != null)
            {
                _radioSource.Clip = clip;
                _radioSource.Loop = false;
                _radioSource.Volume = _muted ? 0f : System.Math.Clamp(CurrentVolume / 10f, 0f, 1f);
                _radioSource.Play();
                _segmentPlaying = true;
                if (!double.IsNaN(_seekBaseAt))
                {
                    float off = _seekBase + (float)(TDU2Session.RadioElapsedSeconds - _seekBaseAt);
                    SeekAudio(off);
                    _seekBaseAt = double.NaN;
                }
            }
            else if (current) { _waitRetry = true; _retryAt = _time + 0.3f; }
            return;
        }
        if (!_originalPlaying) return;
        if (_waitRetry)
        {
            if (_time >= _retryAt && _decodeTask == null) { _waitRetry = false; if (!UseSync) AdvanceSegment(); }
            return;
        }
        if (UseSync) { UpdateSyncedRadio(); return; }
        if (_segmentPlaying && !_radioSource.IsPlaying) { _segmentPlaying = false; AdvanceSegment(); }
    }

    void SeekAudio(float seconds)
    {
        if (_radioSource.Clip == null) return;
        _radioSource.Time = System.Math.Clamp(seconds, 0f, System.Math.Max(0f, _radioSource.Length - 0.05f));
    }

    void UpdateSyncedRadio()
    {
        var info = StationInfo(CurrentStation);
        if (info == null) return;
        var playlist = GetPlaylist(info.Hashcode);
        if (playlist == null || playlist.Total <= 0.5f) return;
        double elapsed = TDU2Session.RadioElapsedSeconds;
        double pos = elapsed - System.Math.Floor(elapsed / playlist.Total) * playlist.Total;
        var seg = playlist.At(pos, out float offset);
        if (seg == null) return;
        var msg = FitConditions(seg, info.Hashcode, System.Math.Floor(elapsed / playlist.Total));
        if (!ReferenceEquals(msg, _syncMessage))
        {
            _syncMessage = msg;
            _segmentPlaying = false;
            _seekBase = offset; _seekBaseAt = elapsed;
            PlayMessage(msg);
            return;
        }
        if (_segmentPlaying && _radioSource.Clip != null && _radioSource.IsPlaying)
        {
            if (System.Math.Abs(_radioSource.Time - offset) > 2f) SeekAudio(offset);
        }
        else if (_decodeTask == null && !_waitRetry)
        {
            _seekBase = offset; _seekBaseAt = elapsed;
            PlayMessage(msg);
        }
    }

    readonly Dictionary<long, RadioMessage> _fitCache = new();

    /// <summary>Synchronous: Deterministically replace weather/time-of-day announcements that do not match the current weather or time.</summary>
    RadioMessage FitConditions(TDU2RadioData.RadioSegment seg, int stationHash, double cycle)
    {
        var m = seg.Message;
        if (m == null || !WorldWeather.Active || _radioData == null) return m!;
        bool isWeather = m.Type == RadioMessageType.WeatherRain || m.Type == RadioMessageType.WeatherStorm || m.Type == RadioMessageType.WeatherSun;
        bool isTime = m.Type == RadioMessageType.TimeMidday || m.Type == RadioMessageType.TimeNightfall || m.Type == RadioMessageType.TimeSunrise;
        if (!isWeather && !isTime) return m;
        RadioMessageType? want = isWeather ? WorldWeather.RadioWeather() : WorldWeather.RadioTime();
        if (want.HasValue && want.Value == m.Type) return m;
        long key = ((long)cycle << 24) ^ (long)(seg.Start * 10f) ^ ((long)(want.HasValue ? (int)want.Value : 99) << 56);
        if (_fitCache.TryGetValue(key, out var hit)) return hit;
        if (_fitCache.Count > 256) _fitCache.Clear();
        var cand = want.HasValue ? _radioData.ForStation(stationHash, want.Value) : new List<RadioMessage>();
        if (cand.Count == 0) cand = _radioData.ForStation(stationHash, RadioMessageType.Short, RadioMessageType.BeforeMusic);
        RadioMessage best = m;
        if (cand.Count > 0)
        {
            uint h = (uint)(key * 2654435761L) ^ 0x5bd1e995u;
            float bestScore = float.MaxValue;
            for (int i = 0; i < cand.Count; i++)
            {
                float d = _radioData.DurationOf(cand[i]);
                float score = (d > seg.Duration + 0.5f ? 1000f : 0f) + System.Math.Abs(seg.Duration - d) + ((h + (uint)i * 2246822519u) % 997u) * 0.001f;
                if (score < bestScore) { bestScore = score; best = cand[i]; }
            }
        }
        _fitCache[key] = best;
        return best;
    }

    TDU2RadioData.RadioPlaylist? GetPlaylist(int stationHash)
    {
        if (_radioData == null) return null;
        int seed = TDU2Session.RadioSeed;
        if (_syncPlaylist != null && _syncCycleStation == stationHash && _syncCycleSeed == seed) return _syncPlaylist;
        _syncPlaylist = _radioData.BuildPlaylist(stationHash, seed, JingleChance, CommentChance, AdChance);
        _syncCycleStation = stationHash; _syncCycleSeed = seed;
        return _syncPlaylist;
    }

    void StartOriginalStation()
    {
        if (_radioData == null) { _pendingRestart = true; return; }
        _originalPlaying = true;
        _playedAnySegment = false; _lastWasMusic = false; _tracksSinceJingle = 99;
        _stationQueue.Clear(); _queueIndex = 0;
        _segmentPlaying = false; _waitRetry = false;
        _syncMessage = null; _seekBaseAt = double.NaN;
        if (!UseSync) AdvanceSegment();
    }

    void StopOriginalAudio()
    {
        _originalPlaying = false; _segmentPlaying = false; _waitRetry = false; _pendingRestart = false;
        _syncMessage = null; _seekBaseAt = double.NaN;
        _radioSource.Stop();
        _radioSource.Clip = null;
        _currentMessage = null;
    }

    void AdvanceSegment()
    {
        if (!_originalPlaying || _radioData == null) return;
        var info = StationInfo(CurrentStation);
        if (info == null) return;
        RadioMessage? m = null;
        if (_playedAnySegment && _lastWasMusic)
        {
            bool forceJingle = _tracksSinceJingle >= 3;
            float roll = (float)Rng.NextDouble();
            if (WorldWeather.NewsPending) m = PickComment(info.Hashcode);
            if (m != null) { }
            else if (forceJingle || roll < JingleChance) m = PickRandom(info.Hashcode, RadioMessageType.Jingle);
            else if (roll < JingleChance + CommentChance) m = PickComment(info.Hashcode);
            else if (roll < JingleChance + CommentChance + AdChance) m = PickRandom(info.Hashcode, RadioMessageType.Advertising);
        }
        if (m == null)
        {
            m = NextMusic(info.Hashcode);
            if (m != null) { _tracksSinceJingle++; _lastWasMusic = true; }
        }
        else
        {
            _lastWasMusic = false;
            if (m.Type == RadioMessageType.Jingle) _tracksSinceJingle = 0;
        }
        if (m == null) return;
        _playedAnySegment = true;
        PlayMessage(m);
    }

    RadioMessage? NextMusic(int stationHashcode)
    {
        if (_stationQueue.Count == 0)
        {
            _stationQueue.AddRange(_radioData!.ForStation(stationHashcode, RadioMessageType.Music));
            Shuffle(_stationQueue);
            _queueIndex = 0;
        }
        if (_stationQueue.Count == 0) return null;
        var m = _stationQueue[_queueIndex];
        _queueIndex++;
        if (_queueIndex >= _stationQueue.Count) { _queueIndex = 0; Shuffle(_stationQueue); }
        return m;
    }

    RadioMessage? PickRandom(int stationHashcode, RadioMessageType type)
    {
        var list = _radioData!.ForStation(stationHashcode, type);
        return list.Count == 0 ? null : list[Rng.Next(0, list.Count)];
    }

    /// <summary>Presentation tailored to the weather and time of day; following a change in the weather, start with the weather report.</summary>
    RadioMessage? PickComment(int stationHashcode)
    {
        if (WorldWeather.Active)
        {
            RadioMessageType? wt = WorldWeather.RadioWeather(), tt = WorldWeather.RadioTime();
            if (WorldWeather.NewsPending)
            {
                WorldWeather.NewsPending = false;
                if (wt.HasValue)
                {
                    var news = _radioData!.ForStation(stationHashcode, wt.Value);
                    if (news.Count > 0) return news[Rng.Next(0, news.Count)];
                }
            }
            var types = new List<RadioMessageType> { RadioMessageType.BeforeAd, RadioMessageType.BeforeMusic, RadioMessageType.Short };
            if (wt.HasValue) types.Add(wt.Value);
            if (tt.HasValue) types.Add(tt.Value);
            var fit = _radioData!.ForStation(stationHashcode, types.ToArray());
            if (fit.Count > 0) return fit[Rng.Next(0, fit.Count)];
        }
        var list = _radioData!.ForStation(stationHashcode,
            RadioMessageType.BeforeAd, RadioMessageType.BeforeMusic, RadioMessageType.Short,
            RadioMessageType.TimeMidday, RadioMessageType.TimeNightfall, RadioMessageType.TimeSunrise,
            RadioMessageType.WeatherRain, RadioMessageType.WeatherStorm, RadioMessageType.WeatherSun);
        return list.Count == 0 ? null : list[Rng.Next(0, list.Count)];
    }

    void PlayMessage(RadioMessage m)
    {
        _currentMessage = m;
        _decodeMessage = m;
        _segmentPlaying = false;
        string? path = _radioData!.BankPathFor(m);
        if (path != null)
        {
            _decodeStation = CurrentStation;
            _decodeTask = Task.Run(() => DecodeBankWav(path));
        }
        else { _waitRetry = true; _retryAt = _time + 0.3f; }
        _currentArtist = m.HasMetadata ? (m.Artist ?? "") : "";
        _currentTitle = m.HasMetadata ? (m.Track ?? "") : "";
        _titleScrollOffset = 0; _artistScrollOffset = 0;
        _nextMarqueeUpdate = _time + MarqueeSpeed;
        UpdateHudText();
    }

    static AudioClip? DecodeBankWav(string path)
    {
        try
        {
            var bank = BnkArchive.Open(path);
            foreach (var e in bank.All(".wav")) { var c = TryDecodeEntry(bank, e); if (c != null) return c; }
            foreach (var e in bank.Entries) { var c = TryDecodeEntry(bank, e); if (c != null) return c; }
        }
        catch (Exception) { }
        return null;
    }

    static AudioClip? TryDecodeEntry(BnkArchive bank, BnkEntry e)
    {
        try { return AudioClip.FromWav("TDU2RadioClip", bank.Read(e)); }
        catch (Exception) { return null; }
    }

    static void Shuffle(List<RadioMessage> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    static void ReadMetadata(string path, out string title, out string artist)
    {
        title = Path.GetFileNameWithoutExtension(path);
        artist = "Unbekannter Interpret";
        try
        {
            using var stream = GameFiles.OpenRead(path) ?? throw new FileNotFoundException(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length >= 10)
            {
                byte[] header = reader.ReadBytes(10);
                if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
                {
                    int version = header[3];
                    int tagSize = ReadSyncSafeInt(header, 6);
                    long tagEnd = System.Math.Min(stream.Length, 10L + tagSize);
                    while (stream.Position + 10 <= tagEnd)
                    {
                        byte[] frame = reader.ReadBytes(10);
                        string id = Encoding.ASCII.GetString(frame, 0, 4);
                        if (id == "\0\0\0\0") break;
                        int size = version == 4 ? ReadSyncSafeInt(frame, 4) : ReadBigEndianInt(frame, 4);
                        if (size <= 0 || stream.Position + size > tagEnd) break;
                        string value = DecodeTextFrame(reader.ReadBytes(size));
                        if (id == "TIT2" && !string.IsNullOrWhiteSpace(value)) title = value;
                        else if (id == "TPE1" && !string.IsNullOrWhiteSpace(value)) artist = value;
                    }
                }
            }
            if ((artist == "Unbekannter Interpret" || string.IsNullOrWhiteSpace(title)) && stream.Length >= 128)
            {
                stream.Position = stream.Length - 128;
                byte[] tag = reader.ReadBytes(128);
                if (Encoding.ASCII.GetString(tag, 0, 3) == "TAG")
                {
                    string v1Title = ReadLatin1(tag, 3, 30), v1Artist = ReadLatin1(tag, 33, 30);
                    if (!string.IsNullOrWhiteSpace(v1Title)) title = v1Title;
                    if (!string.IsNullOrWhiteSpace(v1Artist)) artist = v1Artist;
                }
            }
        }
        catch (Exception ex) { Log.Warn("ID3-Lesefehler: " + ex.Message); }
    }

    static int ReadSyncSafeInt(byte[] d, int o) => (d[o] & 0x7F) << 21 | (d[o + 1] & 0x7F) << 14 | (d[o + 2] & 0x7F) << 7 | (d[o + 3] & 0x7F);
    static int ReadBigEndianInt(byte[] d, int o) => d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];

    static string DecodeTextFrame(byte[] data)
    {
        if (data == null || data.Length <= 1) return string.Empty;
        int encoding = data[0];
        var textBytes = new byte[data.Length - 1];
        Buffer.BlockCopy(data, 1, textBytes, 0, textBytes.Length);
        string value;
        switch (encoding)
        {
            case 0: value = Encoding.Latin1.GetString(textBytes); break;
            case 1: value = DecodeUnicode(textBytes, false); break;
            case 2: value = DecodeUnicode(textBytes, true); break;
            case 3: value = Encoding.UTF8.GetString(textBytes); break;
            default: return string.Empty;
        }
        return value.Trim('\0', ' ', '\r', '\n', '\t');
    }

    static string DecodeUnicode(byte[] data, bool bigEndian)
    {
        int offset = 0;
        if (data.Length >= 2)
        {
            if (data[0] == 0xFF && data[1] == 0xFE) { bigEndian = false; offset = 2; }
            else if (data[0] == 0xFE && data[1] == 0xFF) { bigEndian = true; offset = 2; }
        }
        int length = data.Length - offset;
        length -= length % 2;
        if (length <= 0) return string.Empty;
        return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(data, offset, length);
    }

    static string ReadLatin1(byte[] data, int offset, int length) => Encoding.Latin1.GetString(data, offset, length).Trim('\0', ' ', '\r', '\n', '\t');

    int _pendingHash;

    public void ApplySaved(int stationHash, string? name, int volume)
    {
        CurrentVolume = System.Math.Clamp(volume, 0, 11);
        _hud?.SetFrame("/HUD/radio/volume", CurrentVolume);
        ApplyMute();
        _pendingHash = 0;
        if (!string.IsNullOrEmpty(name) && Enum.TryParse<RadioStation>(name, true, out var byName)) CurrentStation = byName;
        else if (stationHash == 0) CurrentStation = RadioStation.off;
        else if (!ResolveHash(stationHash)) _pendingHash = stationHash;
        if (_hud != null) ChangeStation(0);
        else { StopTrack(); StopOriginalAudio(); }
    }

    bool ResolveHash(int hash)
    {
        if (_radioData == null || !_radioData.Stations.TryGetValue(hash, out var info)) return false;
        if (!Enum.TryParse<RadioStation>(info.LogoLabel, true, out var st)) return false;
        CurrentStation = st;
        return true;
    }

    public (int hash, string name) SavedStation()
    {
        if (_pendingHash != 0) return (_pendingHash, "");
        if (CurrentStation == RadioStation.off) return (0, CurrentStation.ToString());
        return (StationInfo(CurrentStation)?.Hashcode ?? 0, CurrentStation.ToString());
    }

    public void UpdateRadio(string station, string song, string title)
    {
        if (_hud == null) return;
        _hud.SetVariable("/:RadioTitle", station);
        _hud.SetVariable("/:RadioSinger", title);
        _hud.SetVariable("/:RadioTrack", song);
    }

    public void Dispose()
    {
        StopTrack();
        StopOriginalAudio();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
