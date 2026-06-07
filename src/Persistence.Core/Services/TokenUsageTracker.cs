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
}
