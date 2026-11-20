using Paradise.Core.Flash;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Original-Oberfläche (Flash-Bank) mit Ebene (GuiDepth), Maus und Musik – wie im Unity-Projekt:
///   general.bnk -30 (Popups, immer), log_in2.bnk -20 + Musik LOGIN (TDU2Login), main.bnk -10 (TDU2PauseMenu),
///   map.bnk -8 (TDU2BigMap), Interieurs -1 (shopscar/home/casino/shopsavatar, TDU2Interiors), hud.bnk 1 (TDU2Minimap).
/// </summary>
public sealed record ScreenDef(string Bank, int Depth, bool Mouse, string? Music = null, Action<VmfPlayer>? Before = null);

/// <summary>Alle bekannten Oberflächen (eine Stelle für Bank/Ebene/Musik).</summary>
public static class ScreenDefs
{
    public static readonly ScreenDef General = new("general.bnk", -30, true);
    public static readonly ScreenDef Login = new("log_in2.bnk", -20, true, "LOGIN", p => { p.SetValue("/:IsCasinoStandAlone", false); p.SetValue("/:NAT_msg", false); });
    public static readonly ScreenDef Pause = new("main.bnk", -10, true);
    public static readonly ScreenDef Map = new("map.bnk", -8, true);
    /// <summary>Challenge-Editor (GAME_MODE_EDITOR, exe Oberfläche gmeditor.bnk).</summary>
    public static readonly ScreenDef Editor = new("gmeditor.bnk", -8, true);
    public static readonly ScreenDef Hud = new("hud.bnk", 1, false);
    public static readonly ScreenDef Dealer = new("shopscar.bnk", -1, true);
    public static readonly ScreenDef Home = new("home.bnk", -1, true);
    public static readonly ScreenDef Club = new("club.bnk", -1, true);
    public static readonly ScreenDef Casino = new("casino.bnk", -1, true);
    public static readonly ScreenDef Avatar = new("shopsavatar.bnk", -1, true);
    /// <summary>Flughafen (GAME_MODE_AIRPORT, exe Oberfläche "AIRPORT").</summary>
    public static readonly ScreenDef Airport = new("airport.bnk", -1, true);
    /// <summary>TDU Media (Fernseher im Haus, Unity TDU2TDUMedia: GuiDepth -2), wird von GAME_MODE_LIVING geöffnet/geschlossen.</summary>
    public static readonly ScreenDef TdMedia = new("tdmedia.bnk", -2, true);
    /// <summary>Rennschule (GAME_MODE_SCHOOL, Unity RacingSchoolMenu: GuiDepth -2).</summary>
    public static readonly ScreenDef School = new("r_school.bnk", -2, true);
}
