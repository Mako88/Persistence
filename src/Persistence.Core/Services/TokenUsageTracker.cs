using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// In-memory <see cref="ITokenUsageTracker"/>. Singleton — turns are serialized, so a single
/// "last call" value is sufficient. Values are null until the first call with usage data reports in.
/// </summary>
[Singleton(typeof(ITokenUsageTracker))]
public class TokenUsageTracker : ITokenUsageTracker
{
    public int? LastInputTokens { get; private set; }

    public int? LastEstimatedTokens { get; private set; }

    public void Record(int realInputTokens, int estimatedInputTokens)
    {
        LastInputTokens = realInputTokens;
        LastEstimatedTokens = estimatedInputTokens;
    }
}
