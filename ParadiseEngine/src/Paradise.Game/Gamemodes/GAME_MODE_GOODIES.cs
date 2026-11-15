using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>Kleidungsgeschäft (shopsavatar.bnk) – TDU2Interiors.</summary>
public class GAME_MODE_GOODIES : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_GOODIES;
    static readonly ScreenDef[] _screens = { ScreenDefs.Avatar };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
}
