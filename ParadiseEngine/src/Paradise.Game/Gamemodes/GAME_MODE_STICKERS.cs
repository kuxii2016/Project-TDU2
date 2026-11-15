using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>Aufkleber-Shop: Händler-Oberfläche (shopscar.bnk), Interieur-Ablauf aus InteriorUtils.</summary>
public class GAME_MODE_STICKERS : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_STICKERS;
    static readonly ScreenDef[] _screens = { ScreenDefs.Dealer };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.Dealer;
}
