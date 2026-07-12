using Persistence.Contracts;
using System.Net.Http.Json;
using System.Text.Json;

namespace Persistence.Api.Tests;

/// <summary>
/// The /api/conversation/snapshot endpoint gives a freshly-connected client the state it needs to draw
/// before it subscribes to the live stream — the connect path for the future thin-client Console.
/// </summary>
public class SnapshotEndpointTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture api;

    public SnapshotEndpointTests(ApiTestFixture api) => this.api = api;

    [Fact]
    public async Task SnapshotReturnsDrawableStateWithAResumeSeq()
    {
        var client = api.CreateClient();
        await api.EnsureReadyAsync();

        var snap = await client.GetFromJsonAsync<ConversationSnapshot>(
            "/api/conversation/snapshot", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(snap);
        Assert.True(snap!.LatestSeq >= 0);          // the seq a client streams from next
        Assert.NotNull(snap.ScheduledEvents);        // Schedule pane (possibly empty)
        Assert.NotNull(snap.ChatHistory);            // prior conversation to backfill
    }

    [Fact]
    public async Task SnapshotChatHistoryReflectsTheCurrentConversationAfterATurn()
    {
        // A client connecting mid-session must see the conversation as it is NOW — the snapshot queries
        // chat history fresh from the store, not a one-time backfill captured at server-start.
        await api.DriveTurnAsync(
            "what's the capital of France?",
            "<respond>Paris.</respond><continue>false</continue>");

        var client = api.CreateClient();
        var snap = await client.GetFromJsonAsync<ConversationSnapshot>(
            "/api/conversation/snapshot", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(snap);
        Assert.Contains(snap!.ChatHistory, m => m.Content.Contains("capital of France")); // the local-peer message
        Assert.Contains(snap.ChatHistory, m => m.Content.Contains("Paris"));              // the peer's reply
    }
}
