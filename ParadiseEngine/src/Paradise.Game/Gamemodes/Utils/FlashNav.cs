using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Game.Input;

namespace Paradise.Game.Gamemodes;

/// <summary>Menü-Tasten für die Original-Filme.</summary>
public enum FlashKey { Up, Down, Left, Right, Confirm, Back, X, Y }

/// <summary>
/// Gemeinsame Menü-Eingabe für alle Original-Filme (Tastatur, Gamepad/Steam Deck – Game/Config/KeyConfig.cs "menu_*" + TDU2Nav).
/// Wie im Unity-Projekt rufen die Menüs die Film-Funktionen direkt auf: Move("up"/"down"/"left"/"right"), A(), B(), X(), Y()
/// (VmfPlayer.Input). SendKey würde zuerst SWF-Buttons mit keyPress-Bedingung treffen und dort die Eingabe "verschlucken",
/// obwohl der Film eigentlich auf seine Menü-Funktion wartet – deshalb hier keine Tastatur-Codes mehr an den Film schicken.
/// Explizite SendKey-Aufrufe bleiben nur dort, wo sie das Unity-Projekt ebenfalls benutzt (z.B. Avatar-Auswahl).
/// </summary>
public static class FlashNav
{
    /// <summary>Eine Menüaktion an den Film; true = die Film-Funktion hat reagiert.</summary>
    public static bool Send(VmfPlayer ui, FlashKey key) => key switch
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
