using Persistence.Runtime;

namespace Persistence.Api;

/// <summary>
/// Runs the <see cref="IOrchestrator"/> for the web host's lifetime. RunAsync initialises the
/// database, subscribes to local-peer input, starts the wake-up monitor, and awaits the display
/// provider — which for the API never self-completes, so it stays alive until host shutdown.
/// </summary>
public class OrchestratorHostedService : BackgroundService
{
    private readonly IOrchestrator orchestrator;

    /// <summary>
    /// Constructor
    /// </summary>
    public OrchestratorHostedService(IOrchestrator orchestrator)
    {
        this.orchestrator = orchestrator;
    }

    /// <summary>
    /// Runs the orchestrator for the duration of the host's lifetime, stopping when cancelled
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        orchestrator.RunAsync(stoppingToken);
}
