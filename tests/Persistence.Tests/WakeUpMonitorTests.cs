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
}
