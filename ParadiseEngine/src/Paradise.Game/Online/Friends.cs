using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Game.Gamemodes;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Globalization;

namespace Paradise.Game.Online;

/// <summary>Beziehung zu einem Mitspieler – bestimmt u.a. die Farbe seines Punkts auf Minimap/Karte (Unity PlayerRelation).</summary>
public enum PlayerRelation { Normal = 0, Friend = 1, Club = 2 }

/// <summary>
/// Farben der Mitspieler wie im Spiel (runder Punkt auf der Minimap, wie die Gegner im Rennen) – Unity PlayerColors:
/// Weiß = normaler Spieler, Grün = Freund, Orange = Club-Mitglied.
/// </summary>
public static class PlayerColors
{
    // Unity Color -> Color32 (gerundet): Freund (0.36, 0.86, 0.22), Club (1, 0.55, 0.08)
    public static Color32 Normal = new(255, 255, 255, 255);
    public static Color32 Friend = new(92, 219, 56, 255);
    public static Color32 Club = new(255, 140, 20, 255);
    /// <summary>Gegner im Rennen (KI) – wie normale Spieler weiß.</summary>
    public static Color32 Opponent = new(255, 255, 255, 255);

    public static Color32 Of(PlayerRelation r) => r == PlayerRelation.Friend ? Friend : r == PlayerRelation.Club ? Club : Normal;
}

/// <summary>
/// Freundesliste wie im Spiel (Port von Unity TDU2Friends; Server: op "friend", gespeichert in data/friends.json):
///   * Anfrage senden: Spielerkarte "frre" bzw. "Nach Spieler suchen" (im Original genau dafür; Erfolg = Dialog
///     search_player_ok "Freundschaftsanfrage gesendet").
///   * Empfänger: general.bnk /pop_request (RequestType "friend", Text /:mpc_FriendRequest "RIBOS möchte dein Freund sein"):
///     open_mini -> Enter open_big -> Enter valid_request ("frok") bzw. Rücktaste cancel_request ("frca").
///   * Entfernen: Spielerkarte "delf" (nach dem Bestätigungsdialog remove_friend).
///   * Meldungen für TDU Media (Reiter Community) mit den Original-Texten der Spieldatenbank, je Profil gespeichert.
/// Die Liste füllt <see cref="TDU2GameData.Friends"/> (Pausemenü "Freunde"); <see cref="RelationOf"/> färbt Minimap-Punkte.
/// </summary>
public static class Friends
{
    /// <summary>Freundesliste regelmäßig neu holen (Sekunden) – Online-Status der Freunde.</summary>
    public static float RefreshSeconds = 30f;
    /// <summary>Anfrage-Fenster so lange zeigen (Sekunden); unbeantwortet bleibt die Anfrage gespeichert und kommt später wieder.</summary>
    public static float PopupSeconds = 25f;
    /// <summary>Unbeantwortete Anfrage frühestens nach so vielen Sekunden erneut zeigen.</summary>
    public static float ReshowSeconds = 180f;

    static readonly HashSet<string> _friends = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _pendingOut = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> _incoming = new();
    static readonly Dictionary<string, float> _shownAt = new(StringComparer.OrdinalIgnoreCase);
    static IGameModeContext? _ctx;
    static float _clock;

    static VmfPlayer? General => GeneralOverlay.General;

    // ------------------------------------------------------------------ Abfragen
    public static bool IsFriend(string? nick) => !string.IsNullOrEmpty(nick) && _friends.Contains(nick);
    public static bool RequestSentTo(string? nick) => !string.IsNullOrEmpty(nick) && _pendingOut.Contains(nick);
    public static int IncomingCount => _incoming.Count;
    public static IReadOnlyList<string> Incoming => _incoming;
    public static IReadOnlyCollection<string> All => _friends;

    /// <summary>Beziehung zu einem Spieler (Club vor Freund – wie im Spiel).</summary>
    public static PlayerRelation RelationOf(string? nick)
    {
        if (string.IsNullOrEmpty(nick)) return PlayerRelation.Normal;
        foreach (var c in TDU2GameData.Club) if (c != null && string.Equals(c.Nick, nick, StringComparison.OrdinalIgnoreCase)) return PlayerRelation.Club;
        return IsFriend(nick) ? PlayerRelation.Friend : PlayerRelation.Normal;
    }

