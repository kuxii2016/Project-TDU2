using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Game.Input;

namespace Paradise.Game.Gamemodes;

/// <summary>Menü-Tasten für die Original-Filme.</summary>
public enum FlashKey { Up, Down, Left, Right, Confirm, Back, X, Y }

/// <summary>
/// Gemeinsame Menü-Eingabe für alle Original-Filme (Tastatur, Gamepad/Steam Deck – Game/Config/KeyConfig.cs "menu_*" + TDU2Nav).
/// Wie der Flash Player des Spiels: zuerst die Tasten-Buttons des Films (on(keyPress "&lt;Left&gt;") …, VmfPlayer.SendKey – so arbeiten
/// z.B. die Auswahlboxen von shopsavatar.vmf: Pfeil -> Move der Box -> FSCommand …:scne/scpr). Reagiert kein Button, wie bisher die
/// Film-Funktion am obersten Menü (Move("up") / A() / B() / X() / Y(), VmfPlayer.Input).
/// </summary>
public static class FlashNav
{
    // Flash-Tastencodes der keyPress-Bedingung (SWF ButtonCondAction)
    const int KeyLeft = 1, KeyRight = 2, KeyEnter = 13, KeyUp = 14, KeyDown = 15, KeyEscape = 19;

    /// <summary>Eine Taste an den Film; true = Button oder Film-Funktion hat reagiert.</summary>
    public static bool Send(VmfPlayer ui, FlashKey key)
    {
        int code = key switch
        {
            FlashKey.Up => KeyUp,
            FlashKey.Down => KeyDown,
            FlashKey.Left => KeyLeft,
            FlashKey.Right => KeyRight,
            FlashKey.Confirm => KeyEnter,
            FlashKey.Back => KeyEscape,
            _ => 0,
        };
        if (code > 0 && ui.SendKey(code)) return true;
        return key switch
        {
            FlashKey.Up => ui.Input("Move", "up"),
            FlashKey.Down => ui.Input("Move", "down"),
            FlashKey.Left => ui.Input("Move", "left"),
            FlashKey.Right => ui.Input("Move", "right"),
            FlashKey.Confirm => ui.Input("A"),
            FlashKey.Back => ui.Input("B"),
            FlashKey.X => ui.Input("X"),
            FlashKey.Y => ui.Input("Y"),
            _ => false,
        };
    }

    public static bool Pressed(FlashKey key) => key switch
    {
        FlashKey.Up => TDU2Controls.Pressed("menu_up") || TDU2Nav.Up(),
        FlashKey.Down => TDU2Controls.Pressed("menu_down") || TDU2Nav.Down(),
        FlashKey.Left => TDU2Controls.Pressed("menu_left") || TDU2Nav.Left(),
        FlashKey.Right => TDU2Controls.Pressed("menu_right") || TDU2Nav.Right(),
        FlashKey.Confirm => TDU2Controls.ConfirmPressed(),
        FlashKey.Back => TDU2Controls.BackPressed() || TDU2Nav.Back(),
        FlashKey.X => TDU2Controls.Pressed("menu_opt1") || TDU2Nav.X(),
        FlashKey.Y => TDU2Controls.Pressed("menu_opt2") || TDU2Nav.Y(),
        _ => false,
    };

    /// <summary>Pfeile (Tastatur/Steuerkreuz) an den Film.</summary>
    public static void Directions(VmfPlayer ui)
    {
        if (Pressed(FlashKey.Up)) Send(ui, FlashKey.Up);
        if (Pressed(FlashKey.Down)) Send(ui, FlashKey.Down);
        if (Pressed(FlashKey.Left)) Send(ui, FlashKey.Left);
        if (Pressed(FlashKey.Right)) Send(ui, FlashKey.Right);
    }

    /// <summary>
    /// Alles für ein offenes Menü: Pfeile, Bestätigen, X/Y. Zurück liefert das Ergebnis zurück (der Aufrufer schließt, wenn der Film
    /// nicht reagiert hat). Rückgabe: Back gedrückt; <paramref name="backHandled"/> = Film hat Zurück verarbeitet.
    /// </summary>
    public static bool Update(VmfPlayer ui, out bool backHandled)
    {
        Directions(ui);
        if (Pressed(FlashKey.Confirm)) Send(ui, FlashKey.Confirm);
        if (Pressed(FlashKey.X)) Send(ui, FlashKey.X);
        if (Pressed(FlashKey.Y)) Send(ui, FlashKey.Y);
        backHandled = false;
        if (!Pressed(FlashKey.Back)) return false;
        backHandled = Send(ui, FlashKey.Back);
        if (!backHandled) Log.Debug("Flash: Zurück nicht verarbeitet");
        return true;
    }
}
