using Dapper;
using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Store;

namespace Persistence.Tests;

/// <summary>
/// Close (docs/design/close-state.md, built to docs/design/close-build-plan.md PR A): marking a recorded state no
/// longer true. The assertion date follows split and never supersede; a broken split chain refuses instead of
/// inventing a date; the split backfill links only what every check passes and is idempotent by state. Each test
/// names, in its comment, the mutation that turns it red.
/// </summary>
/// <remarks>
/// In <see cref="FragmentStoreStaticsCollection"/>: <c>FragmentStore.TagPaths</c> is static and any command that makes a
/// tag refreshes it (a sleep pass makes <c>sleep/open</c>), so run beside FragmentStoreTests this class's stores
/// overwrote the paths that class's sleep tests were reading (2 failures in 8 full runs; 0 in 12 with this collection).
/// </remarks>
[Collection(nameof(FragmentStoreStaticsCollection))]
public sealed class CloseTests : IDisposable
{
    private readonly string db = Path.Combine(Path.GetTempPath(), $"close-{Guid.NewGuid():N}.db");

    public void Dispose() => TestDatabase.Cleanup(db);

    private async Task<FragmentStore> OpenAsync()
    {
        var config = new AppConfig { PeerName = "Arden" };
        config.DatabasePath = db;
        return await FragmentStore.OpenAsync(config);
    }

    private static Task<ContextFragmentEntity> Add(FragmentStore store, string content, bool state = false, string type = "Personal", IReadOnlyList<string>? tags = null) =>
        store.AddAsync(type, content, null, 0.5f, null, tags ?? [], false, isState: state);

