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

    /// <summary>
    /// Finds due events, marks each triggered, and publishes <c>ScheduledEventTriggered</c> for each
    /// (awaiting subscribers). One sweep — used by the interactive poll loop and by the headless
    /// wake-runner to fire due wakes once and exit.
    /// </summary>
    Task CheckAndFireAsync(CancellationToken ct = default);
}