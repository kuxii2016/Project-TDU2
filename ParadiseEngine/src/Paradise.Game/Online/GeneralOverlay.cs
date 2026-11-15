using Paradise.Core.Flash;
using Paradise.Core.Online;

namespace Paradise.Game.Online;

/// <summary>
/// general.bnk als globales Overlay wie im Spiel (Unity TDU2GameData.UpdateGeneral):
///   saving_logo = Speichern (ab TDU2Session.SaveStarted, mindestens <see cref="SavingLogoMinSeconds"/>),
///   online_evt = Spieler online/offline, deco_pop = Verbindung verloren. Nur diese Clips (und pop_chat) bleiben sichtbar.
/// </summary>
public static class GeneralOverlay
{
    /// <summary>Speicher-Symbol mindestens so lange zeigen (Unity TDU2GameData.SavingLogoMinSeconds).</summary>
    public static float SavingLogoMinSeconds = 2f;

    static VmfPlayer? _general;
    static float _clock, _saveCloseAt = -1f, _evtCloseAt = -1f, _evtFreeAt;
    static volatile bool _saveRequested;
    static readonly Queue<(string nick, bool on)> _events = new();

    /// <summary>general.bnk übernehmen (einmal nach dem Laden).</summary>
    public static void Attach(VmfPlayer? general)
    {
        if (general == null || ReferenceEquals(general, _general)) return;
        _general = general;
        general.SetValue("/:WhatPlatform", 1);
        general.GotoLabel("/", "default", false);
        // nur die benutzten Overlays sichtbar lassen
        if (general.Root != null)
            foreach (var d in general.Root.List.Values) d.Visible = d.Name == "deco_pop" || d.Name == "online_evt" || d.Name == "saving_logo" || d.Name == "pop_chat";
        general.GotoLabel("/pop_chat", "default", false);
        for (int i = 1; i <= 4; i++) general.GotoLabel("/pop_chat/log/line" + i, "default", false);
        TDU2Session.SaveStarted -= OnSaveStarted;
        TDU2Session.SaveStarted += OnSaveStarted;
    }

    static void OnSaveStarted() => _saveRequested = true;

    static bool _decoOpen;

    /// <summary>Verbindung verloren (deco_pop) – schließt mit Enter/Esc oder wenn die Verbindung zurück ist.</summary>
    public static void ShowDisconnected()
    {
        if (_general == null) return;
        _decoOpen = true;
        _general.GotoLabel("/deco_pop", "open", true);
    }

    /// <summary>deco_pop schließen (Unity: Enter/Esc bzw. wieder online).</summary>
    public static void CloseDisconnected()
    {
        if (!_decoOpen || _general == null) return;
        _decoOpen = false;
        if (!_general.CallClip("/deco_pop", "validDeco")) _general.GotoLabel("/deco_pop", "close", true);
    }

    public static bool DisconnectedOpen => _decoOpen;

    /// <summary>Meldung "Spieler online/offline" (online_evt) einreihen.</summary>
    public static void PlayerEvent(string nick, bool online) { if (string.IsNullOrEmpty(nick)) return; lock (_events) if (_events.Count < 8) _events.Enqueue((nick, online)); }

    /// <summary>Jedes Bild (ScreenModule).</summary>
    public static void Update(float dt)
    {
        _clock += dt;
        var g = _general;
        if (g == null) return;

        // Spieler online/offline
        if (_evtCloseAt > 0f && _clock >= _evtCloseAt)
        {
            _evtCloseAt = -1f;
            g.GotoLabel("/online_evt", "close", true);
            _evtFreeAt = _clock + 0.6f;
        }
        if (_evtCloseAt < 0f && _clock >= _evtFreeAt)
        {
            (string nick, bool on) e = default;
            bool has;
            lock (_events) { has = _events.Count > 0; if (has) e = _events.Dequeue(); }
            if (has)
            {
                g.SetVariable("/:OEType", e.on ? "player_on" : "player_off");
                g.SetVariable("/:OEPlayerName", e.nick);
                g.GotoLabel("/online_evt", "open", true);
                _evtCloseAt = _clock + 3.5f;
            }
        }

        // Speichern: Symbol ab dem Start des Speicherns, mindestens SavingLogoMinSeconds (sonst nur ein Aufblitzen)
        if (_saveRequested)
        {
            _saveRequested = false;
            if (_saveCloseAt < 0f) g.GotoLabel("/saving_logo", "open", true);
            _saveCloseAt = _clock + MathF.Max(0.5f, SavingLogoMinSeconds);
        }
        if (_saveCloseAt > 0f && _clock >= _saveCloseAt && !TDU2Session.Saving) { _saveCloseAt = -1f; g.GotoLabel("/saving_logo", "close", true); }
    }
}
