using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Game.Gamemodes;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Numerics;

namespace Paradise.Game.Online;

/// <summary>
/// Mitspieler beitreten und einladen – Port von Unity TDU2Party, Werte und Ablauf der Einladung nach dem Original
/// (unpacked.exe 0x998580–0x99a420, general.bnk /pop_gameInvite):
///   * Beitreten (Spielerkarte pcrd:pjoi, join:wait): hinter den Mitspieler; aus einem Interieur heraus wird es vorher verlassen.
///   * Spiel-Einladung (pcrd:pinv): beim Eingeladenen /pop_gameInvite "open_mini" (exe 0x999bf0): /:m_InviteRequest = Text 0xE81
///     "§PLAYERNAME§ lädt Sie zum Beitritt ein", /:AvatarName, /:WhatDistrict (Gebiet des Einladenden h1…h4/i1…i2, "casino"),
///     /:InviteForbidden, /:InvitePlace (0xE84 "Freie Fahrt", 0xE82 "Zu Hause (§HOUSENAME§)", 0xE85 "Geschäft (§SHOPNAME§)",
///     0xE86 "Ort (§SPOTNAME§)", 0x104C "Casino"). Sichtbar 10 s (exe 0x147e684), dann zu. "opbg" = große Ansicht (Foto/Flagge),
///     "giok" = annehmen, "gica" = ablehnen. Annehmen bringt den Gast zum Einladenden (Interieur/Haus bzw. hinter sein Auto).
///   * Antwort beim Einladenden (exe 0x998c80): eigenes offenes Einladungsfenster "cancel_invite", /:IsInvitAccepted (1/0),
///     /pop_gameInvite "open_answer", nach 10 s "close_answer". Der Server meldet die Antwort sofort (UDP [12] -> status).
///   * Beifahrer (codriving): HUD /HUD/contact (Label "invite", WhatContact "codriving"); acpt/rjct; Ergebnis accept/cancel.
///   * Mitfahrt: eigenes Auto weg, Kamera am Auto des Fahrers (außen bzw. Taste C Beifahrersitz, rechte Maus umsehen), ich als
///     Beifahrer-Avatar in seinem Auto; F = aussteigen (eigenes Auto neben ihm). Der Fahrer sieht seinen Beifahrer.
/// </summary>
public static class Party
{
    /// <summary>Abstand hinter dem Mitspieler beim Beitreten (m, Unity JoinDistance).</summary>
    public static float JoinDistance = 12f;
    /// <summary>Einladungsfenster offen (s) – Original 10 s (exe 0x99a07a: 0x147e684 = 10,0). Unity hatte 25 s.</summary>
    public static float InviteSeconds = 10f;
    /// <summary>Antwort beim Einladenden sichtbar (s) – Original 10 s (exe 0x998d56). Unity hatte 3,5 s.</summary>
    public static float AnswerSeconds = 10f;

    /// <summary>Ich fahre als Beifahrer mit (Spielernummer des Fahrers, 0 = nein).</summary>
    public static int RideWith { get; private set; }
    /// <summary>Mein Beifahrer (0 = keiner).</summary>
    public static int Passenger { get; private set; }
    public static bool IsPassenger => RideWith != 0;
    /// <summary>Gerade ein Einladungsfenster offen (andere Fenster wie die Freundschaftsanfrage warten).</summary>
    public static bool PopupOpen => _popup != null;
    /// <summary>Ich bin Gast im Haus von Spieler … (0 = nein).</summary>
    public static int HomeHost { get; private set; }
    /// <summary>Meine Gäste im Haus.</summary>
    public static readonly HashSet<int> HomeGuests = new();
    /// <summary>Gehört der Spieler zu meiner Gruppe (Mitfahrt, Haus-Einladung)? Häuser zeigen nur diese Mitspieler.</summary>
    public static bool InGroup(int id) => id != 0 && (RideWith == id || Passenger == id || HomeHost == id || HomeGuests.Contains(id));

    static IGameModeContext? _ctx;
    static float _time;
    static VehicleManager? Vm => _ctx?.Vehicles?.Manager;

    static void Say(string text) => Log.Info("Party: " + text);   // Unity Say(): Ausgabe abgeschaltet (keine eigenen Overlays)

    // ================================================================== Beitreten
    /// <summary>Kann man dem Spieler beitreten? <paramref name="error"/> = Grund (Spielerkarte JoinError).</summary>
    public static bool CanJoin(int id, out string error)
    {
        error = "";
        if (!TDU2GameData.Online) { error = "Nicht mit dem Server verbunden."; return false; }
        if (!TDU2GameData.Players.TryGetValue(id, out var p) || !p.Online) { error = "Spieler ist nicht online."; return false; }
        if (RideWith != 0) { error = "Du fährst gerade als Beifahrer mit."; return false; }
        if (!p.SameIsland) { error = p.Nick + " ist auf der anderen Insel."; return false; }
        var rc = RemoteCars.Get(id);
        bool live = rc != null && rc.HasVis && rc.CarId > 0;   // nur ein Auto, das gerade fährt (im Interieur kommen Zustände ohne Auto)
        // im Interieur meldet der Mitspieler die Einfahrt des Spots als Position (Multiplayer.SendState) – dorthin beitreten
        if (!live && (!p.HasPos || p.GamePos.LengthSquared() < 1f)) { error = p.Nick + " ist gerade nicht unterwegs."; return false; }
        var vm = Vm;
        if (vm == null || (vm.Current == null && !InteriorUtils.Inside)) { error = "Kein eigenes Auto."; return false; }
        return true;
    }

