using Persistence.Config;
using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// Estimates the running session cost in USD. When the provider reports an actual cost per call
/// (e.g. OpenRouter's <c>usage.cost</c>), the estimator accumulates and returns that figure
/// directly; otherwise it falls back to an estimate from token counts × the model's price.
/// </summary>
public interface ISessionCostEstimator
{
    /// <summary>
    /// Cumulative session cost in USD — the provider-reported actual when available, otherwise an
    /// estimate from token counts. Null when the model has no known price and no actual cost has been
    /// reported.
    /// </summary>
    decimal? CurrentCost();

    /// <summary>True when <see cref="CurrentCost"/> is the provider's actual reported cost rather than an estimate.</summary>
    bool IsActual { get; }
}

/// <summary>
/// Computes the running session cost from <see cref="ITokenUsageTracker"/> totals and the active model's
/// price, applying provider-aware prompt-cache multipliers. When the tracker has accumulated an actual
/// cost (reported by providers like OpenRouter), that figure is preferred over the estimate. Shared so
/// the sensory cost readout and the turn handler's optional cost ceiling agree on the same number.
/// </summary>
[Singleton(typeof(ISessionCostEstimator))]
public class SessionCostEstimator(ITokenUsageTracker usage, IModelPricingProvider pricing, IAppConfig config)
    : ISessionCostEstimator
{
    /// <inheritdoc />
    public bool IsActual => usage.TotalActualCostUsd is not null;

    /// <inheritdoc />
    public decimal? CurrentCost()
    {
        // Prefer the provider's actual reported cost when available (e.g. OpenRouter's usage.cost).
        if (usage.TotalActualCostUsd is { } actual)
        {
            return actual;
        }

        // Fall back to estimate from token counts × the model's price.
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