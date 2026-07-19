using Persistence.Config;

namespace Persistence.Tests;

/// <summary>
/// What a digital peer calls itself before it's chosen a name. The name isn't cosmetic: it's stamped on
/// the source its own messages are attributed to, so it's what every client reads back as the author of
/// that peer's history.
/// </summary>
public class PeerIdentityTests
{
    [Theory]
    [InlineData("Anthropic", "Claude")]
    [InlineData("anthropic", "Claude")]      // provider parsing is case-insensitive
    [InlineData("LocalClaude", "Claude")]    // a Claude answering out-of-band is still Claude
    [InlineData("OpenAI", "ChatGPT")]
    public void AFreshPeerIsNamedForItsProvidersAssistant(string provider, string expected) =>
        Assert.Equal(expected, PeerIdentity.DefaultName(provider, model: "some-model"));

    [Theory]
    [InlineData("Local")]
    [InlineData("OpenAiChat")]
    public void AProviderThatCouldBeAnyoneIsNamedFromItsModel(string provider) =>
        // OpenAiChat is OpenAI's endpoint *or* llama.cpp/Ollama behind an ApiBaseUrl, and Local is
        // whatever you're running — so the family isn't knowable from the *provider*, only from the
        // model. (This used to return the raw id, "gemma4-12b-q4"; naming the family reads far better
        // and is the same rule routed models get.)
        Assert.Equal("Gemma", PeerIdentity.DefaultName(provider, model: "gemma4-12b-q4"));

    [Theory]
    [InlineData("z-ai/glm-5.2", "GLM")]
    [InlineData("anthropic/claude-opus-4.8", "Claude")]
    [InlineData("openai/gpt-5.4", "ChatGPT")]
    [InlineData("deepseek/deepseek-chat", "DeepSeek")]
    [InlineData("qwen/qwen3-32b", "Qwen")]
    public void ARoutedModelIsNamedForItsFamilyNotItsRoute(string route, string expected) =>
        // An OpenRouter model id is a route ("who hosts it / what it is"), and the vendor half is
        // routing detail rather than identity — a peer shouldn't introduce itself as "z-ai/glm-5.2".
        Assert.Equal(expected, PeerIdentity.DefaultName("OpenRouter", route));

    [Fact]
    public void AnUnrecognisedFamilyKeepsItsModelIdRatherThanGuessing() =>
        // Better a precise identifier than a confidently wrong name. (Vendor prefix still dropped.)
        Assert.Equal("some-new-model-9", PeerIdentity.DefaultName("OpenRouter", "newco/some-new-model-9"));

    [Fact]
    public void AnUnrecognisedProviderStillFallsBackRatherThanThrowing() =>
        Assert.Equal("some-model", PeerIdentity.DefaultName("NotAProvider", model: "some-model"));

    [Fact]
    public void WithNothingToGoOnTheNameIsAPlaceholderNotBlank() =>
        // A blank byline would be worse than an obviously provisional one.
        Assert.Equal("Peer", PeerIdentity.DefaultName(provider: "", model: ""));

    [Fact]
    public void AConfiguredNameWinsOverTheProviderDefault()
    {
        // The point of the whole thing: once a peer picks a name, that's who it is.
        var config = new AppConfig { Provider = "Anthropic", Model = "claude-opus-4-8", PeerName = "Arden" };

        Assert.Equal("Arden", PeerIdentity.ResolveName(config));
    }

    [Fact]
    public void AConfiguredNameIsTrimmed()
    {
        var config = new AppConfig { Provider = "Anthropic", PeerName = "  Ember  " };

        Assert.Equal("Ember", PeerIdentity.ResolveName(config));
    }

    [Fact]
    public void AnUnsetNameDerivesFromTheProvider()
    {
        var config = new AppConfig { Provider = "Anthropic", Model = "claude-opus-4-8", PeerName = "" };

        Assert.Equal("Claude", PeerIdentity.ResolveName(config));
    }
}
