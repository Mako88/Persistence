using Moq;
using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Services;
using Persistence.Utilities;

namespace Persistence.Tests;

/// <summary>
/// Tests that the orchestrator forwards <em>who sent a message</em> down to the turn, rather than
/// resolving it into shared session state on the way in. Resolution moved into the turn (under the
/// turn lock) so concurrent senders can't overwrite one another (ADR-0007); the orchestrator's job is
/// now just to carry the sender's name — from the API's <c>X-Local-Peer</c> header, or null for the
/// configured default — through to <see cref="ITurnHandler"/>. The name→source attribution itself is
/// covered by <see cref="TurnHandlerTests"/>. Real repos over temp SQLite; a recording turn handler.
/// </summary>
public sealed class LocalPeerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private EventBus eventBus = null!;
    private Orchestrator orchestrator = null!;
    private RecordingTurnHandler turnHandler = null!;

    public async Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();

        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-test-{Guid.NewGuid():N}.db");
        var config = new AppConfig { DatabasePath = dbPath, SelectedLocalPeer = "John" };
        var session = new SessionContext { SessionId = Guid.NewGuid().ToString("N") };
        eventBus = new EventBus();

        var resources = new EmbeddedResourceManager();
        var sources = new SourceRepository(config, session);
        var db = new DatabaseManager(config, session, resources, sources);
        var entityTagRepo = new EntityTagRepository(config);
        var fragmentRepo = new ContextFragmentRepository(config, session, entityTagRepo);
        var contextRepo = new WorkingContextRepository(config, session, fragmentRepo, entityTagRepo);
        var proposalRepo = new ProposalRepository(config, session);
        var proposalService = new ProposalService(proposalRepo, contextRepo, session);
        var scheduledEventRepo = new ScheduledEventRepository(config, session, entityTagRepo);

        var display = new Mock<IDisplayProvider>();
        display.Setup(d => d.Start(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var wakeUpMonitor = new Mock<IWakeUpMonitor>();

        turnHandler = new RecordingTurnHandler();
        orchestrator = new Orchestrator(
            db, contextRepo, session, display.Object, eventBus, turnHandler,
            wakeUpMonitor.Object, resources, config, proposalService, proposalRepo, scheduledEventRepo,
            new PeerSeeder(config, new TagRepository(config, session, entityTagRepo), session));

        await orchestrator.RunAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        TestDatabase.Cleanup(dbPath);
        return Task.CompletedTask;
    }

    private Task SendAsync(string input, string? peer = null) =>
        eventBus.PublishAsync(this, new DisplayInputReceived(input, peer));

    [Fact]
    public async Task ForwardsANamedSenderToTheTurn()
    {
        await SendAsync("hi", "Claude");

        Assert.Contains(turnHandler.Executed, e => e.Input == "hi" && e.Peer == "Claude");
    }

    [Fact]
    public async Task ForwardsANullSenderWhenNoNameIsGiven()
    {
        // The orchestrator doesn't apply the default itself — it passes null through, and the turn
        // resolves the configured default when it persists the message.
        await SendAsync("hi", null);

        Assert.Contains(turnHandler.Executed, e => e.Input == "hi" && e.Peer == null);
    }

    [Fact]
    public async Task DistinctSendersAreForwardedDistinctly()
    {
        await SendAsync("one", "Claude");
        await SendAsync("two", "Ember");

        Assert.Contains(turnHandler.Executed, e => e.Input == "one" && e.Peer == "Claude");
        Assert.Contains(turnHandler.Executed, e => e.Input == "two" && e.Peer == "Ember");
    }

    /// <summary>
    /// Records every (input, peer) the orchestrator forwards, and completes each turn immediately so
    /// sends take the direct (non-queued) path. Queue-identity attribution is covered separately.
    /// </summary>
    private sealed class RecordingTurnHandler : ITurnHandler
    {
        public List<(string? Input, string? Peer)> Executed { get; } = [];

        public Task ExecuteTurnAsync(string? input = null, string? peerName = null, string? wakeNote = null,
            Persistence.Data.Entities.SourceType senderType = Persistence.Data.Entities.SourceType.HumanPeer,
            string? addressedTo = null, int relayDepth = 0, CancellationToken ct = default)
        {
            Executed.Add((input, peerName));
            return Task.CompletedTask;
        }

        public void EnqueueInput(string input, string? peerName = null,
            Persistence.Data.Entities.SourceType senderType = Persistence.Data.Entities.SourceType.HumanPeer,
            string? addressedTo = null) => Executed.Add((input, peerName));
        public void EnqueueSystemNote(string note) { }
        public bool HasPendingInput => false;
    }
}