    public static Color32 ColorOf(string? nick) => PlayerColors.Of(RelationOf(nick));

    // ------------------------------------------------------------------ Server
    static Task<NetResponse>? _listReq;
    static float _nextRefresh;
    static bool _wasLoggedIn;
    static readonly List<(Task<NetResponse> task, string kind, string nick, Action<NetResponse?>? done)> _ops = new();

    /// <summary>Liste neu holen (sofort, wenn keine Abfrage läuft).</summary>
    public static void Refresh()
    {
        if (!TDU2Session.LoggedIn || TDU2Session.Offline || TDU2Session.Client == null || _listReq != null) { _nextRefresh = 0f; return; }
        _listReq = TDU2Session.Client.Send(new NetRequest { op = "friend", session = TDU2Session.SessionId, kind = "list" });
        _nextRefresh = _clock + RefreshSeconds;
    }

    /// <summary>Freundschaftsanfrage senden. done bekommt die Antwort (ok, friendResult bzw. error) oder null (keine Verbindung).</summary>
    public static void Request(string nick, Action<NetResponse?>? done) => Send("request", nick, done);
    public static void Remove(string nick, Action<NetResponse?>? done) => Send("remove", nick, done);
    public static void Answer(string nick, bool accept) => Send(accept ? "accept" : "decline", nick, null);

    /// <summary>
    /// Kauf eines der beiden Luxushäuser (EPH_L5Y_0362I / EPH_L5Y_6360) allen Freunden melden – wie GM_Estates "buyh" (0xAED241: Meldung
    /// 5164 "Ihr Freund §FRIENDNAME§ hat eine Yacht gekauft!" / 5628). Server: op "friend", kind "house" -> "friendhouse:&lt;nick&gt;".
    /// </summary>
    public static void AnnounceHouse()
    {
        if (!TDU2Session.LoggedIn || TDU2Session.Offline || TDU2Session.Client == null) return;
        _ = TDU2Session.Client.Send(new NetRequest { op = "friend", session = TDU2Session.SessionId, kind = "house" });
    }

    static void Send(string kind, string nick, Action<NetResponse?>? done)
    {
        if (!TDU2Session.LoggedIn || TDU2Session.Offline || TDU2Session.Client == null || string.IsNullOrEmpty(nick)) { done?.Invoke(null); return; }
        var t = TDU2Session.Client.Send(new NetRequest { op = "friend", session = TDU2Session.SessionId, kind = kind, nick = nick });
        _ops.Add((t, kind, nick, done));
    }

    /// <summary>Status vom Server: Meldungen friendreq/friendok/frienddecl/friendrm -> Liste neu holen, Meldung für TDU Media.</summary>
    public static void OnStatus(NetResponse r)
    {
        if (r?.notices == null) return;
        bool refresh = false;
        foreach (var n in r.notices)
        {
            if (string.IsNullOrEmpty(n) || !n.StartsWith("friend", StringComparison.Ordinal)) continue;
            int i = n.IndexOf(':');
            if (i < 0) continue;
            string what = n.Substring(0, i), nick = n.Substring(i + 1);
            refresh = true;
            switch (what)
            {
                case "friendok": AddNews(NewsAccepted, nick); break;                                   // meine Anfrage wurde angenommen
                case "friendhouse": AddNews(NewsHouse, nick); refresh = false; break;                  // Freund hat ein Luxushaus gekauft (exe 0xAED241)
                case "frienddecl": Say(nick + " hat die Freundschaftsanfrage abgelehnt"); break;       // im Spiel ohne Meldung
                case "friendrm": Say(nick + " hat die Freundschaft beendet"); break;                  // im Spiel ohne Meldung
                case "friendreq": _shownAt.Remove(nick); break;   // neue Anfrage -> sofort zeigen (Meldung beim Eintreffen in der Liste)
            }
        }
        if (refresh) Refresh();
    }

