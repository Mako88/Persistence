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
            audit(target_type="ContextFragmentEntity", target_id={{fid}})
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
        // Fragment headers look like: [#5 | Identity | w:1.0 i:0.9 c:1.0]
        var match = System.Text.RegularExpressions.Regex.Match(
            prompt, $@"#(\d+) \| {fragmentType} ");
        Assert.True(match.Success, $"No {fragmentType} fragment header found in prompt.");
        return long.Parse(match.Groups[1].Value);
    }
}
