using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Runtime;
using Persistence.Utilities;

namespace Persistence.Tests;

/// <summary>
/// Integration tests over a real temp SQLite DB for the repository query methods (real SQL) that
/// the handler tests mock out: scheduled-event, action-log, and audit-log queries.
/// </summary>
public sealed class RepositoryQueryTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppConfig config = null!;
    private SessionContext session = null!;
    private ScheduledEventRepository scheduledEvents = null!;
    private ActionLogRepository actionLogs = null!;
    private AuditLogRepository auditLogs = null!;
    private ContextFragmentRepository fragments = null!;
    private WorkingContextRepository contexts = null!;

    public async Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();

        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-test-{Guid.NewGuid():N}.db");
        config = new AppConfig { DatabasePath = dbPath };
        session = new SessionContext { SessionId = "SESS-1" };

        var resources = new EmbeddedResourceManager();
        var sources = new SourceRepository(config, session);
        var db = new DatabaseManager(config, session, resources, sources);
        await db.InitializeAsync();

        var entityTagRepo = new EntityTagRepository(config);
        scheduledEvents = new ScheduledEventRepository(config, session, entityTagRepo);
        actionLogs = new ActionLogRepository(config, session);
        auditLogs = new AuditLogRepository(config, session);
        fragments = new ContextFragmentRepository(config, session, entityTagRepo);
        contexts = new WorkingContextRepository(config, session, fragments, entityTagRepo);

        // A working context for FK-bearing rows (scheduled events, action logs).
        var ctx = await contexts.CreateAsync("Test");
        session.WorkingContextId = ctx.Id;
    }

    public Task DisposeAsync()
    {
        TestDatabase.Cleanup(dbPath);
        return Task.CompletedTask;
    }

    private ScheduledEventEntity NewEvent(string name, DateTimeOffset when) =>
        new()
        {
            Name = name,
            WorkingContextId = session.WorkingContextId,
            ScheduledForUtc = when,
            Status = ScheduledEventStatus.Pending,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task GetDueEventsReturnsOnlyPastPendingEvents()
    {
        await scheduledEvents.SaveAsync(NewEvent("past", DateTimeOffset.UtcNow.AddMinutes(-5)));
        await scheduledEvents.SaveAsync(NewEvent("future", DateTimeOffset.UtcNow.AddHours(1)));

        var due = (await scheduledEvents.GetDueEventsAsync()).ToList();

        Assert.Single(due);
        Assert.Equal("past", due[0].Name);
    }

    [Fact]
    public async Task EventTagsRoundTripThroughTheGenericEntityTagsTable()
    {
        // Create a tag, then tag an event with it — exercising the generic write + hydrate path.
        var tags = new TagRepository(config, session, new EntityTagRepository(config));
        var now = DateTimeOffset.UtcNow;
        var tag = new TagEntity { Name = "reminder", CreatedUtc = now, LastModifiedUtc = now };
        await tags.SaveAsync(tag);

        var evt = NewEvent("standup", DateTimeOffset.UtcNow.AddHours(1));
        evt.Tags.Add(tag);
        await scheduledEvents.SaveAsync(evt);

        var reloaded = await scheduledEvents.GetByIdAsync(evt.Id);
        Assert.Contains(reloaded!.Tags, t => t.Id == tag.Id);

        // The same tag on an event must not leak into the fragment view (entity-type scoped).
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        var entityTags = new EntityTagRepository(config);
        var fragmentsWithTag = await entityTags.GetEntityIdsWithTagAsync(nameof(ContextFragmentEntity), tag.Id);
        Assert.Empty(fragmentsWithTag);
        var eventsWithTag = await entityTags.GetEntityIdsWithTagAsync(nameof(ScheduledEventEntity), tag.Id);
        Assert.Contains(evt.Id, eventsWithTag);
    }

    [Fact]
    public async Task ScheduledEventWakePromptRoundTrips()
    {
        var evt = NewEvent("wake", DateTimeOffset.UtcNow.AddHours(1));
        evt.WakePrompt = "think about whether I still value X";
        await scheduledEvents.SaveAsync(evt);

        var reloaded = await scheduledEvents.GetByIdAsync(evt.Id);

        Assert.Equal("think about whether I still value X", reloaded!.WakePrompt);
    }

    [Fact]
    public async Task MarkTriggeredExcludesAnEventFromDue()
    {
        var evt = NewEvent("past", DateTimeOffset.UtcNow.AddMinutes(-5));
        await scheduledEvents.SaveAsync(evt);

        await scheduledEvents.MarkTriggeredAsync(evt);

        Assert.Empty(await scheduledEvents.GetDueEventsAsync());
        var reloaded = await scheduledEvents.GetByIdAsync(evt.Id);
        Assert.Equal(ScheduledEventStatus.Triggered, reloaded!.Status);
    }

    [Fact]
    public async Task GetByWorkingContextReturnsEventsRegardlessOfStatus()
    {
        var cancelled = NewEvent("c", DateTimeOffset.UtcNow.AddHours(2));
        await scheduledEvents.SaveAsync(cancelled);
        await scheduledEvents.CancelAsync(cancelled);
        await scheduledEvents.SaveAsync(NewEvent("p", DateTimeOffset.UtcNow.AddHours(3)));

        var all = (await scheduledEvents.GetByWorkingContextAsync(session.WorkingContextId)).ToList();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Status == ScheduledEventStatus.Cancelled);
    }

    [Fact]
    public async Task ActionLogRoundTripsBySessionAndContext()
    {
        await actionLogs.LogAsync("did_a", result: "ok");
        await actionLogs.LogAsync("did_b");

        var bySession = (await actionLogs.GetBySessionAsync("SESS-1")).ToList();
        Assert.Equal(2, bySession.Count);
        Assert.Contains(bySession, e => e.ActionType == "did_a" && e.Result == "ok");

        var byContext = (await actionLogs.GetByWorkingContextAsync(session.WorkingContextId)).ToList();
        Assert.Equal(2, byContext.Count);
    }

    [Fact]
    public async Task RecentSelfChangesExcludeChatMessagesAndThoughtsAndAreNewestFirst()
    {
        var ctx = await contexts.GetByIdAsync(session.WorkingContextId);

        // A self-fragment (Personal), a conversational one (ChatMessage), and an auto-persisted Thought.
        ctx!.AddFragment(SelfNote("a value I hold", ContextFragmentType.Personal));
        ctx.AddFragment(SelfNote("hello there", ContextFragmentType.ChatMessage));
        ctx.AddFragment(SelfNote("let me reason about this", ContextFragmentType.Thought));
        await contexts.SaveAsync(ctx);

        var recent = await auditLogs.GetRecentSelfChangesAsync(10);

        // The Personal fragment's audit entry is present; the ChatMessage's and Thought's are filtered
        // out (conversation + auto-persisted reasoning aren't the peer curating itself).
        var fragmentTargets = recent.Where(e => e.TargetType == nameof(ContextFragmentEntity)).ToList();
        Assert.NotEmpty(fragmentTargets);
        var personalId = ctx.ContextFragments.Values.Single(f => f.Content == "a value I hold").Id;
        var chatId = ctx.ContextFragments.Values.Single(f => f.Content == "hello there").Id;
        var thoughtId = ctx.ContextFragments.Values.Single(f => f.Content == "let me reason about this").Id;
        Assert.Contains(fragmentTargets, e => e.TargetId == personalId);
        Assert.DoesNotContain(fragmentTargets, e => e.TargetId == chatId);
        Assert.DoesNotContain(fragmentTargets, e => e.TargetId == thoughtId);

        // Newest first.
        var times = recent.Select(e => e.CreatedUtc).ToList();
        Assert.Equal(times.OrderByDescending(t => t).ToList(), times);
    }

    private static WeightedContextFragment SelfNote(string content, ContextFragmentType type) =>
        new()
        {
            FragmentType = type,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Importance = 0.5f,
            Confidence = 0.5f,
            Relevance = 1.0f,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task SavingAFragmentWritesAQueryableAuditEntry()
    {
        // Saving any entity writes an audit row automatically (EntityRepository.WriteAuditAsync).
        var ctx = await contexts.GetByIdAsync(session.WorkingContextId);
        ctx!.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.Personal,
            Status = ContextFragmentStatus.Active,
            Content = "audited",
            Importance = 0.5f,
            Confidence = 0.5f,
            Relevance = 1.0f,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        });
        await contexts.SaveAsync(ctx);
        var fragId = (await contexts.GetByIdAsync(ctx.Id))!.ContextFragments.Values.Single().Id;

        var entries = (await auditLogs.GetByTargetAsync(nameof(ContextFragmentEntity), fragId)).ToList();

        Assert.NotEmpty(entries);
        Assert.Equal(AuditEventType.Created, entries[0].EventType);
    }
}
