using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Paradise.Core.Diagnostics;
using Paradise.Core.Online;
using Paradise.Game.Config;

namespace Paradise.Game.Online;

/// <summary>Ein GitHub-Release für die Reiters "Packs" des Updates-Kanals in TDU Media.</summary>
public sealed class GitHubRelease
{
    public string Name = "";
    public string Tag = "";
    public string Body = "";
    public string Url = "";
    public string DownloadUrl = "";
    public DateTime PublishedUtc;
    public bool Prerelease;

    /// <summary>Version zum Anzeigen: Tags wie "Upscaler" werden durch den Release-Namen 0.1.3 ersetzt.</summary>
    public string Version
    {
        get
        {
            foreach (string s in new[] { Name, Tag })
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                string v = s.Trim().TrimStart('v', 'V');
                if (System.Version.TryParse(v, out _)) return v;
            }
            return (string.IsNullOrWhiteSpace(Name) ? Tag : Name).Trim();
        }
    }

    /// <summary>Release entspricht der in <see cref="AppConfig.EngineVersion"/> eingebauten Version.</summary>
    public bool IsCurrent => string.Equals(Version.TrimStart('v', 'V'), AppConfig.EngineVersion.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Lädt die Releases von kuxii2016/Project-TDU2 über die öffentliche GitHub-API (kein Token nötig).
/// Wird nur vom Fernseher im Haus getickt; erfolgreiche Antworten werden lokal zwischengespeichert,
/// damit die Liste auch offline noch gefüllt ist. Ratenlimit ohne Auth: 60 Requests/Stunde pro IP.
/// </summary>
public static class GitHubReleases
{
    const string ApiUrl = "https://api.github.com/repos/kuxii2016/Project-TDU2/releases?per_page=50";
    const float RefreshSeconds = 600f;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Releases, neueste zuerst.</summary>
    public static readonly List<GitHubRelease> Items = new();
    /// <summary>"idle" = noch nichts, "loading" = Anfrage läuft, "ready" = Daten da, "error" = fehlgeschlagen.</summary>
    public static string State = "idle";
    /// <summary>Fehlertext der letzten Anfrage (für die Info-Box).</summary>
    public static string Error = "";
    /// <summary>Gibt es ein stabileres Release als die eingebaute Engine-Version?</summary>
    public static bool UpdateAvailable
    {
        get
        {
            if (!System.Version.TryParse(AppConfig.EngineVersion.TrimStart('v', 'V'), out var local)) return false;
            foreach (var r in Items)
            {
                if (r.Prerelease) continue;
                return System.Version.TryParse(r.Version.TrimStart('v', 'V'), out var remote) && remote > local;
            }
            return false;
        }
    }

    static readonly char[] SeenSeparators = { '|', ';', ',' };

    static string Norm(string? s) => (s ?? "").Trim().TrimStart('v', 'V').ToLowerInvariant();

    /// <summary>Gelesene Releases als tokens (Tag/Version/URL). Alte Spielstände mit einer einzelnen Version bleiben kompatibel.</summary>
    static HashSet<string> SeenSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string raw = TDU2Session.Save?.releasesSeen ?? "";
        foreach (string token in raw.Split(SeenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = Norm(token);
            if (t.Length > 0) set.Add(t);
        }
        return set;
    }

    static IEnumerable<string> Keys(GitHubRelease r)
    {
        if (!string.IsNullOrWhiteSpace(r.Tag)) yield return Norm(r.Tag);
        if (!string.IsNullOrWhiteSpace(r.Version)) yield return Norm(r.Version);
        if (!string.IsNullOrWhiteSpace(r.Name)) yield return Norm(r.Name);
        if (!string.IsNullOrWhiteSpace(r.Url)) yield return r.Url.Trim().ToLowerInvariant();
    }

    /// <summary>Dieses einzelne Release wurde im Spielstand als gelesen markiert.</summary>
    public static bool IsSeen(GitHubRelease? r)
    {
        if (r == null) return false;
        var seen = SeenSet();
        foreach (string key in Keys(r)) if (seen.Contains(key)) return true;
        return false;
    }

    /// <summary>Gibt es Releases, die noch nicht einzeln gelesen wurden ("!"-Marker)?</summary>
    public static bool HasUnseen
    {
        get
        {
            foreach (var r in Items) if (!IsSeen(r)) return true;
            return false;
        }
    }

    /// <summary>Nur das ausgewählte Release als gelesen merken. Andere Releases behalten ihren Marker.</summary>
    public static string MarkSeen(GitHubRelease? release)
    {
        var save = TDU2Session.Save;
        if (save == null || release == null) return save?.releasesSeen ?? "";
        if (IsSeen(release)) return save.releasesSeen ?? "";
        string key = !string.IsNullOrWhiteSpace(release.Tag) ? Norm(release.Tag)
                   : !string.IsNullOrWhiteSpace(release.Version) ? Norm(release.Version)
                   : Norm(release.Url);
        if (key.Length == 0) return save.releasesSeen ?? "";
        var tokens = (save.releasesSeen ?? "").Split(SeenSeparators, StringSplitOptions.RemoveEmptyEntries)
                                                .Select(Norm)
                                                .Where(x => x.Length > 0)
                                                .ToList();
        tokens.Add(key);
        save.releasesSeen = string.Join("|", tokens.Distinct(StringComparer.OrdinalIgnoreCase));
        SessionAutosave.Schedule(2f);
        Log.Info($"GitHub Releases: '{release.Version}' als gelesen markiert ({key})");
        return save.releasesSeen;
    }

    public static event Action? Changed;

    static Task<List<GitHubRelease>?>? _task;
    static bool _requested;
    static float _clock, _nextRefresh = -1f;

    static GitHubReleases()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ParadiseEngine/" + AppConfig.EngineVersion);
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        LoadCache();
    }

    /// <summary>Anfrage vormerken (beim Öffnen des TVs). Mehrfache Aufrufe innerhalb von 10 min wirken nicht.</summary>
    public static void Refresh(bool force = false)
    {
        if (!force && _clock < _nextRefresh) return;
        _requested = true;
        if (Items.Count == 0 && State != "loading") { State = "loading"; Raise(); }
    }

    /// <summary>Jedes Bild, solange der Fernseher offen ist (GAME_MODE_LIVING.UpdateMedia).</summary>
    public static void Tick(float dt)
    {
        _clock += dt;
        if (_task == null && _requested)
        {
            _requested = false;
            _nextRefresh = _clock + RefreshSeconds;
            State = "loading";
            Raise();
            _task = LoadAsync();
            return;
        }
        if (_task == null || !_task.IsCompleted) return;

        var finished = _task;
        _task = null;
        List<GitHubRelease>? loaded = null;
        try { loaded = finished.GetAwaiter().GetResult(); }
        catch (Exception ex) { Error = ex.Message; Log.Warn("GitHub Releases: " + ex.Message); }

        if (loaded != null)
        {
            Items.Clear();
            Items.AddRange(loaded);
            State = "ready";
            Error = "";
            SaveCache(loaded);
            Log.Info($"GitHub Releases: {loaded.Count} Einträge geladen");
            Raise();
        }
        else if (Items.Count == 0)
        {
            State = "error";
            Raise();
        }
    }

    static async Task<List<GitHubRelease>?> LoadAsync()
    {
        try
        {
            using var response = await Http.GetAsync(ApiUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Error = $"HTTP {(int)response.StatusCode}";
                Log.Warn("GitHub Releases: " + Error);
                return null;
            }
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var dtos = await JsonSerializer.DeserializeAsync<List<ReleaseDto>>(stream, Json).ConfigureAwait(false);
            if (dtos == null) return null;
            var list = new List<GitHubRelease>();
            foreach (var dto in dtos)
            {
                if (dto == null || dto.Draft) continue;
                list.Add(FromDto(dto));
            }
            list.Sort((a, b) => b.PublishedUtc.CompareTo(a.PublishedUtc));
            return list;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warn("GitHub Releases: " + ex.Message);
            return null;
        }
    }

    static void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn("GitHub Releases: " + ex.Message); }
    }

    /// <summary>Markdown der Release-Notes als kurzen, lesbaren Text für die Info-Box.</summary>
    public static string PlainBody(string? markdown, int maxChars = 700)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var sb = new StringBuilder();
        bool fence = false;
        foreach (string raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fence = !fence; continue; }
            if (fence) continue;
            string t = line.Trim();
            if (t.Length == 0) { if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n'); continue; }
            if (t.StartsWith("## ", StringComparison.Ordinal)) t = t[3..];
            else if (t.StartsWith("# ", StringComparison.Ordinal)) t = t[2..];
            else if (t.StartsWith("> ", StringComparison.Ordinal)) t = t[2..];
            else if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)) t = "- " + t[2..];
            t = Regex.Replace(t, @"!\[[^\]]*\]\([^)]+\)", "");
            t = Regex.Replace(t, @"\[([^\]]+)\]\([^)]+\)", "$1");
            t = Regex.Replace(t, "<[^>]+>", "");
            t = t.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
            if (t.Length == 0) continue;
            sb.AppendLine(t);
            if (sb.Length >= maxChars) break;
        }
        string text = sb.ToString().Trim();
        return text.Length <= maxChars ? text : text[..(maxChars - 1)].TrimEnd() + "…";
    }

    // ------------------------------------------------------------------ JSON / Cache
    static GitHubRelease FromDto(ReleaseDto d)
    {
        string tag = d.TagName ?? "";
        string download = "";
        if (d.Assets != null)
        {
            var exe = d.Assets.FirstOrDefault(a => a.Name != null && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            download = (exe ?? d.Assets.FirstOrDefault())?.BrowserDownloadUrl ?? "";
        }
        DateTime published = DateTime.MinValue;
        if (!string.IsNullOrEmpty(d.PublishedAt)
            && DateTime.TryParse(d.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)) published = dt.ToUniversalTime();
        return new GitHubRelease
        {
            Name = d.Name ?? "",
            Tag = tag,
            Body = d.Body ?? "",
            Url = d.HtmlUrl ?? "",
            DownloadUrl = download,
            PublishedUtc = published,
            Prerelease = d.Prerelease,
        };
    }

    static ReleaseDto ToDto(GitHubRelease r) => new()
    {
        Name = r.Name,
        TagName = r.Tag,
        Body = r.Body,
        HtmlUrl = r.Url,
        PublishedAt = r.PublishedUtc.ToString("O", CultureInfo.InvariantCulture),
        Prerelease = r.Prerelease,
        Assets = string.IsNullOrEmpty(r.DownloadUrl) ? null : new List<AssetDto> { new() { BrowserDownloadUrl = r.DownloadUrl } },
    };

    static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Paradise Engine", "github-releases.json");

    static void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var dtos = JsonSerializer.Deserialize<List<ReleaseDto>>(File.ReadAllText(CachePath), Json);
            if (dtos == null) return;
            foreach (var d in dtos) if (d != null) Items.Add(FromDto(d));
            Items.Sort((a, b) => b.PublishedUtc.CompareTo(a.PublishedUtc));
            if (Items.Count > 0) State = "ready";
        }
        catch (Exception ex) { Log.Warn("GitHub Releases: Cache nicht lesbar: " + ex.Message); }
    }

    static void SaveCache(List<GitHubRelease> list)
    {
        try
        {
            string? dir = Path.GetDirectoryName(CachePath);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(list.Select(ToDto).ToList(), Json));
        }
        catch (Exception ex) { Log.Warn("GitHub Releases: Cache nicht schreibbar: " + ex.Message); }
    }

    sealed class ReleaseDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; set; }
    }

    sealed class AssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
