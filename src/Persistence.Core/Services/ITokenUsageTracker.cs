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
}
