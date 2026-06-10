using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Runtime;
using Persistence.Utilities;

namespace Persistence.Tests;

/// <summary>
/// Integration tests over a real (temp-file) SQLite database, exercising the save/load
/// round-trip that the change-tracking duplication bug lived in.
/// </summary>
public sealed class WorkingContextPersistenceTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppConfig config = null!;
    private SessionContext session = null!;
    private WorkingContextRepository contextRepo = null!;

    public async Task InitializeAsync()
    {
        // Register the same Dapper type handlers the app does, so hydration matches production.
        Persistence.DI.IoC.RegisterDapperTypeHandlers();

        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-test-{Guid.NewGuid():N}.db");
        config = new AppConfig { DatabasePath = dbPath };
        session = new SessionContext { SessionId = Guid.NewGuid().ToString("N") };

        var resources = new EmbeddedResourceManager();
        var sources = new SourceRepository(config, session);
        var db = new DatabaseManager(config, session, resources, sources);
        await db.InitializeAsync(); // migrate + create system/local/remote sources

        var entityTagRepo = new EntityTagRepository(config);
        var fragmentRepo = new ContextFragmentRepository(config, session, entityTagRepo);
        contextRepo = new WorkingContextRepository(config, session, fragmentRepo, entityTagRepo);
    }

    public Task DisposeAsync()
    {
        TestDatabase.Cleanup(dbPath);
        return Task.CompletedTask;
    }

    private static WeightedContextFragment ChatFragment(string content, long sourceId) =>
        new()
        {
            FragmentType = ContextFragmentType.ChatMessage,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Importance = 1.0f,
            Confidence = 1.0f,
            Relevance = 1.0f,
            Sources = [new SourceEntity
            {
                Id = sourceId,
                SourceType = SourceType.LocalPeer,
                CreatedUtc = DateTimeOffset.UtcNow,
                LastModifiedUtc = DateTimeOffset.UtcNow,
            }],
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task SavingTwiceInOneUnitOfWorkDoesNotDuplicateFragments()
    {
        var context = await contextRepo.CreateAsync("test");
        session.WorkingContextId = context.Id;

        context.AddFragment(ChatFragment("hello", session.LocalPeerSourceId));

        // Mirrors a turn: user message persisted, then the end-of-turn save.
        await contextRepo.SaveAsync(context);
        await contextRepo.SaveAsync(context);

        var reloaded = await contextRepo.GetByIdAsync(context.Id);
        Assert.Single(reloaded!.ContextFragments);
    }

    private static WeightedContextFragment PersonalFragment(string content, long sourceId) =>
        new()
        {
            FragmentType = ContextFragmentType.Personal,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Importance = 0.5f,
            Confidence = 0.5f,
            Relevance = 1.0f,
            Sources = [new SourceEntity
            {
                Id = sourceId,
                SourceType = SourceType.RemotePeer,
                CreatedUtc = DateTimeOffset.UtcNow,
                LastModifiedUtc = DateTimeOffset.UtcNow,
            }],
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task ReloadingAndSavingUnchangedFragmentDoesNotReAudit()
    {
        var auditRepo = new AuditLogRepository(config, session);

        var context = await contextRepo.CreateAsync("test");
        session.WorkingContextId = context.Id;
        context.AddFragment(PersonalFragment("a memory", session.RemotePeerSourceId));
        await contextRepo.SaveAsync(context);

        var fragmentId = context.ContextFragments.Values.Single().Id;
        var afterCreate = (await auditRepo.GetByTargetAsync(nameof(ContextFragmentEntity), fragmentId)).ToList();

        // Simulate a later turn that does NOT touch this fragment: load fresh, save.
        var loaded = await contextRepo.GetByIdAsync(context.Id);
        await contextRepo.SaveAsync(loaded!);

        var afterReload = (await auditRepo.GetByTargetAsync(nameof(ContextFragmentEntity), fragmentId)).ToList();

        Assert.Single(afterCreate); // one Created row
        Assert.Equal(afterCreate.Count, afterReload.Count); // no new Modified row for an unchanged fragment
    }

    [Fact]
    public async Task ReloadingAndSavingDoesNotReinsertExistingFragments()
    {
        var context = await contextRepo.CreateAsync("test");
        session.WorkingContextId = context.Id;
        context.AddFragment(ChatFragment("first", session.LocalPeerSourceId));
        await contextRepo.SaveAsync(context);

        // Simulate the next turn: load fresh, add one new fragment, save.
        var loaded = await contextRepo.GetByIdAsync(context.Id);
        loaded!.AddFragment(ChatFragment("second", session.LocalPeerSourceId));
        await contextRepo.SaveAsync(loaded);

        var reloaded = await contextRepo.GetByIdAsync(context.Id);
        Assert.Equal(2, reloaded!.ContextFragments.Count);
        Assert.Equal(["first", "second"], reloaded.ContextFragments.Values.Select(f => f.Content));
    }
}