    /// <summary>Hinter den Spieler setzen (aus einem Interieur heraus mit dem zuletzt gefahrenen Auto).</summary>
    public static bool Join(int id)
    {
        if (!CanJoin(id, out var err)) { Say(err); return false; }
        if (!TargetBehind(id, JoinDistance, out var pos, out var fwd)) { Say("Position des Spielers unbekannt."); return false; }
        var vm = Vm!;
        if (InteriorUtils.Inside)
        {
            // Unity: ExitInterior(car, false) + SpawnCar – richtig verlassen (Ladebildschirm, Spielmodus zurück), Auto hinter den Spieler
            var car = InteriorUtils._lastCar ?? vm.FindCar(vm.StartCar);
            InteriorUtils.ExitInterior(car);
            if (car != null) vm.SpawnCar(car.FileName, pos, fwd);
        }
        else if (vm.Current != null) vm.TeleportSafe(pos, fwd);   // wartet, bis dort der Boden geladen ist
        else return false;
        Say("Beigetreten: " + TDU2GameData.Player(id).Nick);
        return true;
    }

    /// <summary>Punkt <paramref name="back"/> m hinter dem Auto des Spielers (Spielraum), Blick in seine Fahrtrichtung.</summary>
    static bool TargetBehind(int id, float back, out Vector3 pos, out Vector3 fwd)
    {
        pos = default; fwd = Vector3.UnitZ;
        Vector3 at, f;
        var r = RemoteCars.Get(id);
        if (r != null && r.HasVis && r.CarId > 0) { at = r.VisPos; f = Vector3.Transform(Vector3.UnitZ, r.VisRot); }
        else
        {
            if (!TDU2GameData.Players.TryGetValue(id, out var p) || !p.HasPos) return false;
            at = p.GamePos; f = p.GameForward;
        }
        if (at.LengthSquared() < 1f || !float.IsFinite(at.X)) return false;   // keine Position (0,0,0 = außerhalb der Insel)
        f.Y = 0f;
        fwd = f.LengthSquared() < 1e-4f ? Vector3.UnitZ : Vector3.Normalize(f);
        pos = at - fwd * back;
        return true;
    }

    // ================================================================== Einladen
    static Task<NetResponse>? _inviteTask;
    static readonly Dictionary<int, string> _sentKinds = new();

    /// <summary>
    /// Spiel-Einladung (Spielerkarte "Einladen"): der Eingeladene kommt zu mir – ins selbe Interieur/Haus ("room|SPOT|Bank|Ort")
    /// bzw. hinter mein Auto ("game|"). "Ort" = Text für /:InvitePlace wie im Original (0xE82/0xE85/0xE86/0x104C).
    /// </summary>
    public static void Invite(int id)
    {
        if (!TDU2Session.LoggedIn || TDU2Session.Client == null) { Say("Nicht mit dem Server verbunden."); return; }
        if (RideWith != 0) { Say("Du fährst gerade als Beifahrer mit."); return; }
        string kind;
        var spot = InteriorUtils.CurrentSpot;
        if (InteriorUtils.Inside && spot != null)
        {
            string stem = InteriorUtils.CurrentBankStem;
            if (stem.Length == 0) { Say("Hierher kann man niemanden einladen."); return; }
            kind = "room|" + spot.Code + "|" + stem + "|" + Clean(InteriorUtils.InvitePlaceText());
        }
        else if (Vm?.Current != null) kind = "game|";
        else { Say("Zum Einladen musst du unterwegs oder in einem Gebäude sein."); return; }
        _sentKinds[id] = kind;
        _inviteTask = TDU2Session.Client.Send(new NetRequest { op = "invite", session = TDU2Session.SessionId, target = id, kind = kind });
        Say("Einladung an " + TDU2GameData.Player(id).Nick + " gesendet (" + kind + ").");
    }

    static string Clean(string s) => (s ?? "").Replace("|", "/");

    /// <summary>Spieler als Beifahrer einladen (Server leitet weiter, Antwort kommt über "status").</summary>
    public static void InviteRide(int id)
    {
        if (!TDU2Session.LoggedIn || TDU2Session.Client == null) { Say("Nicht mit dem Server verbunden."); return; }
        if (RideWith != 0) { Say("Du fährst gerade als Beifahrer mit."); return; }
        if (Passenger != 0) { Say("Du hast schon einen Beifahrer."); return; }
        if (Vm?.Current == null) { Say("Zum Einladen musst du im Auto sitzen."); return; }
        _sentKinds[id] = "codriving";
        _inviteTask = TDU2Session.Client.Send(new NetRequest { op = "invite", session = TDU2Session.SessionId, target = id, kind = "codriving" });
        Say("Beifahrer-Einladung an " + TDU2GameData.Player(id).Nick + " gesendet.");
    }

