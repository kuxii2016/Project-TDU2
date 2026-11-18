using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Online;

namespace Paradise.Game.Online;

/// <summary>
/// News vom Server (im Web-Panel angelegt: Überschrift, Text, Symbol, Insel) für den News-Kanal von TDU Media – Port von Unity TDU2News.
/// Abgleich: jede "status"-Antwort bringt newsLatest (höchste Id auf dem Server, TDU2GameData ruft <see cref="OnStatus"/>);
/// ist sie anders als die bekannte, wird mit op "news" (since = bekannte Id) nachgeholt. Zusätzlich alle 2 min zur Sicherheit.
/// Gelesen-Stand je Profil (im Spielstand: SaveData.newsSeen/updatesSeen, DATA U3D_NewsSeen).
/// Bilder einzeln per op "newsimg", im Speicher gehalten.
/// </summary>
public static class TDU2News
{
    /// <summary>Meldungen, neueste zuerst.</summary>
    public static readonly List<NewsEntry> Items = new();
    /// <summary>Höchste bekannte Id (wie der Server sie in newsLatest meldet).</summary>
    public static int Latest { get; private set; }
    /// <summary>Neue oder geänderte Meldungen angekommen (Hauptthread).</summary>
    public static event Action? Changed;

    static string? _nick;
    static Task<NetResponse>? _task;
    static float _clock, _nextFallback;
    static int _wantLatest = -1;

    /// <summary>Ungelesene Meldungen des News-Kanals (Id größer als die zuletzt dort gesehene; ohne Patch-Notes).</summary>
    public static int UnreadCount
    {
        get
        {
            int n = 0;
            foreach (var e in Items) if (!IsUpdate(e) && IsUnread(e)) n++;
            return n;
        }
    }

    /// <summary>Patch-Note für den Updates-Kanal (kind "update").</summary>
    public static bool IsUpdate(NewsEntry? e) => e != null && string.Equals(e.kind, "update", StringComparison.OrdinalIgnoreCase);

    // Gelesen-Stand (Unity: zwei PlayerPrefs-Ints "TDU2.News.Seen.<nick>" / "TDU2.Updates.Seen.<nick>") – hier im Spielstand
    // (SaveData.newsSeen/updatesSeen, Original-DATA als Umgebungsvariable U3D_NewsSeen)
    static SaveData? Save => TDU2Session.Save;

    /// <summary>Noch nicht gesehen (News: im News-Kanal, Patch-Notes: im Updates-Kanal).</summary>
    public static bool IsUnread(NewsEntry? e) => e != null && e.id > (IsUpdate(e) ? Save?.updatesSeen ?? 0 : Save?.newsSeen ?? 0);

    /// <summary>News-Kanal geöffnet: alle News (ohne Patch-Notes) als gelesen merken.</summary>
    public static void MarkSeen() => Mark(false);

    /// <summary>Updates-Kanal verlassen: alle Patch-Notes als gelesen merken.</summary>
    public static void MarkUpdatesSeen() => Mark(true);

    static void Mark(bool updates)
    {
        int max = 0;
        foreach (var e in Items) if (IsUpdate(e) == updates) max = System.Math.Max(max, e.id);
        var s = Save;
        if (s == null) return;
        int before = updates ? s.updatesSeen : s.newsSeen;
        if (max <= before) return;
        if (updates) s.updatesSeen = max; else s.newsSeen = max;
        SessionAutosave.Schedule(2f);
    }

    /// <summary>Aus der Statusabfrage: Server meldet höchste News-Id.</summary>
    public static void OnStatus(int serverLatest)
    {
        CheckProfile();
        if (serverLatest != Latest) _wantLatest = serverLatest;
    }

    static void CheckProfile()
    {
        string? nick = TDU2Session.Nick;
        if (string.Equals(nick, _nick, StringComparison.OrdinalIgnoreCase)) return;
        _nick = nick; Items.Clear(); Latest = 0; _wantLatest = -1;
        if (!string.IsNullOrEmpty(nick)) _wantLatest = int.MaxValue;   // neues Profil: alles holen
    }

    /// <summary>Jedes Bild (TDU2GameData.Update; Unity: eigener Runner).</summary>
    public static void Tick(float dt)
    {
        _clock += dt;
        TickNews();
        TickImages();
    }

