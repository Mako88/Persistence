using System.Net;
using System.Net.Http.Json;

namespace Persistence.Api.Tests;

/// <summary>
/// End-to-end tests over the real API, exercising each piece of functionality the way the
/// manual live sweep did: local peer sends input, Claude-as-remote-peer answers in the tagged
/// format, and we assert on the resulting conversation events. These cover the integration
/// seams that unit tests can't — DI wiring, the turn pipeline, command dispatch, and persistence.
/// </summary>
public class ConversationFlowTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture api;

    public ConversationFlowTests(ApiTestFixture api) => this.api = api;

    private static string Detail(IReadOnlyList<ApiTestFixture.ConversationEvent> events, string kind) =>
        string.Join(" | ", events.Where(e => e.Kind == kind).Select(e => e.Detail ?? e.Text));

    [Fact]
    public async Task Respond_SurfacesReplyToLocalPeer()
    {
        var events = await api.RunTurnAsync(
            "Hello!",
            "<respond>Hi there, good to meet you.</respond><continue>false</continue>");

        Assert.Contains(events, e => e.Kind == "reply" && e.Text.Contains("good to meet you"));
    }

    [Fact]
    public async Task Think_SurfacesThought()
    {
        var events = await api.RunTurnAsync(
            "Ponder something.",
            "<think>I am reasoning in the open.</think><respond>Done.</respond><continue>false</continue>");

        Assert.Contains(events, e => e.Kind == "thought" && e.Text.Contains("reasoning in the open"));
        Assert.Contains(events, e => e.Kind == "reply");
    }

    [Fact]
    public async Task ManageContext_AddIsApplied()
    {
        var events = await api.RunTurnAsync(
            "Remember this.",
            """
            <context>
            add(content="A thing worth remembering.", fragment_type="Personal", importance=0.7, confidence=0.9)
            </context>
            <continue>false</continue>
            """);

        Assert.Contains("Added", Detail(events, "tool"));
    }

    [Fact]
    public async Task AddedFragmentPersistsToNextTurn()
    {
        await api.RunTurnAsync(
            "Remember my favourite colour is teal.",
            """
            <context>
            add(content="My peer's favourite colour is teal.", fragment_type="Relational", importance=0.8, confidence=1.0)
            </context>
            <continue>false</continue>
            """);

        // A fresh turn should see the fragment in the assembled prompt.
        var pending = await api.SendAndGetPendingAsync("What's my favourite colour?");

        Assert.NotNull(pending);
        Assert.Contains("favourite colour is teal", pending!.Prompt);
    }

    [Fact]
    public async Task MultiActionTurn_RunsAllActionsInOrder()
    {
        var events = await api.RunTurnAsync(
            "Do several things.",
            """
            <think>First I reason.</think>
            <context>
            add(content="A note.", fragment_type="Personal", importance=0.5, confidence=0.5)
            </context>
            <respond>Then I reply.</respond>
            <continue>false</continue>
            """);

        Assert.Contains(events, e => e.Kind == "thought");
        Assert.Contains("Added", Detail(events, "tool"));
        Assert.Contains(events, e => e.Kind == "reply" && e.Text.Contains("Then I reply"));
    }

    [Fact]
    public async Task TagThenFetch_InSameTurn_FindsFragment()
    {
        // Regression: fetch must see a tag applied earlier in the same turn (before the
        // end-of-turn DB save), by merging in-memory context matches with persisted ones.
        await api.RunTurnAsync(
            "Set up a value.",
            """
            <context>
            add(content="Integrity matters to me.", fragment_type="Identity", importance=0.9, confidence=1.0)
            create_tag(name="values")
            </context>
            <continue>false</continue>
            """);

        // Find the fragment id from the prompt, then tag + fetch in one turn.
        var pending = await api.SendAndGetPendingAsync("organize");
        var fid = ExtractFragmentId(pending!.Prompt, "Identity");

        var client = api.CreateClient();
        var since = (await client.GetFromJsonAsync<ApiTestFixture.EventsDto>(
            "/api/conversation/events?since=0", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!.Latest;

        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = pending.Id,
            response = $$"""
            <context>
            tag(id={{fid}}, tag="values")
            fetch(tag="values")
            </context>
            <continue>false</continue>
            """,
        });
        await Task.Delay(300);

        var events = await api.EventsSinceAsync(client, since);
        Assert.Contains("Fragments tagged 'values'", Detail(events, "tool"));
    }

    [Fact]
    public async Task SummarizeFragments_FoldsAndArchivesOriginals()
    {
        // Unique marker so this test folds *its own* fragments (the fixture's context is shared
        // across tests, so other Personal fragments may also be present).
        const string marker = "SUMMARIZE_TEST_MARKER";

        await api.RunTurnAsync(
            "Note a couple of things.",
            $$"""
            <context>
            add(content="{{marker}} detail one.", fragment_type="Personal", importance=0.4, confidence=0.8)
            add(content="{{marker}} detail two.", fragment_type="Personal", importance=0.4, confidence=0.8)
            </context>
            <continue>false</continue>
            """);

        // Next turn: find the IDs of *our* marked fragments from the prompt, then fold them.
        var pending = await api.SendAndGetPendingAsync("tidy up");
        var ids = ExtractFragmentIdsWithContent(pending!.Prompt, marker);
        Assert.True(ids.Count >= 2, "expected two marked fragments to summarize");

        var client = api.CreateClient();
        var since = (await client.GetFromJsonAsync<ApiTestFixture.EventsDto>(
            "/api/conversation/events?since=0", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!.Latest;

        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = pending.Id,
            response = $$"""
            <context>
            summarize_fragments(ids=[{{ids[0]}}, {{ids[1]}}], summary="Project setup notes (folded).", importance=0.6)
            </context>
            <continue>false</continue>
            """,
        });
        await Task.Delay(300);

        Assert.Contains("Folded 2 fragment(s)", Detail(await api.EventsSinceAsync(client, since), "tool"));

        // The originals are archived from context; the new Summary fragment is present, and a
        // following prompt no longer shows the folded details.
        var after = await api.SendAndGetPendingAsync("what's in context?");
        Assert.Contains("Project setup notes (folded).", after!.Prompt);
        Assert.DoesNotContain(marker, after.Prompt);
    }

    [Fact]
    public async Task SetSummary_AttachesSummaryWithoutRemovingFragment()
    {
        await api.RunTurnAsync(
            "Remember a long thing.",
            """
            <context>
            add(content="A long-winded fragment that deserves a short summary.", fragment_type="Personal", importance=0.5, confidence=0.7)
            </context>
            <continue>false</continue>
            """);

        var pending = await api.SendAndGetPendingAsync("summarize it");
        var fid = ExtractFragmentId(pending!.Prompt, "Personal");

        var client = api.CreateClient();
        var since = (await client.GetFromJsonAsync<ApiTestFixture.EventsDto>(
            "/api/conversation/events?since=0", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!.Latest;

        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = pending.Id,
            response = $$"""
            <context>
            set_summary(ids=[{{fid}}], summary="Short précis.")
            </context>
            <continue>false</continue>
            """,
        });
        await Task.Delay(300);

        Assert.Contains("Set summary on 1 fragment(s)", Detail(await api.EventsSinceAsync(client, since), "tool"));

        // The fragment is still in context (set_summary doesn't remove it).
        var after = await api.SendAndGetPendingAsync("still there?");
        Assert.Contains("A long-winded fragment", after!.Prompt);
    }

    [Fact]
    public async Task ToggleSummaryDisplay_CollapsesToSummary()
    {
        const string longText = "TOGGLE_TEST a verbose fragment whose full text should disappear when collapsed.";
        const string shortSummary = "TOGGLE_SUMMARY terse.";

        // Add a fragment, then (next turn, once it has an ID) attach a summary and collapse it.
        await api.RunTurnAsync(
            "remember a verbose thing",
            $$"""
            <context>
            add(content="{{longText}}", fragment_type="Personal", importance=0.5, confidence=0.7)
            </context>
            <continue>false</continue>
            """);

        var pending = await api.SendAndGetPendingAsync("collapse it");
        var fid = ExtractFragmentIdsWithContent(pending!.Prompt, "TOGGLE_TEST").First();

        var client = api.CreateClient();
        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = pending.Id,
            response = $$"""
            <context>
            set_summary(ids=[{{fid}}], summary="{{shortSummary}}")
            toggle_summary_display(ids=[{{fid}}], collapsed=true)
            </context>
            <continue>false</continue>
            """,
        });
        await Task.Delay(300);

        // Now the full text should be gone from the prompt, replaced by the summary.
        var after = await api.SendAndGetPendingAsync("what's in context now?");
        Assert.Contains(shortSummary, after!.Prompt);
        Assert.DoesNotContain(longText, after.Prompt);
        Assert.Contains("collapsed", after.Prompt);
    }

    [Fact]
    public async Task UpdateRelevance_PersistsToFragmentHeader()
    {
        const string marker = "RELEVANCE_TEST a fragment whose relevance we will lower.";

        // Add a fragment at default relevance (1.0 -> header shows r:1.0).
        await api.RunTurnAsync(
            "remember something low priority",
            $$"""
            <context>
            add(content="{{marker}}", fragment_type="Personal", importance=0.5, confidence=0.7)
            </context>
            <continue>false</continue>
            """);

        var pending = await api.SendAndGetPendingAsync("lower its relevance");
        var fid = ExtractFragmentIdsWithContent(pending!.Prompt, "RELEVANCE_TEST").First();

        var client = api.CreateClient();
        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = pending.Id,
            response = $$"""
            <context>
            update(id={{fid}}, relevance=0.2)
            </context>
            <continue>false</continue>
            """,
        });
        await Task.Delay(300);

        // The junction relevance should round-trip through save/reload into the header.
        var after = await api.SendAndGetPendingAsync("what's in context now?");
        Assert.Contains("RELEVANCE_TEST", after!.Prompt);
        Assert.Contains("r:0.2", after.Prompt);
    }

    [Fact]
    public async Task FirstWake_PromptContainsOnboardingGuide()
    {
        // The fixture's DB is fresh, so a brand-new context should carry the first-wake guide and
        // the reversibility guidance.
        var pending = await api.SendAndGetPendingAsync("hello for the first time");

        Assert.NotNull(pending);
        Assert.Contains("first time waking", pending!.Prompt);
        Assert.Contains("reversible by default", pending.Prompt);
    }

    [Fact]
    public async Task TagManagement_ListAndDelete()
    {
        // Create a couple of tags, list them, then delete one.
        var created = await api.RunTurnAsync(
            "organize tags",
            """
            <context>
            create_tag(name="zztopic/alpha", description="first")
            create_tag(name="zztopic/beta")
            </context>
            <continue>false</continue>
            """);
        Assert.Contains("Created tag", Detail(created, "tool"));

        var listed = await api.RunTurnAsync(
            "show tags",
            """
            <context>
            list_tags()
            </context>
            <continue>false</continue>
            """);
        var listOut = Detail(listed, "tool");
        Assert.Contains("zztopic", listOut);
        Assert.Contains("alpha", listOut);
        Assert.Contains("beta", listOut);

        var deleted = await api.RunTurnAsync(
            "delete a tag",
            """
            <context>
            delete_tag(tag="zztopic/alpha")
            </context>
            <continue>false</continue>
            """);
        Assert.Contains("Deleted tag 'zztopic/alpha'", Detail(deleted, "tool"));

        // After deletion, alpha is gone but beta remains.
        var relisted = await api.RunTurnAsync(
            "show tags again",
            """
            <context>
            list_tags()
            </context>
            <continue>false</continue>
            """);
        var relistOut = Detail(relisted, "tool");
        Assert.DoesNotContain("alpha", relistOut);
        Assert.Contains("beta", relistOut);
    }

    [Fact]
    public async Task TypeMismatchError_UsesPlainLanguage()
    {
        // Passing text where a number is expected should yield a peer-friendly message, not a CLR
        // type name like "System.String cannot be converted to System.Single". (importance is a
        // float read via GetValue<float>(), which throws on a string — unlike id, which goes
        // through the forgiving ParseId helper.)
        var events = await api.RunTurnAsync(
            "make a mistake",
            """
            <context>
            add(content="oops", importance="high")
            </context>
            <continue>false</continue>
            """);

        var tools = Detail(events, "tool");
        Assert.Contains("a number", tools);
        Assert.DoesNotContain("System.", tools);
    }

    [Fact]
    public async Task MalformedCommand_PreservesReplyAndExplains()
    {
        // A colon instead of '=' is a classic small-model slip. It must not nuke the whole turn:
        // the reply still lands, and the tool feedback explains how to fix the command.
        var events = await api.RunTurnAsync(
            "make a parse slip",
            """
            <context>
            add(content: "missing the equals sign")
            </context>
            <respond>
            Still here despite the slip.
            </respond>
            <continue>false</continue>
            """);

        // The bad command must not have swallowed the reply.
        Assert.Contains(events, e => e.Kind == "reply" && e.Text.Contains("Still here"));

        var tool = Detail(events, "tool");
        Assert.Contains("Couldn't parse", tool);
        Assert.Contains("name=value", tool);
    }

    [Fact]
    public async Task UnknownField_IsFlaggedWithDidYouMean()
    {
        // A typo'd field is silently dropped otherwise; the peer should be told and nudged toward
        // the real field name so it can self-correct.
        var events = await api.RunTurnAsync(
            "typo a field",
            """
            <context>
            add(content="content with a typo'd field", importence=0.8)
            </context>
            <continue>false</continue>
            """);

        var tool = Detail(events, "tool");
        Assert.Contains("importence", tool);
        Assert.Contains("did you mean 'importance'", tool);
    }

    [Fact]
    public async Task ExecuteActions_ScheduleAndListEvents()
    {
        var events = await api.RunTurnAsync(
            "Schedule a check-in.",
            """
            <actions>
            schedule(name="check in", scheduled_for="2026-12-01T09:00:00Z")
            list_events()
            </actions>
            <continue>false</continue>
            """);

        var tools = Detail(events, "tool");
        Assert.Contains("Scheduled event", tools);
        Assert.Contains("check in", tools);
    }

    [Fact]
    public async Task Audit_ReturnsTrailWithoutError()
    {
        // Regression: auditing must not crash on the append-only AuditLogs table
        // (previously failed with "no such column: LastAccessedUtc").
        await api.RunTurnAsync(
            "Make something auditable.",
            """
            <context>
            add(content="Auditable fragment.", fragment_type="Personal", importance=0.5, confidence=0.5)
            </context>
            <continue>false</continue>
            """);

        var pending = await api.SendAndGetPendingAsync("audit it");
        var fid = ExtractFragmentId(pending!.Prompt, "Personal");

        var client = api.CreateClient();
        var since = (await client.GetFromJsonAsync<ApiTestFixture.EventsDto>(
            "/api/conversation/events?since=0", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!.Latest;

        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = pending.Id,
            response = $$"""
            <actions>
            audit(target_id={{fid}})
            </actions>
            <continue>false</continue>
            """,
        });
        await Task.Delay(300);

        var tools = Detail(await api.EventsSinceAsync(client, since), "tool");
        Assert.Contains("Audit trail", tools);
        Assert.DoesNotContain("Error", tools);
    }

    [Fact]
    public async Task ProtectedFragment_CannotBeModified()
    {
        await api.RunTurnAsync(
            "Set a protected truth.",
            """
            <context>
            add(content="This is protected.", fragment_type="Identity", importance=1.0, confidence=1.0, is_protected=true)
            </context>
            <continue>false</continue>
            """);

        var pending = await api.SendAndGetPendingAsync("try to change it");
        var fid = ExtractFragmentId(pending!.Prompt, "Identity");

        var client = api.CreateClient();
        var since = (await client.GetFromJsonAsync<ApiTestFixture.EventsDto>(
            "/api/conversation/events?since=0", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!.Latest;

        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = pending.Id,
            response = $$"""
            <context>
            remove(id={{fid}})
            </context>
            <continue>false</continue>
            """,
        });
        await Task.Delay(300);

        Assert.Contains("protected", Detail(await api.EventsSinceAsync(client, since), "tool"));
    }

    [Fact]
    public async Task UnparseableResponse_DoesNotCrash_AndCanRecover()
    {
        var client = api.CreateClient();
        await client.PostAsJsonAsync("/api/conversation/send", new { input = "go" });

        var pending = await WaitPending(client);
        // Answer with no tags at all → unparseable.
        await client.PostAsJsonAsync("/api/peer/respond", new { id = pending!.Id, response = "just prose, no tags" });
        await Task.Delay(300);

        // The turn re-prompts rather than crashing: a new pending prompt should appear.
        var reprompt = await WaitPending(client);
        Assert.NotNull(reprompt);
        Assert.NotEqual(pending.Id, reprompt!.Id);

        // Recover cleanly.
        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = reprompt.Id,
            response = "<respond>recovered</respond><continue>false</continue>",
        });
    }

    [Fact]
    public async Task Continue_RunsMultipleIterations()
    {
        var client = api.CreateClient();
        await client.PostAsJsonAsync("/api/conversation/send", new { input = "iterate" });

        var first = await WaitPending(client);
        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = first!.Id,
            response = "<think>Step one, keep going.</think><continue>true</continue>",
        });
        await Task.Delay(300);

        var second = await WaitPending(client);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second!.Id);
        Assert.Contains("Continue iteration", second.Prompt);

        await client.PostAsJsonAsync("/api/peer/respond", new
        {
            id = second.Id,
            response = "<respond>Finished across iterations.</respond><continue>false</continue>",
        });
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static async Task<ApiTestFixture.PendingDto?> WaitPending(HttpClient client)
    {
        for (var i = 0; i < 50; i++)
        {
            var resp = await client.GetAsync("/api/peer/pending");
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                return await resp.Content.ReadFromJsonAsync<ApiTestFixture.PendingDto>(
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            }
            await Task.Delay(100);
        }
        return null;
    }

    private static long ExtractFragmentId(string prompt, string fragmentType)
    {
        // Fragment headers look like: [#5 | Identity | r:1.0 i:0.9 c:1.0]
        var match = System.Text.RegularExpressions.Regex.Match(
            prompt, $@"#(\d+) \| {fragmentType} ");
        Assert.True(match.Success, $"No {fragmentType} fragment header found in prompt.");
        return long.Parse(match.Groups[1].Value);
    }

    private static List<long> ExtractFragmentIds(string prompt, string fragmentType) =>
        System.Text.RegularExpressions.Regex.Matches(prompt, $@"#(\d+) \| {fragmentType} ")
            .Select(m => long.Parse(m.Groups[1].Value))
            .ToList();

    /// <summary>
    /// IDs of fragments whose content (the line after the header) contains <paramref name="needle"/>.
    /// A fragment renders as a `[#ID | ...]` header line followed by its content.
    /// </summary>
    private static List<long> ExtractFragmentIdsWithContent(string prompt, string needle)
    {
        var ids = new List<long>();
        var lines = prompt.Split('\n');
        long? currentId = null;

        foreach (var line in lines)
        {
            var header = System.Text.RegularExpressions.Regex.Match(line, @"^\[#(\d+) \| ");
            if (header.Success)
            {
                currentId = long.Parse(header.Groups[1].Value);
            }
            else if (currentId is { } id && line.Contains(needle))
            {
                ids.Add(id);
                currentId = null;
            }
        }

        return ids;
    }
}
