using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.Store;

namespace Persistence.Tests;

/// <summary>
/// The concept graph's rules (docs/design/concept-graph.md) over a real temp store: a fact stands only with a source
/// (held by the database, not just the store), facts about who someone is wait as proposals, change supersedes, a
/// rejection is not offered again, a state fact shows when it was known, and judging facts is awake work.
/// </summary>
public sealed class ConceptGraphTests : IDisposable
{
    private readonly string db = Path.Combine(Path.GetTempPath(), $"concepts-{Guid.NewGuid():N}.db");

    public void Dispose() => TestDatabase.Cleanup(db);

    private Task<FragmentStore> OpenAsync()
    {
        var config = new AppConfig { PeerName = "Claude" };
        config.DatabasePath = db;
        return FragmentStore.OpenAsync(config);
    }

    private async Task<long> FragmentAsync(FragmentStore store, string type, string content) =>
        (await store.AddAsync(type, content, null, 0.5f, null, [], false, null)).Id;

    private SqliteConnection Raw()
    {
        var c = new SqliteConnection($"Data Source={db};Foreign Keys=True;Pooling=False");
        c.Open();
        return c;
    }

    [Fact]
    public async Task A_fact_cannot_stand_without_a_source_even_written_straight_to_the_database()
    {
        var store = await OpenAsync();
        await Assert.ThrowsAsync<RefusedException>(() => store.AddFactAsync("the room bridge", "runs one process per self", []));
        await Assert.ThrowsAsync<RefusedException>(() => store.AddFactAsync("the room bridge", "runs one process per self", [99999]));

        var source = await FragmentAsync(store, "Personal", "The room bridge runs one process per self.");
        var fact = await store.AddFactAsync("the room bridge", "runs one process per self", [source]);

        using var c = Raw();
        void Exec(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        var subject = fact.SubjectId;
        Assert.Throws<SqliteException>(() => Exec($"INSERT INTO Facts (SubjectId, Text, Status, ExtractedBy, CreatedUtc) VALUES ({subject}, 'x', 'active', 't', 'now')"));
        Exec($"INSERT INTO Facts (SubjectId, Text, Status, ExtractedBy, CreatedUtc) VALUES ({subject}, 'y', 'pending', 't', 'now')");
        Assert.Throws<SqliteException>(() => Exec("UPDATE Facts SET Status = 'active' WHERE Text = 'y'"));
        Assert.Throws<SqliteException>(() => Exec($"DELETE FROM FactSources WHERE FactId = {fact.Id}"));
    }

    [Fact]
    public async Task Facts_about_who_someone_is_wait_as_proposals_and_world_facts_stand()
    {
        var store = await OpenAsync();
        var personal = await FragmentAsync(store, "Personal", "The bridge transcribes voice with Whisper small.");
        var relational = await FragmentAsync(store, "Relational", "Ember is John's daughter; \"Emma\" was dictation.");

        // A new concept counts as a person until its kind is confirmed.
        Assert.Equal("proposed", (await store.AddFactAsync("the room bridge", "transcribes voice with Whisper small", [personal])).Status);
        await store.ConfirmKindAsync("the room bridge", "thing");
        Assert.Equal("active", (await store.AddFactAsync("the room bridge", "saves attachments under state/attachments", [personal])).Status);

        // A person as subject or object, or an Identity/Relational source, makes a proposal even about a thing.
        await store.ConceptAsync("Ember", "person");
        await store.ConfirmKindAsync("Ember", "person");
        Assert.Equal("proposed", (await store.AddFactAsync("Ember", "is John's daughter", [personal])).Status);
        Assert.Equal("proposed", (await store.AddFactAsync("the room bridge", "mentions Ember as a name", [personal], "Ember")).Status);
        Assert.Equal("proposed", (await store.AddFactAsync("the room bridge", "matters to John", [relational])).Status);

        var review = await store.ConceptReviewAsync();
        Assert.Contains("is John's daughter", review);
        Assert.DoesNotContain("saves attachments", review);
    }

    [Fact]
    public async Task Change_supersedes_and_a_proposal_replaces_nothing_until_accepted()
    {
        var store = await OpenAsync();
        var a = await FragmentAsync(store, "Personal", "Ember plays on MC 26.2.");
        var b = await FragmentAsync(store, "Personal", "Ember is on MC 26.3 now.");
        await store.ConceptAsync("the mods", "thing");
        await store.ConfirmKindAsync("the mods", "thing");
        var old = await store.AddFactAsync("the mods", "target MC 26.2", [a], isState: true);
        var @new = await store.AddFactAsync("the mods", "target MC 26.3", [b], isState: true, supersedes: old.Id);
        Assert.Equal("active", @new.Status);

        var shown = await store.ConceptShowAsync("the mods", history: false);
        Assert.Contains("26.3", shown);
        Assert.DoesNotContain("26.2", shown);
        var history = await store.ConceptShowAsync("the mods", history: true);
        Assert.Contains($"target MC 26.2 [#{a}]", history);
        Assert.Contains($"superseded by #{@new.Id}", history);

        // About a person: the replacement is a proposal, and the old fact stands until it is accepted.
        await store.ConceptAsync("Ember", "person");
        await store.ConfirmKindAsync("Ember", "person");
        var p1 = await store.AddFactAsync("Ember", "plays Slime Rancher 2", [a]);
        await store.AcceptFactAsync(p1.Id, null);
        var p2 = await store.AddFactAsync("Ember", "plays Slime Rancher 2 and Trailmakers", [b], supersedes: p1.Id);
        Assert.Contains("plays Slime Rancher 2 [", await store.ConceptShowAsync("Ember", false));
        await store.AcceptFactAsync(p2.Id, "she said so");
        var after = await store.ConceptShowAsync("Ember", false);
        Assert.DoesNotContain("plays Slime Rancher 2 [", after);
        Assert.Contains("Trailmakers", after);
    }

    [Fact]
    public async Task A_rejected_fact_is_kept_and_not_offered_again()
    {
        var store = await OpenAsync();
        var src = await FragmentAsync(store, "Personal", "Arden relayed \"Emma\" as John's daughter's name.");
        var wrong = await store.AddFactAsync("John", "has a daughter named Emma", [src]);
        await Assert.ThrowsAsync<UsageException>(() => store.RejectFactAsync(wrong.Id, ""));
        await store.RejectFactAsync(wrong.Id, "dictation; John's own words say Ember (#7153)");

        var again = await Assert.ThrowsAsync<RefusedException>(() => store.AddFactAsync("John", "Has a daughter named  Emma.", [src]));
        Assert.Contains($"#{wrong.Id}", again.Message);
        Assert.Contains("rejected", await store.ConceptShowAsync("John", history: true));
        Assert.DoesNotContain("Emma", await store.ConceptShowAsync("John", history: false));
    }

    [Fact]
    public async Task A_state_fact_shows_when_it_was_known_and_an_event_when_it_happened()
    {
        var store = await OpenAsync();
        var src = await FragmentAsync(store, "Personal", "Synth's first wake is held.");
        var recorded = (await store.GetAsync(src)).CreatedUtc.ToUniversalTime().ToString("yyyy-MM-dd");
        await store.ConceptAsync("Synth", "self");
        var state = await store.AddFactAsync("Synth", "first wake is held", [src], isState: true);
        var @event = await store.AddFactAsync("Synth", "arrived in the room", [src], asOf: "2026-07-20");

        Assert.Equal(recorded, state.RecordedAtUtc![..10]);
        var shown = await store.ConceptShowAsync("Synth", false);
        Assert.Contains($"first wake is held [#{src}] (a state, known as of {recorded}", shown);
        Assert.Contains("arrived in the room", shown);
        Assert.Contains("happened 2026-07-20", shown);
        Assert.DoesNotContain("arrived in the room [#" + src + "] (a state", shown);
        _ = @event;
    }

    [Fact]
    public async Task An_alias_finds_its_concept_and_cannot_name_two()
    {
        var store = await OpenAsync();
        await store.ConceptAsync("Ember", "person");
        await store.ConceptAsync("Arden", "self");
        await store.AddAliasAsync("Ember", "Emma", "a dictation mishearing");
        Assert.StartsWith("Ember (person", await store.ConceptShowAsync("emma", false));
        Assert.Contains("Emma (a dictation mishearing)", await store.ConceptShowAsync("Ember", false));
        await Assert.ThrowsAsync<RefusedException>(() => store.AddAliasAsync("Arden", "Emma", null));
    }

    [Fact]
    public async Task Anyone_may_flag_and_judging_is_refused_in_a_sleep_pass()
    {
        var store = await OpenAsync();
        var src = await FragmentAsync(store, "Personal", "The bridge restarts itself.");
        await store.ConceptAsync("the room bridge", "thing");
        await store.ConfirmKindAsync("the room bridge", "thing");
        var fact = await store.AddFactAsync("the room bridge", "restarts itself after a clean stop", [src], isState: true);
        await store.FlagFactAsync(fact.Id, "Arden", "it doesn't: Restart=on-failure");
        Assert.Contains("flagged by Arden", await store.ConceptReviewAsync());

        await store.SleepBeginAsync(propose: false, max: 12, hours: 4);
        Assert.NotNull(await store.RefusedAsleepAsync("concept", "accept"));
        Assert.NotNull(await store.RefusedAsleepAsync("concept", "reject"));
        Assert.NotNull(await store.RefusedAsleepAsync("concept", "kind"));
        Assert.Null(await store.RefusedAsleepAsync("concept", "add"));
        Assert.Null(await store.RefusedAsleepAsync("concept", "flag"));
    }
}
