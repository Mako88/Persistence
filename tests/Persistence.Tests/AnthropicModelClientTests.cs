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

public class AnthropicModelClientTests
{
    private static readonly string SuccessBody = """
    {
      "content": [
        { "type": "thinking", "thinking": "let me think" },
        { "type": "text", "text": "Hello there" }
      ],
      "usage": { "input_tokens": 42, "output_tokens": 3 }
    }
    """;

    private static (AnthropicModelClient Client, Mock<ISimpleClient> Http, Mock<IDisplayProvider> Display)
        CreateClient(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var config = new AppConfig
        {
            Model = "claude-opus-4-8",
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

        return (new AnthropicModelClient(config, display.Object, http.Object), http, display);
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
        var config = new AppConfig { Provider = "Anthropic", Model = "claude-opus-4-8", ApiKey = apiKey };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AnthropicModelClient(config, new Mock<IDisplayProvider>().Object));

        Assert.Contains("persistence.json", ex.Message);
        Assert.Contains("PERSISTENCE_APIKEY", ex.Message);
    }

    [Fact]
    public void Constructor_AllowsMissingKeyForCustomEndpoint()
    {
        // A custom endpoint (e.g. a proxy) may authenticate differently or not at all.
        var config = new AppConfig
        {
            Provider = "Anthropic",
            Model = "claude-opus-4-8",
            ApiBaseUrl = "http://localhost:1234",
            ApiKey = "",
        };

        var client = new AnthropicModelClient(config, new Mock<IDisplayProvider>().Object);

        Assert.NotNull(client);
    }

    [Fact]
    public async Task PostsToMessagesEndpoint()
    {
        var (client, http, _) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        var request = CapturedRequest(http);
        Assert.Equal("/v1/messages", request.Path);
        Assert.Equal(HttpMethod.Post, request.Method);
    }

