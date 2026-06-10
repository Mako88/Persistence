using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;

namespace Persistence.Runtime;

/// <summary>
/// Background service that periodically checks for due scheduled events, marks them
/// as triggered, and notifies subscribers. Started by <see cref="Orchestrator"/> after
/// session initialization.
/// </summary>
[Singleton]
public class WakeUpMonitor : IWakeUpMonitor
{
    private readonly IScheduledEventRepository scheduledEvents;
    private readonly IEventBus eventBus;

    /// <summary>
    /// Constructor
    /// </summary>
    public WakeUpMonitor(IScheduledEventRepository scheduledEvents, IEventBus eventBus)
    {
        this.scheduledEvents = scheduledEvents;
        this.eventBus = eventBus;
    }

    /// <summary>
    /// Starts the polling loop on a background thread. Runs until <paramref name="ct"/> is cancelled.
    /// </summary>
    public void Start(CancellationToken ct) =>
        Task.Run(() => RunAsync(ct), ct);

    /// <summary>
    /// Polling loop that checks for due events on a fixed interval until cancelled
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await CheckAndFireAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Swallow unexpected errors and keep polling.
            }
        }
    }

    /// <summary>
    /// Finds due events, marks each as triggered, and notifies subscribers (awaiting each subscriber's
    /// handler — so a wake turn runs to completion before the next event fires). Used by the 30s poll
    /// loop and, as a single sweep, by the headless wake-runner.
    /// </summary>
    public async Task CheckAndFireAsync(CancellationToken ct = default)
    {
        var dueEvents = (await scheduledEvents.GetDueEventsAsync()).ToList();

        foreach (var evt in dueEvents)
        {
            try
            {
                // Mark before publishing so a persistence failure can't notify subscribers of an
                // event still flagged Pending.
                await scheduledEvents.MarkTriggeredAsync(evt, ct: ct);
                await eventBus.PublishAsync(this, new ScheduledEventTriggered(evt));
            }
            catch (Exception)
            {
                // Isolate per-event failures: one event failing to persist or notify must not starve
                // the others in this sweep. A failed event stays Pending and due, so it is retried on
                // the next poll.
            }
        }
    }
}
