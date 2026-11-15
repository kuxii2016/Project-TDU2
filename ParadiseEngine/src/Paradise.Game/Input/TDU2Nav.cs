using Paradise.Core.Platform;

namespace Paradise.Game.Input;

/// <summary>
/// Menü-Navigation für Tastatur UND Gamepad/Lenkrad an einer Stelle (alle Flash-Menüs, Karte, Interieurs) –
/// Port von Unity TDU2Nav (Client/Assets/TDU2Map/Runtime/Input/TDU2Nav.cs):
///  - Richtungen: Pfeiltasten bzw. Steuerkreuz (Achsen 6/7, Lenkräder: Hut-Schalter) und beim Pad der linke Stick,
///    mit Wiederholung beim Halten (0,4 s Verzögerung, dann alle 0,12 s).
///  - Zurück: Esc/Rücktaste bzw. B (Profil MENU_BACK), X/Y: Tasten X/Y bzw. Pad X/Y (MENU_OPTION_1/2),
///    LB/RB (MENU_SORT_LEFT/RIGHT), Start (MENU_PAUSE). Bestätigen bleibt TDU2Controls.ConfirmPressed().
/// </summary>
public static class TDU2Nav
{
    static long _frame = -1;
    static int _dx, _dy;
    static float _nextX, _nextY;
    static bool _l, _r, _u, _d;
    static float _lastPad = -100f;
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static float Now => (float)_clock.Elapsed.TotalSeconds;   // Unity Time.unscaledTime

    /// <summary>Wurde zuletzt das Gamepad zum Navigieren benutzt (für Karten-Cursor u.ä.)?</summary>
    public static bool PadActive => Now - _lastPad < 30f;

    static void Poll()
    {
        if (_frame == Joysticks.Frame) return;
        _frame = Joysticks.Frame;
        _l = _r = _u = _d = false;
        float x = 0f, y = 0f;
        if (TDU2InputDevices.HasDevice && !TDU2Controls.Blocked)
        {
            // Steuerkreuz (oben = +) – nur echte Hut-Achsen (Lenkräder: Achse 6/7 sind oft Pedale/Regler mit Ruhe ±1)
            if (!TDU2InputDevices.IsWheel || TDU2InputDevices.IsHatAxis(6)) x = TDU2InputDevices.RawAxis(6);
            if (!TDU2InputDevices.IsWheel || TDU2InputDevices.IsHatAxis(7)) y = TDU2InputDevices.RawAxis(7);
            if (!TDU2InputDevices.IsWheel)
            {
                float sx = TDU2InputDevices.RawAxis(1), sy = -TDU2InputDevices.RawAxis(2);   // linker Stick (unten = +)
                if (MathF.Abs(sx) > MathF.Abs(x)) x = sx;
                if (MathF.Abs(sy) > MathF.Abs(y)) y = sy;
            }
        }
        int dx = x > 0.55f ? 1 : x < -0.55f ? -1 : 0;
        int dy = y > 0.55f ? 1 : y < -0.55f ? -1 : 0;
        // nur die stärkere Richtung (Stick schräg -> kein Doppelschritt)
        if (dx != 0 && dy != 0) { if (MathF.Abs(x) >= MathF.Abs(y)) dy = 0; else dx = 0; }
        float t = Now;
        if (dx != 0 && (dx != _dx || t >= _nextX)) { if (dx > 0) _r = true; else _l = true; _nextX = t + (dx != _dx ? 0.4f : 0.12f); _lastPad = t; }
        if (dy != 0 && (dy != _dy || t >= _nextY)) { if (dy > 0) _u = true; else _d = true; _nextY = t + (dy != _dy ? 0.4f : 0.12f); _lastPad = t; }
        _dx = dx; _dy = dy;
    }

    // ---- nur Gerät
    public static bool PadUp() { Poll(); return _u; }
    public static bool PadDown() { Poll(); return _d; }
    public static bool PadLeft() { Poll(); return _l; }
    public static bool PadRight() { Poll(); return _r; }
    public static bool PadUpHeld() { Poll(); return _dy > 0; }
    public static bool PadDownHeld() { Poll(); return _dy < 0; }
    public static bool PadLeftHeld() { Poll(); return _dx < 0; }
    public static bool PadRightHeld() { Poll(); return _dx > 0; }
    static bool Btn(string id) { if (TDU2Controls.Blocked) return false; bool b = TDU2InputDevices.ButtonPressed(id); if (b) _lastPad = Now; return b; }
    public static bool PadBack() => Btn("back");
    public static bool PadX() => Btn("menu_opt1");
    public static bool PadY() => Btn("menu_opt2");
    public static bool LB() => Btn("menu_sortl");
    public static bool RB() => Btn("menu_sortr");
    public static bool StartPressed() => Btn("pause");

    /// <summary>Linker Stick analog (Pad), x = rechts, y = oben, mit Totzone.</summary>
    public static System.Numerics.Vector2 Stick
    {
        get
        {
            if (!TDU2InputDevices.HasDevice || TDU2InputDevices.IsWheel || TDU2Controls.Blocked) return System.Numerics.Vector2.Zero;
            var v = new System.Numerics.Vector2(TDU2InputDevices.RawAxis(1), -TDU2InputDevices.RawAxis(2));
            float m = v.Length();
            if (m < 0.2f) return System.Numerics.Vector2.Zero;
            _lastPad = Now;
            return v / m * Math.Clamp((m - 0.2f) / 0.8f, 0f, 1f);
        }
    }

    // ---- Tastatur ODER Gerät (Unity: Input.GetKeyDown(KeyCode.UpArrow) usw. in den Menüs)
    const int VK_BACK = 0x08, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_X = 0x58, VK_Y = 0x59;
    public static bool Up() => TDU2Controls.RawKeyPressed(VK_UP) || PadUp();
    public static bool Down() => TDU2Controls.RawKeyPressed(VK_DOWN) || PadDown();
    public static bool Left() => TDU2Controls.RawKeyPressed(VK_LEFT) || PadLeft();
    public static bool Right() => TDU2Controls.RawKeyPressed(VK_RIGHT) || PadRight();
    public static bool UpHeld() => TDU2Controls.RawKeyDown(VK_UP) || PadUpHeld();
    public static bool DownHeld() => TDU2Controls.RawKeyDown(VK_DOWN) || PadDownHeld();
    public static bool LeftHeld() => TDU2Controls.RawKeyDown(VK_LEFT) || PadLeftHeld();
    public static bool RightHeld() => TDU2Controls.RawKeyDown(VK_RIGHT) || PadRightHeld();
    /// <summary>Zurück: Esc oder Rücktaste oder B.</summary>
    public static bool Back() => TDU2Controls.RawKeyPressed(VK_ESCAPE) || TDU2Controls.RawKeyPressed(VK_BACK) || PadBack();
    public static bool X() => TDU2Controls.RawKeyPressed(VK_X) || PadX();
    public static bool Y() => TDU2Controls.RawKeyPressed(VK_Y) || PadY();
}
