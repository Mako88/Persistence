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
/// Integration tests over a real temp SQLite DB for the <see cref="ManageContextHandler"/> commands
/// that genuinely exercise the database — tags, sources, fetch, load, list_fragments — where a
/// mocked repo wouldn't test the real query/tree behaviour.
/// </summary>
public sealed class ManageContextHandlerIntegrationTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppConfig config = null!;
    private SessionContext session = null!;
    private WorkingContextRepository contextRepo = null!;
    private ContextFragmentRepository fragmentRepo = null!;
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
        return Task.CompletedTask;
    }

    private async Task<WorkingContextEntity> NewContextAsync()
    {
        var context = await contextRepo.CreateAsync("Test");
        session.WorkingContextId = context.Id;
        return await contextRepo.GetByIdAsync(context.Id) ?? context;
    }

    private async Task<string> RunAsync(WorkingContextEntity context, string json)
    {
        published.Clear();
        await handler.HandleAsync(context, JsonNode.Parse(json));
        return published.Single().Result;
    }

    private static WeightedContextFragment Note(string content) =>
        new()
        {
            FragmentType = ContextFragmentType.Personal,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Importance = 0.5f,
            Confidence = 0.5f,
            Relevance = 1.0f,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task CreateTagThenReportsDuplicate()
    {
        var context = await NewContextAsync();

        Assert.Contains("Created tag 'knowledge/science'", await RunAsync(context, """{ "create_tag": { "name": "knowledge/science" } }"""));
        Assert.Contains("already exists", await RunAsync(context, """{ "create_tag": { "name": "knowledge/science" } }"""));
    }

    [Fact]
    public async Task ListTagsShowsTheTree()
    {
        var context = await NewContextAsync();
        await RunAsync(context, """{ "create_tag": { "name": "knowledge/science" } }""");

        var result = await RunAsync(context, """{ "list_tags": {} }""");

        Assert.Contains("knowledge", result);
        Assert.Contains("science", result);
    }

    [Fact]
    public async Task TagThenUntagAFragment()
    {
        var context = await NewContextAsync();
        context.AddFragment(Note("taggable"));
        await contextRepo.SaveAsync(context);
        context = await contextRepo.GetByIdAsync(context.Id);
        var fragId = context!.ContextFragments.Values.Single().Id;

        await RunAsync(context, """{ "create_tag": { "name": "topic/alpha" } }""");

        var tagged = await RunAsync(context, $$"""{ "tag": { "id": {{fragId}}, "tag": "topic/alpha" } }""");
        Assert.Contains($"Tagged fragment #{fragId}", tagged);
        Assert.NotEmpty(context.ContextFragments.Values.Single(f => f.Id == fragId).Tags);

        var untagged = await RunAsync(context, $$"""{ "untag": { "id": {{fragId}}, "tag": "topic/alpha" } }""");
        Assert.Contains($"Removed 'topic/alpha' from fragment #{fragId}", untagged);
        Assert.Empty(context.ContextFragments.Values.Single(f => f.Id == fragId).Tags);
    }

    [Fact]
    public async Task DeleteTagRemovesTheLabel()
    {
        var context = await NewContextAsync();
        await RunAsync(context, """{ "create_tag": { "name": "temp/old" } }""");

        var result = await RunAsync(context, """{ "delete_tag": { "tag": "temp/old" } }""");
        Assert.Contains("Deleted tag 'temp/old'", result);

        Assert.Contains("not found", await RunAsync(context, """{ "delete_tag": { "tag": "temp/old" } }"""));
    }

    [Fact]
    public async Task FetchFindsAnInContextFragmentByTag()
    {
        var context = await NewContextAsync();
        context.AddFragment(Note("findable"));
        await contextRepo.SaveAsync(context);
        context = await contextRepo.GetByIdAsync(context.Id);
        var fragId = context!.ContextFragments.Values.Single().Id;

        await RunAsync(context, """{ "create_tag": { "name": "find/me" } }""");
        await RunAsync(context, $$"""{ "tag": { "id": {{fragId}}, "tag": "find/me" } }""");

        var result = await RunAsync(context, """{ "fetch": { "tag": "find/me" } }""");

        Assert.Contains($"Fragments tagged 'find/me'", result);
        Assert.Contains("findable", result);
    }

    [Fact]
    public async Task FetchReportsUnknownTag()
    {
        var context = await NewContextAsync();
        Assert.Contains("not found", await RunAsync(context, """{ "fetch": { "tag": "nope/zilch" } }"""));
    }

    [Fact]
    public async Task LoadBringsAPersistedFragmentIntoContext()
    {
        // Persist a fragment in one context...
        var source = await NewContextAsync();
        source.AddFragment(Note("archived thought"));
        await contextRepo.SaveAsync(source);
        var fragId = (await contextRepo.GetByIdAsync(source.Id))!.ContextFragments.Values.Single().Id;

        // ...then load it into a fresh, empty context.
        var target = await NewContextAsync();
        var result = await RunAsync(target, $$"""{ "load": { "ids": [{{fragId}}] } }""");

        Assert.Contains("Loaded 1 fragment(s)", result);
        Assert.Contains(target.ContextFragments.Values, f => f.Id == fragId);
    }

    [Fact]
    public async Task CreateSourceThenAddItToAFragment()
    {
        var context = await NewContextAsync();
        context.AddFragment(Note("sourced"));
        await contextRepo.SaveAsync(context);
        context = await contextRepo.GetByIdAsync(context.Id);
        var fragId = context!.ContextFragments.Values.Single().Id;

        Assert.Contains("Created source 'a-book'", await RunAsync(context, """{ "create_source": { "name": "a-book" } }"""));

        var added = await RunAsync(context, $$"""{ "add_source": { "id": {{fragId}}, "source": "a-book" } }""");
        Assert.Contains($"#{fragId}", added);
        Assert.Contains(context.ContextFragments.Values.Single(f => f.Id == fragId).Sources, s => s.Name == "a-book");
    }

    [Fact]
    public async Task ListSourcesShowsSeededSources()
    {
        var context = await NewContextAsync();

        var result = await RunAsync(context, """{ "list_sources": {} }""");

        Assert.Contains("Sources", result);
        Assert.Contains("System", result); // a seeded source
    }

    [Fact]
    public async Task ListFragmentsListsContextFragments()
    {
        var context = await NewContextAsync();
        context.AddFragment(Note("alpha entry"));
        await contextRepo.SaveAsync(context);
        context = await contextRepo.GetByIdAsync(context.Id);

        var result = await RunAsync(context!, """{ "list_fragments": { "in_current_context": true, "include_content": true } }""");

        Assert.Contains("Fragments", result);
        Assert.Contains("alpha entry", result);
    }

    private ScheduledEventRepository NewEventRepo() =>
        new(config, session, new EntityTagRepository(config));

    private async Task<ScheduledEventEntity> NewEventAsync(long contextId, string name)
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new ScheduledEventEntity
        {
            Name = name,
            WorkingContextId = contextId,
            ScheduledForUtc = now.AddHours(1),
            Status = ScheduledEventStatus.Pending,
            CreatedUtc = now,
            LastModifiedUtc = now,
        };
        await NewEventRepo().SaveAsync(evt);
        return evt;
    }

    [Fact]
    public async Task TagThenUntagTheCurrentContext()
    {
        var context = await NewContextAsync();
        await RunAsync(context, """{ "create_tag": { "name": "mode/reflection" } }""");

        var tagged = await RunAsync(context, """{ "tag": { "entity_type": "context", "tag": "mode/reflection" } }""");
        Assert.Contains($"Tagged context #{context.Id}", tagged);

        // Persisted on the end-of-turn save, then visible on reload.
        await contextRepo.SaveAsync(context);
        var reloaded = await contextRepo.GetByIdAsync(context.Id);
        Assert.Contains(reloaded!.Tags, t => t.Name == "reflection");

        var untagged = await RunAsync(reloaded, """{ "untag": { "entity_type": "context", "tag": "mode/reflection" } }""");
        Assert.Contains($"Removed 'mode/reflection' from context #{context.Id}", untagged);
        await contextRepo.SaveAsync(reloaded);
        Assert.Empty((await contextRepo.GetByIdAsync(context.Id))!.Tags);
    }

    [Fact]
    public async Task TaggingANonCurrentContextPointsAtSwitch()
    {
        var context = await NewContextAsync();
        await RunAsync(context, """{ "create_tag": { "name": "mode/x" } }""");

        var result = await RunAsync(context, $$"""{ "tag": { "entity_type": "context", "id": {{context.Id + 999}}, "tag": "mode/x" } }""");
        Assert.Contains("switch_context", result);
    }

    [Fact]
    public async Task FetchFindsAContextByTag()
    {
        var context = await NewContextAsync();
        await RunAsync(context, """{ "create_tag": { "name": "mode/journal" } }""");
        await RunAsync(context, """{ "tag": { "entity_type": "context", "tag": "mode/journal" } }""");
        await contextRepo.SaveAsync(context);

        var result = await RunAsync(context, """{ "fetch": { "entity_type": "context", "tag": "mode/journal" } }""");

        Assert.Contains("Working contexts tagged 'mode/journal'", result);
        Assert.Contains($"#{context.Id}", result);
    }

    [Fact]
    public async Task TagAnEventThenFetchItByTag()
    {
        var context = await NewContextAsync();
        var evt = await NewEventAsync(context.Id, "check in on values");
        await RunAsync(context, """{ "create_tag": { "name": "reminder/identity" } }""");

        var tagged = await RunAsync(context, $$"""{ "tag": { "entity_type": "event", "id": {{evt.Id}}, "tag": "reminder/identity" } }""");
        Assert.Contains($"Tagged event #{evt.Id}", tagged);

        // Events persist immediately (not part of the end-of-turn context save).
        var reloaded = await NewEventRepo().GetByIdAsync(evt.Id);
        Assert.Contains(reloaded!.Tags, t => t.Name == "identity");

        var result = await RunAsync(context, """{ "fetch": { "entity_type": "event", "tag": "reminder/identity" } }""");
        Assert.Contains("Scheduled events tagged 'reminder/identity'", result);
        Assert.Contains("check in on values", result);
    }

    [Fact]
    public async Task TagAnUnknownEventReportsNotFound()
    {
        var context = await NewContextAsync();
        await RunAsync(context, """{ "create_tag": { "name": "reminder/x" } }""");

        var result = await RunAsync(context, """{ "tag": { "entity_type": "event", "id": 9999, "tag": "reminder/x" } }""");
        Assert.Contains("no event #9999", result);
    }

    [Fact]
    public async Task TagWithUnknownEntityTypeReports()
    {
        var context = await NewContextAsync();
        var result = await RunAsync(context, """{ "tag": { "entity_type": "banana", "id": 1, "tag": "a/b" } }""");
        Assert.Contains("unknown entity_type", result);
    }
}