    [Fact]
    public async Task SendsMessagesApiRequestShape()
    {
        var (client, http, _) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        var root = SerializedBody(http);
        Assert.Equal("claude-opus-4-8", root.GetProperty("model").GetString());
        Assert.Equal(1234, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("summarized", root.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task MapsRemotePeerRoleToAssistantAndSystemToUser()
    {
        var (client, http, _) = CreateClient(SuccessBody);

        var request = new PromptRequest
        {
            Messages =
            [
                new PromptMessage("developer", "you are a persona"),
                new PromptMessage("user", "hi"),
                new PromptMessage("assistant", "hello"),
                new PromptMessage("developer", "format instructions"),
            ],
        };

        await client.CompleteAsync(request);

        var messages = SerializedBody(http).GetProperty("messages");
        // developer + user collapse into one leading user message; assistant stays; the trailing
        // developer segment becomes its own user message (kept at the end).
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Contains("you are a persona", messages[0].GetProperty("content").GetString());
        Assert.Contains("hi", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Contains("format instructions", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OmitsEffortWhenNotAClaudeLevel()
    {
        var config = new AppConfig { Model = "claude-opus-4-8", MaxOutputTokens = 100, ReasoningEffort = "minimal" };
        ISimpleResponse response = new SimpleResponse { StatusCode = HttpStatusCode.OK, IsSuccessful = true, StringBody = SuccessBody };
        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>())).ReturnsAsync(response);
        var client = new AnthropicModelClient(config, new Mock<IDisplayProvider>().Object, http.Object);

        await client.CompleteAsync(Request());

        Assert.False(SerializedBody(http).TryGetProperty("output_config", out _));
    }

    [Fact]
    public async Task ReturnsText()
    {
        var (client, _, _) = CreateClient(SuccessBody);

        var result = await client.CompleteAsync(Request());

        Assert.Equal("Hello there", result);
    }

    /// <summary>Builds a client whose config uses the given model + reasoning effort, capturing the request.</summary>
    private static (AnthropicModelClient Client, Mock<ISimpleClient> Http) CreateClientWith(string model, string reasoningEffort)
    {
        var config = new AppConfig { Model = model, MaxOutputTokens = 100, ReasoningEffort = reasoningEffort };
        ISimpleResponse response = new SimpleResponse { StatusCode = HttpStatusCode.OK, IsSuccessful = true, StringBody = SuccessBody };
        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>())).ReturnsAsync(response);
        return (new AnthropicModelClient(config, new Mock<IDisplayProvider>().Object, http.Object), http);
    }

    [Fact]
    public async Task DisablesNativeThinkingWhenReasoningEffortOff()
    {
        var (client, http) = CreateClientWith("claude-opus-4-8", "off");

        await client.CompleteAsync(Request());

        var root = SerializedBody(http);
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("output_config", out _)); // effort is moot with thinking off
    }

    [Fact]
    public async Task OmitsThinkingParamForAlwaysOnModelsWhenOff()
    {
        // Fable/Mythos reject an explicit disable (400) — omit the param rather than crash.
        var (client, http) = CreateClientWith("claude-fable-5", "off");

        await client.CompleteAsync(Request());

        Assert.False(SerializedBody(http).TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task ExposesRealUsageAfterCompletion()
    {
        var (client, _, _) = CreateClient(SuccessBody); // usage: 42 in / 3 out

        await client.CompleteAsync(Request());

        Assert.Equal(new ModelUsage(42, 3), client.LastUsage);
    }

    [Fact]
    public async Task ExtractsPromptCacheTokensFromUsage()
    {
        var body = """
        {
          "content": [ { "type": "text", "text": "hi" } ],
          "usage": { "input_tokens": 10, "output_tokens": 5, "cache_read_input_tokens": 1000, "cache_creation_input_tokens": 200 }
        }
        """;
        var (client, _, _) = CreateClient(body);

        await client.CompleteAsync(Request());

        Assert.Equal(new ModelUsage(10, 5, CacheReadTokens: 1000, CacheCreationTokens: 200), client.LastUsage);
    }

    [Fact]
    public async Task PlacesOnePromptCacheBreakpointOnTheSecondToLastMessage()
    {
        var (client, http, _) = CreateClient(SuccessBody);
        // Folds to 3 messages: user(identity+hi), assistant(earlier), user(sensory).
        var request = new PromptRequest
        {
            Messages =
            [
                new PromptMessage("developer", "identity"),
                new PromptMessage("user", "hi"),
                new PromptMessage("assistant", "earlier reply"),
                new PromptMessage("developer", "sensory block"),
            ],
        };

        await client.CompleteAsync(request);

        var messages = SerializedBody(http).GetProperty("messages");
        var count = messages.GetArrayLength();

        // Second-to-last message carries the cache breakpoint (content is a block array with cache_control).
        var cachedContent = messages[count - 2].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, cachedContent.ValueKind);
        Assert.Equal("ephemeral", cachedContent[0].GetProperty("cache_control").GetProperty("type").GetString());

        // The final (volatile sensory) message stays a plain string — uncached, re-sent each turn.
        Assert.Equal(JsonValueKind.String, messages[count - 1].GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task ConcatenatesMultipleTextBlocks()
    {
        var body = """
        {
          "content": [
            { "type": "text", "text": "part one " },
            { "type": "text", "text": "part two" }
          ]
        }
        """;
        var (client, _, _) = CreateClient(body);

        Assert.Equal("part one part two", await client.CompleteAsync(Request()));
    }

    [Fact]
    public async Task RoutesThinkingToDisplay()
    {
        var (client, _, display) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        display.Verify(d => d.ShowReasoning("let me think"), Times.Once);
    }

    [Fact]
    public async Task DoesNotShowReasoningWhenAbsent()
    {
        var body = """{ "content": [ { "type": "text", "text": "hi" } ] }""";
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
    public async Task ThrowsWhenNoText()
    {
        var (client, _, _) = CreateClient("""{ "content": [] }""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(Request()));
    }

    #region Streaming

    private static (AnthropicModelClient Client, Mock<ISimpleClient> Http) CreateStreamingClient(
        string sse, HttpStatusCode status = HttpStatusCode.OK)
    {
        var config = new AppConfig { Model = "claude-opus-4-8", MaxOutputTokens = 1234, ReasoningEffort = "high" };

        var streamResponse = new Mock<ISimpleStreamResponse>();
        streamResponse.SetupGet(r => r.IsSuccessful).Returns((int)status is >= 200 and < 300);
        streamResponse.SetupGet(r => r.StatusCode).Returns(status);
        streamResponse.SetupGet(r => r.Body).Returns(() => new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        streamResponse.Setup(r => r.ReadServerSentEventsAsync(It.IsAny<CancellationToken>())).Returns(() => DecodeSse(sse));

        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeStreamRequest(It.IsAny<ISimpleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(streamResponse.Object);

        return (new AnthropicModelClient(config, new Mock<IDisplayProvider>().Object, http.Object), http);
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
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hmm\"}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello, \"}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"world\"}}\n\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
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
    public async Task StreamAsync_CapturesUsageFromMessageStartAndDelta()
    {
        var sse =
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":42,\"output_tokens\":1}}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":25}}\n\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        var (client, _) = CreateStreamingClient(sse);

        await foreach (var _ in client.StreamAsync(Request())) { }

        // Input from message_start, final output from the last message_delta.
        Assert.Equal(new ModelUsage(42, 25), client.LastUsage);
    }

    [Fact]
    public async Task StreamAsync_RequestsStreaming()
    {
        var (client, http) = CreateStreamingClient("data: {\"type\":\"message_stop\"}\n\n");

        await foreach (var _ in client.StreamAsync(Request())) { }

        var request = (ISimpleRequest)http.Invocations.Single().Arguments[0];
        var body = JsonDocument.Parse(new SimpleHttpDefaultJsonSerializer().Serialize(request.Body!)).RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal("/v1/messages", request.Path);
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
