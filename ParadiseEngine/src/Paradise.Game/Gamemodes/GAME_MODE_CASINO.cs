using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>Casino (casino.bnk) – TDU2Interiors.</summary>
public class GAME_MODE_CASINO : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_CASINO;
    static readonly ScreenDef[] _screens = { ScreenDefs.Casino };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
}