    /// <summary>Jedes Bild (TDU2GameData.Update).</summary>
    public static void Update(IGameModeContext? ctx, float dt)
    {
        _ctx = ctx;
        _clock += dt;
        bool li = TDU2Session.LoggedIn && !TDU2Session.Offline;
        if (li && !_wasLoggedIn) Refresh();   // nach dem Anmelden: Liste und offene Anfragen holen
        if (!li && _wasLoggedIn) Clear();
        _wasLoggedIn = li;
        if (li && _listReq == null && _clock >= _nextRefresh) Refresh();
        PollList();
        PollOps();
        UpdatePopup();
    }

    static void Clear()
    {
        _friends.Clear(); _pendingOut.Clear(); _incoming.Clear(); _shownAt.Clear();
        TDU2GameData.Friends.Clear();
        ClosePopup(false);
    }

    static void PollList()
    {
        if (_listReq == null || !_listReq.IsCompleted) return;
        var r = _listReq.Status == TaskStatus.RanToCompletion ? _listReq.Result : null;
        _listReq = null;
        if (r != null && r.ok) Apply(r);
    }

    static void PollOps()
    {
        for (int i = _ops.Count - 1; i >= 0; i--)
        {
            var op = _ops[i];
            if (!op.task.IsCompleted) continue;
            _ops.RemoveAt(i);
            var r = op.task.Status == TaskStatus.RanToCompletion ? op.task.Result : null;
            if (r != null && (r.friends != null || r.friendRequests != null) && (r.ok || r.error == "no_request")) Apply(r);
            if (r != null && r.ok)
            {
                if (op.kind == "request")
                {
                    if (r.friendResult == "became_friends") AddNews(NewsNewFriend, op.nick);
                    else if (r.friendResult == "sent") AddNews(NewsSent, op.nick);
                    else Say(op.nick + ": " + r.friendResult);
                }
                else if (op.kind == "accept") AddNews(NewsNewFriend, op.nick);
                else Say(op.kind + " " + op.nick + " ok");
            }
            else Say(op.kind + " " + op.nick + ": " + ErrorText(r?.error));
            try { op.done?.Invoke(r); } catch (Exception ex) { Log.Warn("Freunde: " + ex.Message); }
        }
    }

    static string ErrorText(string? e) => e switch
    {
        "unknown_player" => "Spieler nicht gefunden.",
        "self" => "Du kannst dich nicht selbst hinzufügen.",
        "full" => "Die Freundesliste ist voll.",
        null or "" => "keine Verbindung zum Server.",
        _ => e,
    };

    static void Apply(NetResponse r)
    {
        _friends.Clear();
        _pendingOut.Clear();
        if (r.friendPending != null) foreach (var n in r.friendPending) if (!string.IsNullOrEmpty(n)) _pendingOut.Add(n);
        var db = Vm?.Database;
        var old = new Dictionary<string, PlayerInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in TDU2GameData.Friends) if (p != null && !string.IsNullOrEmpty(p.Nick)) old[p.Nick] = p;
        var list = new List<PlayerInfo>();
        foreach (var e in r.friends ?? new List<FriendEntry>())
        {
            if (e == null || string.IsNullOrEmpty(e.nick)) continue;
            _friends.Add(e.nick);
            if (!old.TryGetValue(e.nick, out var p)) p = new PlayerInfo { Nick = e.nick };
            // online: denselben Eintrag wie in der Spielerliste benutzen (Position, Foto, Auto)
            if (e.online)
                foreach (var o in TDU2GameData.Players.Values) if (string.Equals(o.Nick, e.nick, StringComparison.OrdinalIgnoreCase)) { p = o; break; }
            p.Online = e.online;
            if (e.id != 0) p.Id = e.id; else if (!e.online) p.Id = 0;
            if (e.nationality > 0) p.Nationality = e.nationality;
            if (e.carId != 0) { p.CarId = e.carId; p.Color = e.color; }
            if (e.money != 0) p.Money = e.money;
            if (e.nbCars != 0) p.NbCars = e.nbCars;
            if (e.online) { p.PhotoVer = e.photoVer; if (!string.IsNullOrEmpty(e.mood)) p.Mood = e.mood; }
            if (!string.IsNullOrEmpty(e.lastSeen) && DateTime.TryParse(e.lastSeen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen))
                p.LastSeen = seen.Kind == DateTimeKind.Utc ? seen.ToLocalTime() : seen;
            if (p.Car == null && db != null && db.ById.TryGetValue(p.CarId, out var c)) p.Car = c;
            list.Add(p);
        }
        // online zuerst, dann nach Name
        list.Sort((a, b) => a.Online != b.Online ? (a.Online ? -1 : 1) : string.Compare(a.Nick, b.Nick, StringComparison.OrdinalIgnoreCase));
        TDU2GameData.Friends.Clear(); TDU2GameData.Friends.AddRange(list);

