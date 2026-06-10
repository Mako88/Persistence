using Moq;
using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Runtime;
using Persistence.Services;
using Persistence.Utilities;

namespace Persistence.Tests;

/// <summary>
/// Integration tests for the headless wake-runner (<see cref="Orchestrator.RunWakeCycleAsync"/>): a due
/// scheduled event must run a real wake turn (framed as a wake), be marked Triggered, and the cycle must
/// return rather than hang on the initialization gate. Uses real repositories over a temp SQLite store
/// and a real <see cref="WakeUpMonitor"/>; only the turn handler is faked, so no model is needed.
/// </summary>
public sealed class WakeRunnerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppConfig config = null!;
    private SessionContext session = null!;
    private Orchestrator orchestrator = null!;
    private WakeRecordingTurnHandler turnHandler = null!;
    private ScheduledEventRepository scheduledEventRepo = null!;
    private long contextId;

    public async Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();

        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-test-{Guid.NewGuid():N}.db");
        config = new AppConfig { DatabasePath = dbPath };
        session = new SessionContext { SessionId = Guid.NewGuid().ToString("N") };
        var eventBus = new EventBus();

        var resources = new EmbeddedResourceManager();
        var sources = new SourceRepository(config, session);
        var db = new DatabaseManager(config, session, resources, sources);

        var entityTagRepo = new EntityTagRepository(config);
        var fragmentRepo = new ContextFragmentRepository(config, session, entityTagRepo);
        var contextRepo = new WorkingContextRepository(config, session, fragmentRepo, entityTagRepo);
        var proposalRepo = new ProposalRepository(config, session);
        var proposalService = new ProposalService(proposalRepo, contextRepo, session);
        scheduledEventRepo = new ScheduledEventRepository(config, session, entityTagRepo);

        var display = new Mock<IDisplayProvider>();
        display.Setup(d => d.Start(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        turnHandler = new WakeRecordingTurnHandler();
        var wakeUpMonitor = new WakeUpMonitor(scheduledEventRepo, eventBus); // the real monitor

        orchestrator = new Orchestrator(
            db, contextRepo, session, display.Object, eventBus, turnHandler,
            wakeUpMonitor, resources, config, proposalService, proposalRepo, scheduledEventRepo);

        // Create the schema + a working context the scheduled event can reference (FK).
        await db.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var context = new WorkingContextEntity { Name = "c", Summary = "s", CreatedUtc = now, LastModifiedUtc = now };
        await contextRepo.SaveAsync(context);
        contextId = context.Id;
    }

    public Task DisposeAsync()
    {
        TestDatabase.Cleanup(dbPath);
        return Task.CompletedTask;
    }

    private async Task<long> SeedDueEventAsync(string name, string? wakePrompt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new ScheduledEventEntity
        {
            Name = name,
            WorkingContextId = contextId,
            ScheduledForUtc = now.AddMinutes(-1), // already due
            Status = ScheduledEventStatus.Pending,
            WakePrompt = wakePrompt,
            CreatedUtc = now,
            LastModifiedUtc = now,
        };
        await scheduledEventRepo.SaveAsync(evt);
        return evt.Id;
    }

    private async Task RunWakeCycleWithoutHangingAsync()
    {
        var run = orchestrator.RunWakeCycleAsync(CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(run, finished); // returned rather than hanging on the initialization gate
        await run;                  // surface any exception
    }

    [Fact]
    public async Task RunWakeCycle_FiresDueEventAsWakeTurn_MarksTriggered_AndReturns()
    {
        var eventId = await SeedDueEventAsync("MemoryAudit", wakePrompt: "review density");

        await RunWakeCycleWithoutHangingAsync();

        // A wake turn ran, framed as a wake — the note carries the event name and the peer's own prompt.
        var note = Assert.Single(turnHandler.WakeNotes);
        Assert.Contains("MemoryAudit", note);
        Assert.Contains("review density", note);

        // The event is no longer due — it was marked Triggered.
        var stillDue = await scheduledEventRepo.GetDueEventsAsync();
        Assert.DoesNotContain(stillDue, e => e.Id == eventId);
    }

    [Fact]
    public async Task RunWakeCycle_FiresEveryDueEvent()
    {
        await SeedDueEventAsync("audit-a");
        await SeedDueEventAsync("audit-b");

        await RunWakeCycleWithoutHangingAsync();

        Assert.Equal(2, turnHandler.WakeNotes.Count);
        Assert.Empty(await scheduledEventRepo.GetDueEventsAsync());
    }

    [Fact]
    public async Task RunWakeCycle_WithNoDueEvents_RunsNoTurnAndReturns()
    {
        await RunWakeCycleWithoutHangingAsync();

        Assert.Empty(turnHandler.WakeNotes);
    }

    /// <summary>Fake turn handler that records the wake-note of each wake turn (no model call).</summary>
    private sealed class WakeRecordingTurnHandler : ITurnHandler
    {
        public List<string> WakeNotes { get; } = [];

        public Task ExecuteTurnAsync(string? input = null, string? wakeNote = null, CancellationToken ct = default)
        {
            if (wakeNote != null)
            {
                WakeNotes.Add(wakeNote);
            }

            return Task.CompletedTask;
        }

        public void EnqueueInput(string input) { }
        public void EnqueueSystemNote(string note) { }
        public bool HasPendingInput => false;
    }
}
