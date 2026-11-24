using Persistence.Config;
using Persistence.Store;

namespace Persistence.Tests;

/// <summary>
/// Concept recall with the extractor's proposals in their own tier (room 1574775057589342241, Arden's asks in
/// 1574775678543470695): accepted facts on top; at most three proposals after recall, the closest to the message, none
/// when nothing is close; names inside paths don't name concepts; a bare number doesn't name "#81"; a stale state
/// shows only when its subject is named directly. A real temp store, so closeness is scored by the real full-text index.
/// </summary>
public sealed class ConceptProposedTierTests : IDisposable
{
    private readonly string db = Path.Combine(Path.GetTempPath(), $"concept-tier-{Guid.NewGuid():N}.db");

    public void Dispose() => TestDatabase.Cleanup(db);

    private Task<FragmentStore> OpenAsync()
    {
        var config = new AppConfig { PeerName = "Claude" };
        config.DatabasePath = db;
        return FragmentStore.OpenAsync(config);
    }

    private static async Task<long> FragmentAsync(FragmentStore store, string content) =>
        (await store.AddAsync("Personal", content, null, 0.5f, null, [], false, null)).Id;

    private static async Task<long> ProposeAsync(FragmentStore store, string subject, string fact, string? @object = null, bool state = false, string? source = null)
    {
        var from = await FragmentAsync(store, source ?? fact);
        var (row, _, _) = await store.AddFactWhyAsync(subject, fact, [from], @object, isState: state, extractedBy: "extractor:test");
        Assert.Equal("proposed", row.Status);
        return row.Id;
    }

    /// <summary>A store like the live one: John with many proposals, newest about something else, and filler fragments
    /// so the index knows which words are common.</summary>
    private async Task<FragmentStore> JohnAsync()
    {
        var store = await OpenAsync();
        for (var i = 0; i < 20; i++)
        {
            await FragmentAsync(store, $"Routine note {i}: the main branch head was reviewed, deployed and merged in the room.");
        }

        await ProposeAsync(store, "John", "John scored 7 of 8 judging real crop circles against decoys in the blind game.");
        for (var i = 0; i < 6; i++)
        {
            await ProposeAsync(store, "John", $"John made a hedged suggestion number {i} about the main branch review.");
        }

        return store;
    }

    [Fact]
    public async Task Hello_again_names_John_and_shows_none_of_his_proposals()
    {
        // Arden's ask 1: the cap is a ceiling, not a quota. John's "hello again" (msg 1574710394193321985) named him,
        // ubuntu, home and Persistence and got 9 proposals; nothing in it is close to any of them.
        var store = await JohnAsync();
        const string hello = "[voice message, transcribed]: Very long time, no see. Sorry it has been so long. Anyway, hello again John here. "
            + "How are y'all doing? (audio: /home/ubuntu/.config/persistence-room/state/attachments/1574710394193321985/voice-message.ogg)";

        var recall = await store.ConceptRecallAsync(hello, floor: 0.01);
        Assert.Equal("", recall.Top);
        Assert.Equal("", recall.After);
        Assert.NotEmpty(recall.Candidates);                       // John was a candidate: named, so he competed
        Assert.All(recall.Candidates, c => Assert.False(c.Shown));
    }

    [Fact]
    public async Task The_proposal_shown_is_the_closest_to_the_message_not_the_newest()
    {
        var store = await JohnAsync();
        var recall = await store.ConceptRecallAsync("Ember: John trounced us at the crop circles game, judging real from decoys.", floor: 1);

        Assert.Contains("crop circles against decoys", recall.After);
        Assert.DoesNotContain("hedged suggestion", recall.After);
        Assert.StartsWith("Proposed facts closest to this message", recall.After);
        Assert.Contains("(proposed)", recall.After);
        Assert.Equal("", recall.Top);                              // no accepted fact, so nothing on top
    }

    [Fact]
    public async Task At_most_three_proposals_show_across_all_concepts()
    {
        var store = await OpenAsync();
        for (var i = 0; i < 20; i++)
        {
            await FragmentAsync(store, $"filler {i} about nothing in particular");
        }

        foreach (var who in new[] { "Arden", "Ember" })
        {
            for (var i = 0; i < 3; i++)
            {
                await ProposeAsync(store, who, $"{who} measured glider lift coefficient run {i}.");
            }
        }

        var recall = await store.ConceptRecallAsync("Arden and Ember: what was the glider lift coefficient?", floor: 0.5);
        Assert.Equal(FragmentStore.ProposedCap, recall.Candidates.Count(c => c.Shown));
        Assert.Equal(6, recall.Candidates.Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(recall.After, @"\n  #\d+ ").Count);
    }

    [Fact]
    public async Task Below_the_floor_nothing_shows_and_the_default_floor_is_the_measured_one()
    {
        var store = await JohnAsync();
        const string message = "John trounced us at the crop circles game.";
        Assert.Contains((await store.ConceptRecallAsync(message, floor: 1)).Candidates, c => c.Shown);
        Assert.DoesNotContain((await store.ConceptRecallAsync(message, floor: 1000)).Candidates, c => c.Shown);
        Assert.Equal(19.5, FragmentStore.ProposedFloor);
    }

