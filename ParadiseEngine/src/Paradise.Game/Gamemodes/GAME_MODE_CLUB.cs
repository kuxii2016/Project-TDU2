using Paradise.Game.Screens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
