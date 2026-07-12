using Persistence.Events;

namespace Persistence.Tests;

public class EventBusTests
{
    private sealed class Ping(string message) : BaseEvent
    {
        public string Message { get; } = message;
    }

    [Fact]
    public async Task DeliversEventToSubscriber()
    {
        var bus = new EventBus();
        string? received = null;

        bus.Subscribe<Ping>((_, e) => { received = e.Message; return Task.CompletedTask; });
        await bus.PublishAsync(this, new Ping("hi"));

        Assert.Equal("hi", received);
    }

    [Fact]
    public async Task PublishAsyncPropagatesASubscriberException()
    {
        var bus = new EventBus();
        bus.Subscribe<Ping>((_, _) => throw new InvalidOperationException("boom"));

        // Unlike FireAndForget (which routes errors to a callback), the awaited publish surfaces the
        // failure to the caller — so a broken handler on a synchronous path isn't silently swallowed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.PublishAsync(this, new Ping("x")));
    }

    [Fact]
    public async Task DeliversToAllSubscribers()
    {
        var bus = new EventBus();
        var count = 0;

        bus.Subscribe<Ping>((_, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        bus.Subscribe<Ping>((_, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; });

        await bus.PublishAsync(this, new Ping("x"));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task PassesSenderThrough()
    {
        var bus = new EventBus();
        object? sender = null;

        bus.Subscribe<Ping>((s, _) => { sender = s; return Task.CompletedTask; });
        await bus.PublishAsync(this, new Ping("x"));

        Assert.Same(this, sender);
    }

    [Fact]
    public async Task UnsubscribeStopsDelivery()
    {
        var bus = new EventBus();
        var count = 0;

        var unsubscribe = bus.Subscribe<Ping>((_, _) => { count++; return Task.CompletedTask; });
        await bus.PublishAsync(this, new Ping("1"));

        unsubscribe();
        await bus.PublishAsync(this, new Ping("2"));

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PublishWithNoSubscribersIsNoOp()
    {
        var bus = new EventBus();

        // Should not throw.
        await bus.PublishAsync(this, new Ping("nobody listening"));
    }

    [Fact]
    public async Task FireAndForgetDeliversEvent()
    {
        var bus = new EventBus();
        var delivered = new TaskCompletionSource<string>();

        bus.Subscribe<Ping>((_, e) => { delivered.TrySetResult(e.Message); return Task.CompletedTask; });
        bus.FireAndForget(this, new Ping("async"));

        var received = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("async", received);
    }

    [Fact]
    public async Task FireAndForgetRoutesHandlerExceptionToOnError()
    {
        var bus = new EventBus();
        var caught = new TaskCompletionSource<Exception>();

        bus.Subscribe<Ping>((_, _) => throw new InvalidOperationException("boom"));
        bus.FireAndForget(this, new Ping("x"), ex => caught.TrySetResult(ex));

        var error = await caught.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public void FireAndForgetWithNoSubscribersDoesNotThrow()
    {
        var bus = new EventBus();

        // Should not throw on the calling thread.
        bus.FireAndForget(this, new Ping("nobody listening"));
    }
}
