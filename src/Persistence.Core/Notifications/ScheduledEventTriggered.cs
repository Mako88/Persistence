using Persistence.Data.Entities;
using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Event fired when a scheduled event triggers
/// </summary>
public class ScheduledEventTriggered(ScheduledEventEntity evt) : BaseEvent
{
    /// <summary>
    /// The scheduled event that triggered
    /// </summary>
    public ScheduledEventEntity Event { get; } = evt;
}
