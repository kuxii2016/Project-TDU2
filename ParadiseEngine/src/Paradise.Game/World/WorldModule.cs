using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Platform;
using Paradise.Core.World;

namespace Paradise.Game.World;

/// <summary>
/// Findet den Euro-/StreamingAssets-Ordner und listet beim Start die Inseln
/// und ihre Sektoren auf. Grundlage für das spätere Terrain-Streaming
/// (Vorbild: TDU2MapStreamer aus dem Unity-Projekt).
/// </summary>
public sealed class WorldModule : GameModule
{
    private readonly LaunchOptions _opts;
    public string? Root { get; private set; }
    public string? IslandsDir { get; private set; }

    public WorldModule(LaunchOptions opts) => _opts = opts;

    public override void Load()
    {
        Root = ResolveRoot();
        if (Root == null)
        {
            Log.Warn("WorldModule: kein Euro/StreamingAssets-Ordner gefunden. " +
                     "Mit --root <Pfad> angeben oder Euro neben die exe legen.");
            return;
        }

        Paradise.Core.Config.GameData.Root = Root;
        IslandsDir = IslandIndex.FindIslandsDir(Root);
        Log.Info($"WorldModule: Root = {Root}");
        Log.Info($"WorldModule: Daten = {(Paradise.Core.Files.GameFiles.IsPacked ? "gepackt (Bigfiles)" : "lose")}, " +
                 $"{Paradise.Core.Files.GameFiles.ArchiveCount} Archive");
        Log.Info($"WorldModule: islands = {IslandsDir}");

        foreach (var island in IslandIndex.ListIslands(Root))
        {
            try
            {
                var idx = IslandIndex.Scan(Root, island);
                Log.Info("  " + idx);
            }
            catch (Exception ex)
            {
                Log.Warn($"  Insel '{island}' konnte nicht gescannt werden: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reihenfolge: --root &gt; neben der exe &gt; Entwicklungs-Pfad (StreamingAssets).
    /// </summary>
    private string? ResolveRoot()
    {
        var candidates = new List<string?>
        {
            _opts.GameRoot,
            Paradise.Core.Config.AppPaths.ExeDir,   // neben der exe (Single-File: nicht der Entpack-Ordner in %TEMP%)
            Paradise.Core.Config.AppPaths.BaseDir,
            // Entwicklungs-Komfort: das Unity-StreamingAssets, in dem Euro aktuell liegt.
            @"E:\Unity3d\TDU2 To Unity3D\Client\Assets\StreamingAssets",
        };

        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && Paradise.Core.Files.GameFiles.IsGameRoot(c))
                return c;
        return null;
    }
}
