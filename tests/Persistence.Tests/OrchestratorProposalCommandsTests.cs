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
/// Integration tests for the local-peer (participant) proposal slash commands handled by the
/// Orchestrator: /proposals, /accept, /reject. Runs over a real temp SQLite database with the
/// real ProposalService; the display and the turn/wake collaborators are mocked.
/// </summary>
public sealed class OrchestratorProposalCommandsTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppConfig config = null!;
    private SessionContext session = null!;
    private EventBus eventBus = null!;
    private WorkingContextRepository contextRepo = null!;
    private ProposalRepository proposalRepo = null!;
    private ProposalService proposalService = null!;
    private Orchestrator orchestrator = null!;
    private Mock<ITurnHandler> turnHandler = null!;

    private readonly List<string> systemMessages = [];
    private readonly List<string> errors = [];

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
        contextRepo = new WorkingContextRepository(config, session, fragmentRepo, entityTagRepo);
        proposalRepo = new ProposalRepository(config, session);
        proposalService = new ProposalService(proposalRepo, contextRepo, session);

        var display = new Mock<IDisplayProvider>();
        display.Setup(d => d.Start(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        display.Setup(d => d.ShowSystemMessage(It.IsAny<string>())).Callback<string>(systemMessages.Add);
        display.Setup(d => d.ShowError(It.IsAny<string>())).Callback<string>(errors.Add);

        turnHandler = new Mock<ITurnHandler>();
        turnHandler
            .Setup(t => t.ExecuteTurnAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var wakeUpMonitor = new Mock<IWakeUpMonitor>();

        orchestrator = new Orchestrator(
            db, contextRepo, session, display.Object, eventBus, turnHandler.Object,
            wakeUpMonitor.Object, resources, config, proposalService, proposalRepo);

        // RunAsync subscribes to input, initialises the DB, seeds a context, then awaits the
        // (immediately-completed) display — so it returns with the session ready for input.
        await orchestrator.RunAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
        return Task.CompletedTask;
    }

    private async Task SendAsync(string input)
    {
        systemMessages.Clear();
        errors.Clear();
        await eventBus.PublishAsync(this, new DisplayInputReceived(input));
    }

    private async Task<long> CreateAddProposalAsync(string content) =>
        (await proposalService.CreateAsync(
            new ProposalDraft(ProposalKind.AddFragment, "because", ProposedFragmentType: ContextFragmentType.Personal, ProposedContent: content))).Id;

    [Fact]
    public async Task AcceptAppliesTheProposalWhenApprovalAllowsParticipant()
    {
        config.ProposalApproval = "Both";
        var id = await CreateAddProposalAsync("participant approved this");

        await SendAsync($"/accept {id}");

        Assert.Contains(systemMessages, m => m.Contains("added a Personal fragment"));

        var context = await contextRepo.GetByIdAsync(session.WorkingContextId);
        Assert.Contains(context!.ContextFragments.Values, f => f.Content == "participant approved this");
        Assert.Empty(await proposalRepo.GetOpenAsync());
    }

    [Fact]
    public async Task AcceptIsBlockedInSelfMode()
    {
        config.ProposalApproval = "Self";
        var id = await CreateAddProposalAsync("should not apply");

        await SendAsync($"/accept {id}");

        Assert.Contains(systemMessages, m => m.Contains("set to 'Self'"));
        Assert.Single(await proposalRepo.GetOpenAsync()); // untouched
    }

    [Fact]
    public async Task AcceptQueuesAResolutionNoteForThePeer()
    {
        config.ProposalApproval = "Both";
        var id = await CreateAddProposalAsync("note");

        await SendAsync($"/accept {id}");

        turnHandler.Verify(
            t => t.EnqueueSystemNote(It.Is<string>(n => n.Contains($"#{id}") && n.Contains("accepted"))),
            Times.Once);
    }

    [Fact]
    public async Task RejectResolvesTheProposalInAnyMode()
    {
        config.ProposalApproval = "Self";
        var id = await CreateAddProposalAsync("never mind");

        await SendAsync($"/reject {id} not a fit");

        Assert.Contains(systemMessages, m => m.Contains($"Rejected proposal #{id}"));
        Assert.Empty(await proposalRepo.GetOpenAsync());
    }

    [Fact]
    public async Task ProposalsListsOpenOnes()
    {
        await CreateAddProposalAsync("alpha note");

        await SendAsync("/proposals");

        var listing = Assert.Single(systemMessages);
        Assert.Contains("Open proposals (1):", listing);
        Assert.Contains("alpha note", listing);
        Assert.Contains("/accept", listing);
    }

    [Fact]
    public async Task AcceptWithNoIdShowsUsage()
    {
        await SendAsync("/accept");

        Assert.Contains(errors, e => e.Contains("Usage: /accept"));
    }

    [Fact]
    public async Task HelpListsTheLocalCommands()
    {
        await SendAsync("/help");

        var help = Assert.Single(systemMessages);
        Assert.Contains("/proposals", help);
        Assert.Contains("/accept", help);
        Assert.Contains("/reject", help);
    }

    [Fact]
    public async Task AFiredScheduledEventWakesThePeerForAnAutonomousTurn()
    {
        var evt = new ScheduledEventEntity
        {
            Id = 1,
            Name = "reflect on values",
            WorkingContextId = session.WorkingContextId,
            ScheduledForUtc = DateTimeOffset.UtcNow,
            Status = ScheduledEventStatus.Triggered,
            WakePrompt = "reconsider whether I still value X",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

        await eventBus.PublishAsync(this, new ScheduledEventTriggered(evt));

        // The fired event drives exactly one turn, framed as a wake (no local-peer message) that
        // carries both the event name and the peer's own note-to-self.
        turnHandler.Verify(t => t.ExecuteTurnAsync(
            null,
            It.Is<string?>(n => n != null && n.Contains("reflect on values") && n.Contains("reconsider whether I still value X")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
