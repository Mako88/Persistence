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
    /// Publishes an event without awaiting it, dispatching the handler chain on a
    /// thread-pool thread so the calling thread (e.g. a UI thread) isn't blocked by
    /// synchronous handler work before the first await. Exceptions are swallowed via
    /// the optional <paramref name="onError"/> callback rather than propagated.
    /// <para>
    /// Use for one-off, order-insensitive notifications from latency-sensitive callers.
    /// For ordering-sensitive sequences (e.g. streamed deltas), use
    /// <see cref="PublishAsync"/> and await each publish in turn.
    /// </para>
    /// </summary>
    void FireAndForget<T>(object? sender, T theEvent, Action<Exception>? onError = null) where T : BaseEvent;

    /// <summary>
    /// Subscribes an async handler to events of type T. Returns a delegate that removes the subscription.
    /// </summary>
    EventBus.Unsubscribe Subscribe<T>(Func<object?, T, Task> handler) where T : BaseEvent;
}