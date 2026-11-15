using Paradise.Core.Files;
using Paradise.Core.Formats;

namespace Paradise.Core.Audio
{
    public enum RadioMessageType
    {
        Comment = 0,
        BeforeAd = 1,
        BeforeMusic = 2,
        Short = 3,
        TimeMidday = 4,
        TimeNightfall = 5,
        TimeSunrise = 6,
        WeatherRain = 7,
        WeatherStorm = 8,
        WeatherSun = 9,
        Music = 10,
        Advertising = 11,
        Jingle = 13,
    }

    public sealed class RadioStationInfo
    {
        public int Hashcode;
        public string Name = "";
        public string LogoLabel = "";
        public bool IsValid;
        public int IntervalMessages;
        public float WeatherTime;

        public override string ToString() => Name;
    }

    public sealed class RadioMessage
    {
        public ulong Hashcode;
        public int Station;
        public RadioMessageType Type;
        public string File = "";
        public string? Artist;
        public string? Track;
        public float TimeBetweenTwo;

        public bool HasMetadata => !string.IsNullOrEmpty(Artist) || !string.IsNullOrEmpty(Track);

        public override string ToString() => $"{Type} {File}" + (HasMetadata ? $" ({Artist} - {Track})" : "");
    }

    public sealed class RadioTrackInfo
    {
        public string Name = "";
        public ulong Hashcode;
        public float Duration;
        public float LoopStart, LoopEnd;
    }

    /// <summary>
    /// Reads the radio configuration: DBRadioStation + DBRadioMessage from db_data.cpr, the song durations
    /// from sound/radio/setup.xmb, and resolves each message into its corresponding bank in sound/radio/{music,jingle,comment,advertising}.
    /// Pure data storage without the Unity API -> can be loaded in the background.
    /// </summary>
    public sealed class TDU2RadioData
    {
        public readonly Dictionary<int, RadioStationInfo> Stations = new Dictionary<int, RadioStationInfo>();
        public readonly List<RadioMessage> Messages = new List<RadioMessage>();
        public readonly Dictionary<string, RadioTrackInfo> Tracks = new Dictionary<string, RadioTrackInfo>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<ulong, RadioTrackInfo> TracksByHashcode = new Dictionary<ulong, RadioTrackInfo>();
        public readonly Dictionary<string, string> BankPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string BnkDir = "";
        public string RadioDir = "";
        public string? Error;

        public static readonly string[] Categories = { "music", "jingle", "comment", "advertising" };

        public static string CategoryOf(RadioMessageType t)
        {
            switch (t)
            {
                case RadioMessageType.Advertising: return "advertising";
                case RadioMessageType.Jingle: return "jingle";
                case RadioMessageType.Music: return "music";
                default: return "comment";
            }
        }

        public bool IsMusic(RadioMessage? m) => m != null && m.Type == RadioMessageType.Music;

