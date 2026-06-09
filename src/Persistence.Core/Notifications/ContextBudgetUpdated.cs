using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Raised when the prompt's context-budget usage is recalculated (once per prompt assembly).
/// Subscribers can show how full the working context is — e.g. a status-bar gauge.
/// </summary>
public class ContextBudgetUpdated(int usedTokens, int budgetTokens, int percentFull) : BaseEvent
{
    /// <summary>Calibrated estimate of tokens used by the current prompt.</summary>
    public int UsedTokens { get; } = usedTokens;

    /// <summary>The effective token budget (configured working limit, or the model's context window).
    /// Zero when no budget is configured and the model's window is unknown.</summary>
    public int BudgetTokens { get; } = budgetTokens;

    /// <summary>How full the context is, 0–100. Zero when no budget is known.</summary>
    public int PercentFull { get; } = percentFull;
}
