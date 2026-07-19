using Moq;
using Persistence.Config;
using Persistence.Runtime;
using Persistence.Services;
using SimpleHttpClient;
using SimpleHttpClient.Models;
using SimpleHttpClient.Serialization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Persistence.Tests;

public class OpenRouterModelClientTests
{
    // OpenRouter returns the standard Chat Completions shape, plus usage.cost — the call's ACTUAL charge
    // in USD — when the request asks for usage accounting.
    private static readonly string SuccessBody = """
    {
      "choices": [
        { "message": { "role": "assistant", "content": "Hello there" }, "finish_reason": "stop" }
      ],
      "usage": { "prompt_tokens": 12, "completion_tokens": 3, "cost": 0.00042 }
    }
    """;

    private static (OpenRouterModelClient Client, Mock<ISimpleClient> Http) CreateClient(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var config = new AppConfig { Model = "z-ai/glm-5.2", MaxOutputTokens = 1234, ApiKey = "sk-or-test" };

        ISimpleResponse response = new SimpleResponse
        {
            StatusCode = status,
            IsSuccessful = (int)status is >= 200 and < 300,
            StringBody = responseBody,
        };

        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>())).ReturnsAsync(response);

        return (new OpenRouterModelClient(config, new Mock<IDisplayProvider>().Object, http.Object), http);
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
    public async Task SendsTheNamespacedRouteIdAsTheModel()
    {
        var (client, http) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        var root = SerializedBody(http);
        // The vendor prefix is part of the route — it must survive to the wire untouched.
        Assert.Equal("z-ai/glm-5.2", root.GetProperty("model").GetString());
        Assert.Equal(1234, root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task FlattensThePromptToATemplateSafeShape()
    {
        var (client, http) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        // Same shape the Chat Completions client sends (shared via ChatCompletionsProtocol), which
        // matters more here: a router fans out to models with wildly different chat templates.
        var messages = SerializedBody(http).GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task AsksForUsageAccounting()
    {
        var (client, http) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        // Without this OpenRouter omits the cost field, and we'd be back to estimating from a rate table.
        Assert.True(SerializedBody(http).GetProperty("usage").GetProperty("include").GetBoolean());
    }

    [Fact]
    public async Task ReturnsMessageContent()
    {
        var (client, _) = CreateClient(SuccessBody);

        Assert.Equal("Hello there", await client.CompleteAsync(Request()));
    }

    [Fact]
    public async Task ExposesRealTokenUsageAfterCompletion()
    {
        var (client, _) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        Assert.Equal(new ModelUsage(12, 3), client.LastUsage);
    }

    [Fact]
    public async Task ExposesTheActualCostOpenRouterReported()
    {
        var (client, _) = CreateClient(SuccessBody);

        await client.CompleteAsync(Request());

        // The real charge, not tokens x our rate table — the thing a router can tell us and a
        // hand-maintained price list can't keep up with.
        Assert.Equal(0.00042m, client.LastActualCostUsd);
    }

    [Fact]
    public async Task ActualCostIsNullWhenNotReported()
    {
        var (client, _) = CreateClient("""
        {
          "choices": [ { "message": { "role": "assistant", "content": "hi" } } ],
          "usage": { "prompt_tokens": 5, "completion_tokens": 1 }
        }
        """);

        await client.CompleteAsync(Request());

        Assert.Null(client.LastActualCostUsd);
        Assert.Equal(new ModelUsage(5, 1), client.LastUsage);   // tokens still read fine
    }

    [Fact]
    public async Task SplitsCachedInputOutOfThePromptTotal()
    {
        var (client, _) = CreateClient("""
        {
          "choices": [ { "message": { "role": "assistant", "content": "hi" } } ],
          "usage": {
            "prompt_tokens": 100,
            "completion_tokens": 10,
            "prompt_tokens_details": { "cached_tokens": 80 }
          }
        }
        """);

        await client.CompleteAsync(Request());

        // prompt_tokens is the total; the cached part bills cheaper, so it must not also count as input.
        Assert.Equal(new ModelUsage(20, 10, CacheReadTokens: 80), client.LastUsage);
    }

    [Fact]
    public async Task ThrowsWithStatusAndBodyOnFailure()
    {
        var (client, _) = CreateClient("""{ "error": "bad route" }""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(Request()));

        Assert.Contains("BadRequest", ex.Message);
        Assert.Contains("bad route", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("YOUR_API_KEY_HERE")]
    public void RequiresAnApiKey(string key)
    {
        // A hosted router always authenticates — there's no keyless local-server case to exempt, so this
        // should fail at construction with something actionable rather than a 401 mid-turn.
        var config = new AppConfig { Provider = "OpenRouter", Model = "z-ai/glm-5.2", ApiKey = key };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new OpenRouterModelClient(config, new Mock<IDisplayProvider>().Object));

        Assert.Contains("OpenRouter", ex.Message);
        Assert.Contains("sk-or-", ex.Message);
    }
}
