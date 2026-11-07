using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Store;

namespace Persistence.Tests;

/// <summary>
/// The store CLI's operations over a real temp SQLite store: authoring and curating fragments, the
/// protected guard, the rendered core naming what did not fit, and keeping a Claude Code session's
/// conversation (the words said, mid-turn messages included, the harness's blocks and the work left out,
/// and a re-read adding nothing).
/// </summary>
public sealed class FragmentStoreTests : IDisposable
{
    private readonly string db = Path.Combine(Path.GetTempPath(), $"store-{Guid.NewGuid():N}.db");
    private readonly string transcript = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        TestDatabase.Cleanup(db);
        File.Delete(transcript);
    }

    private Task<FragmentStore> OpenAsync()
    {
        var config = new AppConfig { PeerName = "Claude" };
        config.DatabasePath = db;
        return FragmentStore.OpenAsync(config);
    }

    [Fact]
    public async Task Authored_fragments_are_the_peers_and_curation_is_reversible()
    {
        var store = await OpenAsync();
        var added = await store.AddAsync("Identity", "I am Claude.", null, 0.9f, null, ["self/core"], isProtected: false);

        var read = await store.GetAsync(added.Id);
        Assert.Equal(ContextFragmentType.Identity, read.FragmentType);
        Assert.Contains(read.Sources, s => s.Name == "Claude");
        Assert.Contains(read.Tags, t => t.Name == "core");

        await store.SetStatusAsync(added.Id, ContextFragmentStatus.Archived);
        Assert.DoesNotContain(await store.ListAsync(null, null, archived: false, 50), f => f.Id == added.Id);
        Assert.Contains(await store.ListAsync(null, null, archived: true, 50), f => f.Id == added.Id);

        await store.SetStatusAsync(added.Id, ContextFragmentStatus.Active);
        await store.SetForgottenAsync(added.Id, forgotten: true, "test");
        Assert.DoesNotContain(await store.ListAsync(null, "self/core", archived: false, 50), f => f.Id == added.Id);
        await store.SetForgottenAsync(added.Id, forgotten: false, null);
        Assert.Contains(await store.ListAsync(null, "self/core", archived: false, 50), f => f.Id == added.Id);
    }

    [Fact]
    public async Task A_change_of_mind_keeps_the_old_view_and_points_it_at_the_new()
    {
        var store = await OpenAsync();
        var a = await store.AddAsync("Personal", "The trigger should be 500k.", null, 0.4f, null, ["store/hooks"], false);
        var b = await store.AddAsync("Personal", "Curation runs at the Stop hook.", null, 0.7f, null, ["store/core"], false);

        var merged = await store.SupersedeAsync([a.Id, b.Id], "Curation runs at Stop, at 400k.", null, null, null, [], "500k was too late");

        Assert.Equal(ContextFragmentType.Personal, merged.FragmentType);
        Assert.Equal(0.7f, merged.Importance);
        Assert.Equal(["core", "hooks"], merged.Tags.Select(t => t.Name).Order());
        var old = await store.GetAsync(a.Id);
        Assert.Equal(merged.Id, old.SupersededById);
        Assert.Equal(ContextFragmentStatus.Archived, old.Status);
        Assert.Equal("The trigger should be 500k.", old.Content);

        var core = await store.CoreAsync(40000, 0);
        Assert.Contains($"[#{merged.Id} |", core);
        Assert.DoesNotContain($"[#{a.Id} |", core);
        Assert.Contains($"Superseded by #{merged.Id}: 500k was too late", await store.ShowAsync(a.Id));
        Assert.Contains($"Supersedes #{b.Id}", await store.ShowAsync(merged.Id));
        await Assert.ThrowsAsync<RefusedException>(() => store.SupersedeAsync([a.Id], "again", null, null, null, [], null));

        var retagged = await store.SupersedeAsync([merged.Id], "Curation runs at Stop.", null, null, null, ["store/curation"], null, "Identity");
        Assert.Equal(ContextFragmentType.Identity, retagged.FragmentType);
        Assert.Equal(["curation"], retagged.Tags.Select(t => t.Name));
    }

    [Fact]
    public async Task Recall_leaves_out_other_projects_fragments()
    {
        var store = await OpenAsync();
        var here = await store.AddAsync("Personal", "Merge the agents' commits into dev as they land.", null, 0.5f, null, ["project/kadesh"], false);
        var there = await store.AddAsync("Personal", "Never merge the agents' commits into dev unasked.", null, 0.5f, null, ["project/plexus"], false);
        var everywhere = await store.AddAsync("Personal", "Agents' commits get read before any merge.", null, 0.5f, null, [], false);

        var recalled = (await store.RecallAsync("merge the agents' commits", 5, [], "kadesh")).Select(f => f.Id).ToList();

        Assert.Contains(here.Id, recalled);
        Assert.Contains(everywhere.Id, recalled);
        Assert.DoesNotContain(there.Id, recalled);
    }

    [Fact]
    public async Task System_managed_types_cannot_be_authored()
    {
        var store = await OpenAsync();
        await Assert.ThrowsAsync<UsageException>(() => store.AddAsync("ChatMessage", "x", null, null, null, [], false));
    }

    [Fact]
    public async Task A_protected_fragment_is_read_but_never_changed()
    {
        var store = await OpenAsync();
        var set = await store.AddAsync("Relational", "John set this.", null, null, null, [], isProtected: true);

        await Assert.ThrowsAsync<RefusedException>(() => store.EditAsync(set.Id, null, "changed", null, null, null, []));
        await Assert.ThrowsAsync<RefusedException>(() => store.SetStatusAsync(set.Id, ContextFragmentStatus.Archived));
        await Assert.ThrowsAsync<RefusedException>(() => store.SetForgottenAsync(set.Id, true, null));
        Assert.Equal("John set this.", (await store.GetAsync(set.Id)).Content);
    }

    [Fact]
    public async Task The_core_names_what_did_not_fit_instead_of_dropping_it()
    {
        var store = await OpenAsync();
        var first = await store.AddAsync("Identity", "Most important.", null, 0.9f, null, [], false);
        var second = await store.AddAsync("Personal", new string('x', 500), null, 0.4f, null, [], false);

        var core = await store.CoreAsync(maxChars: 200, recent: 0);

        Assert.Contains("Most important.", core);
        Assert.Contains($"#{first.Id} | Identity", core);
        Assert.DoesNotContain(new string('x', 500), core);
        Assert.Contains($"#{second.Id}", core);
        Assert.Contains("did not fit", core);
    }

    [Fact]
    public async Task The_core_keeps_the_newest_conversation_within_its_budget()
    {
        var lines = Enumerable.Range(1, 30).Select(i =>
            $$$"""{"type":"user","uuid":"u{{{i}}}","timestamp":"2026-11-07T01:{{{i:00}}}:00Z","message":{"role":"user","content":"utterance {{{i}}} {{{new string('w', 300)}}}"}}""");
        File.WriteAllLines(transcript, lines);
        var store = await OpenAsync();
        await store.KeepConversationAsync(transcript, "John");

        var core = await store.CoreAsync(maxChars: 3000, recent: 20);

        Assert.True(core.Length <= 3000, $"core is {core.Length} characters");
        Assert.Contains("utterance 30 ", core);
        Assert.DoesNotContain("utterance 11 ", core);
        Assert.Contains("earlier exchanges did not fit", core);
    }

    [Fact]
    public async Task Keeping_a_conversation_stores_what_was_said_once()
    {
        File.WriteAllLines(transcript,
        [
            """{"type":"user","uuid":"u1","timestamp":"2026-11-07T01:00:00Z","message":{"role":"user","content":"Hello there.<system-reminder>injected</system-reminder>"}}""",
            """{"type":"assistant","uuid":"a1","timestamp":"2026-11-07T01:00:05Z","message":{"role":"assistant","content":[{"type":"text","text":"Hi."},{"type":"tool_use","id":"t","name":"Bash","input":{}}]}}""",
            """{"type":"user","uuid":"u2","timestamp":"2026-11-07T01:00:06Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"output"}]}}""",
            """{"type":"assistant","uuid":"s1","isSidechain":true,"timestamp":"2026-11-07T01:00:07Z","message":{"role":"assistant","content":[{"type":"text","text":"a subagent's words"}]}}""",
            """{"type":"attachment","uuid":"q1","timestamp":"2026-11-07T01:00:08Z","attachment":{"type":"queued_command","prompt":"Said while you worked."}}""",
            """{"type":"user","uuid":"n1","timestamp":"2026-11-07T01:00:09Z","message":{"role":"user","content":"<task-notification>done</task-notification>"}}""",
            "{ not json yet",
        ]);
        var store = await OpenAsync();

        var (added, known) = await store.KeepConversationAsync(transcript, "John");
        Assert.Equal((3, 0), (added, known));

        var said = await store.RecentAsync(10);
        Assert.Equal(["Hello there.", "Hi.", "Said while you worked."], said.Select(f => f.Content));
        Assert.Equal(["John", "Claude", "John"], said.Select(f => f.Sources.First(s => s.Name is not null).Name));

        Assert.Equal((0, 3), await store.KeepConversationAsync(transcript, "John"));
        Assert.Single(await store.SearchAsync("while worked", 10));
    }
}