        var now = new List<string>();
        foreach (var e in r.friendRequests ?? new List<FriendEntry>()) if (e != null && !string.IsNullOrEmpty(e.nick)) now.Add(e.nick);
        foreach (var n in now) if (!_incoming.Exists(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) AddNews(NewsIncoming, n);
        _incoming.Clear(); _incoming.AddRange(now);
        if (_popupNick != null && !_incoming.Exists(x => string.Equals(x, _popupNick, StringComparison.OrdinalIgnoreCase))) ClosePopup(false);
        TDU2GameData.NotifyChanged();
    }

    static Paradise.Core.Vehicles.VehicleManager? Vm => TDU2GameData.Vehicles;

    static void Say(string text) => Log.Info("Freunde: " + text);

    // ------------------------------------------------------------------ Meldungen für TDU Media (Reiter "Freunde & Klub")
    // Texte aus der Spieldatenbank (db_<sprache>.cpr), wie im Spiel; für Ablehnen/Entfernen gibt es dort keine Meldung.
    public const int TxtFriendRequest = 1663;     // "Freundesanfrage"
    public const int TxtWantsFriend = 1464;       // "§PLAYER§ möchte Ihr Freund werden." (exe 0x5B8)
    public const int TxtRequestSent = 7516;       // "Freundschaftsanfrage an §PLAYERNAME§ gesendet."
    public const int TxtNewFriendName = 7519;     // "Neuer Freund: §FRIENDNAME§"
    public const int TxtAccepted = 7520;          // "§FRIENDNAME§ hat deine Freundschaftsanfrage angenommen."
    public const int TxtNewFriend = 3399;         // "Neuer Freund"

    const int NewsIncoming = 0, NewsSent = 1, NewsAccepted = 2, NewsNewFriend = 3, NewsHouse = 4;
    public const int TxtFriendHouse = 5164;       // "Ihr Freund §FRIENDNAME§ hat eine Yacht gekauft!" (exe 0x142C)
    public const int TxtFriendHouseText = 5628;   // "Der hat Klasse! Hoffentlich werden Sie mal eingeladen!" (exe 0x15FC)


