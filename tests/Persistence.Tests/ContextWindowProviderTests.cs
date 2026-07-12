using Persistence.Config;

namespace Persistence.Tests;

public class ContextWindowProviderTests
{
    // No model_context_windows.json in the test working dir → the built-in map is used.
    private static readonly ContextWindowProvider Provider = new();

    [Fact]
    public void ExactKnownModelResolves()
    {
        Assert.Equal(400000, Provider.GetContextWindow("gpt-5"));
    }

    [Fact]
    public void PrefixMatchBeatsFallingThroughToDefault()
    {
        // "gpt-5.5-preview" matches the "gpt-5" prefix (no gpt-5.5 in the map) rather than the default.
        Assert.Equal(400000, Provider.GetContextWindow("gpt-5.5-preview"));
    }

    [Fact]
    public void LongestPrefixWinsWhenSeveralMatch()
    {
        // "claude-opus-4-8" prefixes BOTH "claude-opus-4" (1,000,000) and "claude" (200,000);
        // the longer key must win — this is what actually exercises the OrderByDescending(length) tie-break.
        Assert.Equal(1_000_000, Provider.GetContextWindow("claude-opus-4-8"));
    }

    [Fact]
    public void UnknownModelFallsBackToDefault()
    {
        Assert.Equal(128000, Provider.GetContextWindow("some-unknown-model"));
    }

    [Fact]
    public void NullOrEmptyFallsBackToDefault()
    {
        Assert.Equal(128000, Provider.GetContextWindow(""));
        Assert.Equal(128000, Provider.GetContextWindow(null!));
    }
}
