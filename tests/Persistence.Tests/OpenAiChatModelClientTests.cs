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
using System.Text.Json;

namespace Persistence.Tests;

public class OpenAiChatModelClientTests
{
    private static readonly string SuccessBody = """
    {
      "choices": [
        { "message": { "role": "assistant", "content": "Hello there" }, "finish_reason": "stop" }
      ],
      "usage": { "prompt_tokens": 12, "completion_tokens": 3 }
    }
    """;

    private static (OpenAiChatModelClient Client, Mock<ISimpleClient> Http) CreateClient(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var config = new AppConfig { Model = "qwen", MaxOutputTokens = 1234, ApiBaseUrl = "http://localhost:8080/v1" };

        ISimpleResponse response = new SimpleResponse
        {
            StatusCode = status,
            IsSuccessful = (int)status is >= 200 and < 300,
            StringBody = responseBody,
        };

        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>())).ReturnsAsync(response);

        var client = new OpenAiChatModelClient(config, new Mock<IDisplayProvider>().Object, http.Object);
        return (client, http);
    }

    private static PromptRequest Request() => new()
    {
        Messages =
        [
            new PromptMessage("developer", "system prompt"),
            new PromptMessage("user", "hi"),
        ],
    };

    private static ISimpleRequest CapturedRequest(Mock<ISimpleClient> http) =>
        (ISimpleRequest)http.Invocations.Single().Arguments[0];

    private static JsonElement SerializedBody(Mock<ISimpleClient> http)
    {
        var json = new SimpleHttpDefaultJsonSerializer().Serialize(CapturedRequest(http).Body!);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task PostsToChatCompletionsEndpoint()
    {
        var (client, http) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        var request = CapturedRequest(http);
        Assert.Equal("/chat/completions", request.Path);
        Assert.Equal(HttpMethod.Post, request.Method);
    }

    [Fact]
    public async Task SendsChatCompletionsShapeAndMapsDeveloperToSystem()
    {
        var (client, http) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        var root = SerializedBody(http);
        Assert.Equal("qwen", root.GetProperty("model").GetString());
        Assert.Equal(1234, root.GetProperty("max_tokens").GetInt32());
        // "developer" (the prompt builder's system label) is mapped to "system" for the chat template.
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user", root.GetProperty("messages")[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task ReturnsMessageContent()
    {
        var (client, _) = CreateClient(SuccessBody);

        Assert.Equal("Hello there", await client.CompleteAsync(Request()));
    }

    [Fact]
    public async Task ExposesRealUsageAfterCompletion()
    {
        var (client, _) = CreateClient(SuccessBody); // usage: 12 prompt / 3 completion

        await client.CompleteAsync(Request());

        Assert.Equal(new ModelUsage(12, 3), client.LastUsage);
    }

    [Fact]
    public async Task ThrowsWithStatusAndBodyOnFailure()
    {
        var (client, _) = CreateClient("""{ "error": "bad" }""", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(Request()));

        Assert.Contains("InternalServerError", ex.Message);
        Assert.Contains("bad", ex.Message);
    }

    [Fact]
    public async Task ThrowsWhenNoChoices()
    {
        var (client, _) = CreateClient("""{ "choices": [] }""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(Request()));
    }

    [Fact]
    public async Task StreamAsync_YieldsCompletedTextAsSingleDelta()
    {
        var (client, _) = CreateClient(SuccessBody);

        var events = new List<ModelStreamEvent>();
        await foreach (var e in client.StreamAsync(Request()))
        {
            events.Add(e);
        }

        var output = string.Concat(events
            .Where(e => e.Kind == ModelStreamEventKind.OutputTextDelta)
            .Select(e => e.Text));

        Assert.Equal("Hello there", output);
        Assert.Equal(ModelStreamEventKind.Completed, events[^1].Kind);
    }

    [Fact]
    public void Constructor_AllowsKeylessCustomEndpoint()
    {
        // A local OpenAI-compatible server (ApiBaseUrl set) needs no key.
        var config = new AppConfig { Model = "qwen", ApiBaseUrl = "http://localhost:8080/v1", ApiKey = "" };

        var client = new OpenAiChatModelClient(config, new Mock<IDisplayProvider>().Object);

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("")]
    [InlineData("YOUR_API_KEY_HERE")]
    public void Constructor_ThrowsWhenKeyMissingOnDefaultEndpoint(string apiKey)
    {
        var config = new AppConfig { Model = "gpt-4o", ApiKey = apiKey }; // no ApiBaseUrl → default endpoint

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new OpenAiChatModelClient(config, new Mock<IDisplayProvider>().Object));

        Assert.Contains("persistence.json", ex.Message);
    }
}
