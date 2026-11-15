namespace Paradise.Core.Config;

/// <summary>
/// Zentrale Pfade unter Documents\KuxiiSoft\Test Drive Unlimited 2.
/// Hier liegen Config, Savegames, Logs und Crash-Reports — nicht im
/// originalen Eden-Games-Ordner, damit das Originalspiel unberührt bleibt.
/// </summary>
public static class UserPaths
{
    public const string Vendor = "KuxiiSoft";
    public const string Product = "Test Drive Unlimited 2";

    /// <summary>…\Documents\KuxiiSoft\Test Drive Unlimited 2</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Vendor, Product);

    public static string Config => Ensure(Path.Combine(Root, "Config"));
    public static string Saves => Ensure(Path.Combine(Root, "Saves"));
    public static string Logs => Ensure(Path.Combine(Root, "Logs"));
    public static string Crashes => Ensure(Path.Combine(Root, "Crashes"));

    public static string ConfigFile => Path.Combine(Config, "paradise.json");
    public static string ServerListCache => Path.Combine(Config, "servers.json");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
