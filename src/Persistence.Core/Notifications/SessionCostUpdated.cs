using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Raised when the running session cost/usage is recalculated (once per prompt assembly). Subscribers
/// can show a live cost readout — e.g. a status-bar segment. Token counts are estimates; cost is null
/// when no pricing is configured for the active model (show tokens without a dollar figure).
/// </summary>
public class SessionCostUpdated(decimal? costUsd, long inputTokens, long outputTokens, int callCount) : BaseEvent
{
    /// <summary>Estimated cumulative cost in USD this session, or null when the model has no pricing.</summary>
    public decimal? CostUsd { get; } = costUsd;

    /// <summary>Estimated cumulative input tokens billed this session (summed per call).</summary>
    public long InputTokens { get; } = inputTokens;

    /// <summary>Estimated cumulative output tokens generated this session.</summary>
    public long OutputTokens { get; } = outputTokens;

    /// <summary>How many model calls have completed this session.</summary>
    public int CallCount { get; } = callCount;
}
