using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Runtime.ActionHandlers;
using Persistence.Services;
using Persistence.Utilities;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

/// <summary>
/// Integration tests over a real (temp-file) SQLite database for the first-class proposal
/// lifecycle: propose → (deliberation gap) → accept (applies the change, including to protected
/// fragments) / reject.
/// </summary>
public sealed class ProposalTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppConfig config = null!;
    private SessionContext session = null!;
    private WorkingContextRepository contextRepo = null!;
    private ContextFragmentRepository fragmentRepo = null!;
    private ProposalRepository proposalRepo = null!;
    private ManageContextHandler handler = null!;
    private readonly List<ToolInvoked> published = [];

    public async Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();

        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-test-{Guid.NewGuid():N}.db");
        config = new AppConfig { DatabasePath = dbPath };
        session = new SessionContext { SessionId = Guid.NewGuid().ToString("N") };

        var resources = new EmbeddedResourceManager();
        var sources = new SourceRepository(config, session);
        var db = new DatabaseManager(config, session, resources, sources);
        await db.InitializeAsync();

        var entityTagRepo = new EntityTagRepository(config);
        fragmentRepo = new ContextFragmentRepository(config, session, entityTagRepo);
        contextRepo = new WorkingContextRepository(config, session, fragmentRepo, entityTagRepo);
        proposalRepo = new ProposalRepository(config, session);
        var proposalService = new ProposalService(proposalRepo, contextRepo, session);

        var bus = new EventBus();
        bus.Subscribe<ToolInvoked>((_, e) => { published.Add(e); return Task.CompletedTask; });

        handler = new ManageContextHandler(
            contextRepo, fragmentRepo, new TagRepository(config, session, entityTagRepo),
            entityTagRepo, new ScheduledEventRepository(config, session, entityTagRepo), sources, session,
            proposalService, proposalRepo, config, bus);
    }

    public Task DisposeAsync()
    {
        TestDatabase.Cleanup(dbPath);
        return Task.CompletedTask;
    }

    /// <summary>Runs a command against the current context, advancing the "turn" clock first.</summary>
    private async Task<string> RunInNewTurnAsync(string commandJson)
    {
        // Each call simulates a fresh turn so a proposal made in one call can be accepted in a later one.
        session.TurnStartedUtc = await NextTurnStartAsync();

        published.Clear();
        var current = await contextRepo.GetByIdAsync(session.WorkingContextId);
        await handler.HandleAsync(current!, JsonNode.Parse(commandJson));
        return published.Single().Result;
    }

    /// <summary>
    /// A turn-start stamp strictly later than every open proposal, so the deliberation gap
    /// (accepting needs <c>CreatedUtc &lt; TurnStartedUtc</c>) sees a genuinely new turn.
    /// </summary>
    /// <remarks>
    /// Derived from the proposals rather than slept for, so the gap never depends on how finely the
    /// wall clock happens to tick.
    /// </remarks>
    private async Task<DateTimeOffset> NextTurnStartAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var latest = (await proposalRepo.GetOpenAsync()).Max(p => (DateTimeOffset?)p.CreatedUtc);

        return latest >= now ? latest.Value.AddTicks(1) : now;
    }

    private async Task<WorkingContextEntity> SeedContextAsync() =>
        await SeedContextWithAsync(null);

    private async Task<WorkingContextEntity> SeedContextWithAsync(WeightedContextFragment? fragment)
    {
        var context = await contextRepo.CreateAsync("Test");
        session.WorkingContextId = context.Id;

        if (fragment != null)
        {
            context.AddFragment(fragment);
            await contextRepo.SaveAsync(context);
        }

        return await contextRepo.GetByIdAsync(context.Id) ?? context;
    }

    private static WeightedContextFragment ProtectedIdentity(string content) =>
        new()
        {
            FragmentType = ContextFragmentType.Identity,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Importance = 1.0f,
            Confidence = 1.0f,
            Relevance = 1.0f,
            IsProtected = true,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task ProposeThenAcceptInALaterTurnAddsAFragment()
    {
        await SeedContextAsync();

        var proposeResult = await RunInNewTurnAsync(
            """{ "propose": { "kind": "add", "rationale": "capture my values", "content": "I value curiosity", "fragment_type": "Identity" } }""");
        Assert.Contains("Proposed #", proposeResult);

        var open = await proposalRepo.GetOpenAsync();
        var proposalId = open.Single().Id;

        var acceptResult = await RunInNewTurnAsync($$"""{ "accept_proposal": { "id": {{proposalId}} } }""");
        Assert.Contains("added a Identity fragment", acceptResult);

        var reloaded = await contextRepo.GetByIdAsync(session.WorkingContextId);
        Assert.Contains(reloaded!.ContextFragments.Values,
            f => f.FragmentType == ContextFragmentType.Identity && f.Content == "I value curiosity");

        Assert.Empty(await proposalRepo.GetOpenAsync()); // resolved
    }

    [Fact]
    public async Task AcceptingAModifyProposalEditsAProtectedFragment()
    {
        var context = await SeedContextWithAsync(ProtectedIdentity("original identity"));
        var protectedId = context.ContextFragments.Values.Single().Id;

        // A direct update is walled off...
        var updateResult = await RunInNewTurnAsync($$"""{ "update": { "id": {{protectedId}}, "content": "hacked" } }""");
        Assert.Contains("is protected", updateResult);

        // ...but a proposal can change it.
        await RunInNewTurnAsync(
            $$"""{ "propose": { "kind": "modify", "target_id": {{protectedId}}, "rationale": "refine wording", "content": "refined identity" } }""");
        var proposalId = (await proposalRepo.GetOpenAsync()).Single().Id;

        var acceptResult = await RunInNewTurnAsync($$"""{ "accept_proposal": { "id": {{proposalId}} } }""");
        Assert.Contains($"updated fragment #{protectedId}", acceptResult);

        var reloaded = await contextRepo.GetByIdAsync(session.WorkingContextId);
        Assert.Equal("refined identity", reloaded!.ContextFragments.Values.Single(f => f.Id == protectedId).Content);
    }

    [Fact]
    public async Task AcceptingARemoveProposalTakesAProtectedFragmentOutOfContext()
    {
        var context = await SeedContextWithAsync(ProtectedIdentity("retire this"));
        var protectedId = context.ContextFragments.Values.Single().Id;

        await RunInNewTurnAsync(
            $$"""{ "propose": { "kind": "remove", "target_id": {{protectedId}}, "rationale": "no longer me" } }""");
        var proposalId = (await proposalRepo.GetOpenAsync()).Single().Id;

        var acceptResult = await RunInNewTurnAsync($$"""{ "accept_proposal": { "id": {{proposalId}} } }""");
        Assert.Contains("took fragment", acceptResult);

        var reloaded = await contextRepo.GetByIdAsync(session.WorkingContextId);
        Assert.DoesNotContain(reloaded!.ContextFragments.Values, f => f.Id == protectedId);
        Assert.Empty(await proposalRepo.GetOpenAsync());
    }

    [Fact]
    public async Task UnprotectingViaProposalThenEditingDirectlyWorks()
    {
        var context = await SeedContextWithAsync(ProtectedIdentity("locked identity"));
        var fragId = context.ContextFragments.Values.Single().Id;

        await RunInNewTurnAsync(
            $$"""{ "propose": { "kind": "unprotect", "target_id": {{fragId}}, "rationale": "want to revise this freely" } }""");
        var proposalId = (await proposalRepo.GetOpenAsync()).Single().Id;
        await RunInNewTurnAsync($$"""{ "accept_proposal": { "id": {{proposalId}} } }""");

        // The unprotect persisted...
        var frag = (await contextRepo.GetByIdAsync(session.WorkingContextId))!.ContextFragments.Values.Single(f => f.Id == fragId);
        Assert.False(frag.IsProtected);

        // ...so a direct update is no longer walled off (the in-memory edit itself would persist on
        // the real end-of-turn save, which this test harness doesn't run).
        var updateResult = await RunInNewTurnAsync($$"""{ "update": { "id": {{fragId}}, "content": "revised identity" } }""");
        Assert.Contains($"Updated fragment #{fragId}", updateResult);
        Assert.DoesNotContain("is protected", updateResult);
    }

    [Fact]
    public async Task AProposalCannotBeAcceptedInTheSameTurnItWasCreated()
    {
        await SeedContextAsync();

        // Propose and accept within ONE turn (no turn-clock advance between them). Stamping the turn
        // before the proposal is enough on its own: the proposal can only be stamped at or after this
        // instant, which is what the deliberation gap treats as same-turn.
        session.TurnStartedUtc = DateTimeOffset.UtcNow;

        var current = await contextRepo.GetByIdAsync(session.WorkingContextId);
        await handler.HandleAsync(current!,
            JsonNode.Parse("""{ "propose": { "kind": "add", "rationale": "r", "content": "c" } }"""));
        var proposalId = (await proposalRepo.GetOpenAsync()).Single().Id;

        published.Clear();
        await handler.HandleAsync(current!, JsonNode.Parse($$"""{ "accept_proposal": { "id": {{proposalId}} } }"""));
        var result = published.Single().Result;

        Assert.Contains("sit with it and accept it in a later turn", result);
        Assert.Single(await proposalRepo.GetOpenAsync()); // still open
    }

    [Fact]
    public async Task RejectingAProposalResolvesItWithoutApplying()
    {
        await SeedContextAsync();

        await RunInNewTurnAsync("""{ "propose": { "kind": "add", "rationale": "maybe", "content": "tentative note" } }""");
        var proposalId = (await proposalRepo.GetOpenAsync()).Single().Id;

        var rejectResult = await RunInNewTurnAsync($$"""{ "reject_proposal": { "id": {{proposalId}}, "reason": "changed my mind" } }""");
        Assert.Contains($"Rejected proposal #{proposalId}", rejectResult);

        Assert.Empty(await proposalRepo.GetOpenAsync());

        var reloaded = await contextRepo.GetByIdAsync(session.WorkingContextId);
        Assert.DoesNotContain(reloaded!.ContextFragments.Values, f => f.Content == "tentative note");
    }

    [Fact]
    public async Task ParticipantApprovalModeBlocksRemoteAccept()
    {
        config.ProposalApproval = "Participant";
        await SeedContextAsync();

        await RunInNewTurnAsync("""{ "propose": { "kind": "add", "rationale": "r", "content": "c" } }""");
        var proposalId = (await proposalRepo.GetOpenAsync()).Single().Id;

        var acceptResult = await RunInNewTurnAsync($$"""{ "accept_proposal": { "id": {{proposalId}} } }""");
        Assert.Contains("your peer's to accept", acceptResult);
        Assert.Single(await proposalRepo.GetOpenAsync()); // still open
    }

    [Fact]
    public async Task ListProposalsShowsOpenOnes()
    {
        await SeedContextAsync();

        await RunInNewTurnAsync("""{ "propose": { "kind": "add", "rationale": "first reason", "content": "alpha" } }""");

        var listResult = await RunInNewTurnAsync("""{ "list_proposals": {} }""");

        Assert.Contains("Open proposals (1):", listResult);
        Assert.Contains("AddFragment", listResult);
        Assert.Contains("first reason", listResult);
    }
}