    static string Loc(int hash, string fallback) => FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, hash) ?? fallback;

    /// <summary>Meldung als Text (Titel, Inhalt) in der eingestellten Sprache.</summary>
    public static void NewsText(FriendNewsEntry? n, out string title, out string text)
    {
        string nick = n?.nick ?? "";
        string T(int hc, string fb) => Loc(hc, fb).Replace("§PLAYER§", nick).Replace("§PLAYERNAME§", nick).Replace("§FRIENDNAME§", nick);
        switch (n?.kind ?? 0)
        {
            case NewsSent: title = T(TxtFriendRequest, "Friend request"); text = T(TxtRequestSent, "Friend request sent to §PLAYERNAME§."); break;
            case NewsAccepted: title = T(TxtNewFriendName, "New friend: §FRIENDNAME§"); text = T(TxtAccepted, "§FRIENDNAME§ has accepted your friend request."); break;
            case NewsNewFriend: title = T(TxtNewFriend, "New friend"); text = T(TxtNewFriendName, "New friend: §FRIENDNAME§"); break;
            case NewsHouse: title = T(TxtFriendHouse, "Your friend §FRIENDNAME§ bought a yacht!"); text = T(TxtFriendHouseText, "Classy! Hopefully you'll get invited!"); break;
            default: title = T(TxtFriendRequest, "Friend request"); text = T(TxtWantsFriend, "§PLAYER§ wants to be your friend"); break;
        }
    }

    public static DateTime NewsDate(FriendNewsEntry? n) =>
        n != null && DateTime.TryParse(n.date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : DateTime.Now;

    const int MaxNews = 30;
    static readonly List<FriendNewsEntry> _noNews = new();

    /// <summary>Meldungen des Profils im Spielstand (SaveData.friendNews, Original-DATA als Umgebungsvariable U3D_FriendNews).</summary>
    static List<FriendNewsEntry> NewsList
    {
        get
        {
            var s = TDU2Session.Save;
            if (s == null) { _noNews.Clear(); return _noNews; }
            return s.friendNews ??= new List<FriendNewsEntry>();
        }
    }

    /// <summary>Meldungen, neueste zuerst.</summary>
    public static IReadOnlyList<FriendNewsEntry> News => NewsList;
    public static bool HasUnreadNews { get { foreach (var n in NewsList) if (n != null && !n.read) return true; return false; } }

    public static void MarkNewsRead()
    {
        bool ch = false;
        foreach (var n in NewsList) if (n != null && !n.read) { n.read = true; ch = true; }
        if (ch) SaveNews();
    }

    static void AddNews(int kind, string nick)
    {
        if (string.IsNullOrEmpty(nick) || string.IsNullOrEmpty(TDU2Session.Nick)) return;
        if (TDU2Session.Save == null) return;
        var l = NewsList;
        // dieselbe Meldung nicht doppelt (z.B. Anfrage, die beim nächsten Anmelden noch offen ist)
        if (kind == NewsIncoming && l.Exists(x => x.kind == NewsIncoming && string.Equals(x.nick, nick, StringComparison.OrdinalIgnoreCase))) return;
        l.Insert(0, new FriendNewsEntry { kind = kind, nick = nick, date = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
        if (l.Count > MaxNews) l.RemoveRange(MaxNews, l.Count - MaxNews);
        NewsText(l[0], out var t, out var x);
        Say(t + " – " + x);
        SaveNews();
        try { NewsChanged?.Invoke(); } catch (Exception ex) { Log.Warn("Freunde: " + ex.Message); }
    }

    /// <summary>Neue Meldung (TDU Media aktualisiert eine offene Liste).</summary>
    public static event Action? NewsChanged;

    static void SaveNews() => SessionAutosave.Schedule(2f);

    // ------------------------------------------------------------------ Anfrage-Fenster (general.bnk /pop_request)
    static string? _popupNick;
    static bool _popupBig;
    static float _popupUntil, _bigAt;
    static VmfPlayer? _hooked;
    static string? _reqTemplate;

    static bool CanShowPopup()
    {
        var g = General;
        if (g == null || _ctx == null || _ctx.Paused || Chat.ChatTyping) return false;
        var m = GameMode.Current;
        // Unity: nicht bei offener Karte, nicht während Kleider-/Friseur-/Sticker-Laden (Busy), nicht während Zwischensequenzen
        // (Zwischensequenzen gibt es in der Engine noch nicht – Checkliste 26)
        if (m is Gamemode.GAME_MODE_MAP or Gamemode.GAME_MODE_LOGIN or Gamemode.GAME_MODE_DRESSING or Gamemode.GAME_MODE_HAIRDRESSER or Gamemode.GAME_MODE_STICKERS) return false;
        if (Party.PopupOpen) return false;
        return g.Find("/pop_request") != null;
    }

    static void UpdatePopup()
    {
        if (_popupNick == null)
        {
            if (_incoming.Count == 0 || !CanShowPopup()) return;
            foreach (var n in _incoming)
            {
                if (_shownAt.TryGetValue(n, out float at) && _clock - at < ReshowSeconds) continue;
                ShowPopup(n);
                break;
            }
            return;
        }
        var g = General;
        if (g == null) { ClosePopup(false); return; }
        if (_clock > _popupUntil) { ClosePopup(true); return; }   // unbeantwortet: bleibt gespeichert
        if (Chat.ChatTyping || _ctx == null || _ctx.Paused) return;
        if (TDU2Controls.ConfirmPressed())
        {
            if (!_popupBig) { OpenBig(); if (!g.CallClip("/pop_request", "open_big")) g.GotoLabel("/pop_request", "req_friend", true); }
            else if (_clock - _bigAt >= 0.5f)   // erst ansehen, dann (zweites Enter) annehmen
            {
                if (!g.CallClip("/pop_request", "valid_request")) Reply(true);
            }
        }
        else if (TDU2Controls.BackPressed())
        {
            if (!g.CallClip("/pop_request", "cancel_request")) Reply(false);
        }
    }

    static void ShowPopup(string nick)
    {
        var g = General!;
        Hook(g);
        _popupNick = nick; _popupBig = false; _bigAt = -1f;
        _popupUntil = _clock + PopupSeconds;
        _shownAt[nick] = _clock;
        // Kleines Fenster nur per Tastatur/Pad (ein beliebiger Mausklick sollte nichts annehmen), Maus erst in der großen Ansicht
        g.MouseIgnore.Add("/pop_request");
        g.Show("/pop_request");
        g.SetVariable("/:RequestType", "friend");
        g.SetValue("/:RequestForbidden", 0);
        g.SetVariable("/:AvatarName", nick);
        // Ausweis des Anfragenden wie im Spiel (exe 0x998fd0/0x999556: pop_request/disp/player_IDCard – Flagge, Foto …)
        GeneralOverlay.FillIdCard("pop_request", RequestPlayer(nick));
        _reqTemplate ??= g.GetVariable("/:mpc_FriendRequest") ?? "";
        g.SetVariable("/:mpc_FriendRequest", FillNick(_reqTemplate, nick));
        g.GotoLabel("/pop_request", "open_mini", true);
        Say(nick + " möchte befreundet sein (Fenster offen)");
    }

    static readonly Dictionary<string, PlayerInfo> _requesters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Spieler einer Anfrage: online aus der Spielerliste, sonst aus Zuletzt gesehen bzw. ein Eintrag nur mit Namen (Profil per Name).</summary>
    static PlayerInfo RequestPlayer(string nick)
    {
        foreach (var o in TDU2GameData.Players.Values) if (string.Equals(o.Nick, nick, StringComparison.OrdinalIgnoreCase)) return o;
        foreach (var o in TDU2GameData.ListFor("recent_players")) if (string.Equals(o.Nick, nick, StringComparison.OrdinalIgnoreCase)) return o;
        if (!_requesters.TryGetValue(nick, out var p)) _requesters[nick] = p = new PlayerInfo { Nick = nick };
        return p;
    }

    static void OpenBig()
    {
        _popupBig = true; _bigAt = _clock;
        General?.MouseIgnore.Remove("/pop_request");
    }

    /// <summary>Spielername in "_RIBOS wants to be your friend." (Lokalisierung) einsetzen.</summary>
    static string FillNick(string template, string nick)
    {
        if (string.IsNullOrEmpty(template)) return nick + " möchte dein Freund sein.";
        foreach (var tok in new[] { "RIBOS", "Ribos", "%s", "%1$s", "%1", "[NAME]", "<NAME>", "{0}" })
            if (template.Contains(tok)) return template.Replace(tok, nick);
        return nick + ": " + template;
    }

    static void Hook(VmfPlayer? g)
    {
        if (ReferenceEquals(g, _hooked)) return;
        if (_hooked != null) _hooked.FSCommand -= OnGeneralCommand;
        _hooked = g;
        if (g != null) g.FSCommand += OnGeneralCommand;
    }

    static void OnGeneralCommand(string cmd, string args)
    {
        if (_popupNick == null) return;
        switch ((cmd ?? "").Trim().ToLowerInvariant())
        {
            case "gein": case "opbg": if (!_popupBig) OpenBig(); break;
            case "frok":
                if (_popupBig && _clock - _bigAt >= 0.5f) Reply(true);
                else { var g = General; g?.Show("/pop_request"); g?.GotoLabel("/pop_request", _popupBig ? "req_friend" : "open_mini", true); }
                break;
            case "frca": Reply(false); break;
        }
    }

    static void Reply(bool accept)
    {
        string? nick = _popupNick;
        _popupNick = null;
        General?.MouseIgnore.Remove("/pop_request");
        if (nick == null) return;
        _incoming.RemoveAll(x => string.Equals(x, nick, StringComparison.OrdinalIgnoreCase));
        Answer(nick, accept);
        if (!accept) Say("Anfrage von " + nick + " abgelehnt");
    }

    static void ClosePopup(bool animate)
    {
        if (_popupNick == null) return;
        var g = General;
        if (g != null)
        {
            g.MouseIgnore.Remove("/pop_request");
            if (animate) g.GotoLabel("/pop_request", _popupBig ? "close_friend" : "close_mini", true);
        }
        _popupNick = null;
    }
}
