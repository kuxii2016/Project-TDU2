using Paradise.Core.Files;

namespace Paradise.Core.Config;

/// <summary>
/// Ort der Original-Spieldaten (Ordner mit "Euro" oder den bigfile_*.big). Wird vom Spiel beim Start
/// gesetzt (WorldModule); Core-Systeme (Flash, Audio …) lesen daraus ihre Banken
/// (Unity: Application.streamingAssetsPath). Lose Dateien haben Vorrang vor den Bigfiles.
/// </summary>
public static class GameData
{
    private static string? _root;

    public static string? Root
    {
        get => _root;
        set => _root = GameFiles.Init(value) ?? value;
    }

    /// <summary>…\Euro\Bnk (bei gepackten Installationen ggf. nur Video; Banken kommen aus den Bigfiles).</summary>
    public static string? BnkDir => GameFiles.BnkDir ?? (_root == null ? null : Path.Combine(_root, "Euro", "Bnk"));
}
