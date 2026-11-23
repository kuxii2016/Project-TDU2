namespace Paradise.Game.Config;

/// <summary>
/// Schönheitsklinik (GAME_MODE_SURGERY); Werte nur hier im Code ändern.
/// </summary>
public static class SurgeryConfig
{
    /// <summary>
    /// Drehen des Avatars mit der Maus (rechte Taste gehalten): Maus-Delta (Pixel) -> Achswert des Originals. Das Spiel nimmt die
    /// Mausachse seines Eingabe-Managers (exe 0x5302E0, normiert) x -2 (0xBF0F0E); die Normierung liegt im verschlüsselten Teil,
    /// darum hier ein Faktor für die Engine-Maus.
    /// </summary>
    public static float MouseRotateScale = 0.01f;
}