    static void TickNews()
    {
        if (!TDU2Session.LoggedIn || TDU2Session.Offline || TDU2Session.Client == null) return;
        CheckProfile();
        if (_task == null)
        {
            if (_wantLatest < 0 && _clock >= _nextFallback) _wantLatest = int.MaxValue;
            if (_wantLatest < 0) return;
            _nextFallback = _clock + 120f;
            _wantLatest = -1;
            // Ist die bekannte Id größer als die des Servers (gelöscht/Server neu), alles neu holen
            _task = TDU2Session.Client.Send(new NetRequest { op = "news", session = TDU2Session.SessionId, since = Latest });
            return;
        }
        if (!_task.IsCompleted) return;
        var r = _task.Status == TaskStatus.RanToCompletion ? _task.Result : null;
        _task = null;
        if (r == null || !r.ok) { if (r != null && r.error != "not_logged_in" && r.error != "unknown_op") Log.Warn("News: " + r.error); return; }
        Apply(r);
    }

    static void Apply(NetResponse r)
    {
        bool changed = false;
        int added = 0;
        if (r.newsLatest < Latest)
        {
            // Server zurückgesetzt (news.json neu): alles verwerfen und von vorn holen
            Items.Clear(); Latest = 0; _wantLatest = int.MaxValue;
            Raise();
            return;
        }
        if (r.newsIds != null)
        {
            var ids = new HashSet<int>(r.newsIds);
            if (Items.RemoveAll(e => !ids.Contains(e.id)) > 0) changed = true;
        }
        if (r.news != null)
            foreach (var e in r.news)
            {
                if (e == null || Items.Exists(x => x.id == e.id)) continue;
                Items.Add(e); added++; changed = true;
            }
        Items.Sort((a, b) => b.id.CompareTo(a.id));
        Latest = r.newsLatest;
        if (!changed) return;
        Log.Info($"News: {Items.Count} Meldungen ({added} neu, {UnreadCount} ungelesen, höchste Id {Latest})");
        Raise();
    }

    static void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn("News: " + ex.Message); }
    }

    // ------------------------------------------------------------------ Bilder (op "newsimg", einzeln, im Speicher gehalten)
    static readonly Dictionary<int, Texture2D> _images = new();
    static readonly HashSet<int> _imageFailed = new();
    static readonly Queue<int> _imageQueue = new();
    static Task<NetResponse>? _imageTask;
    static int _imageTaskId;

    /// <summary>Bild einer Meldung; null = (noch) keins – wird dann im Hintergrund geholt, danach <see cref="Changed"/>.</summary>
    public static Texture2D? Image(NewsEntry? e)
    {
        if (e == null || !e.hasImage) return null;
        if (_images.TryGetValue(e.id, out var t) && t != null) return t;
        if (!_imageFailed.Contains(e.id) && _imageTaskId != e.id && !_imageQueue.Contains(e.id)) _imageQueue.Enqueue(e.id);
        return null;
    }

    static void TickImages()
    {
        if (_imageTask == null)
        {
            if (_imageQueue.Count == 0 || !TDU2Session.LoggedIn || TDU2Session.Offline || TDU2Session.Client == null) return;
            _imageTaskId = _imageQueue.Dequeue();
            _imageTask = TDU2Session.Client.Send(new NetRequest { op = "newsimg", session = TDU2Session.SessionId, target = _imageTaskId });
            return;
        }
        if (!_imageTask.IsCompleted) return;
        var r = _imageTask.Status == TaskStatus.RanToCompletion ? _imageTask.Result : null;
        int id = _imageTaskId;
        _imageTask = null; _imageTaskId = 0;
        if (r == null || !r.ok || string.IsNullOrEmpty(r.newsImage)) { _imageFailed.Add(id); Log.Warn($"News-Bild {id}: {r?.error ?? "keine Antwort"}"); return; }
        var tex = TDU2GameData.DecodeImage(r.newsImage, true);
        if (tex == null) { _imageFailed.Add(id); return; }
        _images[id] = tex;
        Log.Info($"News-Bild {id}: {tex.width}x{tex.height}");
        Raise();
    }
}
