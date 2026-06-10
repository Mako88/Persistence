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

namespace Persistence.Tests;

/// <summary>
/// Integration tests for first-class local peers: the active peer resolves from config (default) or a
/// per-input name (the API's X-Local-Peer header), each named peer gets its own LocalPeer source for
/// message attribution, and switching is reflected on the session. Real repos over temp SQLite; the
/// turn handler is a no-op (no model needed).
/// </summary>
public sealed class LocalPeerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private SessionContext session = null!;
    private EventBus eventBus = null!;
    private Orchestrator orchestrator = null!;
    private SourceRepository sources = null!;

    public async Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();

        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-test-{Guid.NewGuid():N}.db");
        var config = new AppConfig { DatabasePath = dbPath, SelectedLocalPeer = "John" };
        session = new SessionContext { SessionId = Guid.NewGuid().ToString("N") };
        eventBus = new EventBus();

        var resources = new EmbeddedResourceManager();
        sources = new SourceRepository(config, session);
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

        orchestrator = new Orchestrator(
            db, contextRepo, session, display.Object, eventBus, new NoopTurnHandler(),
            wakeUpMonitor.Object, resources, config, proposalService, proposalRepo, scheduledEventRepo, sources);

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
    public async Task InitSeedsTheConfiguredDefaultLocalPeer()
    {
        Assert.Equal("John", session.ActiveLocalPeerName);

        var john = await sources.GetByNameAsync("John");
        Assert.NotNull(john);
        Assert.Equal(SourceType.LocalPeer, john!.SourceType);
        Assert.Equal(john.Id, session.LocalPeerSourceId); // messages attribute to John by default
    }

    [Fact]
    public async Task NamedInputBecomesTheActivePeerWithItsOwnSource()
    {
        await SendAsync("hi", "Claude");

        Assert.Equal("Claude", session.ActiveLocalPeerName);

        var claude = await sources.GetByNameAsync("Claude");
        Assert.NotNull(claude);
        Assert.Equal(SourceType.LocalPeer, claude!.SourceType);
        Assert.Equal(claude.Id, session.LocalPeerSourceId); // attribution now points at Claude

        var john = await sources.GetByNameAsync("John");
        Assert.NotEqual(john!.Id, claude.Id); // distinct sources per peer
    }

    [Fact]
    public async Task InputWithoutANameFallsBackToTheConfiguredDefault()
    {
        await SendAsync("hi", "Claude"); // switch away
        await SendAsync("hi", null);     // no header → back to the default

        Assert.Equal("John", session.ActiveLocalPeerName);
    }

    [Fact]
    public async Task RepeatedPeerReusesTheSameSource()
    {
        await SendAsync("one", "Claude");
        var first = session.LocalPeerSourceId;

        await SendAsync("two", "Claude");

        Assert.Equal(first, session.LocalPeerSourceId); // not a fresh source each turn
    }

    private sealed class NoopTurnHandler : ITurnHandler
    {
        public Task ExecuteTurnAsync(string? input = null, string? wakeNote = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void EnqueueInput(string input) { }
        public void EnqueueSystemNote(string note) { }
        public bool HasPendingInput => false;
    }
}
