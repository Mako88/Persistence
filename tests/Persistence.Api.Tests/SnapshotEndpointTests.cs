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
}
