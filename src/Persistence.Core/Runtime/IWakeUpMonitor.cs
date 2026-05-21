namespace Persistence.Runtime;

/// <summary>
/// Background service that monitors for due scheduled events and triggers them
/// </summary>
public interface IWakeUpMonitor
{
    /// <summary>
    /// Starts the polling loop on a background thread. Runs until <paramref name="ct"/> is cancelled.
    /// </summary>
    void Start(CancellationToken ct);
}