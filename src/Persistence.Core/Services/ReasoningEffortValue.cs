namespace Persistence.Services;

/// <summary>
/// Interprets a profile's configured reasoning-effort value. "off"/"none"/blank means the model's
/// native reasoning is disabled: Persistence's own <c>&lt;think&gt;</c> mechanism is the reasoning
/// channel (persisted, inspectable), so clients don't ask the provider to think — this avoids a
/// redundant, ephemeral second reasoning channel. Any other value is a provider effort level
/// (e.g. "low"/"medium"/"high") passed through when native reasoning is on.
/// </summary>
internal static class ReasoningEffortValue
{
    /// <summary>True when native reasoning should be off (rely on the peer's own <c>&lt;think&gt;</c>).</summary>
    public static bool IsOff(string? effort) =>
        string.IsNullOrWhiteSpace(effort)
        || effort.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
        || effort.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
}
