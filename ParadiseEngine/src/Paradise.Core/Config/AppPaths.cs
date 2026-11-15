namespace Paradise.Core.Config;

/// <summary>
/// Ordner der gestarteten exe. Bei einer Single-File-exe (PublishSingleFile, z.B. mit IncludeNativeLibrariesForSelfExtract /
/// IncludeAllContentForSelfExtract) zeigt AppContext.BaseDirectory auf den Entpack-Ordner in %TEMP% – die Spieldaten (Euro,
/// GamePC.cpr, Physics.cpr …) liegen aber neben der exe. Environment.ProcessPath ist immer der echte Pfad der exe.
/// </summary>
public static class AppPaths
{
    /// <summary>Ordner der exe (Spieldaten, Korrekturdateien). Rückfall: AppContext.BaseDirectory.</summary>
    public static string ExeDir { get; } = Resolve();

    /// <summary>Entpack-Ordner der Single-File-exe bzw. Ausgabeordner (eingebettete Dateien wie Icons).</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>Kandidaten zum Suchen von Dateien: zuerst neben der exe, dann der Entpack-Ordner (ohne Doppelte).</summary>
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
