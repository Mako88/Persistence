using Dapper;
using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Repositories;
using Persistence.Store;

namespace Persistence.Tests;

/// <summary>
/// Close PR B (docs/design/close-build-plan.md B1 to B4): concept facts whose sources are all closed. The flag is
/// derived when read; the close that closes the last source drafts the fact's end only when every source shares one
/// ended-by; approval revalidates in its own transaction. Each test names, in its comment, the mutation that turns it red.
/// </summary>
[Collection(nameof(FragmentStoreStaticsCollection))]
public sealed class EndDraftTests : IDisposable
{
    private const string RefA = "commit:Persistence@abcdef1";
    private const string RefB = "commit:Persistence@abcdef2";

    private readonly string db = Path.Combine(Path.GetTempPath(), $"enddraft-{Guid.NewGuid():N}.db");

    public void Dispose() => TestDatabase.Cleanup(db);

    private async Task<FragmentStore> OpenAsync()
    {
        var config = new AppConfig { PeerName = "Arden" };
        config.DatabasePath = db;
        return await FragmentStore.OpenAsync(config);
    }

    private static async Task<long> Add(FragmentStore store, string content, bool state = false) =>
        (await store.AddAsync("Personal", content, null, 0.5f, null, [], false, isState: state)).Id;

    private static Task<FactRow> Fact(FragmentStore store, string text, params long[] sources) =>
        store.AddFactAsync("Glider", text, sources, kind: "thing");

