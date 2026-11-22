using Paradise.Core.Config;
using System.IO;
using System.Text.Json;

namespace Paradise.Launcher;

/// <summary>
/// Einstellungen des Launchers. Liegt unter
/// Documents\KuxiiSoft\TDU2toUnity3D\Config\launcher.json.
/// </summary>
public sealed class LauncherConfig
{
    public string MasterUrl { get; set; } = "http://kuxii.de:25564";
    public string PlayerName { get; set; } = Environment.UserName;
    /// <summary>Pfad zur Spiel-exe. Leer = neben dem Launcher suchen.</summary>
    public string GameExe { get; set; } = "";

    private static string Path => System.IO.Path.Combine(UserPaths.Config, "launcher.json");

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(Path)) ?? new LauncherConfig();
        }
        catch { /* beschädigte Datei -> Standardwerte */ }
        return new LauncherConfig();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* nicht kritisch */ }
    }

    /// <summary>Findet die Spiel-exe: konfiguriert, sonst neben dem Launcher.</summary>
    public string ResolveGameExe()
    {
        if (!string.IsNullOrWhiteSpace(GameExe) && File.Exists(GameExe)) return GameExe;
        string dir = Paradise.Core.Config.AppPaths.ExeDir;   // Single-File-exe: echter Ordner, nicht %TEMP%
        foreach (var name in new[] { "ParadiseEngine.exe", "ParadiseEngine" })
        {
            string p = System.IO.Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return System.IO.Path.Combine(dir, "ParadiseEngine.exe");
    }
}
