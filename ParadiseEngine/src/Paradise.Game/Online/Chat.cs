using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using Paradise.Game.Input;

namespace Paradise.Game.Online;

/// <summary>
/// Chat über general.bnk /pop_chat – Port von Unity TDU2GameData (UpdateChat/OpenChat/CloseChat/AddChatLine), Netz: Multiplayer.SendChat.
/// Öffnen: Label "open" (Bild 3 setzt _root.TextToFocus = "/:TxtInput" + "tfoc"), Schließen: "close" (Bild 4 meldet "chva").
/// Verlauf: /pop_chat/log/line1..4 (line4 unten = neueste), Text in lineN/color:txt, Farbe = Bild von lineN/color,
/// Zeile ein-/ausblenden über die Labels open/close der Zeile. Position des Verlaufs: log-Labels ingame/menus/walk_mode.
/// Taste "chat" (Game/Config/KeyConfig.cs, Standard T), Enter = senden, Esc = abbrechen.
/// </summary>
public static class Chat
{
    const int ChatLines = 4;
    /// <summary>Verlauf (Zeit, Name, Text) – z.B. für das Entwicklermenü.</summary>
    public static readonly List<(DateTime time, string nick, string text)> ChatLog = new();
    static readonly (string? text, int color, float until)[] _chatLine = new (string?, int, float)[ChatLines];
    static bool _chatOpen, _hooked;
    static string _chatInput = "";
    static long _chatBlockFrame = -1, _frame;
    static string? _logMode;
    static float _clock;
    static VmfPlayer? _general;

    /// <summary>Ein Film-Textfeld hat die Tastatur (z.B. MyTDULife-Stimmungstext, Profilname): der Chat öffnet sich dann nicht.</summary>
    public static Func<bool>? ExternalTextInput;
    /// <summary>true, solange der Chat die Tastatur hat (andere Komponenten ignorieren dann Esc/Enter usw.).</summary>
    public static bool ChatTyping => _chatOpen || _frame <= _chatBlockFrame;

    /// <summary>Jedes Bild (ScreenModule).</summary>
    public static void Update(IGameModeContext ctx, float dt)
    {
        _clock += dt; _frame++;
        if (!_hooked) { _hooked = true; Multiplayer.ChatReceived += OnChat; }
        var g = GeneralOverlay.General;
        if (g == null) return;
        if (!ReferenceEquals(g, _general))
        {
            _general = g;
            g.GotoLabel("/pop_chat", "default", false);
            for (int i = 1; i <= ChatLines; i++) g.GotoLabel("/pop_chat/log/line" + i, "default", false);
        }

        // Position des Verlaufs je nach Lage
        var m = Screens.GameMode.Current;
        string mode = ctx.Paused || m == Screens.Gamemode.GAME_MODE_PAUSE ? "menus" : InteriorUtils.Inside ? "walk_mode" : "ingame";
        if (mode != _logMode) { _logMode = mode; g.GotoLabel("/pop_chat/log", mode, false); }

        // Zeilen ausblenden, wenn abgelaufen
        for (int i = 0; i < ChatLines; i++)
            if (_chatLine[i].text != null && _clock >= _chatLine[i].until)
            {
                _chatLine[i].text = null;
                g.GotoLabel("/pop_chat/log/line" + (i + 1), "close", true);
            }

        if (!_chatOpen)
        {
            bool free = OnlineConfig.EnableChat && !(ExternalTextInput?.Invoke() ?? false) && GAME_MODE_LIVING._textVar == null && TDU2Session.LoggedIn && !TDU2Session.Offline
                        && m != Screens.Gamemode.GAME_MODE_LOGIN && m != Screens.Gamemode.GAME_MODE_MAP && m != Screens.Gamemode.GAME_MODE_BOOTMENU
                        && !ctx.Paused && !FlashDialog.Active && _frame > _chatBlockFrame;
            if (free && TDU2Controls.Pressed("chat")) OpenChat(g);
            return;
        }
        if (TDU2Controls.RawKeyPressed(0x1B)) { CloseChat(g, false); return; }
        if (TDU2Controls.RawKeyPressed(0x0D)) { CloseChat(g, true); return; }
        // das 'T', mit dem geöffnet wurde, nicht als Text übernehmen
        if (_frame > _chatBlockFrame)
            foreach (char ch in FlashInput.inputString)
            {
                if (ch == '\b') { if (_chatInput.Length > 0) _chatInput = _chatInput.Substring(0, _chatInput.Length - 1); }
                else if (ch == '\n' || ch == '\r' || ch == '\t') continue;
                else if (!char.IsControl(ch) && _chatInput.Length < System.Math.Max(10, OnlineConfig.ChatMaxLength)) _chatInput += ch;
            }
        bool blink = (_clock % 1f) < 0.55f;
        g.SetVariable("/:TxtInput", _chatInput + (blink ? "_" : ""));
    }

    static void OpenChat(VmfPlayer g)
    {
        _chatOpen = true;
        _chatInput = "";
        _chatBlockFrame = _frame + 1;   // das 'T' selbst nicht als Text übernehmen
        TDU2Controls.Blocked = true;
        g.SetVariable("/:TxtInput", "");
        g.SetVariable("/:m_Chat", "Chat:    ");
        g.GotoLabel("/pop_chat", "open", true);
    }

    static void CloseChat(VmfPlayer g, bool send)
    {
        _chatOpen = false;
        _chatBlockFrame = _frame + 1;   // Esc/Enter nicht noch ans Pausemenü/Interieur weitergeben
        TDU2Controls.Blocked = false;
        string text = _chatInput.Trim();
        _chatInput = "";
        g.SetVariable("/:TxtInput", "");
        g.GotoLabel("/pop_chat", "close", true);
        if (send && text.Length > 0)
        {
            if (!Multiplayer.SendChat(text)) AddChatLine(TDU2Session.Nick ?? "", text, 0);   // ohne Verbindung wenigstens lokal zeigen
        }
    }

    static void OnChat(int pid, string nick, string text)
    {
        int color = pid == TDU2Session.PlayerId ? 0 : 1 + pid % 15;
        AddChatLine(nick, text, color);
    }

    /// <summary>Neue Zeile unten (line4), ältere rutschen nach oben (Unity AddChatLine).</summary>
    public static void AddChatLine(string nick, string text, int color)
    {
        ChatLog.Add((DateTime.Now, nick, text));
        if (ChatLog.Count > 200) ChatLog.RemoveAt(0);
        var g = _general;
        if (g == null) return;
        for (int i = 0; i < ChatLines - 1; i++) _chatLine[i] = _chatLine[i + 1];
        _chatLine[ChatLines - 1] = (nick + ": " + text, color, _clock + MathF.Max(3f, OnlineConfig.ChatLineSeconds));
        for (int i = 0; i < ChatLines; i++)
        {
            string line = "/pop_chat/log/line" + (i + 1);
            var l = _chatLine[i];
            if (l.text == null) { g.GotoLabel(line, "default", false); continue; }
            g.SetVariable(line + "/color:txt", l.text);
            g.GotoFrameNumber(line + "/color", System.Math.Clamp(l.color, 0, 15));
            if (i == ChatLines - 1) g.GotoLabel(line, "open", true);   // neue Zeile blendet ein
            else { var d = g.Find(line); if (d?.Sprite != null && d.Sprite.Frame < 10) g.GotoFrameNumber(line, 10); }   // sichtbar stehen lassen
        }
    }
}
