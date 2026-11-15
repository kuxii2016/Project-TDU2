using System.Globalization;

namespace Paradise.Core.Platform;

/// <summary>
/// Startparameter der Game-exe. Vom Launcher gesetzt, z. B.:
///   ParadiseEngine.exe --server 93.42.11.8:7777 --name Michael --fullscreen
/// Alternativ URI-Schema:  tdu2://connect/93.42.11.8:7777
///
/// Unbekannte Argumente werden ignoriert (vorwärtskompatibel).
/// </summary>
public sealed class LaunchOptions
{
    public string? ServerHost { get; private set; }
    public int ServerPort { get; private set; }
    public string? PlayerName { get; private set; }
    public bool Fullscreen { get; private set; }
    public bool Windowed { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? GameRoot { get; private set; }     // Pfad zum Euro-Ordner, falls nicht automatisch gefunden
    public bool Verbose { get; private set; }
    public int Gamemode { get; private set; } = 0; // 0 = Bootmenu, 1 = Login, 2 = Freifahrt

    public bool HasServer => !string.IsNullOrEmpty(ServerHost) && ServerPort > 0;

    public static LaunchOptions Parse(string[] args)
    {
        var o = new LaunchOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            // URI-Schema: tdu2://connect/ip:port
            if (a.StartsWith("tdu2://", StringComparison.OrdinalIgnoreCase))
            {
                ParseUri(o, a);
                continue;
            }

            switch (a.ToLowerInvariant())
            {
                case "--server" or "-s":
                    if (TryNext(args, ref i, out var ep)) SetEndpoint(o, ep);
                    break;
                case "--name" or "-n":
                    if (TryNext(args, ref i, out var n)) o.PlayerName = n;
                    break;
                case "--root":
                    if (TryNext(args, ref i, out var r)) o.GameRoot = r;
                    break;
                case "--width":
                    if (TryNext(args, ref i, out var w) && int.TryParse(w, out var wi)) o.Width = wi;
                    break;
                case "--height":
                    if (TryNext(args, ref i, out var h) && int.TryParse(h, out var hi)) o.Height = hi;
                    break;
                case "--fullscreen": o.Fullscreen = true; break;
                case "--windowed": o.Windowed = true; break;
                case "--verbose" or "-v": o.Verbose = true; break;
                case "--gamemode" or "-gme":
                    if (TryNext(args, ref i, out var gm) && int.TryParse(gm, out var gmi)) o.Gamemode = gmi;
                    break;
            }
        }
        return o;
    }

    private static void ParseUri(LaunchOptions o, string uri)
    {
        // tdu2://connect/93.42.11.8:7777
        var rest = uri["tdu2://".Length..].TrimEnd('/');
        const string connect = "connect/";
        int idx = rest.IndexOf(connect, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) SetEndpoint(o, rest[(idx + connect.Length)..]);
    }

    private static void SetEndpoint(LaunchOptions o, string ep)
    {
        // host:port
        int c = ep.LastIndexOf(':');
        if (c > 0 && int.TryParse(ep[(c + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
        {
            o.ServerHost = ep[..c];
            o.ServerPort = port;
        }
        else
        {
            o.ServerHost = ep;
            o.ServerPort = 7777; // Standardport
        }
    }

    private static bool TryNext(string[] args, ref int i, out string value)
    {
        if (i + 1 < args.Length) { value = args[++i]; return true; }
        value = string.Empty; return false;
    }

    public string ToCommandLine()
    {
        var parts = new List<string>();
        if (HasServer) { parts.Add("--server"); parts.Add($"{ServerHost}:{ServerPort}"); }
        if (!string.IsNullOrEmpty(PlayerName)) { parts.Add("--name"); parts.Add(Quote(PlayerName)); }
        if (!string.IsNullOrEmpty(GameRoot)) { parts.Add("--root"); parts.Add(Quote(GameRoot)); }
        if (Fullscreen) parts.Add("--fullscreen");
        if (Windowed) parts.Add("--windowed");
        if (Verbose) parts.Add("--verbose");
        return string.Join(' ', parts);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
