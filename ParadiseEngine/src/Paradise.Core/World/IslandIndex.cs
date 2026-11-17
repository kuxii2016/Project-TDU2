using Paradise.Core.Files;
using System.Text.RegularExpressions;

namespace Paradise.Core.World;

/// <summary>
/// Finds all map files for an island ( pure IO/regex code):
///   Euro/Bnk/islands/island/level/island.bnk               Island LOD (terrain + objects)
///   .../level/commonworld.bnk                              Object library (trees, bushes, props)
///   .../level/area-A/area-A-B/area-A-B.bnk                 Area LOD
///   .../level/area-A/area-A-B/sector-A-B-C-D.bnk           Sector (full mesh + objects + vegetation)
///   .../level/spots.bin                                    POIs
///   .../fx/grasstypes.xml, .../fx/tallgrass.bnk            Grass
/// The files are located either in the Euro/Bnk folder or in the bigfile_*.big files (GameFiles extracts both).
/// </summary>
public sealed class IslandIndex
{
    public string IslandName = "";
    public string IslandDir = "", LevelDir = "", FxDir = "";
    /// <summary>Euro/Bnk (enthält database, vehicules, sound ...).</summary>
    public string? BnkDir;
    public string? IslandBank, CommonWorldBank, SpotsFile, GrassTypesXml, TallGrassBank, WorldActorsBank, FxIni, ExteriorDir;
    public readonly Dictionary<AreaKey, string> AreaBanks = new();
    public readonly Dictionary<SectorKey, string> SectorBanks = new();
    public int AreasX, AreasZ;

    static readonly Regex AreaRx = new(@"^area-(\d+)-(\d+)\.bnk$", RegexOptions.IgnoreCase);
    static readonly Regex SectorRx = new(@"^sector-(\d+)-(\d+)-(\d+)-(\d+)\.bnk$", RegexOptions.IgnoreCase);

    /// <summary>Searches for the “islands” folder starting from the specified path (game folder, Euro, Bnk, or islands).</summary>
    public static string? FindIslandsDir(string? root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        if (!SamePath(GameFiles.Root, root) && GameFiles.IsGameRoot(root)) GameFiles.Init(root);
        var bnk = GameFiles.BnkDir;
        if (bnk != null)
        {
            var islands = Path.Combine(bnk, "islands");
            if (GameFiles.DirectoryExists(islands)) return islands;
        }

        string[] candidates =
        {
            root,
            Path.Combine(root, "islands"),
            Path.Combine(root, "Bnk", "islands"),
            Path.Combine(root, "Euro", "Bnk", "islands"),
            Path.Combine(root, "Euro~", "Bnk", "islands"),
            Path.Combine(root, "euro", "bnk", "islands"),
        };
        foreach (var c in candidates)
            if (GameFiles.DirectoryExists(c) && (GameFiles.DirectoryExists(Path.Combine(c, "ibiza")) || GameFiles.DirectoryExists(Path.Combine(c, "hawai"))))
                return c;
        return null;
    }

    public static IEnumerable<string> ListIslands(string? root)
    {
        var dir = FindIslandsDir(root);
        if (dir == null) yield break;
        foreach (var d in GameFiles.GetDirectories(dir))
            if (GameFiles.DirectoryExists(Path.Combine(d, "level"))) yield return Path.GetFileName(d);
    }

    public static IslandIndex Scan(string? root, string island)
    {
        var islands = FindIslandsDir(root) ?? throw new DirectoryNotFoundException("islands-Ordner nicht gefunden unter: " + root);
        var idx = new IslandIndex { IslandName = island, IslandDir = Path.Combine(islands, island) };
        idx.BnkDir = GameFiles.BnkDir ?? Directory.GetParent(islands)?.FullName;
        idx.LevelDir = Path.Combine(idx.IslandDir, "level");
        idx.FxDir = Path.Combine(idx.IslandDir, "fx");
        if (!GameFiles.DirectoryExists(idx.LevelDir))
            throw new DirectoryNotFoundException(idx.LevelDir);

        idx.IslandBank = FileOrNull(Path.Combine(idx.LevelDir, island + ".bnk"));
        idx.CommonWorldBank = FileOrNull(Path.Combine(idx.LevelDir, "commonworld.bnk"));
        idx.SpotsFile = FileOrNull(Path.Combine(idx.LevelDir, "spots.bin"));
        idx.GrassTypesXml = FileOrNull(Path.Combine(idx.FxDir, "grasstypes.xml"));
        idx.TallGrassBank = FileOrNull(Path.Combine(idx.FxDir, "tallgrass.bnk"));
        idx.FxIni = FileOrNull(Path.Combine(idx.FxDir, "fx.ini"));
        var ext = Path.Combine(idx.IslandDir, "exterior");
        idx.ExteriorDir = GameFiles.DirectoryExists(ext) ? ext : null;
        idx.WorldActorsBank = FileOrNull(Path.Combine(idx.IslandDir, "scenarics", "packs", "worldactors.bnk"));

        int maxA = -1, maxB = -1;
        foreach (var f in GameFiles.GetFiles(idx.LevelDir, "*.bnk", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            var m = SectorRx.Match(name);
            if (m.Success)
            {
                var k = new SectorKey(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
                idx.SectorBanks[k] = f;
                continue;
            }
            m = AreaRx.Match(name);
            if (m.Success)
            {
                var a = new AreaKey(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                idx.AreaBanks[a] = f;
                maxA = System.Math.Max(maxA, a.X);
                maxB = System.Math.Max(maxB, a.Z);
            }
        }
        idx.AreasX = maxA + 1;
        idx.AreasZ = maxB + 1;
        return idx;
    }

    static string? FileOrNull(string p) => GameFiles.Exists(p) ? p : null;

    static bool SamePath(string? a, string? b)
    {
        if (a == null || b == null) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public override string ToString() => $"{IslandName}: {AreaBanks.Count} Areas ({AreasX}x{AreasZ}), {SectorBanks.Count} Sektoren";
}