    /// <summary>Raw SQL, foreign keys off: how a test makes the states no command writes, or disables an earlier drop.</summary>
    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var conn = new SqliteConnection($"Data Source={db};Foreign Keys=False;");
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, args);
    }

    private async Task<IReadOnlyList<FactEndDraftRow>> DraftsAsync(long factId)
    {
        await using var conn = new SqliteConnection(SqliteConnectionString.For(db));
        await conn.OpenAsync();
        return (await conn.QueryAsync<FactEndDraftRow>(
            "SELECT Id, FactId, EndedBy, Reasons, CreatedUtc, Status, ResolvedUtc, Resolution FROM FactEndDrafts WHERE FactId = @factId ORDER BY Id",
            new { factId })).ToList();
    }

    private async Task<FactRow> FactRowAsync(long id)
    {
        await using var conn = new SqliteConnection(SqliteConnectionString.For(db));
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<FactRow>(
            "SELECT Id, SubjectId, ObjectId, Text, Status, IsState, AsOfUtc, RecordedAtUtc, SupersededById, Supersedes, ExtractedBy, Resolution, CreatedUtc, EndedUtc, EndReason, EndedBy FROM Facts WHERE Id = @id",
            new { id });
    }

    private static async Task<string> RefusedAsync(Func<Task> act) => (await Assert.ThrowsAsync<RefusedException>(act)).Message;

    private static async Task<string> UsageAsync(Func<Task> act) => (await Assert.ThrowsAsync<UsageException>(act)).Message;

    /// <summary>Two sources of one fact, both closed under <see cref="RefA"/>: the fact and its one pending draft.</summary>
    private async Task<(FragmentStore Store, long A, long B, FactRow Fact, FactEndDraftRow Draft)> DraftedAsync()
    {
        var store = await OpenAsync();
        var a = await Add(store, "PR 99 waits on Ember's review.", state: true);
        var b = await Add(store, "The glider wing waits on PR 99.", state: true);
        var fact = await Fact(store, "waits on PR 99", a, b);
        await store.CloseAsync(a, RefA, "PR 99 merged as abcdef1");
        await store.CloseAsync(b, RefA, "the wing went ahead once abcdef1 landed");
        return (store, a, b, fact, (await DraftsAsync(fact.Id)).Single());
    }

    // ---- B1: the flag ----

    [Fact]
    public async Task AllSourcesClosedUnderOneRef_FlagAndOneDraft()
    {
        // Red if: the close doesn't draft, the draft keeps one reason only, or the mark isn't rendered.
        var (store, a, b, fact, draft) = await DraftedAsync();

        Assert.Equal("pending", draft.Status);
        Assert.Equal(RefA, draft.EndedBy);
        Assert.Equal($"#{a}: PR 99 merged as abcdef1\n#{b}: the wing went ahead once abcdef1 landed", draft.Reasons);
        var shown = await store.ConceptShowAsync("Glider", history: false);
        Assert.Contains($"#{fact.Id} [sources closed] ", shown);
        Assert.Contains($"end draft #{draft.Id} pending", shown);
        Assert.Contains("[sources closed]", await store.ConceptBlockAsync("How is the Glider doing?"));
        Assert.Single(await store.EndDraftsRestingOnAsync(b));
    }

    [Fact]
    public async Task OneSourceStillOpen_NoFlagNoDraft()
    {
        // Red if: the draft is made when any one source closes, or the flag is "some source closed".
        var store = await OpenAsync();
        var a = await Add(store, "Waiting on PR 99.", state: true);
        var b = await Add(store, "Also waiting on PR 99.", state: true);
        var fact = await Fact(store, "waits on PR 99", a, b);
        await store.CloseAsync(a, RefA, "merged");

        Assert.Empty(await DraftsAsync(fact.Id));
        Assert.DoesNotContain("[sources closed]", await store.ConceptShowAsync("Glider", history: false));
    }

    [Fact]
    public async Task TwoRefs_FlagListsEachSource_NoDraft()
    {
        // Red if: a draft is made under the first ref (or the last) when the sources disagree.
        var store = await OpenAsync();
        var a = await Add(store, "Waiting on PR 99.", state: true);
        var b = await Add(store, "Waiting on PR 98.", state: true);
        var fact = await Fact(store, "waits on PRs", a, b);
        await store.CloseAsync(a, RefA, "99 merged");
        await store.CloseAsync(b, RefB, "98 merged");

        Assert.Empty(await DraftsAsync(fact.Id));
        var shown = await store.ConceptShowAsync("Glider", history: false);
        Assert.Contains("[sources closed]", shown);
        Assert.Contains($"source #{a} closed", shown);
        Assert.Contains($"(ended by {RefA}): 99 merged", shown);
        Assert.Contains($"(ended by {RefB}): 98 merged", shown);
        Assert.Contains("don't share one ended-by", shown);
    }

    [Fact]
    public async Task ZeroSources_NoFlagNoDraft()
    {
        // Red if the check is "no open source" rather than "at least one, and all closed" (Ember 4).
        var fact = new FactRow(1, 1, null, "x", "active", 0, null, null, null, null, "by hand", null, "2026-11-28 00:00:00.000+00:00");
        Assert.Equal("", FragmentStore.ClosedMark(fact, []));
        Assert.Null(ConceptRepository.SharedEnding([]));
        Assert.Equal("", FragmentStore.ClosedLines(fact, [], []));
    }

    [Fact]
    public async Task OneSourceClosedWithStateAndNoRef_FlagButNoDraft()
    {
        // Red if a ref-less close is skipped when looking for the shared ref (the rest share RefA), or null counts as a ref.
        var store = await OpenAsync();
        var a = await Add(store, "Waiting on PR 99.", state: true);
        var b = await Add(store, "Holding the wing.", state: true);
        var fact = await Fact(store, "waits", a, b);
        await store.CloseAsync(a, RefA, "merged");
        await store.CloseAsync(b, null, "stopped holding");

        Assert.Empty(await DraftsAsync(fact.Id));
        var shown = await store.ConceptShowAsync("Glider", history: false);
        Assert.Contains("[sources closed]", shown);
        Assert.Contains("(ended by no ref): stopped holding", shown);

        // Every source ref-less: flag, still no draft.
        var c = await Add(store, "Waiting again.", state: true);
        var only = await Fact(store, "waits again", c);
        await store.CloseAsync(c, null, "done waiting");
        Assert.Empty(await DraftsAsync(only.Id));
    }

    [Fact]
    public async Task Reopen_ClearsFlagAndDropsExactlyItsDraft_ReCloseDraftsANewOne()
    {
        // Red if: the flag is stored rather than derived, reopen drops every pending draft (or none), or re-close
        // revives the old row instead of drafting a new one.
        var (store, a, b, fact, draft) = await DraftedAsync();
        var c = await Add(store, "The tail waits on PR 99.", state: true);
        var other = await Fact(store, "the tail waits", c);
        await store.CloseAsync(c, RefA, "merged");
        var otherDraft = (await DraftsAsync(other.Id)).Single();

        await store.ReopenAsync(a, "the merge was reverted");
        Assert.DoesNotContain($"#{fact.Id} [sources closed]", await store.ConceptShowAsync("Glider", history: false));
        var dropped = (await DraftsAsync(fact.Id)).Single();
        Assert.Equal("dropped", dropped.Status);
        Assert.Equal($"source #{a} reopened: the merge was reverted", dropped.Resolution);
        Assert.Equal("pending", (await DraftsAsync(other.Id)).Single().Status);
        Assert.Equal(otherDraft.Id, (await DraftsAsync(other.Id)).Single().Id);

        await store.CloseAsync(a, RefA, "merged again");
        var drafts = await DraftsAsync(fact.Id);
        Assert.Equal(2, drafts.Count);
        Assert.Equal("dropped", drafts[0].Status);
        Assert.Equal("pending", drafts[1].Status);
        Assert.NotEqual(draft.Id, drafts[1].Id);
    }

    [Fact]
    public async Task AttachingASource_DropsThePendingDraftAtOnce()
    {
        // Red if trigger FactSources_attach_drops_end_draft is removed from migration 021.
        var (store, _, _, fact, draft) = await DraftedAsync();
        var c = await Add(store, "A newer report on the same wait.");
        await SqlAsync("INSERT INTO FactSources (FactId, FragmentId) VALUES (@f, @c)", new { f = fact.Id, c });

        var after = (await DraftsAsync(fact.Id)).Single();
        Assert.Equal("dropped", after.Status);
        Assert.Equal($"source #{c} attached", after.Resolution);
        Assert.DoesNotContain("[sources closed]", await store.ConceptShowAsync("Glider", history: false));
    }

    // ---- B2/B3: deciding ----

    [Fact]
    public async Task Approve_EndsTheFact_WithTheApprovalReason_DraftKeepsTheSourcesReasons()
    {
        // Red if: EndReason takes a source's reason, approval doesn't leave current facts, or as-of recall drops an
        // ended fact before it ended.
        var (store, a, _, fact, draft) = await DraftedAsync();
        var before = DateTimeOffset.UtcNow;
        var said = await store.DecideEndDraftAsync(draft.Id, approve: true, drop: false, "the wait ended with the merge");

        var ended = await FactRowAsync(fact.Id);
        Assert.NotNull(ended.EndedUtc);
        Assert.Equal("the wait ended with the merge", ended.EndReason);
        Assert.Equal(RefA, ended.EndedBy);
        Assert.Contains($"Fact #{fact.Id} ended", said);
        var resolved = (await DraftsAsync(fact.Id)).Single();
        Assert.Equal("approved", resolved.Status);
        Assert.StartsWith($"#{a}: PR 99 merged as abcdef1\n", resolved.Reasons);

        Assert.DoesNotContain($"#{fact.Id} ", await store.ConceptShowAsync("Glider", history: false));
        var history = await store.ConceptShowAsync("Glider", history: true);
        Assert.Contains($"ended {DateTimeOffset.UtcNow:yyyy-MM-dd}: the wait ended with the merge (ended by {RefA})", history);
        Assert.Contains($"source closed: #{a}: PR 99 merged as abcdef1", history);
        Assert.DoesNotContain($"#{fact.Id} ", await store.ConceptBlockAsync("How is the Glider doing?"));
        Assert.Contains($"#{fact.Id} ", await store.ConceptBlockAsync("How is the Glider doing?", asOf: before));
        Assert.DoesNotContain($"#{fact.Id} ", await store.ConceptBlockAsync("How is the Glider doing?", asOf: DateTimeOffset.UtcNow.AddSeconds(5)));
    }

    [Fact]
    public async Task Drop_KeepsTheFactStandingAndFlagged()
    {
        // Red if drop ends the fact, or clears the derived flag.
        var (store, _, _, fact, draft) = await DraftedAsync();
        await store.DecideEndDraftAsync(draft.Id, approve: false, drop: true, "still waiting in practice");

        Assert.Null((await FactRowAsync(fact.Id)).EndedUtc);
        var d = (await DraftsAsync(fact.Id)).Single();
        Assert.Equal("dropped", d.Status);
        Assert.Equal("still waiting in practice", d.Resolution);
        var shown = await store.ConceptShowAsync("Glider", history: false);
        Assert.Contains($"#{fact.Id} [sources closed]", shown);
        Assert.Contains($"no end draft pending: #{draft.Id} was dropped (still waiting in practice)", shown);
        Assert.Contains("not pending", await RefusedAsync(() => store.DecideEndDraftAsync(draft.Id, true, false, "now")));
    }

    [Fact]
    public async Task ASecondPendingDraftForOneFact_IsRefused()
    {
        // Red if uIdx_FactEndDrafts_PendingFact is removed from migration 021.
        var (_, _, _, fact, _) = await DraftedAsync();
        var ex = await Assert.ThrowsAsync<SqliteException>(() => SqlAsync(
            "INSERT INTO FactEndDrafts (FactId, EndedBy, Reasons, CreatedUtc) VALUES (@f, 'x', 'y', 'z')", new { f = fact.Id }));
        Assert.Contains("UNIQUE", ex.Message);
    }

    [Fact]
    public async Task DecideNeedsExactlyOneActionAndAReason()
    {
        // Red if a bare end-draft approves, or approval goes through without a reason.
        var (store, _, _, _, draft) = await DraftedAsync();
        await UsageAsync(() => store.DecideEndDraftAsync(draft.Id, false, false, "why"));
        await UsageAsync(() => store.DecideEndDraftAsync(draft.Id, true, true, "why"));
        await UsageAsync(() => store.DecideEndDraftAsync(draft.Id, true, false, " "));
        Assert.Equal("pending", (await DraftsAsync(draft.FactId)).Single().Status);
    }

    [Fact]
    public async Task EveryDraftDecisionIsRefusedInASleepPass_ListingIsNot()
    {
        // Red if the store-level check in DecideEndDraftAsync or the "concept end-draft" RefusedAsleep entry is removed.
        var (store, _, _, fact, draft) = await DraftedAsync();
        store.Now = () => DateTimeOffset.UtcNow + TimeSpan.FromDays(8);
        await store.SleepBeginAsync(false, max: 12, hours: 4);

        Assert.Contains("waking self", await RefusedAsync(() => store.DecideEndDraftAsync(draft.Id, true, false, "ended")));
        Assert.Contains("waking self", await RefusedAsync(() => store.DecideEndDraftAsync(draft.Id, false, true, "not")));
        Assert.NotNull(await store.RefusedAsleepAsync("concept", "end-draft"));
        Assert.Null(await store.RefusedAsleepAsync("concept", "end-drafts"));
        Assert.Contains($"end draft #{draft.Id}", await store.EndDraftsAsync());
        Assert.Null((await FactRowAsync(fact.Id)).EndedUtc);
    }

    [Fact]
    public async Task ADraftThatFailsToWrite_RollsTheCloseBack()
    {
        // Red if the close is saved outside the drafting transaction.
        var store = await OpenAsync();
        var a = await Add(store, "Waiting on PR 99.", state: true);
        await Fact(store, "waits", a);
        await SqlAsync("DROP TABLE FactEndDraftBasis");

        // The failure must be the draft's own write, not a lock: a save outside the transaction fails as "database is
        // locked" against the transaction's immediate lock, which is also a SqliteException (mutation run, PR B).
        var ex = await Assert.ThrowsAsync<SqliteException>(() => store.CloseAsync(a, RefA, "merged"));
        Assert.Contains("no such table: FactEndDraftBasis", ex.Message);
        Assert.Null((await store.GetAsync(a)).ClosedUtc);
    }

    // ---- B2: approval revalidates. Each disables the earlier drop, so the approval check is what's tested. ----

    private async Task AssertStaleAsync(FragmentStore store, FactRow fact, FactEndDraftRow draft, string why, bool endedAlready = false)
    {
        var refused = await RefusedAsync(() => store.DecideEndDraftAsync(draft.Id, approve: true, drop: false, "ended"));
        Assert.Contains("stale", refused);
        Assert.Contains(why, refused);
        var d = (await DraftsAsync(fact.Id)).Single(x => x.Id == draft.Id);
        Assert.Equal("dropped", d.Status);
        Assert.StartsWith("stale: ", d.Resolution);
        var row = await FactRowAsync(fact.Id);
        if (!endedAlready)
        {
            Assert.Null(row.EndedUtc);
        }

        Assert.Null(row.EndReason);
    }

    [Fact]
    public async Task Stale_SourceAttachedAfterDrafting()
    {
        // Red if approval skips check 2's "added" half.
        var (store, _, _, fact, draft) = await DraftedAsync();
        var c = await Add(store, "Another report.");
        await SqlAsync("DROP TRIGGER FactSources_attach_drops_end_draft");
        await SqlAsync("INSERT INTO FactSources (FactId, FragmentId) VALUES (@f, @c)", new { f = fact.Id, c });
        await AssertStaleAsync(store, fact, draft, $"source #{c} was attached");
    }

    [Fact]
    public async Task Stale_BasisSourceDetached()
    {
        // Red if approval skips check 2's "removed" half. No command removes a FactSources row at 46df15d, so SQL does.
        var (store, _, b, fact, draft) = await DraftedAsync();
        await SqlAsync("DELETE FROM FactSources WHERE FactId = @f AND FragmentId = @b", new { f = fact.Id, b });
        await AssertStaleAsync(store, fact, draft, $"source #{b} was detached");
    }

    [Fact]
    public async Task Stale_BasisSourceReopenedAndReClosedUnderTheSameRef()
    {
        // Red if approval skips check 3 (the recorded ClosedUtc). Reopen's own drop is bypassed: SQL moves the close.
        var (store, a, _, fact, draft) = await DraftedAsync();
        await SqlAsync("UPDATE ContextFragments SET ClosedUtc = '2099-01-01 00:00:00+00:00' WHERE Id = @a", new { a });
        await AssertStaleAsync(store, fact, draft, $"#{a} was reopened and closed again");
    }

    [Fact]
    public async Task Stale_BasisSourceNowEndedByAnotherRef()
    {
        // Red if approval skips check 4 (the shared EndedBy). Only the ref changes; ClosedUtc stays, so check 3 passes.
        var (store, a, _, fact, draft) = await DraftedAsync();
        await SqlAsync("UPDATE ContextFragments SET EndedBy = @r WHERE Id = @a", new { a, r = RefB });
        await AssertStaleAsync(store, fact, draft, $"#{a} is now ended by {RefB}");
    }

    [Fact]
    public async Task Stale_FactAlreadyEnded()
    {
        // Red if approval skips check 1's "no EndedUtc".
        var (store, _, _, fact, draft) = await DraftedAsync();
        await SqlAsync("UPDATE Facts SET EndedUtc = '2026-11-01 00:00:00.000+00:00' WHERE Id = @f", new { f = fact.Id });
        await AssertStaleAsync(store, fact, draft, "already ended", endedAlready: true);
    }

    [Fact]
    public async Task MigrationsRerun_ChangeNothing()
    {
        // Red if 021 to 024 aren't idempotent on a store that has them (a second open re-runs nothing that fails).
        var (_, _, _, fact, draft) = await DraftedAsync();
        _ = await OpenAsync();
        Assert.Equal(draft.Id, (await DraftsAsync(fact.Id)).Single().Id);
    }
}