    /// <summary>Raw SQL against the store file, foreign keys off: how a test makes the corrupt rows no command writes.</summary>
    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var conn = new SqliteConnection($"Data Source={db};Foreign Keys=False;");
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, args);
    }

    private async Task<long> AuditRowsAsync()
    {
        await using var conn = new SqliteConnection(SqliteConnectionString.For(db));
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM AuditLogs");
    }

    /// <summary>A split as a store before migration 019 holds it: the note names the parts, no part points back.</summary>
    private async Task<IReadOnlyList<ContextFragmentEntity>> LegacySplitAsync(FragmentStore store, ContextFragmentEntity original, int parts = 2)
    {
        var made = await store.SplitAsync(original.Id, Enumerable.Range(1, parts).Select(i => $"part {i} of #{original.Id}").ToList(), null);
        foreach (var part in made)
        {
            await SqlAsync("UPDATE ContextFragments SET SplitFromId = NULL WHERE Id = @id", new { id = part.Id });
        }

        return made;
    }

    private static async Task<string> RefusedAsync(Func<Task> act) =>
        (await Assert.ThrowsAnyAsync<Exception>(act)) switch
        {
            RefusedException r => r.Message,
            ProvenanceException p => p.Message,
            UsageException u => u.Message,
            var other => throw new Xunit.Sdk.XunitException($"expected a refusal, got {other.GetType().Name}: {other.Message}"),
        };

    // ---- Design tests (close-state.md §2, Ember's notes) ----

    [Fact]
    public async Task Split_then_close_accepts_an_ending_after_the_original_and_refuses_one_before_it()
    {
        // Mutation: assertion date = the part's own CreatedUtc → the first close is refused (the part is newer than E).
        var store = await OpenAsync();
        var a = await Add(store, "PR #19 revised. Waiting on Ember's review.");
        var e = await Add(store, "Ember approved #19.");
        var parts = await store.SplitAsync(a.Id, ["PR #19 revised.", "Waiting on Ember's review."], "event and state");

        var closed = await store.CloseAsync(parts[1].Id, $"#{e.Id}", "Ember's approval ended the wait");
        Assert.NotNull(closed.ClosedUtc);

        var before = await Add(store, "Ember reviewed something.");
        var a2 = await Add(store, "Waiting on John. Then the deploy.");
        var parts2 = await store.SplitAsync(a2.Id, ["Waiting on John.", "Then the deploy."], null);
        var why = await RefusedAsync(() => store.CloseAsync(parts2[0].Id, $"#{before.Id}", "it ended"));
        Assert.Contains($"#{before.Id}", why);
    }

    [Fact]
    public async Task The_assertion_date_never_crosses_supersede()
    {
        // Mutation: walk SupersededById backward too → O's date is used and the close is accepted.
        var store = await OpenAsync();
        var o = await Add(store, "The deploy waits on Claude.");
        var e = await Add(store, "Claude deployed.");
        var s = await store.SupersedeAsync([o.Id], "The deploy waits on Ember now.", null, null, null, [], "newly asserted");

        await RefusedAsync(() => store.CloseAsync(s.Id, $"#{e.Id}", "the deploy happened"));
        Assert.Equal(s.CreatedUtc, await store.AssertionDateAsync(s.Id));
    }

    [Fact]
    public async Task A_repeated_split_dates_the_grandchild_by_the_first_original_and_the_middle_names_its_parts()
    {
        // Mutation: follow SplitFromId one hop only → the grandchild gets the middle part's date, newer than E: refused.
        var store = await OpenAsync();
        var a = await Add(store, "Three things, one a state.");
        var e = await Add(store, "The state ended.");
        var parts = await store.SplitAsync(a.Id, ["thing one", "thing two, and the state"], null);
        var grand = await store.SplitAsync(parts[1].Id, ["thing two", "the state"], null);

        Assert.Equal(a.CreatedUtc, await store.AssertionDateAsync(grand[1].Id));
        await store.CloseAsync(grand[1].Id, $"#{e.Id}", "it ended");

        var why = await RefusedAsync(() => store.CloseAsync(parts[1].Id, $"#{e.Id}", "it ended"));
        Assert.Contains($"#{grand[0].Id}", why);
        Assert.Contains($"#{grand[1].Id}", why);
    }

    [Fact]
    public async Task Close_reopen_close_keeps_only_the_current_close_and_the_log_keeps_all_three()
    {
        // Mutation: reopen leaves Reason/EndedBy in place → the columns check fails; drop the audit read → show lacks lines.
        var store = await OpenAsync();
        var state = await Add(store, "Merging waits on CI.", state: true);
        await store.CloseAsync(state.Id, "commit:Persistence@c5aeacb", "CI went green and it merged");
        await store.ReopenAsync(state.Id, "the merge was reverted");
        var second = await store.CloseAsync(state.Id, "msg:1575338912480497695", "merged again");

        Assert.Equal("merged again", second.Reason);
        Assert.Equal("msg:1575338912480497695", second.EndedBy);
        Assert.Null(second.ReopenReason);

        var show = await store.ShowAsync(state.Id);
        var history = show.Split('\n').Where(l => l.StartsWith("- ") && (l.Contains("closed") || l.Contains("reopened"))).ToList();
        Assert.Equal(3, history.Count);
        Assert.Contains("closed", history[0]);
        Assert.Contains("CI went green and it merged", history[0]);
        Assert.Contains("commit:Persistence@c5aeacb", history[0]);
        Assert.Contains("reopened", history[1]);
        Assert.Contains("the merge was reverted", history[1]);
        Assert.Contains("closed", history[2]);
        Assert.Contains("merged again", history[2]);
    }

    // ---- Refusals ----

    [Fact]
    public async Task Close_refuses_what_is_not_a_closable_state_each_with_its_own_reason()
    {
        var store = await OpenAsync();
        var e = await Add(store, "An event much later.");
        var who = await Add(store, "I care about being right.", type: "Identity");
        await RefusedAsync(() => store.CloseAsync(who.Id, $"#{e.Id}", "no"));

        var o = await Add(store, "old view");
        var s1 = await store.SupersedeAsync([o.Id], "newer view", null, null, null, [], null);
        var s2 = await store.SupersedeAsync([s1.Id], "newest view", null, null, null, [], null);
        Assert.Contains($"#{s2.Id}", await RefusedAsync(() => store.CloseAsync(o.Id, "msg:1", "ended")));

        var note = await store.NoteAsync("where I am");
        await RefusedAsync(() => store.CloseAsync(note!.Id, "msg:1", "ended"));

        var guarded = await store.AddAsync("Personal", "the local peer's", null, 0.5f, null, [], isProtected: true);
        await RefusedAsync(() => store.CloseAsync(guarded.Id, "msg:1", "ended"));

        var state = await Add(store, "Waiting on John.", state: true);
        await store.CloseAsync(state.Id, null, "John answered");
        Assert.Contains("reopen", await RefusedAsync(() => store.CloseAsync(state.Id, null, "again")));
    }

    [Fact]
    public async Task Close_refuses_a_split_original_naming_every_part()
    {
        var store = await OpenAsync();
        var a = await Add(store, "one. two. three.");
        var parts = await store.SplitAsync(a.Id, ["one.", "two.", "three."], null);
        var why = await RefusedAsync(() => store.CloseAsync(a.Id, "msg:1", "ended"));
        Assert.All(parts, p => Assert.Contains($"#{p.Id}", why));

        // A legacy split (no back-links) still names its parts, from the note.
        var b = await Add(store, "four. five.");
        var legacy = await LegacySplitAsync(store, b);
        why = await RefusedAsync(() => store.CloseAsync(b.Id, "msg:1", "ended"));
        Assert.All(legacy, p => Assert.Contains($"#{p.Id}", why));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Close_refuses_an_empty_reason(string? reason)
    {
        var store = await OpenAsync();
        var state = await Add(store, "Waiting.", state: true);
        await RefusedAsync(() => store.CloseAsync(state.Id, "msg:12", reason));
        await RefusedAsync(() => store.CloseAsync(state.Id, null, reason));
        Assert.Null((await store.GetAsync(state.Id)).ClosedUtc);
    }

    [Fact]
    public async Task Close_needs_an_ended_by_unless_the_fragment_was_written_as_a_state()
    {
        var store = await OpenAsync();
        var plain = await Add(store, "PR waits on X.");
        Assert.Contains("ended-by", await RefusedAsync(() => store.CloseAsync(plain.Id, null, "X happened")));
        Assert.Contains("ended-by", await RefusedAsync(() => store.CloseAsync(plain.Id, "  ", "X happened")));

        var state = await Add(store, "Waiting on X.", state: true);
        var closed = await store.CloseAsync(state.Id, null, "X happened");
        Assert.Null(closed.EndedBy);
        var label = FragmentStore.Render(closed);
        Assert.Contains($"closed {closed.ClosedUtc!.Value.UtcDateTime:yyyy-MM-dd}: X happened", label);
        Assert.DoesNotContain("(ended by", label);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("#")]
    [InlineData("#12a")]
    [InlineData("commit:Persistence")]
    [InlineData("commit:Persistence@abc12")]          // 5 hex: too short
    [InlineData("commit:Persistence@" + "0123456789abcdef0123456789abcdef012345678")] // 41 hex
    [InlineData("commit:@c5aeacb")]
    [InlineData("commit:Persistence@c5aeacz")]
    [InlineData("msg:")]
    [InlineData("msg:12x")]
    [InlineData("pr:95")]
    public async Task Close_refuses_a_malformed_ended_by(string endedBy)
    {
        var store = await OpenAsync();
        var plain = await Add(store, "PR waits on X.");
        await RefusedAsync(() => store.CloseAsync(plain.Id, endedBy, "X happened"));
        Assert.Null((await store.GetAsync(plain.Id)).ClosedUtc);
    }

    [Fact]
    public async Task A_cited_fragment_must_exist_be_remembered_be_another_and_be_newer_by_assertion_date()
    {
        // Mutation: >= instead of > → the tie is accepted.
        var store = await OpenAsync();
        var older = await Add(store, "An event before the state.");
        var state = await Add(store, "Waiting on the event.");
        var gone = await Add(store, "A later event, forgotten.");
        await store.SetForgottenAsync(gone.Id, true, "slip");
        var tie = await Add(store, "An event at the same instant.");
        await SqlAsync("UPDATE ContextFragments SET CreatedUtc = (SELECT CreatedUtc FROM ContextFragments WHERE Id = @s) WHERE Id = @t",
            new { s = state.Id, t = tie.Id });

        await RefusedAsync(() => store.CloseAsync(state.Id, "#999999", "ended"));
        await RefusedAsync(() => store.CloseAsync(state.Id, $"#{gone.Id}", "ended"));
        await RefusedAsync(() => store.CloseAsync(state.Id, $"#{state.Id}", "ended"));
        await RefusedAsync(() => store.CloseAsync(state.Id, $"#{older.Id}", "ended"));
        await RefusedAsync(() => store.CloseAsync(state.Id, $"#{tie.Id}", "ended"));
        Assert.Null((await store.GetAsync(state.Id)).ClosedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sleep_pass_neither_closes_nor_reopens(bool propose)
    {
        var store = await OpenAsync();
        var open = await Add(store, "Waiting.", state: true);
        var closed = await Add(store, "Waited.", state: true);
        var e0 = await Add(store, "the ending, for the reopen case");
        await store.CloseAsync(closed.Id, $"#{e0.Id}", "ended");
        var e = await Add(store, "Ended it.");
        store.Now = () => DateTimeOffset.UtcNow + TimeSpan.FromDays(8);

        await store.SleepBeginAsync(propose, max: 12, hours: 4);
        Assert.Contains("waking self", await RefusedAsync(() => store.CloseAsync(open.Id, $"#{e.Id}", "ended")));
        Assert.Contains("waking self", await RefusedAsync(() => store.ReopenAsync(closed.Id, "not ended")));
        Assert.NotNull(await store.RefusedAsleepAsync("close", open.Id.ToString()));
        Assert.NotNull(await store.RefusedAsleepAsync("reopen", closed.Id.ToString()));
        Assert.Null((await store.GetAsync(open.Id)).ClosedUtc);
        Assert.NotNull((await store.GetAsync(closed.Id)).ClosedUtc);
    }

    // ---- Behaviour ----

    [Fact]
    public async Task A_closed_fragment_leaves_every_core_section_and_comes_back_labelled_everywhere_else()
    {
        // Mutation: drop the ClosedUtc filter in CoreAsync, or in the group's member list → the id is in the core.
        var store = await OpenAsync();
        store.Session = "s1";
        await store.SetHeadAsync("work/close", "Close: the build.", null);
        var mine = await Add(store, "The zanzibar build waits on review.", state: true, tags: ["work/close"]);
        var weighty = await store.AddAsync("Personal", "The quokka gate waits on CI.", null, 1f, null, [], false, isState: true);
        await store.LoadAsync("work/close", load: true);
        await store.NoteAsync("where I am");

        var core = await store.CoreAsync(40_000, 0);
        Assert.Contains($"#{mine.Id}", core);
        Assert.Contains($"#{weighty.Id}", core);

        await store.CloseAsync(mine.Id, null, "review came back");
        await store.CloseAsync(weighty.Id, null, "CI green");

        // Session's own section, the type sections, the group's members, and (with a tight budget) "did not fit".
        foreach (var budget in new[] { 40_000, 900 })
        {
            core = await store.CoreAsync(budget, 0);
            Assert.DoesNotContain($"#{mine.Id}", core);
            Assert.DoesNotContain($"#{weighty.Id}", core);
        }

        Assert.Contains("Close: the build.", await store.CoreAsync(40_000, 0)); // the group's head stays

        Assert.Contains((await store.RecallAsync("zanzibar build waits", 5, new HashSet<long>())).Here, f => f.Id == mine.Id);
        var found = Assert.Single(await store.SearchAsync("zanzibar", 5));
        Assert.Contains("closed ", FragmentStore.Render(found));
        Assert.Contains(await store.ListAsync(null, null, false, 50), f => f.Id == mine.Id && f.ClosedUtc is not null);
        Assert.Contains("closed ", await store.ShowAsync(mine.Id));
    }

    [Fact]
    public async Task Close_changes_no_content_type_status_or_tags_and_an_archived_fragment_stays_archived()
    {
        var store = await OpenAsync();
        var f = await Add(store, "Deploy waits on Ember.", state: true, tags: ["work/deploy"]);
        await store.SetStatusAsync(f.Id, ContextFragmentStatus.Archived);
        await store.CloseAsync(f.Id, null, "Ember deployed");

        var read = await store.GetAsync(f.Id);
        Assert.Equal("Deploy waits on Ember.", read.Content);
        Assert.Equal(ContextFragmentType.Personal, read.FragmentType);
        Assert.Equal(ContextFragmentStatus.Archived, read.Status);
        Assert.Contains(read.Tags, t => t.Name == "deploy");
        var label = FragmentStore.Render(read);
        Assert.Contains("archived", label);
        Assert.Contains("closed ", label);
    }

    [Fact]
    public async Task Reopen_brings_it_back_to_the_core_and_clears_the_close()
    {
        // Mutation: reopen leaves ClosedUtc set → still out of the core.
        var store = await OpenAsync();
        var f = await Add(store, "The wallaby gate waits.", state: true);
        await store.CloseAsync(f.Id, null, "opened");
        await RefusedAsync(() => store.ReopenAsync(f.Id, "  "));

        var reopened = await store.ReopenAsync(f.Id, "it shut again");
        Assert.Null(reopened.ClosedUtc);
        Assert.Null(reopened.Reason);
        Assert.Null(reopened.EndedBy);
        Assert.Equal("it shut again", reopened.ReopenReason);
        Assert.Contains($"#{f.Id}", await store.CoreAsync(40_000, 0));
        Assert.Contains("isn't closed", await RefusedAsync(() => store.ReopenAsync(f.Id, "again")));
    }

    [Fact]
    public async Task F2_an_event_split_after_the_state_is_dated_by_its_original()
    {
        // Mutation: compare the cited fragment's own CreatedUtc → the close is accepted.
        var store = await OpenAsync();
        var e = await Add(store, "The event. And something else.");
        var s = await Add(store, "Waiting on the event.");
        var eParts = await store.SplitAsync(e.Id, ["The event.", "And something else."], null);

        await RefusedAsync(() => store.CloseAsync(s.Id, $"#{eParts[1].Id}", "the event ended it"));
        await RefusedAsync(() => store.CloseAsync(s.Id, $"#{eParts[0].Id}", "the event ended it"));
    }

    [Fact]
    public async Task F4_split_of_a_closed_fragment_is_refused_supersede_is_allowed_and_a_closed_event_may_be_cited()
    {
        var store = await OpenAsync();
        var f = await Add(store, "Waiting on A. Waiting on B.", state: true);
        await store.CloseAsync(f.Id, null, "both arrived");
        Assert.Contains("reopen", await RefusedAsync(() => store.SplitAsync(f.Id, ["Waiting on A.", "Waiting on B."], null)));

        var successor = await store.SupersedeAsync([f.Id], "Waiting on C now.", null, null, null, [], "a new wait");
        Assert.Null(successor.ClosedUtc);
        var old = await store.GetAsync(f.Id);
        Assert.NotNull(old.ClosedUtc);
        Assert.Equal(successor.Id, old.SupersededById);

        var state = await Add(store, "Waiting on D.");
        var ended = await Add(store, "D arrived (a state of its own, later closed).", state: true);
        await store.CloseAsync(ended.Id, null, "D left again");
        await store.CloseAsync(state.Id, $"#{ended.Id}", "D's arrival ended the wait");
    }

    [Fact]
    public async Task Split_links_every_part_and_passes_on_IsState_supersede_does_not()
    {
        // Mutation: set SplitFromId only on the parts after the first → the first part's assertion is wrong.
        var store = await OpenAsync();
        var f = await Add(store, "A wait. Another wait.", state: true);
        var parts = await store.SplitAsync(f.Id, ["A wait.", "Another wait."], null);
        Assert.All(parts, p => Assert.Equal(f.Id, p.SplitFromId));
        Assert.All(parts, p => Assert.True(p.IsState));
        Assert.All(parts, p => Assert.True(store.GetAsync(p.Id).Result.IsState));

        var g = await Add(store, "Another state.", state: true);
        var successor = await store.SupersedeAsync([g.Id], "A new assertion.", null, null, null, [], null);
        Assert.False(successor.IsState);
        Assert.Null(successor.SplitFromId);
    }

    [Fact]
    public async Task All_six_columns_survive_insert_and_update_through_a_fresh_store()
    {
        // Mutation: leave any one column out of GetInsertSql or GetUpdateSql → its assertion fails.
        var store = await OpenAsync();
        var f = await Add(store, "A wait. Another.", state: true);
        var parts = await store.SplitAsync(f.Id, ["A wait.", "Another."], null); // insert: SplitFromId, IsState (both paths)
        await store.CloseAsync(parts[0].Id, "msg:42", "it ended");                  // update: the three close columns
        await store.CloseAsync(parts[1].Id, "msg:43", "this one too");
        await store.ReopenAsync(parts[1].Id, "it didn't");                          // update: ReopenReason

        var fresh = await OpenAsync();
        var a = await fresh.GetAsync(parts[0].Id);
        var b = await fresh.GetAsync(parts[1].Id);
        Assert.NotNull(a.ClosedUtc);
        Assert.Equal("it ended", a.Reason);
        Assert.Equal("msg:42", a.EndedBy);
        Assert.True(a.IsState);
        Assert.True(b.IsState);
        Assert.Equal(f.Id, a.SplitFromId);
        Assert.Equal(f.Id, b.SplitFromId);
        Assert.Equal("it didn't", b.ReopenReason);
        Assert.Null(b.ClosedUtc);
    }

    // ---- Broken provenance (Ember 3): refuse, never a date ----

    public enum Broken { MissingParent, SelfCycle, TwoCycle, HopLimit }

    /// <summary>Makes <paramref name="kind"/> of broken chain ending at the returned fragment, by raw SQL (no command writes one).</summary>
    private async Task<ContextFragmentEntity> BrokenChainAsync(FragmentStore store, Broken kind)
    {
        var x = await Add(store, $"broken chain: {kind}");
        switch (kind)
        {
            case Broken.MissingParent:
                await SqlAsync("UPDATE ContextFragments SET SplitFromId = 987654 WHERE Id = @x", new { x = x.Id });
                break;
            case Broken.SelfCycle:
                await SqlAsync("UPDATE ContextFragments SET SplitFromId = @x WHERE Id = @x", new { x = x.Id });
                break;
            case Broken.TwoCycle:
                var y = await Add(store, "the other half of the cycle");
                await SqlAsync("UPDATE ContextFragments SET SplitFromId = @y WHERE Id = @x", new { x = x.Id, y = y.Id });
                await SqlAsync("UPDATE ContextFragments SET SplitFromId = @x WHERE Id = @y", new { x = x.Id, y = y.Id });
                break;
            case Broken.HopLimit:
                // A chain one longer than the limit: x → c1 → c2 → c3 → root is four hops, the limit three.
                store.SplitHopLimit = 3;
                var prev = x.Id;
                for (var i = 0; i < 4; i++)
                {
                    var next = await Add(store, $"chain link {i}");
                    await SqlAsync("UPDATE ContextFragments SET SplitFromId = @n WHERE Id = @p", new { p = prev, n = next.Id });
                    prev = next.Id;
                }

                break;
        }

        return x;
    }

    [Theory]
    [InlineData(Broken.MissingParent, "missing")]
    [InlineData(Broken.SelfCycle, "itself")]
    [InlineData(Broken.TwoCycle, "cycle")]
    [InlineData(Broken.HopLimit, "hop limit")]
    public async Task A_broken_split_chain_refuses_close_on_the_fragment_and_on_a_cited_id(Broken kind, string which)
    {
        // Mutation: return the last date reached instead of throwing → AssertionDateAsync returns and the closes run.
        var store = await OpenAsync();
        var state = await Add(store, "Waiting, cited by a broken chain.", state: true);
        var x = await BrokenChainAsync(store, kind);

        var ex = await Assert.ThrowsAsync<ProvenanceException>(() => store.AssertionDateAsync(x.Id));
        Assert.Contains(which, ex.Message);
        Assert.Contains($"#{x.Id}", ex.Message);

        Assert.Contains("provenance", await RefusedAsync(() => store.CloseAsync(x.Id, "msg:1", "ended")));
        Assert.Contains("provenance", await RefusedAsync(() => store.CloseAsync(state.Id, $"#{x.Id}", "ended")));
        Assert.Null((await store.GetAsync(x.Id)).ClosedUtc);
        Assert.Null((await store.GetAsync(state.Id)).ClosedUtc);
    }

    [Fact]
    public async Task A_chain_exactly_at_the_hop_limit_still_has_a_date()
    {
        var store = await OpenAsync();
        store.SplitHopLimit = 3;
        var root = await Add(store, "root");
        var prev = root.Id;
        long last = 0;
        for (var i = 0; i < 3; i++)
        {
            var next = await Add(store, $"part {i}");
            await SqlAsync("UPDATE ContextFragments SET SplitFromId = @p WHERE Id = @n", new { p = prev, n = next.Id });
            prev = last = next.Id;
        }

        Assert.Equal(root.CreatedUtc, await store.AssertionDateAsync(last));
    }

    [Theory]
    [InlineData(Broken.MissingParent)]
    [InlineData(Broken.SelfCycle)]
    [InlineData(Broken.TwoCycle)]
    [InlineData(Broken.HopLimit)]
    public async Task The_backfill_skips_a_split_whose_original_has_broken_provenance(Broken kind)
    {
        var store = await OpenAsync();
        var x = await BrokenChainAsync(store, kind);
        var saved = await store.GetAsync(x.Id);
        var link = saved.SplitFromId;
        await SqlAsync("UPDATE ContextFragments SET SplitFromId = NULL WHERE Id = @x", new { x = x.Id });
        var parts = await LegacySplitAsync(store, await store.GetAsync(x.Id));
        await SqlAsync("UPDATE ContextFragments SET SplitFromId = @l WHERE Id = @x", new { x = x.Id, l = link });

        var report = await store.BackfillSplitsAsync(apply: true);
        Assert.Contains($"#{x.Id}", report);
        Assert.Contains("provenance", report);
        Assert.All(parts, p => Assert.Null(store.GetAsync(p.Id).Result.SplitFromId));
    }

    // ---- The split backfill (A2) ----

    [Fact]
    public async Task The_backfill_links_a_clean_split_only_with_apply_and_a_second_apply_writes_nothing()
    {
        // Mutation: report "has SplitFromId" as a failure when every part already points at the original → second run reads as a skip.
        var store = await OpenAsync();
        var o = await Add(store, "a. b. c.");
        var parts = await LegacySplitAsync(store, o, parts: 3);
        var audit = await AuditRowsAsync();

        var dry = await store.BackfillSplitsAsync(apply: false);
        Assert.Contains($"#{o.Id}", dry);
        Assert.All(parts, p => Assert.Null(store.GetAsync(p.Id).Result.SplitFromId));
        Assert.Equal(audit, await AuditRowsAsync());

        await store.BackfillSplitsAsync(apply: true);
        Assert.All(parts, p => Assert.Equal(o.Id, store.GetAsync(p.Id).Result.SplitFromId));
        Assert.Equal(o.CreatedUtc, await store.AssertionDateAsync(parts[2].Id));

        audit = await AuditRowsAsync();
        var again = await store.BackfillSplitsAsync(apply: true);
        Assert.Contains($"#{o.Id}: already correct (3 parts)", again);
        Assert.Equal(audit, await AuditRowsAsync());
    }

    [Fact]
    public async Task A_split_made_by_the_new_split_reads_already_correct_on_the_first_run()
    {
        var store = await OpenAsync();
        var o = await Add(store, "x. y.");
        await store.SplitAsync(o.Id, ["x.", "y."], null);
        var audit = await AuditRowsAsync();
        Assert.Contains($"#{o.Id}: already correct (2 parts)", await store.BackfillSplitsAsync(apply: true));
        Assert.Equal(audit, await AuditRowsAsync());
    }

    public enum Mix { Partial, DifferentlyLinked, Mixed }

    [Theory]
    [InlineData(Mix.Partial, "partial")]
    [InlineData(Mix.DifferentlyLinked, "differently linked")]
    [InlineData(Mix.Mixed, "mixed")]
    public async Task A_part_set_neither_all_null_nor_all_correct_is_a_loud_skip_that_changes_nothing(Mix mix, string named)
    {
        // Mutation: complete a partial set (link the nulls) → the null part gets linked.
        var store = await OpenAsync();
        var elsewhere = await Add(store, "an unrelated fragment");
        var o = await Add(store, "p. q. r.");
        var parts = await LegacySplitAsync(store, o, parts: 3);
        long?[] links = mix switch
        {
            Mix.Partial => [o.Id, null, null],
            Mix.DifferentlyLinked => [elsewhere.Id, null, null],
            _ => [o.Id, null, elsewhere.Id],
        };
        for (var i = 0; i < 3; i++)
        {
            await SqlAsync("UPDATE ContextFragments SET SplitFromId = @l WHERE Id = @p", new { l = links[i], p = parts[i].Id });
        }

        var audit = await AuditRowsAsync();
        var report = await store.BackfillSplitsAsync(apply: true);
        Assert.Contains($"#{o.Id}", report);
        Assert.Contains(named, report);
        Assert.Equal(links, parts.Select(p => store.GetAsync(p.Id).Result.SplitFromId).ToArray());
        Assert.Equal(audit, await AuditRowsAsync());
    }

    [Fact]
    public async Task The_backfill_reports_each_failing_check_and_links_none_of_them()
    {
        var store = await OpenAsync();

        // Wrong first id: the note's order doesn't match SupersededById.
        var wrongFirst = await Add(store, "w1. w2.");
        var wf = await LegacySplitAsync(store, wrongFirst);
        await SqlAsync("UPDATE ContextFragments SET Notes = @n WHERE Id = @o", new { n = $"Split into #{wf[1].Id}, #{wf[0].Id}.", o = wrongFirst.Id });

        // A part that doesn't exist.
        var missing = await Add(store, "m1. m2.");
        var mp = await LegacySplitAsync(store, missing);
        await SqlAsync("UPDATE ContextFragments SET Notes = @n WHERE Id = @o", new { n = $"Split into #{mp[0].Id}, #{mp[1].Id}, #876543.", o = missing.Id });

        // A part older than the original.
        var later = await Add(store, "l1. l2.");
        var lp = await LegacySplitAsync(store, later);
        await SqlAsync("UPDATE ContextFragments SET CreatedUtc = @t WHERE Id = @o", new { t = DateTimeOffset.UtcNow.AddDays(1).ToString("O"), o = later.Id });

        // A forgotten part.
        var forgot = await Add(store, "f1. f2.");
        var fp = await LegacySplitAsync(store, forgot);
        await store.SetForgottenAsync(fp[1].Id, true, "slip");

        // An id named in two split notes.
        var twiceA = await Add(store, "t1. t2.");
        var ta = await LegacySplitAsync(store, twiceA);
        var twiceB = await Add(store, "t3. t4.");
        var tb = await LegacySplitAsync(store, twiceB);
        await SqlAsync("UPDATE ContextFragments SET Notes = @n WHERE Id = @o", new { n = $"Split into #{tb[0].Id}, #{ta[1].Id}.", o = twiceB.Id });

        // #502's shape: "split into" inside a supersede reason, not a split note. Read unanchored it would pass every
        // other check (first id = the successor, the other part newer and the same type), so only the anchor stops it.
        // Mutation: match "split into #a, #b" anywhere → s502 and extra are linked.
        var o502 = await Add(store, "a view");
        var s502 = await store.SupersedeAsync([o502.Id], "a better view", null, null, null, [], "merged");
        var extra = await Add(store, "another view, written after");
        await SqlAsync("UPDATE ContextFragments SET Notes = @n WHERE Id = @o",
            new { n = $"Merged; the old one should have been split into #{s502.Id}, #{extra.Id} but wasn't.", o = o502.Id });

        var report = await store.BackfillSplitsAsync(apply: true);
        foreach (var skipped in new[] { wrongFirst, missing, later, forgot, twiceA, twiceB })
        {
            Assert.Contains(report.Split('\n'), l => l.Contains($"#{skipped.Id}:") && l.Contains("skipped"));
        }

        Assert.Contains(report.Split('\n'), l => l.StartsWith($"#{o502.Id}: not read"));
        foreach (var p in wf.Concat(mp).Concat(lp).Concat(fp).Concat(ta).Concat(tb))
        {
            Assert.Null((await store.GetAsync(p.Id)).SplitFromId);
        }

        Assert.Null((await store.GetAsync(s502.Id)).SplitFromId);
        Assert.Null((await store.GetAsync(extra.Id)).SplitFromId);
        Assert.Null((await store.GetAsync(wrongFirst.Id)).SplitFromId);
    }
}

/// <summary>
/// Test classes whose stores write <c>FragmentStore</c>'s static tag paths, run one at a time. The statics are shared by
/// every store in the process; making them per-store is the real fix, and a change of its own.
/// </summary>
[CollectionDefinition(nameof(FragmentStoreStaticsCollection))]
public sealed class FragmentStoreStaticsCollection;
