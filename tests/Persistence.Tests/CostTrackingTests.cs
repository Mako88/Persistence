using Persistence.Config;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// Unit tests for the running-cost building blocks: the data-driven pricing lookup and the
/// cumulative usage accumulation. Cost is assembled from these plus the model clients' real usage.
/// </summary>
public class CostTrackingTests
{
    // -- ModelPricingProvider (built-in fallback; no model_pricing.json in the test cwd) --

    [Theory]
    [InlineData("claude-opus-4-8", 5, 25)]     // longest prefix "claude-opus" beats "claude"
    [InlineData("claude-sonnet-4-6", 3, 15)]
    [InlineData("claude-haiku-4-5", 1, 5)]
    [InlineData("claude-fable-5", 10, 50)]
    [InlineData("claude-something-new", 3, 15)] // falls back to the generic "claude" entry
    public void PricesKnownModelFamiliesByLongestPrefix(string model, int input, int output)
    {
        var pricing = new ModelPricingProvider().GetPricing(model);

        Assert.NotNull(pricing);
        Assert.Equal(input, pricing!.Value.InputPerMillion);
        Assert.Equal(output, pricing.Value.OutputPerMillion);
    }

    [Theory]
    [InlineData("gemini-2.5-pro")]  // no built-in price — caller shows tokens without a dollar figure
    [InlineData("gemma")]
    [InlineData("")]
    public void ReturnsNullForUnpricedModels(string model)
    {
        Assert.Null(new ModelPricingProvider().GetPricing(model));
    }

    // -- TokenUsageTracker cumulative accumulation + calibration --

    [Fact]
    public void AddUsageAccumulatesTotalsAndCallCount()
    {
        var tracker = new TokenUsageTracker();

        tracker.AddUsage(new ModelUsage(100, 50, CacheReadTokens: 40, CacheCreationTokens: 10));
        tracker.AddUsage(new ModelUsage(200, 30, CacheReadTokens: 60));

        Assert.Equal(300, tracker.TotalInputTokens);
        Assert.Equal(80, tracker.TotalOutputTokens);
        Assert.Equal(100, tracker.TotalCacheReadTokens);
        Assert.Equal(10, tracker.TotalCacheCreationTokens);
        Assert.Equal(2, tracker.CallCount);
    }

    [Fact]
    public void AddUsageIgnoresNegativeCounts()
    {
        var tracker = new TokenUsageTracker();

        tracker.AddUsage(new ModelUsage(-5, -1)); // a malformed/negative report must not corrupt the totals

        Assert.Equal(0, tracker.TotalInputTokens);
        Assert.Equal(0, tracker.TotalOutputTokens);
        Assert.Equal(1, tracker.CallCount);
    }

    [Fact]
    public void CalibrateScalesByTheLastRealToEstimatedRatio()
    {
        var tracker = new TokenUsageTracker();

        Assert.Equal(100, tracker.Calibrate(100)); // no real usage yet → estimate unchanged

        tracker.Record(realInputTokens: 120, estimatedInputTokens: 100); // provider counted 1.2x our estimate

        Assert.Equal(120, tracker.Calibrate(100)); // subsequent estimates scaled by 1.2
    }

    [Fact]
    public void CalibrateReturnsEstimateUnchangedWhenRecordedEstimateIsZero()
    {
        var tracker = new TokenUsageTracker();

        tracker.Record(realInputTokens: 120, estimatedInputTokens: 0); // guards against divide-by-zero

        Assert.Equal(100, tracker.Calibrate(100));
    }
}
