using Persistence.Services;

namespace Persistence.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void EmptyOrNullIsZero()
    {
        Assert.Equal(0, TokenEstimator.Estimate((string?)null));
        Assert.Equal(0, TokenEstimator.Estimate(""));
    }

    [Theory]
    [InlineData("abcd", 1)]        // 4 chars → 1 token
    [InlineData("abcdefgh", 2)]    // 8 chars → 2 tokens
    [InlineData("abcde", 2)]       // 5 chars → rounds up to 2
    public void EstimatesByCharsPerToken(string text, int expected)
    {
        Assert.Equal(expected, TokenEstimator.Estimate(text));
    }

    [Fact]
    public void SumsMultipleStrings()
    {
        // 4 + 8 chars = 12 chars → 3 tokens
        Assert.Equal(3, TokenEstimator.Estimate(["abcd", "abcdefgh"]));
    }
}
