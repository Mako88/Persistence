using Persistence.Client;
using Persistence.Contracts;

namespace Persistence.Api.Tests;

/// <summary>
/// The thin client's transport, exercised against the real in-memory API host: fetch the connect
/// snapshot and parse the live SSE stream back into <see cref="ConversationEvent"/>s. This is the
/// wire the client-mode Console (ADR-0006 stage 3) drives its TUI from.
/// </summary>
public class PersistenceHttpClientTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture api;

    public PersistenceHttpClientTests(ApiTestFixture api) => this.api = api;

    [Fact]
    public async Task GetSnapshotReturnsConnectStateWithAResumeSeq()
    {
        await api.EnsureReadyAsync();
        var client = new PersistenceHttpClient(api.CreateClient());

        var snap = await client.GetSnapshotAsync();

        Assert.True(snap.LatestSeq >= 0);
        Assert.NotNull(snap.ScheduledEvents);
        Assert.NotNull(snap.ChatHistory);
    }

    [Fact]
    public async Task StreamParsesBacklogEventsFromTheSseFraming()
    {
        // A full turn produces a backlog (a thought + a reply) for the stream to replay.
        await api.DriveTurnAsync(
            "hello",
            "<think>noting</think><respond>hi there</respond><continue>false</continue>");

        var client = new PersistenceHttpClient(api.CreateClient());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<ConversationEvent>();
        try
        {
            await foreach (var e in client.StreamAsync(since: 0, cts.Token))
            {
                events.Add(e);
                if (e is { Kind: "reply", Text: "hi there" })
                {
                    break; // got what we came for; stop reading the (otherwise open) stream
                }
            }
        }
        catch (OperationCanceledException)
        {
            // timeout guard — the assertions below report the real failure
        }

        Assert.Contains(events, e => e.Kind == "reply" && e.Text == "hi there");
        Assert.Contains(events, e => e.Kind == "thought" && e.Text == "noting");
        // Sequences arrive strictly increasing — the parser preserves order and doesn't duplicate.
        var seqs = events.Select(e => e.Seq).ToList();
        Assert.Equal(seqs.OrderBy(s => s).ToList(), seqs);
    }
}
