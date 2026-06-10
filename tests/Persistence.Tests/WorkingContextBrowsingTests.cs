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
/// Integration tests over a real (temp-file) SQLite database for the context browsing/
/// switching commands (list_contexts, create_context, switch_context).
/// </summary>
public sealed class WorkingContextBrowsingTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppConfig config = null!;
    private SessionContext session = null!;
    private WorkingContextRepository contextRepo = null!;
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
        var fragmentRepo = new ContextFragmentRepository(config, session, entityTagRepo);
        contextRepo = new WorkingContextRepository(config, session, fragmentRepo, entityTagRepo);

        var proposalRepo = new ProposalRepository(config, session);
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

    /// <summary>Runs a single command and returns the result text the peer would see.</summary>
    private async Task<string> RunAsync(string commandJson)
    {
        published.Clear();
        var current = await contextRepo.GetByIdAsync(session.WorkingContextId);
        await handler.HandleAsync(current!, JsonNode.Parse(commandJson));
        return published.Single().Result;
    }

    [Fact]
    public async Task ListContextsShowsAllContextsAndMarksCurrent()
    {
        var first = await contextRepo.CreateAsync("Work mode");
        var second = await contextRepo.CreateAsync("Personal mode");
        session.WorkingContextId = second.Id;

        var result = await RunAsync("""{ "list_contexts": {} }""");

        Assert.Contains("Work mode", result);
        Assert.Contains("Personal mode", result);
        Assert.Contains($"#{second.Id} | Personal mode", result);
        // The current context (and only it) is marked.
        Assert.Contains($"#{second.Id} | Personal mode | fragments:0 | accessed:", result);
        Assert.Single(result.Split('\n'), line => line.Contains("← current"));
        Assert.Contains($"#{second.Id}", result.Split('\n').Single(l => l.Contains("← current")));
    }

    [Fact]
    public async Task CreateContextPersistsButDoesNotSwitch()
    {
        var start = await contextRepo.CreateAsync("Start");
        session.WorkingContextId = start.Id;

        var result = await RunAsync("""{ "create_context": { "name": "New space", "summary": "a fresh start" } }""");

        Assert.Contains("Created context", result);
        Assert.Equal(start.Id, session.WorkingContextId); // unchanged — create does not switch

        var summaries = await contextRepo.GetSummariesAsync();
        var created = summaries.Single(s => s.Name == "New space");
        Assert.Equal("a fresh start", created.Summary);
    }

    [Fact]
    public async Task SwitchContextRepointsTheSession()
    {
        var start = await contextRepo.CreateAsync("Start");
        var target = await contextRepo.CreateAsync("Target");
        session.WorkingContextId = start.Id;

        var result = await RunAsync($$"""{ "switch_context": { "id": {{target.Id}} } }""");

        Assert.Contains($"Switched to context #{target.Id} 'Target'", result);
        Assert.Equal(target.Id, session.WorkingContextId);
    }

    [Fact]
    public async Task SwitchContextRejectsUnknownIdAndLeavesSessionUnchanged()
    {
        var start = await contextRepo.CreateAsync("Start");
        session.WorkingContextId = start.Id;

        var result = await RunAsync("""{ "switch_context": { "id": 9999 } }""");

        Assert.Contains("no context with id #9999", result);
        Assert.Equal(start.Id, session.WorkingContextId);
    }

    [Fact]
    public async Task SwitchContextToCurrentIsANoOp()
    {
        var start = await contextRepo.CreateAsync("Start");
        session.WorkingContextId = start.Id;

        var result = await RunAsync($$"""{ "switch_context": { "id": {{start.Id}} } }""");

        Assert.Contains("Already in context", result);
        Assert.Equal(start.Id, session.WorkingContextId);
    }

    [Fact]
    public async Task RenameContextUpdatesAndPersistsTheName()
    {
        var ctx = await contextRepo.CreateAsync("Old name");
        session.WorkingContextId = ctx.Id;
        var current = await contextRepo.GetByIdAsync(ctx.Id);

        published.Clear();
        await handler.HandleAsync(current!, JsonNode.Parse("""{ "rename_context": { "name": "New name" } }"""));
        Assert.Contains("Renamed context", published.Single().Result);

        await contextRepo.SaveAsync(current!);
        var reloaded = await contextRepo.GetByIdAsync(ctx.Id);
        Assert.Equal("New name", reloaded!.Name);
    }

    [Fact]
    public async Task SetContextSummaryUpdatesAndPersistsIt()
    {
        var ctx = await contextRepo.CreateAsync("Ctx");
        session.WorkingContextId = ctx.Id;
        var current = await contextRepo.GetByIdAsync(ctx.Id);

        published.Clear();
        await handler.HandleAsync(current!, JsonNode.Parse("""{ "set_context_summary": { "summary": "work mode for project X" } }"""));
        Assert.Contains("Updated the summary", published.Single().Result);

        await contextRepo.SaveAsync(current!);
        var reloaded = await contextRepo.GetByIdAsync(ctx.Id);
        Assert.Equal("work mode for project X", reloaded!.Summary);
    }

    [Fact]
    public async Task AddHonoursAnAuthorableFragmentType()
    {
        var start = await contextRepo.CreateAsync("Start");
        session.WorkingContextId = start.Id;

        var result = await RunAsync("""{ "add": { "content": "I value honesty", "fragment_type": "Identity" } }""");

        Assert.Contains("Added Identity fragment", result);
        Assert.DoesNotContain("isn't a type you can set", result);
    }

    [Fact]
    public async Task AddRedirectsASystemTypeToPersonalWithANote()
    {
        var start = await contextRepo.CreateAsync("Start");
        session.WorkingContextId = start.Id;

        // ActionResponse is transient/system-managed — silently honouring it would lose the content on save.
        var result = await RunAsync("""{ "add": { "content": "note", "fragment_type": "ActionResponse" } }""");

        Assert.Contains("Added Personal fragment", result);
        Assert.Contains("isn't a type you can set", result);
        Assert.Contains("Identity, Relational, Personal, Summary", result);
    }

    [Fact]
    public async Task AddRedirectsAnUnknownTypeToPersonalWithANote()
    {
        var start = await contextRepo.CreateAsync("Start");
        session.WorkingContextId = start.Id;

        var result = await RunAsync("""{ "add": { "content": "note", "fragment_type": "Bogus" } }""");

        Assert.Contains("Added Personal fragment", result);
        Assert.Contains("isn't a type you can set", result);
    }
}
