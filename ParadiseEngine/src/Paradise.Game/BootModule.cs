using Paradise.Core.Engine;
using Paradise.Core.Platform;

namespace Paradise.Game;

/// <summary>
/// Erstes, minimales Spiel-Modul. Zeigt, wo die Startparameter ankommen und
/// dient als Platzhalter, bis World-Streaming, Fahrzeuge und Online folgen.
/// </summary>
public sealed class BootModule : GameModule
{
    private readonly LaunchOptions _opts;
    private double _nextLog;

    public BootModule(LaunchOptions opts) => _opts = opts;

    public override void Load()
    {
    }

    public override void Update(in GameTime time)
    {
    }
}