    static void PollInvite()
    {
        if (_inviteTask == null || !_inviteTask.IsCompleted) return;
        var r = _inviteTask.Status == TaskStatus.RanToCompletion ? _inviteTask.Result : null;
        _inviteTask = null;
        if (r != null && r.ok) return;
        string e = r?.error ?? "";
        Say("Einladung nicht möglich: " + (e == "other_island" ? "andere Insel" : e == "busy" ? "Spieler fährt schon mit" : e == "unknown_player" ? "Spieler nicht online" :
            e == "has_passenger" ? "du hast schon einen Beifahrer" : e == "is_passenger" ? "du fährst selbst mit" : e));
    }

    // ================================================================== Status vom Server (TDU2GameData.PollStatus)
    static readonly HashSet<int> _shownInvites = new();
    static readonly Queue<InviteEntry> _pendingInvites = new();
    static int _leftDriver; static float _leftAt = -100f;

    public static void OnStatus(NetResponse r)
    {
        if (r == null) return;
        var list = r.invites ?? new List<InviteEntry>();
        foreach (var inv in list)
            if (inv != null && inv.from != 0 && _shownInvites.Add(inv.from)) _pendingInvites.Enqueue(inv);
        // Einladungen, die der Server nicht mehr kennt, dürfen wiederkommen
        var still = new HashSet<int>();
        foreach (var inv in list) if (inv != null) still.Add(inv.from);
        _shownInvites.RemoveWhere(f => !still.Contains(f) && (_popup == null || _popup.from != f));

        foreach (var n in r.notices ?? new List<string>())
        {
            var parts = (n ?? "").Split(':', 3);
            if (parts.Length < 3) continue;
            int.TryParse(parts[1], out int pid);
            string nick = parts[2];
            switch (parts[0])
            {
                case "accepted": Say(nick + " fährt bei dir mit."); HudResult(true, nick); break;
                case "joined":
                    Say(nick + " hat die Einladung angenommen und kommt zu dir.");
                    if (_sentKinds.TryGetValue(pid, out var k) && IsHouseKind(k)) HomeGuests.Add(pid);
                    GameAnswer(true, pid, nick);
                    break;
                case "declined":
                    Say(nick + " hat die Einladung abgelehnt.");
                    if (_sentKinds.TryGetValue(pid, out var dk) && dk != "codriving") GameAnswer(false, pid, nick); else HudResult(false, nick);
                    break;
                case "rideend": Say("Mitfahrt mit " + nick + " beendet."); break;
                case "news":   // Server-Konsole "say <Text>": Meldung an alle (Flash-Fenster pop_news des General-Films)
                    Chat.AddChatLine("Server", nick, 0);
                    if (GeneralOverlay.General != null) GeneralOverlay.SendNews(nick);
                    else Log.Info("Server: " + nick);
                    break;
            }
        }
        // Mitfahrt (Server ist maßgeblich)
        if (r.rideWith != 0 && r.rideWith == _leftDriver && _time - _leftAt < 6f) { }   // Antwort von vor dem Aussteigen
        else if (r.rideWith != RideWith)
        {
            if (r.rideWith != 0) BeginRide(r.rideWith);
            else if (RideWith != 0) EndRideLocal(false);
        }
        if (r.passenger != Passenger) { Passenger = r.passenger; UpdatePassengerAvatar(true); }
    }

    // ================================================================== Beifahrer-Einladung: HUD (hud.vmf /HUD/contact)
    static InviteEntry? _popup;
    static bool _popupGame, _gameBig;
    static float _popupUntil, _bigAt = -1f, _answerCloseAt = -1f, _acceptAt = -1f, _declineAt = -1f;
    static VmfPlayer? _hud, _hudHooked, _generalHooked;

    static VmfPlayer? Hud
    {
        get
        {
            var h = Vm?.Minimap?.Hud;
            if (!ReferenceEquals(h, _hudHooked))
            {
                if (_hudHooked != null) _hudHooked.FSCommand -= OnHudCommand;
                _hudHooked = h;
                if (h != null) h.FSCommand += OnHudCommand;
            }
            _hud = h;
            return h;
        }
    }

    static VmfPlayer? General
    {
        get
        {
            var g = GeneralOverlay.General;
            if (!ReferenceEquals(g, _generalHooked))
            {
                if (_generalHooked != null) _generalHooked.FSCommand -= OnGeneralCommand;
                _generalHooked = g;
                if (g != null) g.FSCommand += OnGeneralCommand;
            }
            return g;
        }
    }

    static bool CanShowPopup()
    {
        if (Vm?.Current == null || Hud == null || InteriorUtils.Inside) return false;
        if (_ctx == null || _ctx.Paused || GameMode.Current == Gamemode.GAME_MODE_MAP || RideWith != 0) return false;
        return true;
    }

    static void ShowInvite(InviteEntry inv)
    {
        var h = Hud!;
        _popup = inv; _popupGame = false;
        _popupUntil = _time + InviteSeconds;
        h.SetVariable("/:WhatContact", "codriving");
        h.SetValue("/:nb_players", 1);
        h.SetVariable("/HUD/contact/opponents/p1:name", inv.nick);
        h.GotoLabel("/HUD/contact", "invite", true);
        Say(inv.nick + " lädt dich als Beifahrer ein");
    }

    static void HudResult(bool accepted, string nick)
    {
        var h = Hud;
        if (h == null) return;
        h.SetVariable("/:WhatContact", "codriving");
        h.SetValue("/:nb_players", 1);
        h.SetVariable("/HUD/contact/opponents/p1:name", nick);
        h.GotoLabel("/HUD/contact", accepted ? "accept" : "cancel", true);
    }

