using Moq;
using Persistence.Client;
using Persistence.Contracts;
using SimpleHttpClient;
using SimpleHttpClient.Models;
using System.Net;

namespace Persistence.Tests;

/// <summary>
/// The thin-client transport, built on <see cref="ISimpleClient"/> (mocked, as the model clients are):
/// it POSTs local-peer input, fetches the connect snapshot, and parses the live SSE stream back into
/// <see cref="ConversationEvent"/>s.
/// </summary>
public class PersistenceHttpClientTests
{
    private static PersistenceHttpClient ClientFor(Mock<ISimpleClient> http) => new(http.Object);

    [Fact]
    public async Task SendPostsInputToTheSendEndpoint()
    {
        ISimpleRequest? captured = null;
        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>()))
            .Callback<ISimpleRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new SimpleResponse { StatusCode = HttpStatusCode.Accepted, IsSuccessful = true, StringBody = "" });

        await ClientFor(http).SendAsync("hello");

        Assert.NotNull(captured);
        Assert.Equal("/api/conversation/send", captured!.Path);
        Assert.Equal(HttpMethod.Post, captured.Method);
    }

    [Fact]
    public async Task SendThrowsOnAFailureStatus()
    {
        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>()))
            .ReturnsAsync(new SimpleResponse { StatusCode = HttpStatusCode.BadRequest, IsSuccessful = false, StringBody = "Input is required." });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ClientFor(http).SendAsync(""));
        Assert.Contains("Input is required.", ex.Message);
    }

    [Fact]
    public async Task GetSnapshotDeserializesTheServerSnapshot()
    {
        const string json = """{"latestSeq":7,"openProposalCount":2,"scheduledEvents":[],"chatHistory":[]}""";
        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>()))
            .ReturnsAsync(new SimpleResponse { StatusCode = HttpStatusCode.OK, IsSuccessful = true, StringBody = json });

        var snap = await ClientFor(http).GetSnapshotAsync();

        Assert.Equal(7, snap.LatestSeq);
        Assert.Equal(2, snap.OpenProposalCount);
        Assert.Empty(snap.ScheduledEvents);
    }

    [Fact]
    public async Task StreamParsesConversationEventsFromTheSseData()
    {
        var sse =
            "data: {\"seq\":1,\"kind\":\"reply\",\"text\":\"hi\"}\n\n" +
            "data: {\"seq\":2,\"kind\":\"thought\",\"text\":\"hmm\"}\n\n";

        var streamResponse = new Mock<ISimpleStreamResponse>();
        streamResponse.SetupGet(r => r.IsSuccessful).Returns(true);
        streamResponse.SetupGet(r => r.StatusCode).Returns(HttpStatusCode.OK);
        streamResponse.Setup(r => r.ReadServerSentEventsAsync(It.IsAny<CancellationToken>())).Returns(() => DecodeSse(sse));

        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeStreamRequest(It.IsAny<ISimpleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(streamResponse.Object);

        var events = new List<ConversationEvent>();
        await foreach (var e in ClientFor(http).StreamAsync(0))
        {
            events.Add(e);
        }

        Assert.Equal(2, events.Count);
        Assert.Equal(("reply", "hi", 1L), (events[0].Kind, events[0].Text, events[0].Seq));
        Assert.Equal(("thought", "hmm", 2L), (events[1].Kind, events[1].Text, events[1].Seq));
    }

    [Fact]
    public async Task StreamThrowsOnAFailureStatus()
    {
        var streamResponse = new Mock<ISimpleStreamResponse>();
        streamResponse.SetupGet(r => r.IsSuccessful).Returns(false);
        streamResponse.SetupGet(r => r.StatusCode).Returns(HttpStatusCode.InternalServerError);

        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeStreamRequest(It.IsAny<ISimpleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(streamResponse.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in ClientFor(http).StreamAsync(0)) { }
        });
    }

    /// <summary>Mimics the ServerSentEvents SimpleHttpClient's reader would produce from raw SSE text.</summary>
    private static async IAsyncEnumerable<ServerSentEvent> DecodeSse(string sse)
    {
        foreach (var block in sse.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var data = string.Join("\n", block.Split('\n')
                .Where(l => l.StartsWith("data:"))
                .Select(l =>
                {
                    var payload = l["data:".Length..];
                    return payload.StartsWith(' ') ? payload[1..] : payload;
                }));

            yield return new ServerSentEvent(data, eventType: null!, id: null!, retry: null);
        }

        await Task.CompletedTask;
    }
}
