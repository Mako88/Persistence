using Moq;
using Persistence.Config;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// The shared session-cost estimator (used by the sensory cost line and the turn handler's optional hard
/// ceiling). Verifies the pricing math and the provider-aware cache discount.
/// </summary>
public class SessionCostEstimatorTests
{
    private static ISessionCostEstimator Make(ITokenUsageTracker usage, ModelPricing? price, string provider = "Anthropic")
    {
        var pricing = new Mock<IModelPricingProvider>();
        pricing.Setup(p => p.GetPricing(It.IsAny<string>())).Returns(price);
        var config = new AppConfig { Model = "m", Provider = provider };
        return new SessionCostEstimator(usage, pricing.Object, config);
    }

    private static TokenUsageTracker Usage(ModelUsage u)
    {
        var t = new TokenUsageTracker();
        t.AddUsage(u);
        return t;
    }

    [Fact]
    public void NullWhenTheModelHasNoPrice()
    {
        Assert.Null(Make(Usage(new ModelUsage(1000, 500)), price: null).CurrentCost());
    }

    [Fact]
    public void CostIsInputPlusOutputAtTheModelRate()
    {
        // 1M input + 200k output at $5/M in, $25/M out -> $5 + $5 = $10.
        var cost = Make(Usage(new ModelUsage(1_000_000, 200_000)), new ModelPricing(5m, 25m)).CurrentCost();
        Assert.Equal(10m, cost);
    }

    [Fact]
    public void OpenAiCacheReadsAreBilledAtHalfInput()
    {
        // 1M cached-read tokens, no fresh input/output, at $2.50/M in -> 1M * 2.50/M * 0.5 = $1.25 (OpenAI).
        var cost = Make(Usage(new ModelUsage(0, 0, CacheReadTokens: 1_000_000)), new ModelPricing(2.5m, 10m), provider: "OpenAI")
            .CurrentCost();
        Assert.Equal(1.25m, cost);
    }

    [Fact]
    public void AnthropicCacheReadsAreBilledAtTenPercentInput()
    {
        // Same 1M cached read, Anthropic multiplier 0.1 -> 1M * 2.50/M * 0.1 = $0.25.
        var cost = Make(Usage(new ModelUsage(0, 0, CacheReadTokens: 1_000_000)), new ModelPricing(2.5m, 10m), provider: "Anthropic")
            .CurrentCost();
        Assert.Equal(0.25m, cost);
    }
}
