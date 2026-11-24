using Paradise.Core.Diagnostics;
using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Zuordnung GAME_MODE_* -> Klasse in Game/Gamemodes (eine Instanz je Modus, wiederverwendet).
/// Neuer Modus: Klasse GAME_MODE_xxx : GameModeHandler anlegen und hier eintragen.
/// Modi ohne eigene Klasse laufen über <see cref="GAME_MODE_EMPTY"/> (keine Oberfläche) mit einem Hinweis im Log.
/// </summary>
public static class GameModes
{
    static readonly Dictionary<Gamemode, GameModeHandler> _cache = new();

    public static GameModeHandler Get(Gamemode mode)
    {
        if (_cache.TryGetValue(mode, out var h)) return h;
        h = Create(mode);
        _cache[mode] = h;
        return h;
    }

    static GameModeHandler Create(Gamemode mode) => mode switch
    {
        Gamemode.GAME_MODE_BOOTMENU => new GAME_MODE_BOOTMENU(),
        Gamemode.GAME_MODE_LOGIN => new GAME_MODE_LOGIN(),
        Gamemode.GAME_MODE_FREERIDE => new GAME_MODE_FREERIDE(),
        Gamemode.GAME_MODE_TESTCAR => new GAME_MODE_TESTCAR(),
        Gamemode.GAME_MODE_MISSION => new GAME_MODE_MISSION(),
        Gamemode.GAME_MODE_MAP => new GAME_MODE_MAP(),
        Gamemode.GAME_MODE_SHOP => new GAME_MODE_SHOP(),
        Gamemode.GAME_MODE_STICKERS => new GAME_MODE_STICKERS(),
        Gamemode.GAME_MODE_TUNER => new GAME_MODE_TUNER(),
        Gamemode.GAME_MODE_AIRPORT => new GAME_MODE_AIRPORT(),
        Gamemode.GAME_MODE_CAR_WASH => new GAME_MODE_CAR_WASH(),
        Gamemode.GAME_MODE_LIVING => new GAME_MODE_LIVING(),
        Gamemode.GAME_MODE_GARAGE => new GAME_MODE_GARAGE(),
        Gamemode.GAME_MODE_CASINO => new GAME_MODE_CASINO(),
        Gamemode.GAME_MODE_GOODIES => new GAME_MODE_GOODIES(),
        Gamemode.GAME_MODE_HAIRDRESSER => new GAME_MODE_HAIRDRESSER(),
        Gamemode.GAME_MODE_SURGERY => new GAME_MODE_SURGERY(),
        Gamemode.GAME_MODE_ESTATES => new GAME_MODE_ESTATES(),
        Gamemode.GAME_MODE_DRESSING => new GAME_MODE_DRESSING(),
        Gamemode.GAME_MODE_PAUSE => new GAME_MODE_PAUSE(),
        Gamemode.GAME_MODE_SCHOOL => new GAME_MODE_SCHOOL(),
        Gamemode.GAME_MODE_CHAMPIONSHIP => new GAME_MODE_CHAMPIONSHIP(),
        Gamemode.GAME_MODE_AVATAR_CHOICE => new GAME_MODE_AVATAR_CHOICE(),
        Gamemode.GAME_MODE_FIRST_WM => new GAME_MODE_FIRST_WM(),
        Gamemode.GAME_MODE_CINE => new GAME_MODE_CINE(),
        Gamemode.GAME_MODE_CLUB => new GAME_MODE_CLUB(),
        Gamemode.GAME_MODE_EDITOR => new GAME_MODE_EDITOR(),
        Gamemode.GAME_MODE_GAMBLING_HALL => new GAME_MODE_GAMBLING_HALL(),
        _ => Empty(mode),
    };

    static GameModeHandler Empty(Gamemode mode)
    {
        Log.NotImplemented($"Missing Gamemode: {mode} Create empty mode.");
        return new GAME_MODE_EMPTY(mode);
    }

    /// <summary>Startmodus aus der Kommandozeile: --gamemode FREERIDE | GAME_MODE_FREERIDE | 1.</summary>
    public static Gamemode? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (int.TryParse(s, out int n) && Enum.IsDefined(typeof(Gamemode), n)) return (Gamemode)n;
        if (!s.StartsWith("GAME_MODE_", StringComparison.OrdinalIgnoreCase)) s = "GAME_MODE_" + s;
        return Enum.TryParse<Gamemode>(s, true, out var m) ? m : null;
    }
}

/// <summary>Platzhalter für Modi ohne eigene Klasse.</summary>
public sealed class GAME_MODE_EMPTY : GameModeHandler
{
    readonly Gamemode _mode;
    public GAME_MODE_EMPTY(Gamemode mode) => _mode = mode;
    public override Gamemode Mode => _mode;
}
