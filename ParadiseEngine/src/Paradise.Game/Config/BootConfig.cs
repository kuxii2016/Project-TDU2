namespace Paradise.Game.Config;

/// <summary>Spielstart (GAME_MODE_BOOTMENU): Logo und Original-Intros.</summary>
public sealed class BootConfig
{
    /// <summary>Wie lange startup.png (in die exe eingebettet) gezeigt wird, in Sekunden.</summary>
    public float LogoSeconds { get; set; } = 3f;
    /// <summary>Ein-/Ausblenden des Logos, in Sekunden.</summary>
    public float LogoFadeSeconds { get; set; } = 0.5f;
    /// <summary>Intros aus Euro\Bnk\Video, in dieser Reihenfolge.</summary>
    public string[] Videos { get; set; } = { "Bumper_ATARI.mkv", "Bumper_EDEN_TDU.mkv" };
    /// <summary>Lautstärke der Intros (0..1).</summary>
    public float VideoVolume { get; set; } = .65f;
    /// <summary>Taste oder Mausklick überspringt Logo/Video.</summary>
    public bool Skippable { get; set; } = true;
}
