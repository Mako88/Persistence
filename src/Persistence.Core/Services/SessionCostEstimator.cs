using Persistence.Config;
using Persistence.DI;

namespace Persistence.Services;

/// <summary>Estimates the running session cost in USD from tracked token usage and the active model's price.</summary>
public interface ISessionCostEstimator
{
    /// <summary>Cumulative estimated session cost in USD, or null if the active model has no known price.</summary>
    decimal? CurrentCost();
}

/// <summary>
/// Computes the running session cost from <see cref="ITokenUsageTracker"/> totals and the active model's
/// price, applying provider-aware prompt-cache multipliers. Shared so the sensory cost readout and the
/// turn handler's optional cost ceiling agree on the same number. Mirrors the formula in
/// <c>PromptFormatter.FormatSessionCost</c>; keep the two in step (a test asserts the shape).
/// </summary>
[Singleton(typeof(ISessionCostEstimator))]
public class SessionCostEstimator(ITokenUsageTracker usage, IModelPricingProvider pricing, IAppConfig config)
    : ISessionCostEstimator
{
    public decimal? CurrentCost()
    {
        if (pricing.GetPricing(config.Model) is not { } r)
        {
            return null;
        }

        var (readMult, writeMult) = CacheMultipliers(config.Provider);
        return (usage.TotalInputTokens * r.InputPerMillion
                + usage.TotalCacheReadTokens * r.InputPerMillion * readMult
                + usage.TotalCacheCreationTokens * r.InputPerMillion * writeMult
                + usage.TotalOutputTokens * r.OutputPerMillion) / 1_000_000m;
    }

    /// <summary>Prompt-cache pricing relative to base input, by provider: OpenAI reads ~50% with no write
    /// premium; Anthropic (and others) reads ~10% / writes ~125%.</summary>
    internal static (decimal Read, decimal Write) CacheMultipliers(string provider) =>
        Enum.TryParse<ModelProvider>(provider, ignoreCase: true, out var p)
            && p is ModelProvider.OpenAI or ModelProvider.OpenAiChat
            ? (0.5m, 1.0m)
            : (0.1m, 1.25m);
}
