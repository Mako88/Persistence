using Moq;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using System.Data;

namespace Persistence.Tests;

/// <summary>
/// Unit tests for <see cref="WakeUpMonitor"/>'s fire logic (<c>CheckAndFireAsync</c>), exercised
/// directly so the 30-second polling timer isn't involved.
/// </summary>
public class WakeUpMonitorTests
{
    private static ScheduledEventEntity Event(long id) =>
        new()
        {
            Id = id,
            Name = $"evt{id}",
            WorkingContextId = 1,
            ScheduledForUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status = ScheduledEventStatus.Pending,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task FiresEachDueEventAndPublishesIt()
    {
        var repo = new Mock<IScheduledEventRepository>();
        repo.Setup(r => r.GetDueEventsAsync()).ReturnsAsync([Event(1), Event(2)]);
        repo.Setup(r => r.MarkTriggeredAsync(It.IsAny<ScheduledEventEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var bus = new EventBus();
        var fired = new List<long>();
        bus.Subscribe<ScheduledEventTriggered>((_, e) => { fired.Add(e.Event.Id); return Task.CompletedTask; });

        await new WakeUpMonitor(repo.Object, bus).CheckAndFireAsync();

        Assert.Equal([1, 2], fired);
        repo.Verify(r => r.MarkTriggeredAsync(It.IsAny<ScheduledEventEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DoesNothingWhenNoEventsAreDue()
    {
        var repo = new Mock<IScheduledEventRepository>();
        repo.Setup(r => r.GetDueEventsAsync()).ReturnsAsync([]);

        var bus = new EventBus();
        var fired = 0;
        bus.Subscribe<ScheduledEventTriggered>((_, _) => { fired++; return Task.CompletedTask; });

        await new WakeUpMonitor(repo.Object, bus).CheckAndFireAsync();

        Assert.Equal(0, fired);
        repo.Verify(r => r.MarkTriggeredAsync(It.IsAny<ScheduledEventEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFailedMarkDoesNotFireThatEvent()
    {
        // Mark-then-publish ordering matters: if persistence fails we must NOT have already told
        // subscribers the event fired (which would wake the peer for an event still marked Pending).
        var repo = new Mock<IScheduledEventRepository>();
        repo.Setup(r => r.GetDueEventsAsync()).ReturnsAsync([Event(1)]);
        repo.Setup(r => r.MarkTriggeredAsync(It.IsAny<ScheduledEventEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var bus = new EventBus();
        var fired = 0;
        bus.Subscribe<ScheduledEventTriggered>((_, _) => { fired++; return Task.CompletedTask; });

        // The failure is contained, not propagated — the event stays Pending and due, to be retried
        // on the next poll. No ScheduledEventTriggered is published.
        await new WakeUpMonitor(repo.Object, bus).CheckAndFireAsync();

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task AFailedEventDoesNotBlockLaterEventsInTheSameSweep()
    {
        // Resilience contract: a throw while firing one event must NOT starve the others in this
        // sweep. Event 1 fails to persist; event 2 must still be marked triggered and published.
        var repo = new Mock<IScheduledEventRepository>();
        repo.Setup(r => r.GetDueEventsAsync()).ReturnsAsync([Event(1), Event(2)]);
        repo.Setup(r => r.MarkTriggeredAsync(It.Is<ScheduledEventEntity>(e => e.Id == 1), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom on event 1"));
        repo.Setup(r => r.MarkTriggeredAsync(It.Is<ScheduledEventEntity>(e => e.Id == 2), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var bus = new EventBus();
        var fired = new List<long>();
        bus.Subscribe<ScheduledEventTriggered>((_, e) => { fired.Add(e.Event.Id); return Task.CompletedTask; });

        await new WakeUpMonitor(repo.Object, bus).CheckAndFireAsync();

        // Only event 2 fired (event 1's failure was isolated), and event 2 was marked triggered.
        Assert.Equal([2], fired);
        repo.Verify(r => r.MarkTriggeredAsync(It.Is<ScheduledEventEntity>(e => e.Id == 2), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
