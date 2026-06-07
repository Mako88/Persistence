using Moq;
using Persistence.Config;
using Persistence.Runtime;
using Persistence.Services;
using Persistence.Services.Streaming;
using SimpleHttpClient;
using SimpleHttpClient.Models;
using SimpleHttpClient.Serialization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Persistence.Tests;

public class OpenAiModelClientTests
{
    private static readonly string SuccessBody = """
    {
      "output": [
        { "type": "reasoning", "summary": [ { "type": "summary_text", "text": "let me think" } ] },
        { "type": "message", "content": [ { "type": "output_text", "text": "Hello there" } ] }
      ]
    }
    """;

    private static (OpenAiModelClient Client, Mock<ISimpleClient> Http, Mock<IDisplayProvider> Display)
        CreateClient(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var config = new AppConfig
        {
            Model = "gpt-5",
            MaxOutputTokens = 1234,
            ReasoningEffort = "high",
        };

        ISimpleResponse response = new SimpleResponse
        {
            StatusCode = status,
            IsSuccessful = (int)status is >= 200 and < 300,
            StringBody = responseBody,
        };

        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>())).ReturnsAsync(response);

        var display = new Mock<IDisplayProvider>();

        return (new OpenAiModelClient(config, display.Object, new TokenUsageTracker(), http.Object), http, display);
    }

    private static PromptRequest Request() => new()
    {
        Messages = [new PromptMessage("user", "hi")],
    };

    /// <summary>The request the client handed to SimpleHttpClient.</summary>
    private static ISimpleRequest CapturedRequest(Mock<ISimpleClient> http) =>
        (ISimpleRequest)http.Invocations.Single().Arguments[0];

    /// <summary>Serializes the captured request body the same way SimpleHttpClient would.</summary>
    private static JsonElement SerializedBody(Mock<ISimpleClient> http)
    {
        var json = new SimpleHttpDefaultJsonSerializer().Serialize(CapturedRequest(http).Body!);
        return JsonDocument.Parse(json).RootElement;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("YOUR_API_KEY_HERE")]
    public void Constructor_ThrowsWhenKeyMissingOrPlaceholderOnDefaultEndpoint(string apiKey)
    {
        // The real (production) constructor builds the HTTP client and so validates the key; this is
        // where a missing/placeholder key fails fast at startup, with an actionable message.
        var config = new AppConfig { Provider = "OpenAI", Model = "gpt-5", ApiKey = apiKey };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new OpenAiModelClient(config, new Mock<IDisplayProvider>().Object, new TokenUsageTracker()));

        Assert.Contains("persistence.json", ex.Message);
        Assert.Contains("PERSISTENCE_APIKEY", ex.Message);
    }

    [Fact]
    public void Constructor_AllowsMissingKeyForCustomEndpoint()
    {
        // A custom OpenAI-compatible endpoint (e.g. a local model) may legitimately need no key.
        var config = new AppConfig
        {
            Provider = "OpenAI",
            Model = "custom",
            ApiBaseUrl = "http://localhost:1234/v1",
            ApiKey = "",
        };

        var client = new OpenAiModelClient(config, new Mock<IDisplayProvider>().Object, new TokenUsageTracker());

        Assert.NotNull(client);
    }

    [Fact]
    public async Task PostsToResponsesEndpoint()
    {
        var (client, http, _) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        var request = CapturedRequest(http);
        Assert.Equal("/responses", request.Path);
        Assert.Equal(HttpMethod.Post, request.Method);
    }

    [Fact]
    public async Task SendsResponsesApiRequestShape()
    {
        var (client, http, _) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        var root = SerializedBody(http);
        Assert.Equal("gpt-5", root.GetProperty("model").GetString());
        Assert.Equal(1234, root.GetProperty("max_output_tokens").GetInt32());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", root.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.Equal("user", root.GetProperty("input")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task ReturnsOutputText()
    {
        var (client, _, _) = CreateClient(SuccessBody);

        var result = await client.CompleteAsync(Request());

        Assert.Equal("Hello there", result);
    }

    [Fact]
    public async Task ConcatenatesMultipleOutputTextParts()
    {
        var body = """
        {
          "output": [
            { "type": "message", "content": [
              { "type": "output_text", "text": "part one " },
              { "type": "output_text", "text": "part two" }
            ] }
          ]
        }
        """;
        var (client, _, _) = CreateClient(body);

        Assert.Equal("part one part two", await client.CompleteAsync(Request()));
    }

    [Fact]
    public async Task RoutesReasoningSummaryToDisplay()
    {
        var (client, _, display) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        display.Verify(d => d.ShowReasoning("let me think"), Times.Once);
    }

    [Fact]
    public async Task DoesNotShowReasoningWhenAbsent()
    {
        var body = """
        { "output": [ { "type": "message", "content": [ { "type": "output_text", "text": "hi" } ] } ] }
        """;
        var (client, _, display) = CreateClient(body);

        await client.CompleteAsync(Request());

        display.Verify(d => d.ShowReasoning(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ThrowsWithStatusAndBodyOnFailure()
    {
        var (client, _, _) = CreateClient("""{ "error": "bad key" }""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(Request()));

        Assert.Contains("Unauthorized", ex.Message);
        Assert.Contains("bad key", ex.Message);
    }

    [Fact]
    public async Task ThrowsWhenNoOutputText()
    {
        var (client, _, _) = CreateClient("""{ "output": [] }""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(Request()));
    }

    #region Streaming

    private static (OpenAiModelClient Client, Mock<ISimpleClient> Http) CreateStreamingClient(
        string sse, HttpStatusCode status = HttpStatusCode.OK)
    {
        var config = new AppConfig { Model = "gpt-5", MaxOutputTokens = 1234, ReasoningEffort = "high" };

        var streamResponse = new Mock<ISimpleStreamResponse>();
        streamResponse.SetupGet(r => r.IsSuccessful).Returns((int)status is >= 200 and < 300);
        streamResponse.SetupGet(r => r.StatusCode).Returns(status);
        // Body is read on the error path; ReadServerSentEventsAsync yields parsed events on success.
        streamResponse.SetupGet(r => r.Body).Returns(() => new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        streamResponse.Setup(r => r.ReadServerSentEventsAsync(It.IsAny<CancellationToken>())).Returns(() => DecodeSse(sse));

        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeStreamRequest(It.IsAny<ISimpleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(streamResponse.Object);

        return (new OpenAiModelClient(config, new Mock<IDisplayProvider>().Object, new TokenUsageTracker(), http.Object), http);
    }

    /// <summary>
    /// Parses raw SSE text into the <see cref="ServerSentEvent"/>s SimpleHttpClient's
    /// ReadServerSentEventsAsync would produce (data lines joined, one optional space stripped).
    /// </summary>
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

    [Fact]
    public async Task StreamAsync_YieldsParsedEvents()
    {
        var sse =
            "data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"hmm\"}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello, \"}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"world\"}\n\n" +
            "data: {\"type\":\"response.completed\"}\n\n";
        var (client, _) = CreateStreamingClient(sse);

        var events = new List<ModelStreamEvent>();
        await foreach (var e in client.StreamAsync(Request()))
        {
            events.Add(e);
        }

        var output = string.Concat(events
            .Where(e => e.Kind == ModelStreamEventKind.OutputTextDelta)
            .Select(e => e.Text));

        Assert.Equal("Hello, world", output);
        Assert.Contains(events, e => e.Kind == ModelStreamEventKind.ReasoningSummaryDelta && e.Text == "hmm");
        Assert.Equal(ModelStreamEventKind.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task StreamAsync_RequestsStreaming()
    {
        var (client, http) = CreateStreamingClient("data: {\"type\":\"response.completed\"}\n\n");

        await foreach (var _ in client.StreamAsync(Request())) { }

        var request = (ISimpleRequest)http.Invocations.Single().Arguments[0];
        var body = JsonDocument.Parse(new SimpleHttpDefaultJsonSerializer().Serialize(request.Body!)).RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal("/responses", request.Path);
    }

    [Fact]
    public async Task StreamAsync_ThrowsWithStatusAndBodyOnFailure()
    {
        var (client, _) = CreateStreamingClient("""{ "error": "bad key" }""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(Request())) { }
        });

        Assert.Contains("Unauthorized", ex.Message);
        Assert.Contains("bad key", ex.Message);
    }

    #endregion
}
