using System.Text.Json;

namespace TDU2Server.Data;

/// <summary>
/// Eine hochgeladene Challenge (.ig2 aus dem Race-Editor des Spiels bzw. eine CRC-Challenge aus dem Web-Panel).
/// Index in &lt;dataDir&gt;/challenges.json, die Datei selbst in &lt;dataDir&gt;/challenges/&lt;id&gt;.ig2.
/// Felder wie die Karte des Originals (ChallengeIDCard, exe 0x975340 ff.): Gebühr (fee1), Basisbelohnung (rewm),
/// Laufzeit in Tagen (1..7), Zugänglichkeit (0 Alle, 1 Freunde, 2 Klub, 3 Freunde &amp; Klub).
/// </summary>
public sealed class ChallengeItem
{
    public int id;
    public string author = "";
    public string name = "";
    public string description = "";
    public int mode;              // ig2 gmth (0 Zeitfahren, 1 Radar, 3 Rennen, 10 Tempo …)
    public int island;            // ig2 isla (0 Ibiza, 1 Hawaii)
    public int length;            // ig2 leme (m)
    public int checkpoints;       // ig2 NbCp
    public int fee;
    public int reward;
    public int duration = 1;      // Tage
    public int postTo;            // 0 Alle, 1 Freunde, 2 Klub, 3 Freunde & Klub
    public bool official;         // CRC: im Panel hochgeladen (Community Racing Center)
    public string created = "";   // ISO (UTC)
    public string expires = "";   // ISO (UTC), leer = kein Ende (CRC)
    public bool removed;          // im Panel entfernt
    public int bestScore;         // beste Wertung (Zeitarten: ms, kleiner ist besser; Tempo/Radar: km/h x 100, größer ist besser)
    public string bestNick = "";  // Halter der besten Wertung (leer = noch niemand)
    public string bestCar = "";
    public int tries;             // gewertete Fahrten


    public bool IsActive
    {
        get
        {
            if (removed) return false;
            if (!string.IsNullOrEmpty(expires) && DateTime.TryParse(expires, null, System.Globalization.DateTimeStyles.RoundtripKind, out var e)
                && DateTime.UtcNow >= e.ToUniversalTime()) return false;
            return true;
        }
    }
}

/// <summary>Challenge für den Client (Liste ohne Datei; Datei per "challget"). Gleiche Namen im Client (Paradise.Core/Online/Protocol).</summary>
public sealed class ChallengeEntry
{
    public int id;
    public string author = "";
    public string name = "";
    public string description = "";
    public int mode;
    public int island;
    public int length;
    public int checkpoints;
    public int fee;
    public int reward;
    public int duration;
    public int postTo;
    public bool official;
    public string created = "";
    public string expires = "";
    public int bestScore;         // beste Wertung (Zeitarten: ms, kleiner ist besser; Tempo/Radar: km/h x 100, größer ist besser)
    public string bestNick = "";  // Halter der besten Wertung (leer = noch niemand)
    public string bestCar = "";
    public int tries;             // gewertete Fahrten

    public static ChallengeEntry Of(ChallengeItem c) => new()
    {
        id = c.id,
        author = c.author,
        name = c.name,
        description = c.description,
        mode = c.mode,
        island = c.island,
        length = c.length,
        checkpoints = c.checkpoints,
        fee = c.fee,
        reward = c.reward,
        duration = c.duration,
        postTo = c.postTo,
        official = c.official,
        created = c.created,
        expires = c.expires,
        bestScore = c.bestScore,
        bestNick = c.bestNick,
        bestCar = c.bestCar,
        tries = c.tries,
    };
}

/// <summary>Hochladen aus dem Editor ("challup"): Karte + .ig2 (Base64).</summary>
public sealed class ChallengeUpload
{
    public string name = "";
    public string description = "";
    public int fee;
    public int reward;
    public int duration = 1;
    public int postTo;
    public string data = "";      // .ig2, Base64
}

