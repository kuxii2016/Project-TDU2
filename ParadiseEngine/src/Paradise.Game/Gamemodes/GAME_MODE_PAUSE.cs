using Paradise.Core.Engine;
using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Pausemenü (ESC, main.bnk) – Unity TDU2PauseMenu als eigener Spielmodus. Der pausierte Modus bleibt im Hintergrund stehen
/// (Fahrzeug eingefroren, seine Oberflächen wie das HUD sichtbar); beim Schließen geht es dorthin zurück.
/// Öffnen/Schließen über <see cref="IGameModeContext.Paused"/> (ESC global in ScreenModule Update, wie Unity TDU2PauseMenu).
/// Die Logik des Menüs (Optionen, Steuerung, Spielerlisten, Beenden) liegt in Screens/PauseMenu.
/// </summary>
public class GAME_MODE_PAUSE : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_PAUSE;

    /// <summary>Modus, aus dem pausiert wurde (Rückkehr beim Schließen).</summary>
    public static Gamemode Return { get; private set; } = Gamemode.GAME_MODE_FREERIDE;
    static bool _returnForced;

    PauseMenu? _menu;
    /// <summary>Das Pausemenü (nach dem ersten Öffnen).</summary>
    public PauseMenu? Menu => _menu;

    /// <summary>Oberflächen des pausierten Modus bleiben sichtbar (HUD unter dem Menü); main.bnk zeigt das Menü selbst.</summary>
    public override IReadOnlyList<ScreenDef> Screens => GameModes.Get(Return).Screens;

    /// <summary>Pausieren: aktuellen Modus merken und in den Pausemodus wechseln.</summary>
    public static void Pause()
    {
        if (GameMode.Current == Gamemode.GAME_MODE_PAUSE) return;
        Return = GameMode.Current;
        _returnForced = GameMode.IsForced;
        GameMode.Push(Gamemode.GAME_MODE_PAUSE);
    }

    /// <summary>
    /// Modus nach dem Schließen statt des pausierten (exe: Pausemenü +0xac, z.B. "goed" -> 5 = GAME_MODE_EDITOR, 0x9e97a0).
    /// </summary>
    public static Gamemode? Next { get; set; }

    /// <summary>Weiterspielen: zurück in den pausierten Modus (oder in <see cref="Next"/>).</summary>
    public static void Resume()
    {
        if (GameMode.Current != Gamemode.GAME_MODE_PAUSE) return;
        if (Next is Gamemode n)
        {
            Next = null;
            GameMode.Set(n);   // gilt erst nach Pop (Pause ist ausdrücklich gesetzt)
            GameMode.Pop();
            return;
        }
        if (_returnForced) GameMode.Push(Return);
        else GameMode.Pop();
    }

    /// <summary>Pausemenü anlegen und die gespeicherten Optionen anwenden (Sprache, Grafik, Audio …), ohne es zu öffnen.
    /// Unity: TDU2PauseMenu lädt beim Start (EnsureLoaded) und wendet die Optionen an (ApplyAll).</summary>
    public void EnsureMenu(IGameModeContext ctx)
    {
        _menu ??= new PauseMenu(ctx);
        _menu.EnsureOptions();
    }

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _menu ??= new PauseMenu(ctx);
        _menu.Attach(ctx.Open(ScreenDefs.Pause));
        _menu.Open();
    }

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        if (_menu == null) return;
        _menu.Update(time.DeltaTime);
        if (!_menu.IsOpen) Resume();   // Menü hat sich geschlossen (paou, ESC, Notausgang F10)
    }

    public override void Exit(IGameModeContext ctx, Gamemode to) => _menu?.Close();
}