    static void OnHudCommand(string cmd, string args)
    {
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (_popup == null || _popupGame) return;
        switch (c)
        {
            case "acpt": Reply(true); break;
            case "rjct": case "cncl": Reply(false); break;
        }
    }

    // ================================================================== Antworten
    static Task<NetResponse>? _replyTask;
    static int _replyFrom; static bool _replyAccept; static InviteEntry? _replyInv;

    static void Reply(bool accept)
    {
        var inv = _popup;
        _popup = null;
        General?.MouseIgnore.Remove("/pop_gameInvite");
        if (inv == null || TDU2Session.Client == null) return;
        _replyTask = TDU2Session.Client.Send(new NetRequest { op = "invreply", session = TDU2Session.SessionId, target = inv.from, accept = accept });
        _replyFrom = inv.from; _replyAccept = accept; _replyInv = inv;
        if (!accept) Say("Einladung von " + inv.nick + " abgelehnt.");
    }

    static void PollReply()
    {
        if (_replyTask == null || !_replyTask.IsCompleted) return;
        var r = _replyTask.Status == TaskStatus.RanToCompletion ? _replyTask.Result : null;
        _replyTask = null;
        if (!_replyAccept) return;
        if (r != null && r.ok && !string.IsNullOrEmpty(r.inviteKind) && !r.inviteKind.StartsWith("codriving")) { Arrive(_replyFrom, _replyInv, r.inviteKind, r.save); return; }
        if (r != null && r.ok && r.rideWith != 0) { Hud?.GotoLabel("/HUD/contact", "accept", true); BeginRide(r.rideWith); }
        else Say("Mitfahren nicht möglich" + (r != null && !string.IsNullOrEmpty(r.error) ? " (" + r.error + ")" : "") + ".");
    }

    // ================================================================== Spiel-Einladung (general.bnk /pop_gameInvite, exe 0x999bf0)
    static bool IsHouseKind(string? kind)
    {
        var p = (kind ?? "").Split('|');
        return p.Length > 1 && p[0] == "room" && p[1].StartsWith("EPH", StringComparison.OrdinalIgnoreCase) && !p[1].StartsWith("EPHOT", StringComparison.OrdinalIgnoreCase);
    }

    static bool CanShowGamePopup()
    {
        if (General == null || _ctx == null || _ctx.Paused || RideWith != 0) return false;
        var m = GameMode.Current;
        // Original (exe 0x999c06…0x999cb6): nicht in Menüs/Läden, nicht während Ladebildschirm/Rennen
        if (m is Gamemode.GAME_MODE_MAP or Gamemode.GAME_MODE_LOGIN or Gamemode.GAME_MODE_GOODIES or Gamemode.GAME_MODE_HAIRDRESSER or Gamemode.GAME_MODE_SURGERY
            or Gamemode.GAME_MODE_DRESSING or Gamemode.GAME_MODE_STICKERS or Gamemode.GAME_MODE_PAUSE) return false;
        if (LoadingScreen.Visible) return false;
        return true;
    }

    static void ShowGameInvite(InviteEntry inv)
    {
        var g = General!;
        _popup = inv; _popupGame = true; _gameBig = false; _bigAt = -1f;
        _popupUntil = _time + InviteSeconds;
        // Das kleine Fenster hat einen Maus-Button, der sofort annimmt (valid_invite) – ein beliebiger Klick nahm so die Einladung an.
        // Kleine Ansicht nur per Tastatur; Maus erst in der großen Ansicht (Unity).
        g.MouseIgnore.Add("/pop_gameInvite");
        var parts = (inv.kind ?? "").Split('|');
        var host = TDU2GameData.Players.TryGetValue(inv.from, out var hp) ? hp : null;
        g.Show("/pop_gameInvite");
        // exe 0x999cd1: Text 0xE81 mit §PLAYERNAME§
        g.SetVariable("/:m_InviteRequest", Loc(0xE81, "§PLAYERNAME§ lädt Sie zum Beitritt ein").Replace("§PLAYERNAME§", inv.nick));
        g.SetVariable("/:AvatarName", inv.nick);
        // exe 0x99999f: Ausweis des Einladenden (Flagge, Titel, Online-Symbol, MyTDULife-Foto); ohne Spieler Flagge Bild 135
        GeneralOverlay.FillIdCard("pop_gameInvite", host);
        // exe 0x999d9b: Casino -> "casino", sonst Gebiet des Einladenden (Plakette icon/bkgd: erster Buchstabe h/i)
        g.SetVariable("/:WhatDistrict", District(host, parts));
        // exe 0x999e0d: Grund, warum man nicht beitreten kann (leer = erlaubt) -> /:InviteForbidden, /:m_InviteAvert
        string forbid = ForbiddenReason(inv, host);
        g.SetValue("/:InviteForbidden", forbid.Length > 0);
        if (forbid.Length > 0) g.SetVariable("/:m_InviteAvert", forbid);
        g.SetValue("/:InviteAccepted", 0);
        // exe 0x999e7e…0x99a060: Ort – unterwegs 0xE84 "Freie Fahrt", sonst der Text des Einladenden (Haus/Geschäft/Ort/Casino)
        string place = parts[0] == "room" ? (parts.Length > 3 ? parts[3] : "") : Loc(0xE84, "Freie Fahrt");
        g.SetVariable("/:InvitePlace", place);
        g.GotoLabel("/pop_gameInvite", "open_mini", true);
        Say(inv.nick + " lädt dich ein (" + place + ")");
    }

