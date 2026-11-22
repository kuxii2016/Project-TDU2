using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Platform;
using Paradise.Core.Render;
using Paradise.Core.Render.Vulkan;
using Paradise.Game;
using Paradise.Game.Config;

// 1) Absturzsicherung ganz zuerst, damit auch frühe Fehler einen Report erzeugen.
CrashHandler.Install("Game");
Log.Init("game");

// 2) Startparameter (vom Launcher übergeben).
var opts = LaunchOptions.Parse(args);
if (opts.Verbose) Log.MinLevel = LogLevel.Debug;
CrashHandler.SetContext("server", opts.HasServer ? $"{opts.ServerHost}:{opts.ServerPort}" : "(kein)");
CrashHandler.SetContext("player", opts.PlayerName ?? "(anonym)");

Log.Info(AppConfig.EngineName + " v.: " + AppConfig.EngineVersion + " started");
if (opts.HasServer)
    Log.Info($"Verbinde später zu Server {opts.ServerHost}:{opts.ServerPort} als '{opts.PlayerName}'.");
else
    Log.Info("Kein Server übergeben – Einzelspieler/Freifahrt.");

// 3) Fenster + Renderer. Etappe 1: NullRenderer. Etappe 2: Vulkan.
int w = opts.Width > 0 ? opts.Width : 1280;
int h = opts.Height > 0 ? opts.Height : 720;

IGameWindow window = new Win32Window("Test Drive Unlimited 2", w, h);
// Grafik (Sichtweite/LOD, Kantenglättung, VSync, Texturen, Effekte): Game/Config/GraphicsConfig.cs – vor dem Renderer
new Paradise.Game.Config.GraphicsConfig().Apply();
// Bloom / Ambient Occlusion (Zusatz, nicht im Original): Stärken in Game/Config/PostFxConfig.cs
new Paradise.Game.Config.PostFxConfig().Apply();

// Vulkan-Renderer; fällt bei Init-Fehler NICHT automatisch zurück – ein
// Fehler landet im Crash-Report, damit man die Ursache sieht. Zum Testen ohne
// Grafik: --nullrenderer übergeben.
IRenderer renderer = args.Contains("--nullrenderer") ? new NullRenderer() : new VulkanRenderer();

var loop = new GameLoop(window, renderer);
// Physiktakt wie im Unity-Projekt während der Fahrt (TDU2VehicleManager.FixedTimeStep, Game/Config/VehicleConfig.cs)
loop.FixedStep = new Paradise.Game.Config.VehicleConfig().FixedTimeStep;
// Lautstärken: Game/Config/AudioConfig.cs
new Paradise.Game.Config.AudioConfig().Apply();

// 4) Spiel-Module registrieren. Hier wachsen World, Vehicles, UI, Online rein.
var world = new Paradise.Game.World.WorldModule(opts);
loop.Add(new BootModule(opts));
loop.Add(world);
var terrain = new Paradise.Game.World.TerrainModule(renderer, world, window, loop.Overlay);
loop.Add(terrain);
// Fahrzeuge (Laden/Aufbau/Zeichnen in Paradise.Core.Vehicles)
var vehicles = new Paradise.Game.World.VehicleModule(renderer, terrain, window, loop.Overlay);
loop.Add(vehicles);
// Wrecks/Metalldetektor: Daten aus wrecks.bnk, Anzeige ausschließlich im nativen HUD (hud.bnk/map.bnk)
loop.Add(new Paradise.Game.Gameplay.Wrecks(terrain, vehicles));
// Cruising/Road-Discovery: befahrene Straßen aufdecken (grau -> farbig), Dealer freischalten, Pause/F1 anbinden
var discovery = new Paradise.Game.Gameplay.Discovery(terrain, vehicles);
loop.Add(discovery);
// Wettbewerbs-Fortschritt (Rennschule/Meisterschaften/Pokale) für Pausemenü und UserProgress
loop.Add(new Paradise.Game.Gameplay.Racing());
// Collection- und Social-Fortschritt (DBRewardXP + Savegame-Zähler) für Pausemenü und UserProgress
loop.Add(new Paradise.Game.Gameplay.Collectable());
loop.Add(new Paradise.Game.Gameplay.Social());
// Original-Oberflächen je Spielmodus (Flash/Sounds aus Paradise.Core)
// Login wie im Original nach dem Bootmenü: Online-Profile (Server aus --server) oder Offline-Profile (nur lokale DATA).
// Direkt Freifahrt ohne Profil: --freeride
// Beliebiger Startmodus: --gamemode FREERIDE | GAME_MODE_FREERIDE | 1 (Klasse in Game/Gamemodes)
bool login = !args.Contains("--freeride");
int gmArg = Array.FindIndex(args, a => string.Equals(a, "--gamemode", StringComparison.OrdinalIgnoreCase));
// Standard wie im Original: GAME_MODE_BOOTMENU (Logo + Intros), danach Login (mit Server) bzw. Freifahrt.
// --login / --freeride überspringen das Intro.
Paradise.Game.Gamemodes.GAME_MODE_BOOTMENU.Next = login ? Paradise.Game.Screens.Gamemode.GAME_MODE_LOGIN : Paradise.Game.Screens.Gamemode.GAME_MODE_FREERIDE;
bool skipIntro = args.Contains("--login") || args.Contains("--freeride");
var startMode = (gmArg >= 0 && gmArg + 1 < args.Length ? Paradise.Game.Gamemodes.GameModes.Parse(args[gmArg + 1]) : null)
    ?? (skipIntro ? Paradise.Game.Gamemodes.GAME_MODE_BOOTMENU.Next : Paradise.Game.Screens.Gamemode.GAME_MODE_BOOTMENU);
