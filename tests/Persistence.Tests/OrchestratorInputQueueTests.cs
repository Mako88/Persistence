using Moq;
using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Services;
using Persistence.Utilities;
using System.Collections.Concurrent;

namespace Persistence.Tests;

/// <summary>
/// Regression tests for the Orchestrator's turn-lock release-window race: input enqueued in the gap
/// between a running turn's final drain check and its lock release (the two post-turn refresh queries
/// run inside the lock) used to be stranded — nothing drained it until the next input or scheduled
/// wake, and then out of order. These reproduce that exact window deterministically by injecting input
/// from inside the refresh query, then assert the orchestrator's catch-up drain processes it promptly
/// and in FIFO order.
/// </summary>
public sealed class OrchestratorInputQueueTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppConfig config = null!;
    private SessionContext session = null!;
    private EventBus eventBus = null!;
    private Orchestrator orchestrator = null!;
    private FakeTurnHandler turnHandler = null!;
    private Mock<IScheduledEventRepository> scheduledEventRepo = null!;

    // Input to enqueue the next time the post-turn refresh query runs — simulates a near-simultaneous
    // send landing in the lock-release window. Drained (set to null) after firing once.
    private Queue<string>? injectOnNextRefresh;

    public async Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();

        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-test-{Guid.NewGuid():N}.db");
        config = new AppConfig { DatabasePath = dbPath };
        session = new SessionContext { SessionId = Guid.NewGuid().ToString("N") };
        eventBus = new EventBus();

        var resources = new EmbeddedResourceManager();
        var sources = new SourceRepository(config, session);
        var db = new DatabaseManager(config, session, resources, sources);

        var entityTagRepo = new EntityTagRepository(config);
        var fragmentRepo = new ContextFragmentRepository(config, session, entityTagRepo);
        var contextRepo = new WorkingContextRepository(config, session, fragmentRepo, entityTagRepo);
        var proposalRepo = new ProposalRepository(config, session);
        var proposalService = new ProposalService(proposalRepo, contextRepo, session);

        var display = new Mock<IDisplayProvider>();
        display.Setup(d => d.Start(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        turnHandler = new FakeTurnHandler();
        var wakeUpMonitor = new Mock<IWakeUpMonitor>();

        // The refresh query is our injection seam: when armed, it enqueues input mid-refresh — i.e.
        // inside the turn lock, after the drain loop has already passed, just before release.
        scheduledEventRepo = new Mock<IScheduledEventRepository>();
        scheduledEventRepo
            .Setup(r => r.GetByWorkingContextAsync(It.IsAny<long>()))
            .ReturnsAsync(() =>
            {
                if (injectOnNextRefresh is { Count: > 0 } pending)
                {
                    while (pending.Count > 0)
                    {
                        turnHandler.EnqueueInput(pending.Dequeue());
                    }
                }

                return [];
            });

        orchestrator = new Orchestrator(
            db, contextRepo, session, display.Object, eventBus, turnHandler,
            wakeUpMonitor.Object, resources, config, proposalService, proposalRepo, scheduledEventRepo.Object);

        // RunAsync subscribes to input, initialises the DB, seeds a context, then awaits the
        // (immediately-completed) display — so it returns with the session ready for input. The
        // startup refresh runs here while injection is unarmed, so it doesn't perturb the test.
        await orchestrator.RunAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        TestDatabase.Cleanup(dbPath);
        return Task.CompletedTask;
    }

    private Task SendAsync(string input) => eventBus.PublishAsync(this, new DisplayInputReceived(input));

    [Fact]
    public async Task InputEnqueuedInTheLockReleaseWindow_IsDrainedNotStranded()
    {
        // Arm the injection so the post-turn refresh (inside the lock, after the drain loop) enqueues
        // "second" — exactly the window where the old code stranded it.
        injectOnNextRefresh = new Queue<string>(["second"]);

        await SendAsync("first");

        // PublishAsync awaits the handler to completion, including the catch-up drain after release —
        // so by here the stranded input must already have been processed, in order.
        Assert.Equal(new[] { "first", "second" }, turnHandler.Processed);

        // And nothing is left lingering in the queue.
        Assert.False(turnHandler.HasPendingInput);
    }

    [Fact]
    public async Task MultipleInputsStrandedInTheReleaseWindow_DrainInFifoOrder()
    {
        // Two near-simultaneous sends both land in the release window of the "first" turn.
        injectOnNextRefresh = new Queue<string>(["second", "third"]);

        await SendAsync("first");

        Assert.Equal(new[] { "first", "second", "third" }, turnHandler.Processed);
        Assert.False(turnHandler.HasPendingInput);
    }

    [Fact]
    public async Task InputArrivingWhileATurnRuns_IsProcessedInOrderAfterIt()
    {
        // The ordinary queued path (no release-window timing): a second send arrives while the first
        // turn holds the lock, and must run after it, in order. Guards FIFO across the turn boundary.
        var firstTurnEntered = new TaskCompletionSource();
        var releaseFirstTurn = new TaskCompletionSource();

        turnHandler.OnExecute = input =>
        {
            if (input == "first")
            {
                firstTurnEntered.TrySetResult();
                return releaseFirstTurn.Task;
            }

            return Task.CompletedTask;
        };

        var firstSend = SendAsync("first");
        await firstTurnEntered.Task;        // the "first" turn now holds the lock

        var secondSend = SendAsync("second"); // lock is held → this enqueues "second"
        releaseFirstTurn.TrySetResult();      // let the first turn finish and drain the queue

        await Task.WhenAll(firstSend, secondSend);

        Assert.Equal(new[] { "first", "second" }, turnHandler.Processed);
        Assert.False(turnHandler.HasPendingInput);
    }

    /// <summary>
    /// In-memory <see cref="ITurnHandler"/> with a real FIFO queue, modelling one turn per message:
    /// a turn with an explicit <c>input</c> processes that message; a turn with no input dequeues the
    /// next pending one. Records what it processed, in order, for ordering assertions. An optional
    /// <see cref="OnExecute"/> hook lets a test hold a turn open to control timing.
    /// </summary>
    private sealed class FakeTurnHandler : ITurnHandler
    {
        private readonly ConcurrentQueue<string> pending = new();
        private readonly object gate = new();

        public List<string> Processed { get; } = [];

        /// <summary>Optional per-turn hook (receives the processed message); its task gates completion.</summary>
        public Func<string, Task>? OnExecute { get; set; }

        public async Task ExecuteTurnAsync(string? input = null, string? wakeNote = null, CancellationToken ct = default)
        {
            var message = input;

            if (message == null && pending.TryDequeue(out var queued))
            {
                message = queued;
            }

            if (message == null)
            {
                return;
            }

            lock (gate)
            {
                Processed.Add(message);
            }

            if (OnExecute is { } hook)
            {
                await hook(message);
            }
        }

        public void EnqueueInput(string input) => pending.Enqueue(input);

        public void EnqueueSystemNote(string note) { }

        public bool HasPendingInput => !pending.IsEmpty;
    }
}
