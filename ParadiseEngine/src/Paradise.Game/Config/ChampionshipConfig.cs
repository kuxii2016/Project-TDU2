namespace Paradise.Game.Config;

/// <summary>
/// Einzelspieler-Meisterschaften (Gamemodes/Utils/Championships, GAME_MODE_CHAMPIONSHIP). Grundwerte wie im Unity-Projekt
/// (Inspektorwerte von TDU2Championships).
/// </summary>
public static class ChampionshipConfig
{
    /// <summary>Alle Meisterschaften freischalten (Test).</summary>
    public static bool UnlockAll = false;
    /// <summary>Nur mit einem Auto der Meisterschaftsklasse starten (wie im Spiel).</summary>
    public static bool RequireCarCategory = true;
    /// <summary>Abstand zum Treffpunkt/Startpunkt (m), ab dem gestartet werden kann.</summary>
    public static float TriggerRadius = 16f;
    /// <summary>Höchsttempo (km/h), um am Treffpunkt/Startpunkt starten zu können.</summary>
    public static float TriggerMaxKmh = 35f;
    /// <summary>Punkte je Platz.</summary>
    public static int[] PointsTable = { 10, 8, 6, 5, 4, 3, 2, 1 };
    /// <summary>Tempo-Faktor der Gegner (1 = Standard).</summary>
    public static float BotSpeedScale = 1f;
    /// <summary>Abstand der Startreihen (m).</summary>
    public static float GridRowSpacing = 8f;
    /// <summary>Anteil des Renn-Preisgelds je Platz (1., 2., 3. …). [vermutet]</summary>
    public static float[] PrizeShare = { 1f, 0.5f, 0.25f };
    /// <summary>Während eines Meisterschaftsrennens Mitspieler ausblenden und selbst unsichtbar sein (Unity TDU2OnlineSettings.HidePlayersInCups).</summary>
    public static bool HidePlayersInCups = true;
}
