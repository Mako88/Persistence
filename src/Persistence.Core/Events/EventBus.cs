using System.Collections.Concurrent;
using Persistence.DI;

namespace Persistence.Events;

/// <summary>
/// Async event bus for decoupled inter-component communication. Handlers for a given
/// event are dispatched concurrently. Thread-safe for concurrent subscribe/publish.
/// </summary>
[Singleton]
public class EventBus : IEventBus
{
    public delegate void Unsubscribe();

    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Guid, object>> handlers = [];

    /// <summary>
    /// Subscribes an async handler to events of type T. Returns a delegate that
    /// removes the subscription.
    /// </summary>
    public Unsubscribe Subscribe<T>(Func<object?, T, Task> handler) where T : BaseEvent
    {
        var key = typeof(T);
        var bucket = handlers.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, object>());

        var handlerId = Guid.NewGuid();
        bucket[handlerId] = handler;

        return () => bucket.TryRemove(handlerId, out _);
    }

    /// <summary>
    /// Publishes an event to all subscribers of type T concurrently and awaits
    /// their completion.
    /// </summary>
    public async Task PublishAsync<T>(object? sender, T theEvent) where T : BaseEvent
    {
        var key = typeof(T);

        if (!handlers.TryGetValue(key, out var registered))
        {
            return;
        }

        var tasks = registered.Values
            .Cast<Func<object?, T, Task>>()
            .Select(h => h(sender, theEvent));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Publishes an event on a thread-pool thread without awaiting it, so the calling
    /// thread isn't blocked by synchronous handler work. Failures are routed to
    /// <paramref name="onError"/> if supplied.
    /// </summary>
    public void FireAndForget<T>(object? sender, T theEvent, Action<Exception>? onError = null) where T : BaseEvent =>
        _ = Task.Run(() => PublishAsync(sender, theEvent))
            .ContinueWith(
                t => onError?.Invoke(t.Exception?.InnerException ?? t.Exception!),
                TaskContinuationOptions.OnlyOnFaulted);
}
