namespace Paradise.Game.Config;

/// <summary>Vorgaben des Friseurs (GAME_MODE_HAIRDRESSER).</summary>
public static class HairdresserConfig
{
    /// <summary>
    /// Mausdrehung der gehaltenen C_360 (wie die Schönheitsklinik): Anteil += -(2 x Mausachse) x Faktor x dt. Die Normierung der
    /// Mausachse liegt im verschlüsselten Teil der exe; der Faktor bringt Pixel auf die Größenordnung der Stick-Achse.
    /// </summary>
    public static float MouseRotateScale = 0.01f;
}
