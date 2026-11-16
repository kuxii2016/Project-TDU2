using Paradise.Core.Audio;
using Paradise.Core.Flash;
using Paradise.Core.Platform;
using Paradise.Core.Vehicles;
using Paradise.Game.Input;

namespace Paradise.Game.Screens;

/// <summary>
/// Das Original-Zubehörmenü im HUD-Film (/HUD/ig_menu) – Port von Unity TDU2AccessoryMenu (Vehicles/TDU2Accessories.cs):
/// öffnen (Label "open" ruft SetIcons, liest _root.HasHood, HasWindows, HasTurnSignals, IsBike), Auswahl über ValidIGM(&lt;Symbol&gt;) –
/// das Menü bestätigt und schließt sich selbst.
/// Tasten: Pfeile/WASD oder 1–4 (oben Verdeck, rechts Blinker rechts, unten Fenster, links Blinker links), Zubehör-Taste/ESC schließt.
/// </summary>
public static class AccessoryMenu
{
    const string Menu = "/HUD/ig_menu";
    static CarAccessories? _acc;
    static float _openedAt;
    static bool _hudShown;
    public static bool IsOpen { get; private set; }
    static long _closedFrame = -10;
    /// <summary>ESC gehört gerade dem Zubehörmenü (offen bzw. in diesem/letztem Bild per ESC geschlossen) – Pausemenü nicht öffnen.</summary>
    public static bool OwnsEscape => IsOpen || Joysticks.Frame - _closedFrame <= 1;

    /// <summary>HUD-Film (Unity TDU2UIManager.Hud = hud.bnk der Minimap) – gesetzt von VehicleModule.</summary>
    public static Func<VmfPlayer?>? HudSource;
    static VmfPlayer? Hud => HudSource?.Invoke();

    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static float Now => (float)_clock.Elapsed.TotalSeconds;   // Unity Time.unscaledTime

    public static bool Open(CarAccessories? acc, bool bike = false)
    {
        var h = Hud;
        if (h == null || acc == null || h.Find(Menu) == null) return false;
        _acc = acc; IsOpen = true; _openedAt = Now;
        h.SetValue("/:IsBike", bike);
        h.SetValue("/:HasHood", acc.HasRoof);
        h.SetValue("/:HasWindows", acc.HasWindows);
        h.SetValue("/:HasTurnSignals", acc.HasTurnSignals);
        var title = h.GetVariable("/:m_Accessories");
        if (string.IsNullOrEmpty(title) || title.StartsWith("_") || title.StartsWith("m_")) h.SetVariable("/:m_Accessories", "Zubehör");
        _hudShown = h.Find(Menu)!.Visible;
        h.SetVisible(Menu, true);
        h.GotoLabel(Menu, "open", true);
        return true;
    }

    public static void Close(bool animate = true)
    {
        if (!IsOpen) return;
        IsOpen = false; _closedFrame = Joysticks.Frame;
        var h = Hud;
        if (h != null && h.Find(Menu) != null)
        {
            if (animate && h.Label(Menu) != "default") h.GotoLabel(Menu, "close", true);
            else { h.GotoLabel(Menu, "default", false); if (!_hudShown) h.SetVisible(Menu, false); }
        }
        _acc = null;
    }

    /// <summary>Symbol wählen: Menü bestätigt (Animation) und schließt; Wirkung am Auto.</summary>
    static void Choose(int slot)
    {
        var h = Hud; var a = _acc;
        if (h == null || a == null) return;
        string? msg = h.GetVariable(Menu + ":ig_msg_" + slot);
        if (string.IsNullOrEmpty(msg)) msg = slot == 1 ? "hood" : slot == 2 ? "clir" : slot == 3 ? "wind" : "clil";
        bool locked = (msg == "hood" && !a.HasRoof) || (msg == "wind" && !a.HasWindows) || ((msg == "clir" || msg == "clil") && !a.HasTurnSignals);
        if (locked) { FrontendSounds.Command("sndp:wrong"); return; }
        switch (msg)
        {
            case "hood": a.ToggleRoof(); break;
            case "wind": a.ToggleWindows(); break;
            case "clil": a.ToggleIndicator(true); break;
            case "clir": a.ToggleIndicator(false); break;
        }
        IsOpen = false; _acc = null;
        if (!h.CallClip(Menu, "ValidIGM", msg)) h.GotoLabel(Menu, "close", true);
    }

    const int VK_1 = 0x31, VK_2 = 0x32, VK_3 = 0x33, VK_4 = 0x34, VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44;

    /// <summary>Jedes Bild, solange das Menü offen ist; true = Tasten verbraucht.</summary>
    /// <param name="arrows">Pfeile/WASD wählen mit aus (Händler); beim Fahren nur 1–4, die Pfeile lenken.</param>
    public static bool Update(bool arrows = true)
    {
        if (!IsOpen) return false;
        var h = Hud;
        if (h == null || _acc == null || _acc.Model == null || _acc.Model.Visual == null) { IsOpen = false; _acc = null; return false; }
        if (Now - _openedAt < 0.15f) return true;
        if (TDU2Controls.RawKeyPressed(VK_1) || (arrows && (TDU2Nav.Up() || TDU2Controls.RawKeyPressed(VK_W)))) Choose(1);
        else if (TDU2Controls.RawKeyPressed(VK_2) || (arrows && (TDU2Nav.Right() || TDU2Controls.RawKeyPressed(VK_D)))) Choose(2);
        else if (TDU2Controls.RawKeyPressed(VK_3) || (arrows && (TDU2Nav.Down() || TDU2Controls.RawKeyPressed(VK_S)))) Choose(3);
        else if (TDU2Controls.RawKeyPressed(VK_4) || (arrows && (TDU2Nav.Left() || TDU2Controls.RawKeyPressed(VK_A)))) Choose(4);
        else if (TDU2Controls.Pressed("accessories") || TDU2Nav.Back()) Close();
        return true;
    }
}
