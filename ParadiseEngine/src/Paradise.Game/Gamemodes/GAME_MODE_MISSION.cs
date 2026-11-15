using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>Mission: wie Freifahrt (HUD, Pausemenü, Karte).</summary>
public class GAME_MODE_MISSION : GAME_MODE_FREERIDE
{
    public override Gamemode Mode => Gamemode.GAME_MODE_MISSION;
}
