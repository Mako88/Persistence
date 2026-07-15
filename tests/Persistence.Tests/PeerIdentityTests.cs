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
    public void AProviderThatCouldBeAnyoneFallsBackToTheModelId(string provider) =>
        // OpenAiChat is OpenAI's endpoint *or* llama.cpp/Ollama behind an ApiBaseUrl, and Local is
        // whatever you're running — so the family isn't knowable from the provider. The model id is the
        // most informative thing we actually have, and beats guessing "ChatGPT" for a Gemma.
        Assert.Equal("gemma4-12b-q4", PeerIdentity.DefaultName(provider, model: "gemma4-12b-q4"));

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