    /// <summary>Text der Sprachdatenbank (db_&lt;lang&gt;.cpr) über den HashCode.</summary>
    static string Loc(int hash, string fallback) => FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, hash) ?? fallback;

    static string District(PlayerInfo? host, string[] parts)
    {
        if (parts.Length > 1 && parts[0] == "room" && parts[1].StartsWith("ECAS", StringComparison.OrdinalIgnoreCase)) return "casino";
        var menu = ((GAME_MODE_PAUSE)GameModes.Get(Gamemode.GAME_MODE_PAUSE)).Menu;
        Vector3? at = null;
        var r = host != null ? RemoteCars.Get(host.Id) : null;
        if (r != null && r.HasVis) at = r.VisPos;
        else if (host != null && host.HasPos) at = host.GamePos;
        string? a = at.HasValue && menu != null ? menu.AreaAt(at.Value.X, at.Value.Z) : null;
        return a ?? (host != null && host.Island == 2 ? "h1" : "i1");
    }

    /// <summary>
    /// Original exe 0x418f16 (Einladender, Ort) -> Hinweistext, falls Beitreten nicht geht. Engine: andere Insel bzw. ich fahre als
    /// Beifahrer mit (die übrigen Gründe des Originals – Rennen, Clubs – gibt es hier noch nicht).
    /// </summary>
    static string ForbiddenReason(InviteEntry inv, PlayerInfo? host)
    {
        if (RideWith != 0) return "Du fährst gerade als Beifahrer mit.";
        if (host != null && !host.SameIsland) return host.Nick + " ist auf der anderen Insel.";
        return "";
    }

    static void UpdateGamePopup()
    {
        var g = General;
        if (g == null) { Reply(false); return; }
        if (_time > _popupUntil)
        {
            // exe 0x99a1c8: Zeit abgelaufen -> close_mini (große Ansicht close_big), Einladung gilt als abgelehnt
            g.GotoLabel("/pop_gameInvite", _gameBig ? "close_big" : "close_mini", true);
            Reply(false);
            return;
        }
        if (Chat.ChatTyping || _ctx == null || _ctx.Paused) return;
        if (TDU2Controls.ConfirmPressed())
        {
            if (!_gameBig) { OpenBig(); if (!g.CallClip("/pop_gameInvite", "open_big")) g.GotoLabel("/pop_gameInvite", "open_big", true); }
            else if (_time - _bigAt >= 0.5f)   // erst ansehen, dann (zweites Enter) annehmen
            {
                if (!g.CallClip("/pop_gameInvite", "valid_invite")) Reply(true);
                _acceptAt = _time + 1.5f;   // falls "giok" nicht kommt
            }
        }
        else if (TDU2Controls.BackPressed())
        {
            if (!g.CallClip("/pop_gameInvite", "B")) { g.GotoLabel("/pop_gameInvite", _gameBig ? "close_big" : "close_mini", true); Reply(false); }
            _declineAt = _time + 1f;
        }
    }

    static void OnGeneralCommand(string cmd, string args)
    {
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (_popup == null || !_popupGame) return;
        switch (c)
        {
            case "gein": case "opbg": if (!_gameBig) OpenBig(); break;   // exe 0x999957: "opbg" -> große Ansicht
            case "giok":                                                   // exe 0x99994c: annehmen
                if (_gameBig && _time - _bigAt >= 0.5f) Reply(true);
                else { var g = General; g?.Show("/pop_gameInvite"); g?.GotoLabel("/pop_gameInvite", _gameBig ? "open_big" : "open_mini", true); }
                break;
            case "gica": Reply(false); break;                              // exe 0x999941: ablehnen
        }
    }

    /// <summary>Große Ansicht (exe 0x99996a): Zeit läuft weiter, Ausweis des Einladenden (Flagge, Foto).</summary>
    static void OpenBig()
    {
        _gameBig = true; _bigAt = _time;
        var g = General;
        if (g == null) return;
        g.MouseIgnore.Remove("/pop_gameInvite");
        var host = _popup != null && TDU2GameData.Players.TryGetValue(_popup.from, out var hp) ? hp : null;
        // exe 0x999a3f: ohne Spielerdaten Bild 0x87 der Flagge; sonst Flagge = Bild Ländernummer
        g.SetFrame("/pop_gameInvite/disp/player_IDCard/flag", host != null && host.Nationality > 0 ? host.Nationality : 0x87);
    }

    /// <summary>Antwort beim Einladenden (exe 0x998c80): open_answer mit /:IsInvitAccepted, 10 s später close_answer.</summary>
    static void GameAnswer(bool accepted, int pid, string nick)
    {
        var g = General;
        if (g == null) return;
        // eigenes offenes Einladungsfenster zuerst abbrechen (exe 0x998c87: "cancel_invite")
        if (_popup != null && _popupGame) { g.CallClip("/pop_gameInvite", "cancel_invite"); Reply(false); }
        g.Show("/pop_gameInvite");
        // Ausweis des Antwortenden (exe 0x999a9d: wie beim Einladen – Flagge, Titel, Online-Symbol, Foto)
        g.SetVariable("/:AvatarName", nick);
        GeneralOverlay.FillIdCard("pop_gameInvite", TDU2GameData.Players.TryGetValue(pid, out var ap) ? ap : null);
        g.SetValue("/:IsInvitAccepted", accepted ? 1 : 0);
        g.GotoLabel("/pop_gameInvite", "open_answer", true);
        _answerCloseAt = _time + AnswerSeconds;
    }

    /// <summary>Angenommen: zum Einladenden (Interieur/Haus bzw. hinter sein Auto).</summary>
    static void Arrive(int from, InviteEntry? inv, string kind, SaveData? hostSave)
    {
        string nick = inv != null ? inv.nick : TDU2GameData.Player(from).Nick;
        var parts = (kind ?? "").Split('|');
        if (parts[0] == "room" && parts.Length >= 3)
        {
            Spot? spot = null;
            var spots = _ctx?.Terrain?.Streamer?.Spots?.Spots;
            if (spots != null) foreach (var sp in spots) if (string.Equals(sp.Code, parts[1], StringComparison.OrdinalIgnoreCase)) { spot = sp; break; }
            if (spot == null) { Say("Ort der Einladung nicht gefunden (" + parts[1] + ")."); return; }
            if (!InteriorUtils.EnterSpotRoom(spot, parts[2])) { Say("Ort der Einladung kann nicht betreten werden."); return; }
            if (IsHouseKind(kind))
            {
                // nach dem Betreten (EnterSpot verlässt ein altes Interieur und setzt die Gast-Daten zurück); die Szene baut sich erst
                // in den nächsten Bildern auf und liest dann Garage und Einrichtung
                HomeHost = from;
                InteriorUtils.GuestHost = from;
                if (hostSave != null)
                {
                    var cars = new List<string>();
                    if (!string.IsNullOrEmpty(hostSave.currentCar)) cars.Add(hostSave.currentCar);
                    foreach (var c in hostSave.ownedCars ?? new List<string>()) if (!string.IsNullOrEmpty(c) && !cars.Contains(c)) cars.Add(c);
                    var liv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var l in hostSave.liveries ?? new List<LiveryEntry>()) if (l != null && !string.IsNullOrEmpty(l.car)) liv[l.car] = l.data;
                    InteriorUtils.GuestGarageCars = cars;
                    InteriorUtils.GuestLiveries = liv;
                    GAME_MODE_LIVING.GuestHouses = hostSave.houses;   // My House: Einrichtung des Gastgebers
                }
            }
            Say("Du bist jetzt bei " + nick + ".");
            return;
        }
        if (!Join(from)) Say("Zu " + nick + " konnte nicht teleportiert werden.");
    }

    /// <summary>Haus-Gruppe auflösen, sobald Gast bzw. Gastgeber das Haus verlassen hat.</summary>
    static void UpdateGroups()
    {
        var spot = InteriorUtils.CurrentSpot;
        bool inHouse = InteriorUtils.Inside && spot != null && IsHouseKind("room|" + spot.Code);
        if (HomeHost != 0 && (!inHouse || InteriorUtils.GuestHost != HomeHost)) HomeHost = 0;
        if (HomeGuests.Count > 0 && !inHouse) HomeGuests.Clear();
        if (HomeGuests.Count > 0) HomeGuests.RemoveWhere(id => !TDU2GameData.Players.TryGetValue(id, out var p) || !p.Online);
    }

    // ================================================================== Mitfahrt (Beifahrer)
    static CarPhysics? _ownCar;
    static RemoteCars.Remote? _seatOn;
    static bool _cockpit, _camInit;
    static float _yaw, _pitch = 12f, _lostSince = -1f;
    static Vector3 _camPos;

    static void BeginRide(int driver)
    {
        if (RideWith == driver) return;
        var vm = Vm;
        if (vm == null || _ctx?.Vehicles == null) return;
        if (InteriorUtils.Inside) { _ownCar = InteriorUtils._lastCar; InteriorUtils.LeaveForTravel(); }
        else _ownCar = vm.CurrentCar ?? _ownCar;
        vm.Despawn();
        _ctx.Vehicles.Suspended = true;
        RideWith = driver;
        _camInit = false; _yaw = 0f; _pitch = 12f; _lostSince = -1f;
        Say("Du fährst bei " + TDU2GameData.Player(driver).Nick + " mit (F = aussteigen, C = Kamera)");
    }

    /// <summary>Aussteigen: eigenes Auto neben dem Fahrer.</summary>
    public static void LeaveRide() => EndRideLocal(true);

    static void EndRideLocal(bool tellServer)
    {
        if (RideWith == 0) return;
        int driver = RideWith;
        RideWith = 0;
        _leftDriver = driver; _leftAt = _time;
        if (tellServer && TDU2Session.Client != null) TDU2Session.Client.Send(new NetRequest { op = "leaveride", session = TDU2Session.SessionId });
        DestroySeatAvatar();
        var vm = Vm;
        if (vm == null || _ctx?.Vehicles == null) return;
        _ctx.Vehicles.Suspended = false;
        var car = _ownCar ?? vm.FindCar(vm.StartCar) ?? (vm.Available.Count > 0 ? vm.Available[0] : null);
        Vector3 pos, fwd;
        var r = RemoteCars.Get(driver);
        if (r != null && r.HasVis)
        {
            var right = Vector3.Transform(Vector3.UnitX, r.VisRot); right.Y = 0f;
            right = right.LengthSquared() > 1e-4f ? Vector3.Normalize(right) : Vector3.UnitX;
            var f = Vector3.Transform(Vector3.UnitZ, r.VisRot); f.Y = 0f;
            fwd = f.LengthSquared() > 1e-4f ? Vector3.Normalize(f) : Vector3.UnitZ;
            pos = r.VisPos + right * 4f - fwd * 2f;
        }
        else
        {
            var cam = _ctx.Terrain?.Camera; var st = _ctx.Terrain?.Streamer;
            pos = cam != null && st != null ? st.RenderToGame(cam.Position) : Vector3.Zero;
            var f = cam?.Forward ?? Vector3.UnitZ; f.Y = 0f;
            fwd = f.LengthSquared() > 1e-4f ? Vector3.Normalize(f) : Vector3.UnitZ;
        }
        if (car != null) vm.SpawnCar(car.FileName, pos, fwd);
    }

    static void DestroySeatAvatar()
    {
        if (_seatOn != null) { _seatOn.Seat?.Dispose(); _seatOn.Seat = null; }
        _seatOn = null;
    }

    static void UpdateRide(float dt)
    {
        if (RideWith == 0) return;
        var r = RemoteCars.Get(RideWith);
        // Fahrer weg (offline, im Interieur, kein Auto): nach einer Weile aussteigen
        if (r == null || r.Model == null || !r.HasVis)
        {
            if (_lostSince < 0f) _lostSince = _time;
            if (_time - _lostSince > 12f) { Say("Fahrer nicht mehr unterwegs – Mitfahrt beendet."); EndRideLocal(true); }
            return;
        }
        _lostSince = -1f;
        // ich selbst auf dem Beifahrersitz (neu nach Autowechsel des Fahrers)
        if (!ReferenceEquals(_seatOn, r) || r.Seat == null || !ReferenceEquals(r.Seat.Model, r.Model))
        {
            DestroySeatAvatar();
            var rend = _ctx?.Vehicles?.Renderer;
            if (rend != null && AvatarLibrary.Ready)
            {
                int d = RideWith;
                r.Seat = CarAvatar.Attach(rend, r.Model, AvatarConfigs.Local, true, () => RemoteCars.RemoteSteer(d));
                if (r.Seat != null) r.Seat.ConfigCode = () => TDU2Session.Save?.avatar;
                _seatOn = r;
            }
        }
        if (!Chat.ChatTyping && _ctx != null && !_ctx.Paused)
        {
            if (TDU2Controls.KeyPressed(0x46)) { EndRideLocal(true); return; }   // F
            if (TDU2Controls.KeyPressed(0x43)) { _cockpit = !_cockpit; _yaw = 0f; _pitch = _cockpit ? 0f : 12f; }   // C
            var input = _ctx.Terrain?.Input;
            if (input != null && input.RightMouseDown)
            {
                // Unity GetAxis("Mouse X/Y") ≈ Pixel × 0,1 (Y nach oben)
                _yaw += input.MouseDeltaX * 0.1f * 3f;
                _pitch = System.Math.Clamp(_pitch - (-input.MouseDeltaY * 0.1f) * 3f, _cockpit ? -40f : -5f, _cockpit ? 40f : 60f);
            }
            else if (!_cockpit) _yaw = MoveTowards(_yaw, 0f, dt * 60f);
        }
        PlaceRideCamera(r, dt);
    }

    static float MoveTowards(float v, float t, float d) => MathF.Abs(t - v) <= d ? t : v + MathF.Sign(t - v) * d;

    /// <summary>Kamera am Auto des Fahrers (Unity LateUpdate): Beifahrersitz (PSGSEAT_HR/LR) bzw. außen dahinter.</summary>
    static void PlaceRideCamera(RemoteCars.Remote r, float dt)
    {
        var t = _ctx?.Terrain; var cam = t?.Camera; var st = t?.Streamer;
        if (t == null || cam == null || st == null) return;
        var fwdT = Vector3.Transform(Vector3.UnitZ, r.VisRot);
        var upT = Vector3.Transform(Vector3.UnitY, r.VisRot);
        var rightT = Vector3.Transform(Vector3.UnitX, r.VisRot);
        t.FlyCameraEnabled = false;
        var seat = _cockpit && r.Model != null ? (r.Model.Find("PSGSEAT_HR") ?? r.Model.Find("PSGSEAT_LR")) : null;
        if (seat != null)
        {
            var seatGame = st.RenderToGame(seat.World.Translation);
            var pos = seatGame + upT * 0.68f + fwdT * 0.05f;
            var look = Quaternion.CreateFromAxisAngle(upT, _yaw * MathF.PI / 180f) * Quaternion.CreateFromAxisAngle(rightT, _pitch * MathF.PI / 180f);
            cam.Position = st.GameToRender(pos);
            cam.Override = (Vector3.Normalize(Vector3.Transform(fwdT, look)), upT);
            t.NearClip = 0.05f;
            _camPos = pos; _camInit = true;
            return;
        }
        float len = MathF.Max(2f, r.BoxSize.Z * 0.5f);
        var flat = fwdT; flat.Y = 0f;
        flat = flat.LengthSquared() < 1e-4f ? Vector3.UnitZ : Vector3.Normalize(flat);
        // Quaternion.LookRotation(flat) * Euler(pitch, yaw, 0) * back
        float yaw = MathF.Atan2(flat.X, flat.Z) + _yaw * MathF.PI / 180f, pitch = _pitch * MathF.PI / 180f;
        var back = -new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), -MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        var lookAt = r.VisPos + Vector3.UnitY * 1.1f;
        var p = lookAt + back * (len * 2.2f + 2.5f) + Vector3.UnitY * 0.6f;
        float k = 1f - MathF.Exp(-dt * 8f);
        if (!_camInit || (p - _camPos).LengthSquared() > 40f * 40f) { _camPos = p; _camInit = true; }
        else _camPos = Vector3.Lerp(_camPos, p, k);
        cam.Position = st.GameToRender(_camPos);
        cam.Override = (Vector3.Normalize(lookAt - _camPos), Vector3.UnitY);
        t.NearClip = 1f;
    }

    // ================================================================== Fahrer: Beifahrer im eigenen Auto
    static CarAvatar? _guest;
    static CarModel? _guestModel;

    static void UpdatePassengerAvatar(bool force)
    {
        var vm = Vm;
        var model = vm?.Current != null ? vm.Model : null;
        if (Passenger == 0 || model == null)
        {
            _guest?.Dispose(); _guest = null; _guestModel = null;
            return;
        }
        if (!force && _guest != null && ReferenceEquals(_guestModel, model)) return;
        _guest?.Dispose(); _guest = null; _guestModel = null;
        var rend = _ctx?.Vehicles?.Renderer;
        if (rend == null || !AvatarLibrary.Ready) return;
        int pid = Passenger;
        var veh = vm!.Current;
        _guest = CarAvatar.Attach(rend, model, AvatarConfigs.ForPlayer(pid), true, () => veh != null ? veh.Steer : 0f);
        if (_guest != null) { _guest.ConfigCode = () => AvatarConfigs.RemoteCode(pid); _guestModel = model; }
    }

    /// <summary>VehicleModule.Render nach dem eigenen Auto: mein Beifahrer.</summary>
    public static void RenderOwnPassenger(Vector3 cameraRender)
    {
        if (_guest != null && Passenger != 0) _guest.Render(cameraRender, Paradise.Core.World.WorldWeather.Inside);
    }

    // ================================================================== Ablauf (ScreenModule nach TDU2GameData/Multiplayer)
    public static void Update(IGameModeContext ctx, float dt)
    {
        _ctx = ctx;
        _time += dt;
        PollInvite();
        PollReply();
        _ = General; _ = Hud;   // FSCommand-Handler anhängen
        // Einladung anzeigen, sobald das HUD/der General-Film frei ist
        if (_popup == null && _pendingInvites.Count > 0)
        {
            var next = _pendingInvites.Peek();
            bool ride = string.IsNullOrEmpty(next.kind) || next.kind.StartsWith("codriving");
            if (ride ? CanShowPopup() : CanShowGamePopup())
            {
                _pendingInvites.Dequeue();
                if (ride) ShowInvite(next); else ShowGameInvite(next);
            }
        }
        UpdateGroups();
        if (_answerCloseAt > 0f && _time >= _answerCloseAt) { _answerCloseAt = -1f; General?.GotoLabel("/pop_gameInvite", "close_answer", true); }
        if (_popup != null && _popupGame) UpdateGamePopup();
        else if (_popup != null)
        {
            if (_time > _popupUntil) { Hud?.CallClip("/HUD/contact", "cancel_contact"); if (_popup != null) Reply(false); }
            else if (!Chat.ChatTyping && !ctx.Paused)
            {
                var h = Hud;
                if (TDU2Controls.ConfirmPressed())
                {
                    if (h == null || !h.CallClip("/HUD/contact", "accept")) Reply(true);
                    _acceptAt = _time + 1.5f;   // Film meldet "acpt" erst nach der Animation – spätestens nach 1,5 s selbst antworten
                }
                else if (TDU2Controls.BackPressed())
                {
                    if (h == null || !h.CallClip("/HUD/contact", "cancel_contact")) Reply(false);
                    _declineAt = _time + 1f;
                }
            }
        }
        if (_acceptAt > 0f && _time >= _acceptAt) { _acceptAt = -1f; if (_popup != null) { if (_popupGame) General?.GotoLabel("/pop_gameInvite", "close_big", true); Reply(true); } }
        if (_declineAt > 0f && _time >= _declineAt) { _declineAt = -1f; if (_popup != null) Reply(false); }
        UpdateRide(dt);
        if (Passenger != 0 || _guest != null) UpdatePassengerAvatar(false);
    }

    /// <summary>Abmelden/Beenden.</summary>
    public static void Reset()
    {
        _popup = null; _pendingInvites.Clear(); _shownInvites.Clear(); _sentKinds.Clear();
        DestroySeatAvatar();
        _guest?.Dispose(); _guest = null; _guestModel = null;
        RideWith = 0; Passenger = 0; HomeHost = 0; HomeGuests.Clear();
    }
}
