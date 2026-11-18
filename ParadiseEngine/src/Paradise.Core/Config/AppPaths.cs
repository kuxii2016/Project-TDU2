namespace Paradise.Core.Config;

/// <summary>
/// Folder of the launched EXE. For a single-file EXE (PublishSingleFile, e.g., with IncludeNativeLibrariesForSelfExtract /
/// IncludeAllContentForSelfExtract), AppContext.BaseDirectory points to the extraction folder in %TEMP%—but the game data (Euro,
/// GamePC.cpr, Physics.cpr …) is located next to the EXE. Environment.ProcessPath is always the actual path of the EXE.
/// </summary>
public static class AppPaths
{
    /// <summary>Folder containing the exe (game data, patch files). Fallback: AppContext.BaseDirectory.</summary>
    public static string ExeDir { get; } = Resolve();

    /// <summary>Extraction folder for the single-file EXE or output folder (embedded files such as icons).</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>Options for searching for files: first next to the exe file, then the extracted folder (without duplicates).</summary>
    public static IEnumerable<string> SearchDirs()
    {
        yield return ExeDir;
        if (!string.Equals(Path.GetFullPath(BaseDir).TrimEnd('\\', '/'), Path.GetFullPath(ExeDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            yield return BaseDir;
    }

    static string Resolve()
    {
        try
        {
            var p = Environment.ProcessPath;
            var dir = string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }
        catch (Exception) { }
        return AppContext.BaseDirectory;
    }
}