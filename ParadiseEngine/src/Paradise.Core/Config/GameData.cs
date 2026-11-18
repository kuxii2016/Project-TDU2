using Paradise.Core.Files;

namespace Paradise.Core.Config;

/// <summary>
/// Location of the original game data (folder containing “Euro” or the bigfile_*.big files). Set by the game at startup
/// (WorldModule); core systems (Flash, Audio, etc.) read their banks from this location
/// (Unity: Application.streamingAssetsPath). Individual files take precedence over the bigfiles.
/// </summary>
public static class GameData
{
    private static string? _root;

    public static string? Root
    {
        get => _root;
        set => _root = GameFiles.Init(value) ?? value;
    }

    /// <summary>…\Euro\Bnk (for compressed installations, possibly only the video; the banks come from the Bigfiles).</summary>
    public static string? BnkDir => GameFiles.BnkDir ?? (_root == null ? null : Path.Combine(_root, "Euro", "Bnk"));
}
