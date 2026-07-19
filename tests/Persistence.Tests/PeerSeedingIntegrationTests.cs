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
/// Integration tests for per-database identity seeding over real temp SQLite: a <c>{dbName}.json</c>
/// seed file becomes authored (non-protected, remote-peer-sourced) fragments in a brand-new store, and
/// a freshly-booted Orchestrator wires that in — skipping the generic first-wake guide when the peer
/// already arrives with an authored identity.
/// </summary>
public sealed class PeerSeedingIntegrationTests : IAsyncLifetime
{
    private readonly List<string> tempDbs = [];
    private readonly List<string> tempDirs = [];

    public Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var db in tempDbs)
        {
            TestDatabase.Cleanup(db);
        }

        foreach (var dir in tempDirs.Where(Directory.Exists))
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }

        return Task.CompletedTask;
    }

    /// <summary>Everything a test needs against one fresh temp store + seeds folder.</summary>
    private sealed record Harness(
        AppConfig Config,
        SessionContext Session,
        DatabaseManager Db,
        WorkingContextRepository ContextRepo,
        PeerSeeder Seeder,
        Func<Orchestrator> BuildOrchestrator);

    /// <summary>
    /// Builds a harness over a brand-new temp database. When <paramref name="seedJson"/> is non-null it
    /// is written to <c>{seedsDir}/{dbName}.json</c> so the seeder/orchestrator will pick it up.
    /// </summary>
    private async Task<Harness> CreateHarnessAsync(string? seedJson)
    {
        var seedsDir = Path.Combine(Path.GetTempPath(), $"seeds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(seedsDir);
        tempDirs.Add(seedsDir);

        var dbPath = Path.Combine(Path.GetTempPath(), $"persistence-test-{Guid.NewGuid():N}.db");
        tempDbs.Add(dbPath);

        if (seedJson != null)
        {
            var dbName = Path.GetFileNameWithoutExtension(dbPath);
            await File.WriteAllTextAsync(Path.Combine(seedsDir, $"{dbName}.json"), seedJson);
        }

        var config = new AppConfig { DatabasePath = dbPath, SeedsDirectory = seedsDir };
        var session = new SessionContext { SessionId = Guid.NewGuid().ToString("N") };

        var resources = new EmbeddedResourceManager();
        var sources = new SourceRepository(config, session);
        var db = new DatabaseManager(config, session, resources, sources);
        await db.InitializeAsync(); // migrate + create sources (sets session.RemotePeerSourceId)

        var entityTagRepo = new EntityTagRepository(config);
        var fragmentRepo = new ContextFragmentRepository(config, session, entityTagRepo);
        var contextRepo = new WorkingContextRepository(config, session, fragmentRepo, entityTagRepo);
        var tagRepo = new TagRepository(config, session, entityTagRepo);
        var seeder = new PeerSeeder(config, tagRepo, session);

        Orchestrator BuildOrchestrator()
        {
            var proposalRepo = new ProposalRepository(config, session);
            var proposalService = new ProposalService(proposalRepo, contextRepo, session);
            var scheduledEventRepo = new ScheduledEventRepository(config, session, entityTagRepo);
            var display = new Mock<IDisplayProvider>();
            display.Setup(d => d.Start(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var wakeUpMonitor = new Mock<IWakeUpMonitor>();

            return new Orchestrator(
                db, contextRepo, session, display.Object, new EventBus(), new NoopTurnHandler(),
                wakeUpMonitor.Object, resources, config, proposalService, proposalRepo, scheduledEventRepo,
                seeder);
        }

        return new Harness(config, session, db, contextRepo, seeder, BuildOrchestrator);
    }

    private sealed class NoopTurnHandler : ITurnHandler
    {
        public Task ExecuteTurnAsync(string? input = null, string? peerName = null, string? wakeNote = null,
            Persistence.Data.Entities.SourceType senderType = Persistence.Data.Entities.SourceType.HumanPeer,
            string? addressedTo = null, int relayDepth = 0, string? messageId = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void EnqueueInput(string input, string? peerName = null,
            Persistence.Data.Entities.SourceType senderType = Persistence.Data.Entities.SourceType.HumanPeer,
            string? addressedTo = null, string? messageId = null, int relayDepth = 0) { }
        public void EnqueueSystemNote(string note) { }
        public bool HasPendingInput => false;
    }

    private const string FirstWakeMarker = "this is your first time waking in this system";

    [Fact]
    public async Task SeedsAuthoredFragmentsWithTagsAndRemotePeerSource_SkippingBlankAndDowngradingUnknownTypes()
    {
        var harness = await CreateHarnessAsync("""
            [
              { "Type": "Identity", "Content": "I am Test, and I value clarity.", "Tags": "identity/core",
                "Importance": 0.9, "Confidence": 0.8, "Relevance": 0.7 },
              { "Type": "ChatMessage", "Content": "system-managed type — not authorable, becomes Personal" },
              { "Type": "Personal", "Content": "   " }
            ]
            """);

        var context = await harness.ContextRepo.CreateAsync("Default");
        harness.Session.WorkingContextId = context.Id;

        var seeded = await harness.Seeder.SeedAsync(context);
        Assert.Equal(2, seeded); // blank-content entry skipped

        await harness.ContextRepo.SaveAsync(context);

        var reloaded = await harness.ContextRepo.GetByIdAsync(context.Id);
        var fragments = reloaded!.ContextFragments.Values.ToList();
        Assert.Equal(2, fragments.Count);

        var identity = Assert.Single(fragments, f => f.FragmentType == ContextFragmentType.Identity);
        Assert.Equal("I am Test, and I value clarity.", identity.Content);
        Assert.Equal(0.9f, identity.Importance);
        Assert.Equal(0.7f, identity.Relevance);
        Assert.False(identity.IsProtected); // the peer owns it — curatable from turn one
        Assert.Contains(identity.Sources, s => s.SourceType == SourceType.DigitalPeer);
        Assert.Contains(identity.Tags, t => t.Name == "core"); // leaf of identity/core

        // A non-authorable requested type falls back to Personal rather than being dropped.
        Assert.Contains(fragments, f =>
            f.FragmentType == ContextFragmentType.Personal && f.Content.StartsWith("system-managed type"));
    }

    [Fact]
    public async Task SeedReturnsZeroWhenNoSeedFileExistsForThisDatabase()
    {
        var harness = await CreateHarnessAsync(seedJson: null); // empty seeds folder

        var context = await harness.ContextRepo.CreateAsync("Default");
        harness.Session.WorkingContextId = context.Id;

        Assert.Equal(0, await harness.Seeder.SeedAsync(context));
        Assert.Empty(context.ContextFragments);
    }

    [Fact]
    public async Task OrchestratorBootSeedsIdentityAndSkipsTheFirstWakeGuideWhenASeedFileExists()
    {
        var harness = await CreateHarnessAsync("""
            [ { "Type": "Identity", "Content": "I am a pre-seeded peer." } ]
            """);

        await harness.BuildOrchestrator().RunAsync(CancellationToken.None);

        var context = await harness.ContextRepo.GetMostRecentAsync();
        var fragments = context!.ContextFragments.Values.ToList();

        Assert.Contains(fragments, f =>
            f.FragmentType == ContextFragmentType.Identity && f.Content == "I am a pre-seeded peer.");
        // The "your context is empty, decide who to be" guide would contradict the seeded identity.
        Assert.DoesNotContain(fragments, f => f.Content.Contains(FirstWakeMarker));
    }

    [Fact]
    public async Task OrchestratorBootAddsTheFirstWakeGuideWhenThereIsNoSeedFile()
    {
        var harness = await CreateHarnessAsync(seedJson: null);

        await harness.BuildOrchestrator().RunAsync(CancellationToken.None);

        var context = await harness.ContextRepo.GetMostRecentAsync();
        Assert.Contains(context!.ContextFragments.Values, f => f.Content.Contains(FirstWakeMarker));
    }
}
