using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Online;
using Paradise.Core.Platform;
using Paradise.Core.Render;
using Paradise.Core.Render.Vulkan;
using Paradise.Game;
using Paradise.Game.Config;

Log.Init("Gamelog");
CrashHandler.Install("Game");
#region args Reader & Crash Report Handler
var opts = LaunchOptions.Parse(args);
if (opts.Verbose)
    Log.MinLevel = LogLevel.Debug;
CrashHandler.SetContext("server", opts.HasServer ? $"{opts.ServerHost}:{opts.ServerPort}" : "(none)");
CrashHandler.SetContext("player", opts.PlayerName ?? "(anonym)");
CrashHandler.SetContext("engine-version", AppConfig.EngineVersion);
#endregion

Log.Info(AppConfig.EngineName + " v.: " + AppConfig.EngineVersion + " started");
if (opts.HasServer)
    Log.Info($"Connect later to the server at {opts.ServerHost}:{opts.ServerPort} as ‘{opts.PlayerName}’.");

#region Init Windows
int w = opts.Width > 0 ? opts.Width : 1280; int h = opts.Height > 0 ? opts.Height : 720;
IGameWindow window = new Win32Window("Test Drive Unlimited 2", w, h);
IRenderer renderer = args.Contains("--nullrenderer") ? new NullRenderer() : new VulkanRenderer();
#endregion

var loop = new GameLoop(window, renderer);

#region Set Gamesettings
try
{
    new Paradise.Game.Config.GraphicsConfig().Apply();
    new Paradise.Game.Config.PostFxConfig().Apply();
    new Paradise.Game.Config.AudioConfig().Apply();
    loop.FixedStep = new Paradise.Game.Config.VehicleConfig().FixedTimeStep;
}
catch (Exception ex)
{
    Log.Error("Failed init engine settings: " + ex.Message);
}
#endregion

#region Set Gamemodule
{
    var world = new Paradise.Game.World.WorldModule(opts);
    loop.Add(new BootModule(opts));
    loop.Add(world);
    var terrain = new Paradise.Game.World.TerrainModule(renderer, world, window, loop.Overlay);
    loop.Add(terrain);
    var vehicles = new Paradise.Game.World.VehicleModule(renderer, terrain, window, loop.Overlay);
    loop.Add(vehicles);
    loop.Add(new Paradise.Game.Gameplay.Wrecks(terrain, vehicles));
    var discovery = new Paradise.Game.Gameplay.Discovery(terrain, vehicles);
    loop.Add(discovery);
    loop.Add(new Paradise.Game.Gameplay.Racing());
    loop.Add(new Paradise.Game.Gameplay.Collectable());
    loop.Add(new Paradise.Game.Gameplay.Social());
    bool login = !args.Contains("--freeride");
    int gmArg = Array.FindIndex(args, a => string.Equals(a, "--gamemode", StringComparison.OrdinalIgnoreCase));
    Paradise.Game.Gamemodes.GAME_MODE_BOOTMENU.Next = login ? Paradise.Game.Screens.Gamemode.GAME_MODE_LOGIN : Paradise.Game.Screens.Gamemode.GAME_MODE_FREERIDE;
    bool skipIntro = args.Contains("--login") || args.Contains("--freeride");
    var startMode = (gmArg >= 0 && gmArg + 1 < args.Length ? Paradise.Game.Gamemodes.GameModes.Parse(args[gmArg + 1]) : null)
        ?? (skipIntro ? Paradise.Game.Gamemodes.GAME_MODE_BOOTMENU.Next : Paradise.Game.Screens.Gamemode.GAME_MODE_BOOTMENU);
    Paradise.Core.Savegame.TDU2SaveGame.Hook();
    loop.Add(new Paradise.Game.Screens.ScreenModule(window, opts, terrain, vehicles, loop.Stop) { StartMode = startMode });

//#if DEBUG
    //Developer Menue
    loop.Add(new Paradise.Game.Screens.DevMenu(renderer, terrain, vehicles, window, opts));
//#endif

    loop.Run();
}
#endregion

TDU2Session.SaveAsync();

#region Shutdown and Release recources
try
{
    loop.Dispose();
    renderer.Dispose();
    window.Dispose();
}
catch (Exception ex)
{
    Log.Error("Fehler beim Shutdown: " + ex);
}
finally
{
    Log.Info("Beenden: Prozess wird beendet");
    ProcessEnd.Now();
}
#endregion

static class ProcessEnd
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    public static void Now()
    {
        if (OperatingSystem.IsWindows()) TerminateProcess(GetCurrentProcess(), 0);
        Environment.Exit(0);
    }
}