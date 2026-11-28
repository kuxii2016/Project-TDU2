using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Platform;
using Paradise.Game.Screens;
using Paradise.Game.World;

namespace Paradise.Game.Gamemodes;

/// <summary>Was ein Spielmodus von der Umgebung braucht (bereitgestellt vom ScreenModule).</summary>
public interface IGameModeContext
{
    /// <summary>Oberfläche laden (einmal, danach aus dem Zwischenspeicher).</summary>
    VmfPlayer? Open(ScreenDef screen);
    /// <summary>Taste in diesem Bild neu gedrückt (Windows-VK-Code).</summary>
    bool Pressed(int vk);
    /// <summary>Pausemenü (main.bnk) sichtbar?</summary>
    bool Paused { get; set; }
    /// <summary>Startparameter (Server-Adresse für den Login).</summary>
    LaunchOptions Options { get; }
    /// <summary>Welt (Streamer, Kamera) – null ohne Welt.</summary>
    TerrainModule? Terrain { get; }
    /// <summary>Fahrzeuge (Datenbank, Spawnen) – null ohne Welt.</summary>
    VehicleModule? Vehicles { get; }
    /// <summary>Spiel beenden (Unity Application.Quit).</summary>
    void Quit();
}

/// <summary>
/// Ein Spielmodus (GAME_MODE_*). Je Modus eine Klasse gleichen Namens in Game/Gamemodes.
/// Ablauf bei einem Wechsel: alter Modus <see cref="Exit"/>, Oberflächen aus <see cref="Screens"/> einblenden, neuer Modus
/// <see cref="Enter"/>; danach jedes Bild <see cref="Run"/> (Logik/Eingaben) und <see cref="Render"/> (Zeichnen).
/// </summary>
public abstract class GameModeHandler
{
    /// <summary>Der Modus, den diese Klasse abarbeitet.</summary>
    public abstract Gamemode Mode { get; }
    /// <summary>Oberflächen, die in diesem Modus sichtbar sind (die übrigen werden ausgeblendet, bleiben aber geladen).</summary>
    public virtual IReadOnlyList<ScreenDef> Screens => Array.Empty<ScreenDef>();

    /// <summary>Beim Wechsel in diesen Modus (Oberflächen sind schon eingeblendet). from == GAME_MODE_PAUSE: Weiterspielen nach dem Pausemenü.</summary>
    public virtual void Enter(IGameModeContext ctx, Gamemode from) { }
    /// <summary>Jedes Bild: Logik, Eingaben, Oberflächen-Werte.</summary>
    public virtual void Run(IGameModeContext ctx, in GameTime time) { }
    /// <summary>Jedes Bild: modusspezifisches Zeichnen (Welt/Fahrzeuge zeichnen ihre Module).</summary>
    public virtual void Render(IGameModeContext ctx, in GameTime time) { }
    /// <summary>Beim Verlassen dieses Modus.</summary>
    public virtual void Exit(IGameModeContext ctx, Gamemode to) { }

    protected const int VK_ESCAPE = 0x1B;

    // Profiler-Messpunkte "GAME_MODE_X.Run" / ".Render" (ScreenModule misst damit den aktiven Modus)
    ProfilerMarker? _runMarker, _renderMarker;
    internal ProfilerMarker RunMarker => _runMarker ??= ProfilerMarker.Get(GetType().Name + ".Run", ProfilerCategory.Scripts);
    internal ProfilerMarker RenderMarker => _renderMarker ??= ProfilerMarker.Get(GetType().Name + ".Render", ProfilerCategory.Rendering);

    /// <summary>
    /// ESC: Pausemenü öffnen – heute global in ScreenModule.Update (Unity TDU2PauseMenu). Hier nur noch für Modusse,
    /// die es extern aufrufen; offen gehört ESC dem Film (Zurück/Schließen).
    /// </summary>
    protected static void HandlePauseKey(IGameModeContext ctx)
    {
        if (ctx.Paused) return;
        if (ctx.Pressed(VK_ESCAPE)) ctx.Paused = true;
    }
}
