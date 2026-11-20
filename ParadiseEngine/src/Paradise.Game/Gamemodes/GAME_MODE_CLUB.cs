using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes
{
    public class GAME_MODE_CLUB : InteriorUtils
    {
        public override Gamemode Mode => Gamemode.GAME_MODE_CLUB;
        static readonly ScreenDef[] _screens = { ScreenDefs.Club };
        public override IReadOnlyList<ScreenDef> Screens => _screens;
        protected override ScreenDef Screen => ScreenDefs.Club;
    }
}
