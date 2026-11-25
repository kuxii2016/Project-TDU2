using Paradise.Core.Engine;
using Paradise.Core.Platform;

namespace Paradise.Game;

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