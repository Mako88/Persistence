using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// In-memory <see cref="ITokenUsageTracker"/>. Singleton — turns are serialized, so a single
/// "last call" value is sufficient. Values are null until the first call with usage data reports in.
/// </summary>
[Singleton(typeof(ITokenUsageTracker))]
public class TokenUsageTracker : ITokenUsageTracker
{
    /// <summary>
    /// The provider's real input-token count from the most recent call, or null if none recorded yet
    /// </summary>
    public int? LastInputTokens { get; private set; }

    /// <summary>
    /// Our estimated input-token count for the most recent call, or null if none recorded yet
    /// </summary>
    public int? LastEstimatedTokens { get; private set; }

    /// <summary>
    /// Records the real and estimated input-token counts for the most recent call
    /// </summary>
    public void Record(int realInputTokens, int estimatedInputTokens)
    {
        LastInputTokens = realInputTokens;
        LastEstimatedTokens = estimatedInputTokens;
    }

    /// <inheritdoc />
    public int Calibrate(int estimatedTokens)
    {
        if (LastInputTokens is { } real && LastEstimatedTokens is { } est && est > 0)
        {
            return (int)Math.Round(estimatedTokens * ((double)real / est));
        }

        return estimatedTokens;
    }

    /// <inheritdoc />
    public long TotalInputTokens { get; private set; }

    /// <inheritdoc />
    public long TotalOutputTokens { get; private set; }

    /// <inheritdoc />
    public long TotalCacheReadTokens { get; private set; }

    /// <inheritdoc />
    public long TotalCacheCreationTokens { get; private set; }

    /// <inheritdoc />
    public int CallCount { get; private set; }

    /// <inheritdoc />
    public decimal? TotalActualCostUsd { get; private set; }

    /// <inheritdoc />
    public void AddUsage(ModelUsage usage)
    {
        TotalInputTokens += Math.Max(0, usage.InputTokens);
        TotalOutputTokens += Math.Max(0, usage.OutputTokens);
        TotalCacheReadTokens += Math.Max(0, usage.CacheReadTokens);
        TotalCacheCreationTokens += Math.Max(0, usage.CacheCreationTokens);
        if (usage.ActualCostUsd is { } actual)
            TotalActualCostUsd = (TotalActualCostUsd ?? 0m) + actual;
        CallCount++;
    }
}
