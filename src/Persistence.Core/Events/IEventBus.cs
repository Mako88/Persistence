namespace Persistence.Events;

/// <summary>
/// Async event bus for decoupled inter-component communication
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes an event to all subscribers concurrently and awaits their completion
    /// </summary>
    Task PublishAsync<T>(object? sender, T theEvent) where T : BaseEvent;

    /// <summary>
    /// Subscribes an async handler to events of type T. Returns a delegate that removes the subscription.
    /// </summary>
    EventBus.Unsubscribe Subscribe<T>(Func<object?, T, Task> handler) where T : BaseEvent;
}