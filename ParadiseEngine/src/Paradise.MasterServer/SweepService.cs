namespace Paradise.MasterServer;

/// <summary>Räumt periodisch tote Server aus der Liste.</summary>
public sealed class SweepService : BackgroundService
{
    private readonly ServerRegistry _registry;
    private readonly ILogger<SweepService> _log;

    public SweepService(ServerRegistry registry, ILogger<SweepService> log)
    {
        _registry = registry;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int removed = _registry.Sweep();
                if (removed > 0) _log.LogInformation("{Count} inaktive Server entfernt.", removed);
            }
            catch (Exception ex) { _log.LogError(ex, "Sweep fehlgeschlagen."); }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
