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
    public void LongestPrefixWins()
    {
        // "gpt-5.5-preview" should match "gpt-5" prefix in the built-in map (no gpt-5.5 there),
        // i.e. a prefix match rather than falling through to default.
        Assert.Equal(400000, Provider.GetContextWindow("gpt-5.5-preview"));
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
