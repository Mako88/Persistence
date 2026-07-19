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
    public void AProviderReportedCostIsPreferredOverTheEstimate()
    {
        // The whole point of the actual-cost wiring (GLM's): when the provider tells us what the call
        // really cost, that figure wins over tokens × rate. Added during review — the feature merged
        // green with this behaviour disabled entirely, because nothing asserted the preference itself.
        var reported = 0.00042m;
        var usage = Usage(new ModelUsage(1_000_000, 1_000_000, ActualCostUsd: reported));

        // Priced so the ESTIMATE would be wildly different — if the estimate ever wins, this fails loudly
        // rather than passing on a coincidentally-similar number.
        var estimator = Make(usage, new ModelPricing(InputPerMillion: 100m, OutputPerMillion: 100m));

        Assert.Equal(reported, estimator.CurrentCost());
        Assert.True(estimator.IsActual);
    }

    [Fact]
    public void TheEstimateIsUsedWhenTheProviderReportsNoCost()
    {
        // Every provider except OpenRouter reports tokens only, so the fallback is the common path —
        // and it must stay exact, not merely "some number".
        var usage = Usage(new ModelUsage(1_000_000, 1_000_000));
        var estimator = Make(usage, new ModelPricing(InputPerMillion: 3m, OutputPerMillion: 15m));

        Assert.Equal(18m, estimator.CurrentCost());
        Assert.False(estimator.IsActual);
    }

    [Fact]
    public void ActualCostAccumulatesAcrossCallsRatherThanReportingOnlyTheLast()
    {
        // A session cost is cumulative. Reporting the most recent call's cost as the session total would
        // read plausibly and be wrong by however many turns have gone before.
        var tracker = new TokenUsageTracker();
        tracker.AddUsage(new ModelUsage(10, 10, ActualCostUsd: 0.001m));
        tracker.AddUsage(new ModelUsage(10, 10, ActualCostUsd: 0.002m));

        Assert.Equal(0.003m, Make(tracker, new ModelPricing(1m, 1m)).CurrentCost());
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