// Spielstand im Original-Format (PLAYERSAVE/DATA) bei jedem Speichern mitschreiben (Unity: TDU2SaveGame.Hook)

Paradise.Core.Savegame.TDU2SaveGame.Hook();
loop.Add(new Paradise.Game.Screens.ScreenModule(window, opts, terrain, vehicles, loop.Stop) { StartMode = startMode });
// Entwicklermenü (F1, Unity TDU2DevMenu) – zuletzt, damit es über allen Oberflächen liegt
loop.Add(new Paradise.Game.Screens.DevMenu(renderer, terrain, vehicles, window, opts));

loop.Run();

Log.Info("Game Stopp, Clean up and Shutdown");
// Aufräumen Schritt für Schritt mit Log. Hängt ein Schritt (Spiel reagiert nach "Beenden" nicht mehr), nennt der Wächter ihn
// nach 5 s im Log und beendet den Prozess – die Ursache wird dann dort behoben.
string shutdownStep = "Module";
using var shutdownCts = new CancellationTokenSource();

var shutdownGuard = new Thread(() =>
{
    if (!shutdownCts.Token.WaitHandle.WaitOne(5000))
    {
        Log.Warn("Beenden hängt bei: " + shutdownStep);
        ProcessEnd.Now();
    }
})
{
    IsBackground = true,
    Name = "Beenden-Wächter"
};
shutdownGuard.Start();

Log.Info("Game Stopp, Clean up and Shutdown");

try
{
    Log.Debug("Beenden: Module");

    loop.Dispose();

    Log.Debug("Beenden: Renderer");
    renderer.Dispose();

    Log.Debug("Beenden: Fenster");
    window.Dispose();

    Log.Debug("Beenden: fertig");
    shutdownCts.Cancel();
}
catch (Exception ex)
{
    Log.Error("Fehler beim Shutdown: " + ex);
}
finally
{
    Log.Info("Beenden: Prozess wird beendet");
    ProcessEnd.Now();
    shutdownCts.Cancel();
}

//loop.Dispose();
//shutdownStep = "Renderer"; Log.Info("Beenden: Module freigegeben");
//renderer.Dispose();
//shutdownStep = "Fenster"; Log.Info("Beenden: Renderer freigegeben");
//window.Dispose();
//shutdownCts.Cancel();
//Log.Info("Beenden: fertig");
//// Alles ist freigegeben und gespeichert (Log: "fertig"). Environment.Exit(0) blieb danach hängen (Logs 10:34/10:41: "fertig",
//// Fenster offen): ExitProcess wartet beim Entladen der DLLs auf Treiber-/Audio-Threads. Wie Unity Application.Quit:
//// Prozess sofort beenden, ohne DLL-Entladen.
//Log.Info("Beenden: Prozess wird beendet");
//ProcessEnd.Now();

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