        public RadioStationInfo? StationByLabel(string label)
        {
            foreach (var s in Stations.Values)
                if (string.Equals(s.LogoLabel, label, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        public List<RadioMessage> ForStation(int stationHashcode, params RadioMessageType[] types)
        {
            var list = new List<RadioMessage>();
            foreach (var m in Messages)
            {
                if (m.Station != stationHashcode) continue;
                if (types != null && types.Length > 0 && Array.IndexOf(types, m.Type) < 0) continue;
                list.Add(m);
            }
            return list;
        }

        public string? BankPathFor(RadioMessage? m)
        {
            if (m == null || string.IsNullOrEmpty(m.File)) return null;
            if (BankPaths.TryGetValue(m.File, out var path)) return path;
            return null;
        }

        public static TDU2RadioData Load(string bnkDir)
        {
            var d = new TDU2RadioData { BnkDir = bnkDir, RadioDir = Path.Combine(bnkDir, "sound", "radio") };
            try
            {
                d.LoadDatabase(Path.Combine(bnkDir, "database", "db_data.cpr"));
                d.IndexBanks();
                d.LoadSetup(Path.Combine(d.RadioDir, "setup.xmb"));
            }
            catch (Exception ex)
            {
                d.Error = ex.Message;
            }
            return d;
        }

        void LoadDatabase(string dbPath)
        {
            if (!GameFiles.Exists(dbPath))
            {
                Error = "db_data.cpr is missing: " + dbPath;
                return;
            }
            var root = XmbFile.Parse(CprCrypto.Decrypt(GameFiles.ReadAllBytes(dbPath)));

            var st = root.Get("DBRadioStation");
            if (st != null)
                foreach (var r in st.Items)
                    Stations[r.I("_hashcode")] = new RadioStationInfo
                    {
                        Hashcode = r.I("_hashcode"),
                        Name = r.S("_radiostation") ?? "",
                        LogoLabel = r.S("_logoflashlabel") ?? "",
                        IsValid = r.B("_isvalid"),
                        IntervalMessages = r.I("_intervaltimemessages"),
                        WeatherTime = r.F("_timeweather"),
                    };

            var msg = root.Get("DBRadioMessage");
            if (msg != null)
                foreach (var r in msg.Items)
                    Messages.Add(new RadioMessage
                    {
                        Hashcode = r.U("_hashcode"),
                        Station = r.I("_radio"),
                        Type = (RadioMessageType)r.I("_type"),
                        File = r.S("_file") ?? "",
                        Artist = r.S("_artist"),
                        Track = r.S("_trackname"),
                        TimeBetweenTwo = r.F("_timebetweentwo"),
                    });
        }

        void IndexBanks()
        {
            if (!GameFiles.DirectoryExists(RadioDir)) return;
            foreach (var cat in Categories)
            {
                string dir = Path.Combine(RadioDir, cat);
                if (!GameFiles.DirectoryExists(dir)) continue;
                foreach (var p in GameFiles.GetFiles(dir, "*.bnk"))
                    BankPaths[Path.GetFileNameWithoutExtension(p)] = p;
            }
        }

        void LoadSetup(string path)
        {
            if (!GameFiles.Exists(path)) return;
            if (!XmbFile.IsXmb(GameFiles.ReadAllBytes(path))) { }
            var root = XmbFile.Parse(GameFiles.ReadAllBytes(path));
            var tracks = root.Get("Tracks") ?? root.Get("ProjectExport")?.Get("Tracks");
            if (tracks == null) return;
            foreach (var t in tracks.Items)
            {
                string? name = t.S("Name");
                if (string.IsNullOrEmpty(name)) continue;
                var info = new RadioTrackInfo
                {
                    Name = name,
                    Hashcode = t.U("Hashcode"),
                    Duration = t.F("Duration"),
                    LoopStart = t.F("LoopStart"),
                    LoopEnd = t.F("LoopEnd"),
                };
                Tracks[name] = info;
                if (info.Hashcode != 0) TracksByHashcode[info.Hashcode] = info;
            }
        }

        public float DurationOf(RadioMessage? m)
        {
            if (m == null) return 0f;
            if (m.Hashcode != 0 && TracksByHashcode.TryGetValue(m.Hashcode, out var byHash) && byHash.Duration > 0f)
                return byHash.Duration;
            if (!string.IsNullOrEmpty(m.File) && Tracks.TryGetValue(m.File + ".wav", out var byName) && byName.Duration > 0f)
                return byName.Duration;
            return 0f;
        }

        public sealed class RadioSegment
        {
            public RadioMessage Message = null!;
            public float Start;
            public float Duration;
            public override string ToString() => $"{Start:F0}s +{Duration:F1}s {Message}";
        }

        public sealed class RadioPlaylist
        {
            public RadioSegment[] Segments = Array.Empty<RadioSegment>();
            public float Total;

            public RadioSegment? At(double pos, out float offset)
            {
                offset = 0f;
                if (Segments == null || Segments.Length == 0)
                    return null;
                if (pos < 0) pos = 0;
                int lo = 0, hi = Segments.Length - 1, idx = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (Segments[mid].Start <= pos) { idx = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                offset = (float)(pos - Segments[idx].Start);
                return Segments[idx];
            }
        }

        struct DetRng
        {
            uint _s;
            public DetRng(uint seed)
            {
                _s = seed == 0 ? 0x9E3779B9u : seed;
            }
            public uint Next()
            {
                uint x = _s;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                _s = x;
                return x;
            }
            public double NextDouble() => (Next() >> 8) * (1.0 / 16777216.0);
            public int Range(int n) => n <= 1 ? 0 : (int)(Next() % (uint)n);
        }

        static uint Mix(uint a, uint b)
        {
            uint h = a * 0x9E3779B1u + b * 0x85EBCA77u;
            h ^= h >> 16; h *= 0x7FEB352Du;
            h ^= h >> 15; h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }

        public RadioPlaylist BuildPlaylist(int stationHashcode, int seed, float jingleChance, float commentChance, float adChance)
        {
            var songs = ForStation(stationHashcode, RadioMessageType.Music);
            var rng = new DetRng(Mix((uint)seed, (uint)stationHashcode));
            for (int i = songs.Count - 1; i > 0; i--)
            {
                int j = rng.Range(i + 1);
                var tmp = songs[i]; songs[i] = songs[j]; songs[j] = tmp;
            }

            var msgs = new List<RadioMessage?>();
            for (int j = 0; j < songs.Count; j++)
            {
                msgs.Add(songs[j]);
                var inter = PickInterstitial(stationHashcode, seed, j, jingleChance, commentChance, adChance);
                if (inter != null) msgs.Add(inter);
            }

            var list = new List<RadioSegment>(msgs.Count);
            float t = 0f;
            foreach (var m in msgs)
            {
                if (m == null) continue;
                float d = DurationOf(m);
                if (d <= 0.2f) continue;
                list.Add(new RadioSegment { Message = m, Start = t, Duration = d });
                t += d;
            }

            return new RadioPlaylist
            {
                Segments = list.ToArray(),
                Total = t
            };
        }

        RadioMessage? PickInterstitial(int stationHashcode, int seed, int songIndex, float jingleChance, float commentChance, float adChance)
        {
            var rng = new DetRng(Mix(Mix((uint)seed, (uint)stationHashcode) ^ (uint)songIndex, 0x68BC21EBu));
            bool forceJingle = (songIndex % 4) == 3;
            double roll = rng.NextDouble();

            if (forceJingle || roll < jingleChance)
                return PickFrom(stationHashcode, rng, RadioMessageType.Jingle);
            if (roll < jingleChance + commentChance)
                return PickFrom(stationHashcode, rng,
                    RadioMessageType.BeforeAd, RadioMessageType.BeforeMusic, RadioMessageType.Short,
                    RadioMessageType.TimeMidday, RadioMessageType.TimeNightfall, RadioMessageType.TimeSunrise,
                    RadioMessageType.WeatherRain, RadioMessageType.WeatherStorm, RadioMessageType.WeatherSun);
            if (roll < jingleChance + commentChance + adChance)
                return PickFrom(stationHashcode, rng, RadioMessageType.Advertising);
            return null;
        }

        RadioMessage? PickFrom(int stationHashcode, DetRng rng, params RadioMessageType[] types)
        {
            var list = ForStation(stationHashcode, types);
            return list.Count > 0 ? list[rng.Range(list.Count)] : null;
        }
    }
}