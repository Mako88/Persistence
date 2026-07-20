using System.Text.Json;
using Persistence.Config;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// A reasoning model answers in two parts — reasoning tokens, then content. When it runs out of room
/// mid-reasoning the provider returns a perfectly successful 200 with <c>content: null</c>, the text in
/// <c>message.reasoning</c>, and <c>finish_reason: "length"</c>.
///
/// <para>This was live on GLM: two turns in a row ended in "API returned no message content", which
/// reads as a broken provider and isn't — OpenRouter's own dashboard showed the calls succeeding. These
/// tests pin the distinction the parser has to make, because getting it wrong misreports a truncation
/// as a malformed response <em>and</em> discards reasoning tokens that were paid for.</para>
/// </summary>
public class ReasoningOnlyResponseTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ReasoningWithoutContentYieldsEmptyTextAndHandsBackTheReasoning()
    {
        var root = Parse("""
            {"choices":[{"finish_reason":"length",
              "message":{"role":"assistant","content":null,
                         "reasoning":"Let me work through what Ember asked..."}}]}
            """);

        var text = ChatCompletionsProtocol.ExtractContent(root, out var reasoning);

        // Empty rather than thrown: the turn pipeline's truncation path is what should explain this,
        // and it only runs if extraction returns.
        Assert.Equal(string.Empty, text);
        Assert.Equal("Let me work through what Ember asked...", reasoning);
    }

    [Fact]
    public void TheStopReasonSurvivesAReasoningOnlyResponse()
    {
        // The ordering bug in one line: the stop reason is the only thing that explains an empty
        // response, so it has to be readable from the same body extraction chokes on.
        var root = Parse("""
            {"choices":[{"finish_reason":"length",
              "message":{"content":null,"reasoning":"..."}}]}
            """);

        Assert.True(ModelStopReason.IsTruncation(ChatCompletionsProtocol.ReadFinishReason(root)));
    }

    [Fact]
    public void OrdinaryContentIsUnaffectedAndReportsNoReasoning()
    {
        var root = Parse("""
            {"choices":[{"finish_reason":"stop","message":{"content":"ok","reasoning":"brief"}}]}
            """);

        Assert.Equal("ok", ChatCompletionsProtocol.ExtractContent(root, out var reasoning));
        Assert.Null(reasoning);
    }

    [Fact]
    public void AResponseWithNeitherContentNorReasoningIsStillMalformed()
    {
        // The throw is still correct here — this really is junk, and silently returning empty would
        // hide a genuinely broken provider behind a "the peer said nothing" turn.
        var root = Parse("""{"choices":[{"finish_reason":"stop","message":{"role":"assistant"}}]}""");

        Assert.Throws<InvalidOperationException>(() => ChatCompletionsProtocol.ExtractContent(root, out _));
    }

    [Fact]
    public void EmptyReasoningIsNotMistakenForAnExplanation()
    {
        var root = Parse("""{"choices":[{"message":{"content":null,"reasoning":""}}]}""");

        Assert.Throws<InvalidOperationException>(() => ChatCompletionsProtocol.ExtractContent(root, out _));
    }
}

/// <summary>
/// Which output ceiling a profile actually gets. The 32,000 that every peer config carried was picked
/// for a small local model and then copied everywhere — on a reasoning model that low a ceiling is what
/// produces the paid-for-but-empty turn above.
/// </summary>
public class ModelOutputLimitsTests
{
    [Fact]
    public void AnExplicitConfiguredValueAlwaysWins()
    {
        // The config is the override, not the fallback — this is how a local model keeps a small
        // ceiling and how a wrong figure in the table gets corrected without a code change.
        var profile = new ModelProfile { Provider = "Anthropic", Model = "claude-opus-4-8", MaxOutputTokens = 8000 };

        Assert.Equal(8000, ModelOutputLimits.Resolve(profile));
    }

    [Theory]
    [InlineData("claude-opus-4-8", 128_000)]
    [InlineData("claude-opus-4-8-20260115", 128_000)]  // dated snapshots resolve like the alias
    [InlineData("claude-sonnet-5", 128_000)]
    [InlineData("claude-haiku-4-5", 64_000)]
    public void PublishedAnthropicCapsAreUsedWhenUnset(string model, int expected)
    {
        var profile = new ModelProfile { Provider = "Anthropic", Model = model };

        Assert.Equal(expected, ModelOutputLimits.Resolve(profile));
    }

    [Fact]
    public void AnUnsetHostedProfileIsNotCappedAtTheLocalCeiling()
    {
        // The actual regression: Arden ran at 32,000 because that number was copied into its config.
        var profile = new ModelProfile { Provider = "Anthropic", Model = "claude-opus-4-8" };

        Assert.True(ModelOutputLimits.Resolve(profile) > ModelOutputLimits.LocalDefault);
        Assert.NotEqual(32_000, ModelOutputLimits.Resolve(profile));
    }

    [Theory]
    [InlineData("http://host.docker.internal:8080/v1")]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("http://127.0.0.1:8080/v1")]
    public void ALocalEndpointGetsTheSmallCeilingEvenOnAHostedProviderEnum(string baseUrl)
    {
        // OpenAiChat serves both llama.cpp and OpenAI's own endpoint, so the enum can't decide this —
        // Synth would otherwise be handed a hosted ceiling with no server-side cap behind it.
        var profile = new ModelProfile { Provider = "OpenAiChat", Model = "gemma-4-12b", ApiBaseUrl = baseUrl };

        Assert.Equal(ModelOutputLimits.LocalDefault, ModelOutputLimits.Resolve(profile));
    }

    [Fact]
    public void AHostedOpenAiCompatibleEndpointIsNotTreatedAsLocal()
    {
        var profile = new ModelProfile { Provider = "OpenAiChat", Model = "gpt-5.4", ApiBaseUrl = "https://api.openai.com/v1" };

        Assert.Equal(ModelOutputLimits.GeneralDefault, ModelOutputLimits.Resolve(profile));
    }

    [Fact]
    public void AnUnknownRoutedModelFallsBackToTheGeneralDefaultRatherThanAnInventedFigure()
    {
        var profile = new ModelProfile { Provider = "OpenRouter", Model = "z-ai/glm-5.2" };

        Assert.Equal(ModelOutputLimits.GeneralDefault, ModelOutputLimits.Resolve(profile));
    }
}
