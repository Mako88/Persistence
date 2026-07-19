namespace Persistence.Services;

/// <summary>
/// Records the real prompt-token usage reported by the model provider so the context-budget
/// readout can be grounded in actual data rather than estimate alone.
///
/// Real usage is only known *after* a call returns, while the budget line is rendered *before*
/// the next call — so the tracker's value is always "last turn's" real input size. Because
/// context grows incrementally, that's a strong predictor of the current prompt; the formatter
/// uses it to calibrate the current estimate (real ÷ estimated) rather than reporting it raw.
/// </summary>
public interface ITokenUsageTracker
{
    /// <summary>The provider-reported input-token count from the most recent call, if any.</summary>
    int? LastInputTokens { get; }

    /// <summary>
    /// The token estimate (from <see cref="TokenEstimator"/>) of the prompt that produced
    /// <see cref="LastInputTokens"/>, so a calibration ratio can be derived.
    /// </summary>
    int? LastEstimatedTokens { get; }

    /// <summary>Records the real and estimated input-token counts for a completed call.</summary>
    void Record(int realInputTokens, int estimatedInputTokens);

    /// <summary>
    /// Adjusts a raw token estimate by the last call's real:estimated ratio (when known), so the
    /// figure tracks the provider's actual tokenizer rather than the ~4-chars/token heuristic alone.
    /// Returns the estimate unchanged before any real usage has been recorded.
    /// </summary>
    int Calibrate(int estimatedTokens);

    // --- Cumulative session usage (for the running cost readout) ---

    /// <summary>Cumulative uncached (full-price) input tokens billed this session.</summary>
    long TotalInputTokens { get; }

    /// <summary>Cumulative output tokens generated this session.</summary>
    long TotalOutputTokens { get; }

    /// <summary>Cumulative prompt-cache read tokens this session (billed cheap).</summary>
    long TotalCacheReadTokens { get; }

    /// <summary>Cumulative prompt-cache creation tokens this session (billed at a write premium).</summary>
    long TotalCacheCreationTokens { get; }

    /// <summary>How many model calls have been accounted for this session.</summary>
    int CallCount { get; }

    /// <summary>
    /// Cumulative actual cost in USD reported by the provider (e.g. OpenRouter's <c>usage.cost</c>),
    /// or null when no provider has reported one. When non-null, the session-cost readout and ceiling
    /// use this figure directly instead of the token-based estimate.
    /// </summary>
    decimal? TotalActualCostUsd { get; }

    /// <summary>Adds one completed call's real token usage (input/output + cache portions) to the session totals.</summary>
    void AddUsage(ModelUsage usage);
}