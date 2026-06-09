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
                await CheckAndFireAsync();
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
    /// Finds due events, marks each as triggered, and notifies subscribers. Internal (not on
    /// <see cref="IWakeUpMonitor"/>) so tests can exercise the fire logic without the 30s timer.
    /// </summary>
    internal async Task CheckAndFireAsync()
    {
        var dueEvents = (await scheduledEvents.GetDueEventsAsync()).ToList();

        foreach (var evt in dueEvents)
        {
            await scheduledEvents.MarkTriggeredAsync(evt);
            await eventBus.PublishAsync(this, new ScheduledEventTriggered(evt));
        }
    }
}
