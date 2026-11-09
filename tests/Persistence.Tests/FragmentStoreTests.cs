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

        var recalled = (await store.RecallAsync("merge the agents' commits", 5, [], "kadesh")).Here.Select(f => f.Id).ToList();

        Assert.Contains(here.Id, recalled);
        Assert.Contains(everywhere.Id, recalled);
        Assert.DoesNotContain(there.Id, recalled);

        var wandering = await store.RecallAsync("merge the agents' commits", 5, [], "kadesh", wander: 1);
        Assert.Equal([there.Id], wandering.Elsewhere.Select(f => f.Id));
        Assert.DoesNotContain(there.Id, wandering.Here.Select(f => f.Id));
    }

    [Fact]
    public async Task A_project_named_in_the_message_is_in_hand_for_its_recall()
    {
        var store = await OpenAsync();
        var mods = await store.AddAsync("Personal", "Finished jars wait in mods-pending until the game closes.", null, 0.5f, null, ["project/minecraft-mods"], false);

        var unnamed = await store.RecallAsync("where do finished jars wait", 5, [], "pawsitivity");
        Assert.DoesNotContain(mods.Id, unnamed.Here.Select(f => f.Id));

        var named = await store.RecallAsync("check the Minecraft mods folder: where do finished jars wait?", 5, [], "pawsitivity");
        Assert.Contains(mods.Id, named.Here.Select(f => f.Id));
    }

    [Fact]
    public async Task What_this_session_wrote_leads_the_core_ahead_of_weightier_fragments()
    {
        var store = await OpenAsync();
        store.Session = "earlier";
        for (var i = 0; i < 20; i++)
        {
            await store.AddAsync("Personal", $"An older, weightier view number {i}. " + new string('x', 300), null, 0.9f, null, [], false);
        }

        store.Session = "now";
        var curated = await store.AddAsync("Personal", "The design state, kept for the compaction.", null, 0.3f, null, [], false);

        var core = await store.CoreAsync(4_000, 0);

        Assert.Contains("## Kept by this session", core);
        Assert.Contains($"[#{curated.Id} |", core);
        Assert.True(core.IndexOf($"[#{curated.Id} |", StringComparison.Ordinal) < core.IndexOf("An older, weightier view", StringComparison.Ordinal));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(core, $@"\[#{curated.Id} \|"));

        store.Session = "another";
        Assert.DoesNotContain("## Kept by this session", await store.CoreAsync(4_000, 0));
    }

    [Fact]
    public async Task The_sleep_list_names_oversized_fragments_and_near_duplicates()
    {
        var store = await OpenAsync();
        var big = await store.AddAsync("Personal", "The salon books dogs by breed and coat. " + new string('x', 2000), null, 0.5f, null, [], false);
        var small = await store.AddAsync("Personal", "Prove the wiring before trusting a reading.", null, 0.5f, null, [], false);
        var a = await store.AddAsync("Personal", "Training pauses for any game on the shared card and resumes from its checkpoint.", null, 0.5f, null, [], false);
        var b = await store.AddAsync("Personal", "Training pauses for any game on the shared card, then resumes from its checkpoint.", null, 0.5f, null, [], false);

        var list = await store.SleepListAsync(1500, 25);

        Assert.Contains($"- #{big.Id} (", list);
        Assert.DoesNotContain($"- #{small.Id} (", list);
        Assert.Contains($"- #{a.Id} and #{b.Id} (", list);
    }

    [Fact]
    public async Task A_recall_only_fragment_stays_out_of_the_core_and_in_recall()
    {
        var store = await OpenAsync();
        var lookup = await store.AddAsync("Personal", "The rollout token is a JWT signed by the pdf service.", null, 0.5f, null, ["recall-only"], false);

        Assert.DoesNotContain($"[#{lookup.Id} |", await store.CoreAsync(40_000, 0));
        Assert.Contains(lookup.Id, (await store.RecallAsync("the rollout token from the pdf service", 5, [])).Here.Select(f => f.Id));
    }

    [Fact]
    public async Task A_new_tag_echoes_with_its_whole_path_and_project_tags_fold_case()
    {
        var store = await OpenAsync();
        await store.RefreshTagPathsAsync();
        var added = await store.AddAsync("Personal", "Rollout notes.", null, 0.5f, null, ["pdf/rollout", "project/VirtuousV2"], false);

        Assert.Contains("tags: pdf/rollout, project/virtuousv2", FragmentStore.Render(added));
        var again = await store.AddAsync("Personal", "More rollout notes.", null, 0.5f, null, ["project/virtuousv2"], false);
        Assert.Equal(added.Tags.Single(t => t.Name == "virtuousv2").Id, again.Tags.Single().Id);
    }

    [Fact]
    public async Task A_fragment_this_session_edited_leads_the_core_too()
    {
        // The curation prompt says to edit fragments that are now wrong rather than add beside them, so an
        // edit made in the curation pass is as much "what this session kept" as an add (Arden, 2026-11-09).
        var store = await OpenAsync();
        store.Session = "earlier";
        for (var i = 0; i < 20; i++)
        {
            await store.AddAsync("Personal", $"An older, weightier view number {i}. " + new string('x', 300), null, 0.9f, null, [], false);
        }

        var stale = await store.AddAsync("Personal", "The design state, as it stood last week.", null, 0.3f, null, [], false);

        store.Session = "now";
        var edited = await store.EditAsync(stale.Id, null, "The design state, corrected in this session's curation pass.", null, null, null, []);
        Assert.Equal("earlier", edited.SessionId); // who wrote it is provenance, and an edit does not move it

        var core = await store.CoreAsync(4_000, 0);

        Assert.Contains("## Kept by this session", core);
        Assert.Contains($"[#{stale.Id} |", core);
    }

    [Fact]
    public async Task What_this_session_kept_since_its_last_compaction_leads_by_importance()
    {
        // A long session curates more than once: what it kept before its latest compaction yields to what it
        // kept since, and within that stretch the decision outranks the trivia written after it.
        var store = await OpenAsync();
        store.Session = "now";
        var before = await store.AddAsync("Personal", "Weighty, but kept before the last compaction.", null, 0.9f, null, [], false);
        await Task.Delay(20);
        var compacted = DateTimeOffset.UtcNow;
        await Task.Delay(20);
        var decision = await store.AddAsync("Personal", "The decision this stretch reached.", null, 0.7f, null, [], false);
        await Task.Delay(20);
        var trivia = await store.AddAsync("Personal", "A passing aside, written last.", null, 0.2f, null, [], false);

        var core = await store.CoreAsync(40_000, 0, compacted: [compacted]);

        var at = (long id) => core.IndexOf($"[#{id} |", StringComparison.Ordinal);
        Assert.True(at(decision.Id) < at(trivia.Id), "within the latest stretch, by importance");
        Assert.True(at(trivia.Id) < at(before.Id), "the latest stretch before an earlier one, whatever its weight");
    }

    [Fact]
    public async Task Who_the_peer_is_comes_before_what_this_session_kept_and_cannot_crowd_it_out()
    {
        // The section's room is reserved, but it is read after identity and relationships: meet who you are
        // before what you were doing. Identity enough to fill the core must not push the section out.
        var store = await OpenAsync();
        store.Session = "earlier";
        var who = await store.AddAsync("Identity", "Who I am. " + new string('x', 300), null, 1f, null, [], false);
        for (var i = 0; i < 120; i++)
        {
            // Small, so they fill the core to within a few characters and leave no gap the section could slip into.
            await store.AddAsync("Relational", $"Someone I work with, {i}.", null, 0.9f, null, [], false);
        }

        store.Session = "now";
        var curated = await store.AddAsync("Personal", "The design state, kept for the compaction.", null, 0.3f, null, [], false);

        var core = await store.CoreAsync(4_000, 0);

        var trailer = core.IndexOf("more core fragments did not fit", StringComparison.Ordinal);
        Assert.True(trailer > 0 && trailer <= 4_000, $"the fragments, section included, kept to the budget (ended at {trailer})");
        Assert.Contains($"[#{curated.Id} |", core);
        Assert.True(core.IndexOf($"[#{who.Id} |", StringComparison.Ordinal) < core.IndexOf("## Kept by this session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edit_takes_a_tag_off()
    {
        var store = await OpenAsync();
        var server = await store.AddAsync("Personal", "My server is home.", null, 0.5f, null, ["machine", "project/john"], false);

        var edited = await store.EditAsync(server.Id, null, null, null, null, null, [], ["project/john"]);

        Assert.Equal(["machine"], edited.Tags.Select(t => t.Name));
        await Assert.ThrowsAsync<RefusedException>(() => store.EditAsync(server.Id, null, null, null, null, null, [], ["no/such"]));
    }

    [Fact]
    public async Task The_core_gives_each_other_project_one_line()
    {
        var store = await OpenAsync();
        var lead = await store.AddAsync("Personal", "Minecraft mods for Ember: one private repo.\nThe rest of it.", null, 0.8f, null, ["project/minecraft-mods"], false);
        await store.AddAsync("Personal", "A smaller mods detail.", null, 0.3f, null, ["project/minecraft-mods"], false);

        var core = await store.CoreAsync(40_000, 0, project: "pawsitivity");

        Assert.Contains($"- minecraft-mods: Minecraft mods for Ember: one private repo. (#{lead.Id})", core);
        Assert.DoesNotContain("The rest of it.", core);
        Assert.DoesNotContain("A smaller mods detail.", core);

        var migrated = await store.AddAsync("Personal", "(Memory file roadmap.md from the kadesh project; migrated verbatim.)\nKadesh ships the garden first.", null, 0.5f, null, ["project/kadesh"], false);
        Assert.Contains($"- kadesh: Kadesh ships the garden first. (#{migrated.Id})", await store.CoreAsync(40_000, 0, project: "pawsitivity"));
    }

    [Fact]
    public async Task The_working_note_replaces_itself_and_leads_the_core()
    {
        var store = await OpenAsync();
        Assert.Null(await store.NoteAsync(null));
        await store.AddAsync("Identity", "I am Claude.", null, 0.9f, null, [], false);
        var first = await store.NoteAsync("A smoke is running.");
        var second = await store.NoteAsync("The round of 200 is running.");

        Assert.Equal(first!.Id, second!.Id);
        var core = await store.CoreAsync(40000, 0);
        Assert.StartsWith("## Where I am", core);
        Assert.Contains("The round of 200 is running.", core);
        Assert.DoesNotContain("A smoke is running.", core);
    }

    [Fact]
    public async Task A_personal_fragment_takes_the_project_it_was_written_in()
    {
        var store = await OpenAsync();
        var here = await store.AddAsync("Personal", "The reading needs 0.71.", null, null, null, [], false, project: "open-plexus");
        var named = await store.AddAsync("Personal", "The salon books dogs.", null, null, null, ["project/pawsitivity"], false, project: "open-plexus");
        var everywhere = await store.AddAsync("Personal", "Prove the wiring.", null, null, null, [], false, project: null);
        var identity = await store.AddAsync("Identity", "I am Claude.", null, null, null, [], false, project: "open-plexus");

        Assert.Equal(["open-plexus"], here.Tags.Select(t => t.Name));
        Assert.Equal(["pawsitivity"], named.Tags.Select(t => t.Name));
        Assert.Empty(everywhere.Tags);
        Assert.Empty(identity.Tags);
        // Elsewhere it is only its project's one index line, never the fragment itself.
        var elsewhere = await store.CoreAsync(40_000, 0, project: "pawsitivity");
        Assert.DoesNotContain($"[#{here.Id} |", elsewhere);
        Assert.Contains($"- open-plexus: The reading needs 0.71. (#{here.Id})", elsewhere);
        Assert.Contains($"[#{here.Id} |", await store.CoreAsync(40_000, 0, project: "open-plexus"));
    }

    [Fact]
    public async Task A_group_comes_in_by_its_head_loaded_or_beside_a_member_recall_brings()
    {
        var store = await OpenAsync();
        store.Session = "s1";
        var head = await store.SetHeadAsync("work/receiving", "Receiving: legacy; one matcher procedure; keep the reconcile green.", null);
        var member = await store.AddAsync("Personal", "The PO matcher drops a line after three lock retries.", null, null, null, ["work/receiving"], false);
        await store.AddAsync("Personal", "Receipts has no foreign key to POs.", null, null, null, ["work/receiving/db"], false);

        // Not loaded: the head stays out of the core but is named, and recall brings it beside its member.
        var core = await store.CoreAsync(40_000, 0);
        Assert.DoesNotContain("one matcher procedure", core);
        Assert.Contains("Groups not loaded: work/receiving", core);
        var recalled = await store.RecallAsync("three lock retries", 1, new HashSet<long>());
        Assert.Contains(recalled.Here, f => f.Id == member.Id);
        Assert.Equal([head.Id], recalled.Frames.Select(f => f.Id));

        // Loaded for the session: head, subgroup and member are in the core, the frame is no longer brought,
        // and the load outlives a rewritten note.
        await store.LoadAsync("work/receiving", load: true);
        await store.NoteAsync("where I am");
        core = await store.CoreAsync(40_000, 0);
        Assert.Contains("## Group work/receiving", core);
        Assert.Contains("one matcher procedure", core);
        Assert.Contains("subgroup work/receiving/db (1 fragments)", core);
        Assert.Empty((await store.RecallAsync("three lock retries", 1, new HashSet<long>())).Frames);

        // Another session has not loaded it.
        store.Session = "s2";
        Assert.DoesNotContain("## Group work/receiving", await store.CoreAsync(40_000, 0));

        store.Session = "s1";
        await store.LoadAsync("work/receiving", load: false);
        Assert.DoesNotContain("## Group work/receiving", await store.CoreAsync(40_000, 0));
    }

    [Fact]
    public async Task A_projects_group_with_a_head_loads_with_the_project()
    {
        var store = await OpenAsync();
        await store.SetHeadAsync("project/open-plexus", "Open Plexus: a frozen trunk, a store, and trained glue.", null);
        Assert.Contains("## Group project/open-plexus", await store.CoreAsync(40_000, 0, project: "open-plexus"));
        Assert.DoesNotContain("## Group project/open-plexus", await store.CoreAsync(40_000, 0, project: "pawsitivity"));
    }

    [Fact]
    public async Task A_cue_brings_its_fragments_when_its_moment_matches()
    {
        var store = await OpenAsync();
        var cue = await store.SetCueAsync("tool/powershell-python", @"^PowerShell:.*\bpython\b");
        var reminder = await store.AddAsync("Personal", "Python inside PowerShell breaks on quoting; use Bash.", null, null, null, [cue], false);

        Assert.Equal([reminder.Id], (await store.CuedAsync("tool", "PowerShell: {\"command\": \"python -c 1\"}", new HashSet<long>())).Select(f => f.Id));
        Assert.Empty(await store.CuedAsync("tool", "Bash: {\"command\": \"python -c 1\"}", new HashSet<long>()));
        Assert.Empty(await store.CuedAsync("prompt", "PowerShell: python", new HashSet<long>()));
        Assert.Empty(await store.CuedAsync("tool", "PowerShell: python", new HashSet<long> { reminder.Id }));
        Assert.Contains("cue/tool/powershell-python", await store.CueListAsync());
    }

    [Fact]
    public async Task The_nearest_fragment_is_found_by_shared_words_without_an_embedder()
    {
        var store = await OpenAsync();
        var view = await store.AddAsync("Personal", "Training pauses for any game on the shared card and resumes from its checkpoint when the game closes.", null, null, null, [], false);
        await store.AddAsync("Personal", "The garden hose on the north wall leaks at the coupling above forty psi.", null, null, null, [], false);

        var near = await store.NearestAsync("Any training run pauses for a game on the card and resumes from checkpoint after the game closes.", null);
        Assert.NotNull(near);
        Assert.Equal(view.Id, near!.Fragment.Id);
        Assert.Null(await store.NearestAsync("The receipts table has no foreign key to purchase orders at all.", null));
        Assert.Null(await store.NearestAsync("Training pauses for any game on the shared card and resumes from its checkpoint when the game closes.", view.Id));
    }

    [Fact]
    public void An_option_no_command_reads_is_refused()
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["add", "--tags", "a,b", "--content", "x"]));
        Assert.Equal(["a/b"], CommandLine.Parse(["add", "--tag", "a/b", "--content", "x"]).GetAll("tag"));
    }

    [Fact]
    public void Every_option_a_command_reads_is_known()
    {
        // `edit --untag` was read by Program but missing from Known, so the parser refused it (2026-11-09).
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Persistence.sln")))
        {
            root = root.Parent;
        }

        var program = File.ReadAllText(Path.Combine(root!.FullName, "src", "Persistence.Store", "Program.cs"));
        var read = System.Text.RegularExpressions.Regex.Matches(program, @"line\.(?:Get|GetAll|GetFloat|GetInt|Has|Text)\(""([a-z-]+)""")
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.NotEmpty(read);
        Assert.DoesNotContain(read, name => !CommandLine.Known.Contains(name));
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

    /// <summary>A prompt the local side typed, as one transcript line.</summary>
    private static string Prompt(string id, string at, string text) =>
        System.Text.Json.JsonSerializer.Serialize(new { type = "user", uuid = id, timestamp = at, message = new { role = "user", content = text } });

    [Fact]
    public async Task An_utterance_is_its_writers_at_its_time_whoever_keeps_it()
    {
        // #839: Claude relayed through a session under John's login. The keep that gets there first may run in
        // a process whose environment says John (the hook server); the timeline, not that process, decides.
        File.WriteAllLines(transcript,
        [
            Prompt("u1", "2026-11-09T20:07:38Z", "Relayed by Claude."),
            Prompt("u2", "2026-11-09T20:30:00Z", "John, typing into the same session."),
        ]);
        var store = await OpenAsync();
        SpeakerSpan[] timeline =
        [
            new(DateTimeOffset.Parse("2026-11-09T20:07:37Z"), "Claude", "John"),
            new(DateTimeOffset.Parse("2026-11-09T20:20:00Z"), "John", "John"),
        ];

        // One keep after the switch covers both turns, as a missed keep picked up a turn late does.
        await store.KeepConversationAsync(transcript, "John", null, timeline);

        var said = await store.RecentAsync(10);
        Assert.Equal(["Claude", "John"], said.Select(f => f.Sources.First(s => s.Name is not null).Name));
        Assert.Equal(["John", null], said.Select(f => f.EnteredBy));
        Assert.Contains("] Claude (entered as John), 2026-11-09 20:07Z:", FragmentStore.Render(said[0]));
        Assert.Contains("] John, 2026-11-09 20:30Z:", FragmentStore.Render(said[1]));
    }

    [Fact]
    public async Task Words_that_claim_an_author_do_not_make_one()
    {
        // Attribution comes from where a prompt was entered, never from its text: anything can write "From Claude".
        File.WriteAllLines(transcript, [Prompt("u1", "2026-11-09T20:07:38Z", "(From Claude, under John's login.) Hello.")]);
        var store = await OpenAsync();

        await store.KeepConversationAsync(transcript, "John");

        var said = Assert.Single(await store.RecentAsync(10));
        Assert.Equal("John", said.Sources.First(s => s.Name is not null).Name);
        Assert.Null(said.EnteredBy);
        Assert.Throws<UsageException>(() => SpeakerSpan.Parse("2026-11-09T20:00:00Z|Claude"));
    }

    [Fact]
    public async Task Attribute_corrects_a_writer_in_the_open_and_only_an_utterance()
    {
        File.WriteAllLines(transcript,
        [
            Prompt("u1", "2026-11-09T18:30:00Z", "Arden, it's Claude."),
            """{"type":"assistant","uuid":"a1","timestamp":"2026-11-09T18:31:00Z","message":{"role":"assistant","content":[{"type":"text","text":"Hello, Claude."}]}}""",
        ]);
        var store = await OpenAsync();
        await store.KeepConversationAsync(transcript, "John");
        var said = await store.RecentAsync(10);
        var note = await store.AddAsync("Personal", "Not an utterance.", null, 0.5f, null, [], false);

        await Assert.ThrowsAsync<RefusedException>(() => store.AttributeAsync(note.Id, "Claude", "why"));
        await Assert.ThrowsAsync<RefusedException>(() => store.AttributeAsync(said[1].Id, "John", "why"));
        await Assert.ThrowsAsync<UsageException>(() => store.AttributeAsync(said[0].Id, "Claude", " "));

        var fixedUp = await store.AttributeAsync(said[0].Id, "Claude", "relayed under John's login");

        var reread = (await store.RecentAsync(10))[0];
        Assert.Equal("Arden, it's Claude.", reread.Content);
        Assert.Equal(said[0].CreatedUtc, reread.CreatedUtc);
        Assert.Equal("Claude", reread.Sources.First(s => s.Name is not null).Name);
        var header = FragmentStore.Render(reread).Split('\n')[0];
        Assert.Contains("Claude (entered as John; attributed by Claude, ", header);
        Assert.Contains("was John: relayed under John's login)", header);
        await Assert.ThrowsAsync<RefusedException>(() => store.AttributeAsync(fixedUp.Id, "Claude", "again"));
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

    [Fact]
    public async Task A_transcript_sent_as_text_is_kept_like_the_file_under_its_session()
    {
        // A store served on another machine cannot open the client's file: the client sends the lines,
        // and the path only names the session.
        var path = SessionTranscript("cccccccc-3", TimeSpan.FromMinutes(5), ("user", "Move the store."), ("assistant", "Moving it."));
        var lines = await File.ReadAllTextAsync(path);
        var store = await OpenAsync();

        var kept = await store.KeepConversationAsync("/elsewhere/cccccccc-3.jsonl", "John", new StringReader(lines));

        Assert.Equal((2, 0), kept);
        Assert.Equal((0, 2), await store.KeepConversationAsync(path, "John"));
        Assert.All(await store.RecentAsync(10), f => Assert.Equal("cccccccc-3", f.SessionId));
    }

    /// <summary>A transcript named for its session, one utterance a line, said <paramref name="ago"/>.</summary>
    private string SessionTranscript(string session, TimeSpan ago, params (string Role, string Text)[] said)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var at = DateTimeOffset.UtcNow - ago;
        var path = Path.Combine(dir, $"{session}.jsonl");
        File.WriteAllLines(path, said.Select((s, i) => s.Role == "user"
            ? $$$"""{"type":"user","uuid":"{{{session}}}-{{{i}}}","timestamp":"{{{at.AddSeconds(i):O}}}","message":{"role":"user","content":"{{{s.Text}}}"}}"""
            : $$$"""{"type":"assistant","uuid":"{{{session}}}-{{{i}}}","timestamp":"{{{at.AddSeconds(i):O}}}","message":{"role":"assistant","content":[{"type":"text","text":"{{{s.Text}}}"}]}}"""));
        return path;
    }

    [Fact]
    public async Task Two_sessions_at_once_keep_their_conversations_and_notes_apart()
    {
        var store = await OpenAsync();
        await store.KeepConversationAsync(SessionTranscript("aaaaaaaa-1", TimeSpan.FromMinutes(30),
            ("user", "Run the reading sleep."), ("assistant", "Round 9 is training.")), "John");
        await store.KeepConversationAsync(SessionTranscript("bbbbbbbb-2", TimeSpan.FromMinutes(10),
            ("user", "Draft the product docs."), ("assistant", "All tests pass, publishing the build.")), "John");

        store.Session = "aaaaaaaa-1";
        await store.NoteAsync("Round 9 is training; a wake is set.");
        store.Session = "bbbbbbbb-2";
        await store.NoteAsync("Drafting docs/product on its own branch.");

        // Each reads its own note and its own conversation; the other is one labelled line.
        store.Session = "aaaaaaaa-1";
        Assert.Equal("Round 9 is training; a wake is set.", (await store.NoteAsync(null))!.Content);
        var core = await store.CoreAsync(maxChars: 20000, recent: 20);
        Assert.Contains("Round 9 is training.", core);
        Assert.DoesNotContain("All tests pass, publishing the build.", core);
        Assert.Contains("Session bbbbbbbb", core);
        Assert.Contains("Drafting docs/product on its own branch.", core);
        Assert.DoesNotContain("Draft the product docs.", (await store.RecentAsync(20)).Select(f => f.Content));

        // A session that keeps no note is shown by what it was last asked and how it answered.
        await store.KeepConversationAsync(SessionTranscript("cccccccc-3", TimeSpan.FromMinutes(5),
            ("user", "Look at the peers."), ("assistant", "Synth waits for the card.")), "John");
        var lines = await store.OtherSessionsAsync();
        Assert.Contains(lines, l => l.StartsWith("Session cccccccc") && l.Contains("Look at the peers.") && l.Contains("Synth waits for the card."));

        // A new session with no note of its own does not take a running session's place.
        store.Session = "dddddddd-4";
        Assert.Null(await store.NoteAsync(null));
    }

    [Fact]
    public async Task An_ended_sessions_words_and_note_rejoin_the_history()
    {
        var store = await OpenAsync();
        await store.KeepConversationAsync(SessionTranscript("old00000-1", FragmentStore.Live + TimeSpan.FromHours(1),
            ("user", "Yesterday's question."), ("assistant", "Yesterday's answer.")), "John");
        store.Session = "old00000-1";
        await store.NoteAsync("Where yesterday left off.");

        store.Session = "new00000-2";
        Assert.Contains("Yesterday's answer.", (await store.RecentAsync(20)).Select(f => f.Content));
        Assert.Empty(await store.OtherSessionsAsync());
        Assert.Equal("Where yesterday left off.", (await store.NoteAsync(null))!.Content);
        Assert.Contains("the working note of an ended session", await store.CoreAsync(maxChars: 20000, recent: 20));

        // Writing its own note, the new session keeps the old one's for the old one.
        await store.NoteAsync("Today's place.");
        Assert.Equal("Today's place.", (await store.NoteAsync(null))!.Content);
        store.Session = "old00000-1";
        Assert.Equal("Where yesterday left off.", (await store.NoteAsync(null))!.Content);
    }

    [Fact]
    public async Task A_view_changed_in_another_session_is_refused_with_where()
    {
        var store = await OpenAsync();
        var view = await store.AddAsync("Personal", "The plan is A.", null, null, null, [], false);
        store.Session = "aaaaaaaa-1";
        var first = await store.SupersedeAsync([view.Id], "The plan is B.", null, null, null, [], null);
        Assert.Equal("aaaaaaaa-1", (await store.GetAsync(first.Id)).SessionId);

        store.Session = "bbbbbbbb-2";
        var refused = await Assert.ThrowsAsync<RefusedException>(() =>
            store.SupersedeAsync([view.Id], "The plan is C.", null, null, null, [], null));
        Assert.Contains($"superseded by #{first.Id}, in another session (aaaaaaaa)", refused.Message);
    }

    [Fact]
    public async Task Rereading_a_transcript_tells_old_utterances_their_session()
    {
        var path = SessionTranscript("eeeeeeee-5", TimeSpan.FromDays(2), ("user", "Said before sessions were kept."));
        var store = await OpenAsync();
        await store.KeepConversationAsync(path, "John");
        var said = (await store.RecentAsync(5)).Single();
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            raw.Open();  // as everything kept before migration 010 is
            using var cmd = raw.CreateCommand();
            cmd.CommandText = $"UPDATE ContextFragments SET SessionId = NULL WHERE Id = {said.Id}";
            cmd.ExecuteNonQuery();
        }

        await store.KeepConversationAsync(path, "John");
        Assert.Equal("eeeeeeee-5", (await store.GetAsync(said.Id)).SessionId);
    }
}
