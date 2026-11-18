using Paradise.Core.Diagnostics;

namespace Paradise.Game.Screens;

/// <summary>
/// Aktueller Spielmodus (Port von TDU2GameMode aus dem Unity-Projekt, ohne Discord-Anzeige).
/// Abfrage: <see cref="Current"/>, Änderungen über <see cref="Changed"/>. Der Modus wird gesetzt (<see cref="Set"/>);
/// Missionen/Zwischensequenzen setzen ihn ausdrücklich mit <see cref="Push"/> und geben ihn mit <see cref="Pop"/> frei.
/// </summary>
public static class GameMode
{
    static Gamemode _current = Gamemode.GAME_MODE_BOOTMENU;
    static Gamemode _auto = Gamemode.GAME_MODE_BOOTMENU;
    static Gamemode? _forced;
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static double _since;

    /// <summary>Aktueller Spielmodus.</summary>
    public static Gamemode Current => _current;
    /// <summary>Name ohne Präfix, z.B. "FREERIDE".</summary>
    public static string CurrentName => _current.ToString().Replace("GAME_MODE_", "");
    /// <summary>Sekunden seit dem letzten Moduswechsel.</summary>
    public static float SecondsInMode => (float)(_clock.Elapsed.TotalSeconds - _since);
    /// <summary>UTC-Zeitpunkt des letzten Moduswechsels.</summary>
    public static DateTime ModeStartedUtc { get; private set; } = DateTime.UtcNow;
    /// <summary>(alt, neu) bei jedem Wechsel.</summary>
    public static event Action<Gamemode, Gamemode>? Changed;

    /// <summary>Modus aus dem Spielzustand (im Unity-Projekt je Bild über Detect() bestimmt).</summary>
    public static void Set(Gamemode mode) { _auto = mode; if (!_forced.HasValue) Apply(mode); }
    /// <summary>Modus ausdrücklich setzen (Mission, Kino, Avatar-Wahl …); gilt bis <see cref="Pop"/>.</summary>
    public static void Push(Gamemode mode) { _forced = mode; Apply(mode); }
    /// <summary>Ausdrücklich gesetzten Modus freigeben (danach wieder der automatische).</summary>
    public static void Pop() { _forced = null; Apply(_auto); }
    /// <summary>Gilt gerade ein ausdrücklich gesetzter Modus?</summary>
    public static bool IsForced => _forced.HasValue;

    static void Apply(Gamemode m)
    {
        if (m == _current) return;
        var old = _current;
        _current = m; _since = _clock.Elapsed.TotalSeconds; ModeStartedUtc = DateTime.UtcNow;
        Log.Info("---------------- GAMEMODE -----------------");
        Log.Info("GAME MODE : " + CurrentName);   // wie DebugManager.switchGamemode (Banner im Original-Log)
        Log.Info("-------------------------------------------");
        try { Changed?.Invoke(old, m); }
        catch (Exception ex) { Log.Error("GameMode.Changed: " + ex.Message); }
    }
}
