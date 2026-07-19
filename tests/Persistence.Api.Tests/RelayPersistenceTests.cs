using Persistence.Contracts;
using System.Net.Http.Json;
using System.Text.Json;

namespace Persistence.Api.Tests;

/// <summary>
/// That a relayed message's cross-peer identity survives the whole pipeline — controller, turn, store,
/// snapshot (ADR-0008 §4, migration 007). The unit tests cover each seam; this proves the id a relay
/// sends is the id that comes back out.
///
/// <para>Its own class, so it gets its own <see cref="ApiTestFixture"/> and therefore a <b>clean working
/// context</b>. That isn't tidiness: the snapshot returns only the most recent messages, so in a context
/// already saturated by another class's turns this assertion fails for reasons that have nothing to do
/// with relaying. The narrow window is a real limitation of the snapshot contract (see TODO — "all" shows
/// the N most recent per peer), not something to paper over inside the test.</para>
/// </summary>
public class RelayPersistenceTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture api;

    public RelayPersistenceTests(ApiTestFixture api) => this.api = api;

    [Fact]
    public async Task ARelayedMessageKeepsItsIdentityAndItsDepthThroughTheWholePipeline()
    {
        var utteranceId = Guid.NewGuid().ToString();
        var content = $"carried across the room {utteranceId}";

        await api.DriveRelayedTurnAsync(content, "<respond>Understood.</respond><continue>false</continue>",
            fromPeer: "Arden", addressedTo: "Ember", messageId: utteranceId, relayDepth: 1);

        var client = api.CreateClient();
        var snap = await client.GetFromJsonAsync<ConversationSnapshot>(
            "/api/conversation/snapshot", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var relayed = snap!.ChatHistory.Single(m => m.Content == content);

        // This store now holds the *same* utterance under the same identity, and knows it arrived one hop
        // out rather than having been said here. Both halves matter: the id makes it the same thing said,
        // the depth makes the breaker's view of the chain survive the message being at rest.
        Assert.Equal(utteranceId, relayed.MessageId);
        Assert.Equal(1, relayed.RelayDepth);

        // Attribution: it landed as the peer's voice, not as the human who carried it.
        Assert.Equal("assistant", relayed.Role);
        Assert.Equal("Arden", relayed.Author);
    }
}