/// <summary>Challenges auf der Platte (atomar über .tmp geschrieben). Ids steigen nur.</summary>
public sealed class ChallengeStore
{
    sealed class FileData { public int nextId = 1; public List<ChallengeItem> items = new(); }

    /// <summary>Höchstzahl gleichzeitig laufender eigener Challenges je Spieler (Film: "max_chall_posted").</summary>
    public const int MaxActivePerPlayer = 3;
    /// <summary>Längste Laufzeit (Tage).</summary>
    public const int MaxDuration = 7;
    /// <summary>Gebühr und Belohnung: 0 .. 1 000 000 $.</summary>
    public const int MaxMoney = 1000000;
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxText = 63;              // exe: Name/Beschreibung je 0x40 Byte

    readonly string _file, _dir;
    readonly object _lock = new();
    static readonly JsonSerializerOptions FileJson = new() { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true };
    FileData _data = new();

    public ChallengeStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "challenges.json");
        _dir = Path.Combine(dataDir, "challenges");
        Directory.CreateDirectory(_dir);
        try
        {
            if (File.Exists(_file)) _data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_file), FileJson) ?? new FileData();
        }
        catch (Exception ex) { Core.Log.Info("challenges.json fehlerhaft (" + ex.Message + ") – leer begonnen"); _data = new FileData(); }
        _data.items ??= new();
    }

    void SaveLocked()
    {
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, FileJson));
        File.Move(tmp, _file, true);
    }

    string FileOf(int id) => Path.Combine(_dir, id + ".ig2");

    public int ActiveCount(string nick)
    {
        lock (_lock) return _data.items.Count(c => !c.official && c.IsActive && string.Equals(c.author, nick, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Neue Challenge. Fehler: bad_file, too_big, max_posted. Rückgabe: Eintrag oder null.</summary>
    public ChallengeItem Add(string author, byte[] ig2, string name, string description, int fee, int reward, int duration, int postTo, bool official, out string error)
    {
        error = "";
        if (ig2 == null || ig2.Length < 0x20 || ig2[0] != (byte)'I' || ig2[1] != (byte)'G' || ig2[2] != (byte)'E' || ig2[3] != (byte)'2') { error = "bad_file"; return null; }
        if (ig2.Length > MaxFileBytes) { error = "too_big"; return null; }
        var info = Ige2Info.Read(ig2);
        lock (_lock)
        {
            if (!official && _data.items.Count(c => !c.official && c.IsActive && string.Equals(c.author, author, StringComparison.OrdinalIgnoreCase)) >= MaxActivePerPlayer)
            { error = "max_posted"; return null; }
            var now = DateTime.UtcNow;
            duration = Math.Clamp(duration, 1, MaxDuration);
            var c = new ChallengeItem
            {
                id = _data.nextId++,
                author = author,
                name = Clip(string.IsNullOrWhiteSpace(name) ? info.Name : name),
                description = Clip(string.IsNullOrWhiteSpace(description) ? info.Description : description),
                mode = info.Mode,
                island = info.Island,
                length = info.Length,
                checkpoints = info.Checkpoints,
                fee = Math.Clamp(fee, 0, MaxMoney),
                reward = Math.Clamp(reward, 0, MaxMoney),
                duration = duration,
                postTo = Math.Clamp(postTo, 0, 3),
                official = official,
                created = now.ToString("o"),
                expires = official ? "" : now.AddDays(duration).ToString("o"),
            };
            File.WriteAllBytes(FileOf(c.id), ig2);
            _data.items.Add(c);
            SaveLocked();
            return c;
        }
    }

    static string Clip(string s) { s = (s ?? "").Trim(); return s.Length > MaxText ? s.Substring(0, MaxText) : s; }

    /// <summary>Aktive Challenges: kind "crc" = offizielle (Panel), "community" = von Spielern, sonst alle. island &lt; 0 = alle Inseln.</summary>
    public List<ChallengeEntry> List(string kind, int island)
    {
        lock (_lock)
            return _data.items.Where(c => c.IsActive
                    && (kind == "crc" ? c.official : kind == "community" ? !c.official : true)
                    && (island < 0 || c.island == island))
                .OrderByDescending(c => c.id).Select(ChallengeEntry.Of).ToList();
    }

    public List<ChallengeItem> All() { lock (_lock) return _data.items.OrderByDescending(c => c.id).ToList(); }

    public ChallengeItem Get(int id) { lock (_lock) return _data.items.FirstOrDefault(c => c.id == id); }

    /// <summary>.ig2 einer aktiven Challenge (null = unbekannt/abgelaufen).</summary>
    public byte[] Data(int id)
    {
        var c = Get(id);
        if (c == null || !c.IsActive) return null;
        try { return File.ReadAllBytes(FileOf(id)); } catch (Exception) { return null; }
    }

    /// <summary>Tempo (gmth 10) und Radar (1): größere Wertung ist besser; sonst kleinere (Zeit).</summary>
    public static bool HigherIsBetter(int mode) => mode == 1 || mode == 10;

    /// <summary>Fahrt werten ("challres"). Rückgabe: true = neue Bestwertung. Fehler: not_found.</summary>
    public bool Submit(int id, string nick, int score, string car, out string error)
    {
        error = "";
        lock (_lock)
        {
            var c = _data.items.FirstOrDefault(x => x.id == id);
            if (c == null || !c.IsActive) { error = "not_found"; return false; }
            c.tries++;
            bool better = score > 0 && (string.IsNullOrEmpty(c.bestNick) || (HigherIsBetter(c.mode) ? score > c.bestScore : score < c.bestScore));
            if (better) { c.bestScore = score; c.bestNick = nick; c.bestCar = car ?? ""; }
            SaveLocked();
            return better;
        }
    }

    public bool Remove(int id)
    {
        lock (_lock)
        {
            var c = _data.items.FirstOrDefault(x => x.id == id);
            if (c == null) return false;
            c.removed = true;
            SaveLocked();
            return true;
        }
    }
}

/// <summary>Kopfwerte einer .ig2 (IGE2, big-endian; MDSC-Tags: char[4] Tag, u8 Flags, u8 Gruppe, u16 Länge, Wert).</summary>
public readonly struct Ige2Info
{
    public readonly int Mode, Island, Length, Checkpoints;
    public readonly string Name, Description;

    Ige2Info(int mode, int island, int length, int cps, string name, string desc) { Mode = mode; Island = island; Length = length; Checkpoints = cps; Name = name; Description = desc; }

    public static Ige2Info Read(byte[] b)
    {
        int mode = 0, island = 1, len = 0, cps = 0; string name = "", desc = "";
        try
        {
            int mdsc = BE(b, 4), size = BE(b, mdsc + 4), count = BE(b, mdsc + 8), end = Math.Min(b.Length, mdsc + size), p = mdsc + 12;
            for (int i = 0; i < count && p + 8 <= end; i++)
            {
                string tag = System.Text.Encoding.ASCII.GetString(b, p, 4).Trim('\0');
                int l = (b[p + 6] << 8) | b[p + 7];
                int v = p + 8;
                if (l == 4)
                {
                    int x = BE(b, v);
                    if (tag == "gmth") mode = x; else if (tag == "isla") island = x; else if (tag == "leme") len = x; else if (tag == "NbCp") cps = x;
                }
                else if (l == 64 && (tag == "mnme" || tag == "DesC"))
                {
                    int z = Array.IndexOf(b, (byte)0, v, 64); if (z < 0) z = v + 64;
                    string s = System.Text.Encoding.Latin1.GetString(b, v, z - v);
                    if (tag == "mnme") name = s; else desc = s;
                }
                p = v + l;
            }
        }
        catch (Exception) { }
        return new Ige2Info(mode, island, len, cps, name, desc);
    }

    static int BE(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
