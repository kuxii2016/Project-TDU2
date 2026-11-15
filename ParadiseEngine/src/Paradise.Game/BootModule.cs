using Paradise.Core.Diagnostics;
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
        Log.Info("BootModule geladen.");
        if (_opts.HasServer)
        {
            // Etappe „Online": hier später TDU2Net/MasterClient verbinden.
            Log.Info($"[Online] Zielserver: {_opts.ServerHost}:{_opts.ServerPort}");
        }
    }

    public override void Update(in GameTime time)
    {
        // Lebenszeichen alle 5 s, damit man im Log sieht, dass der Loop läuft.
        if (time.TotalTime >= _nextLog)
        {
            _nextLog = time.TotalTime + 5.0;
            Log.Debug($"Gameloop … t={time.TotalTime:F1}s, Frame={time.FrameCount}, dt={time.DeltaTime * 1000f:F1}ms");
        }
    }
}