    [Fact]
    public async Task Accepted_facts_keep_the_top_and_proposals_never_join_them_there()
    {
        var store = await OpenAsync();
        var source = await FragmentAsync(store, "The room bridge runs one process per self, under systemd.");
        await store.ConceptAsync("the room bridge", "thing");
        await store.ConfirmKindAsync("the room bridge", "thing");
        var accepted = await store.AddFactAsync("the room bridge", "runs one process per self", [source]);
        Assert.Equal("active", accepted.Status);
        var proposed = await ProposeAsync(store, "the room bridge", "the room bridge restarted twice under systemd overnight");

        var recall = await store.ConceptRecallAsync("why did the room bridge restart twice overnight?", floor: 0.5);
        Assert.Contains($"#{accepted.Id} runs one process per self", recall.Top);
        Assert.Contains("1 accepted, 1 proposed", recall.Top);
        Assert.DoesNotContain($"#{proposed} ", recall.Top);
        Assert.Contains($"#{proposed} ", recall.After);
    }

    [Fact]
    public async Task Names_inside_paths_urls_and_attachment_notes_name_no_concept()
    {
        // Cause 2 of the measurement: every voice message's attachment path named ubuntu, home and Persistence.
        var store = await OpenAsync();
        foreach (var name in new[] { "ubuntu", "home", "Persistence" })
        {
            await ProposeAsync(store, name, $"{name} was deployed again");
        }

        await ProposeAsync(store, "/tmp/cv0", "/tmp/cv0 holds the concept trial");

        Assert.Empty(await store.ConceptsTouchedAsync("hi (audio: /home/ubuntu/.config/persistence-room/state/voice.ogg)"));
        Assert.Empty(await store.ConceptsTouchedAsync("see https://github.com/Mako88/Persistence/pull/90 and ~/work/home/notes.md"));
        Assert.Single(await store.ConceptsTouchedAsync("ubuntu restarted"));
        Assert.Single(await store.ConceptsTouchedAsync("Persistence 11/23 and/or the /loop command"));
        // A concept whose name is a path is still named by that path.
        Assert.Equal("/tmp/cv0", Assert.Single(await store.ConceptsTouchedAsync("look in /tmp/cv0 for it")).Concept.Name);
    }

    [Fact]
    public async Task An_identifier_concept_is_named_by_its_identifier_not_the_bare_number()
    {
        var store = await OpenAsync();
        await ProposeAsync(store, "#81", "#81 adds the referent check");

        Assert.Empty(await store.ConceptsTouchedAsync("John has 81 proposed facts and 15 omitted"));
        Assert.Single(await store.ConceptsTouchedAsync("Arden approved #81 at its head"));
        Assert.Empty(await store.ConceptsTouchedAsync("see #810 instead"));
    }

    [Fact]
    public async Task A_stale_state_shows_only_when_its_subject_is_named_directly_and_with_its_date()
    {
        var store = await OpenAsync();
        await FragmentAsync(store, "filler");
        var state = await ProposeAsync(store, "Arden", "Arden holds the deploy lock for the glider build", @object: "glider", state: true);
        var later = DateTimeOffset.UtcNow.AddDays(FragmentStore.StaleAfterDays + 1);

        // Reached through its object only: held back.
        var viaObject = await store.ConceptRecallAsync("who holds the deploy lock for the glider build?", asOf: later, floor: 0.5);
        Assert.Equal("a stale state whose subject isn't named directly", Assert.Single(viaObject.Candidates).Held);
        Assert.Equal("", viaObject.After);

        // Its subject named: shown, dated.
        var direct = await store.ConceptRecallAsync("does Arden still hold the deploy lock for the glider build?", asOf: later, floor: 0.5);
        Assert.Contains($"#{state} Arden → glider: Arden holds the deploy lock", direct.After);
        Assert.Contains("a state, known as of ", direct.After);

        // Fresh, the object alone is enough.
        Assert.Contains($"#{state} ", (await store.ConceptRecallAsync("who holds the deploy lock for the glider build?", floor: 0.5)).After);
    }

    [Fact]
    public async Task A_flagged_proposal_waits_and_a_repeated_sentence_shows_once()
    {
        var store = await OpenAsync();
        await FragmentAsync(store, "filler");
        var src = await FragmentAsync(store, "Ember drew the glider wing.");
        var (flagged, _, _) = await store.AddFactWhyAsync("Ember", "drew the glider wing in pencil", [src], extractedBy: "extractor:t",
            flag: "referent unsure: may mean the daughter");
        var first = await ProposeAsync(store, "Arden", "Arden reviewed the glider wing spar");
        // The same sentence about another subject, as #951 (Claude) and #1008 (84d8bb5 → GitHub) were.
        var again = await ProposeAsync(store, "glider", "Arden reviewed the glider wing spar", source: "A later note: Arden reviewed the glider wing spar.");
        Assert.NotEqual(first, again);

        var recall = await store.ConceptRecallAsync("Ember and Arden: the glider wing spar in pencil?", floor: 0.5);
        Assert.Equal("flagged", recall.Candidates.Single(c => c.Fact.Id == flagged.Id).Held);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(recall.After, @"\n  #\d+ [^\n]*reviewed the glider wing spar"));
    }

    [Fact]
    public async Task As_of_a_time_only_facts_written_before_it_count()
    {
        // Arden's ask 2: a replay must not show facts from the future, before or after the change alike.
        var store = await JohnAsync();
        var before = DateTimeOffset.UtcNow;
        await Task.Delay(20);
        var late = await ProposeAsync(store, "John", "John won the crop circles rematch against the decoys.");

        var then = await store.ConceptRecallAsync("John and the crop circles decoys", asOf: before, floor: 0.5);
        Assert.DoesNotContain(then.Candidates, c => c.Fact.Id == late);
        Assert.DoesNotContain($"#{late} ", await store.ConceptBlockAsync("John and the crop circles decoys", asOf: before));
        Assert.Contains((await store.ConceptRecallAsync("John and the crop circles decoys", floor: 0.5)).Candidates, c => c.Fact.Id == late);
    }
}
