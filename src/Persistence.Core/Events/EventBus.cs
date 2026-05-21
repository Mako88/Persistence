using Persistence.DI;

namespace Persistence.Events;

/// <summary>
/// Async event bus for decoupled inter-component communication. Handlers for a given
/// event are dispatched concurrently.
/// </summary>
[Singleton]
public class EventBus : IEventBus
{
    public delegate void Unsubscribe();

    private readonly Dictionary<Type, IDictionary<Guid, object>> handlers = [];

    /// <summary>
    /// Subscribes an async handler to events of type T. Returns a delegate that removes the subscription.
    /// </summary>
    public Unsubscribe Subscribe<T>(Func<object?, T, Task> handler) where T : BaseEvent
    {
        var key = typeof(T);

        if (!handlers.ContainsKey(key))
        {
            handlers.Add(key, new Dictionary<Guid, object>());
        }

        var handlerId = Guid.NewGuid();
        handlers[key].Add(handlerId, handler);

        return () => handlers[key].Remove(handlerId);
    }

    /// <summary>
    /// Publishes an event to all subscribers of type T concurrently and awaits their completion
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
}
