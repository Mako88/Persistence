namespace Persistence.Config;

/// <summary>
/// Resolves a model's hard context-window size (in tokens) from a runtime-editable map, so the
/// budget readout's denominator reflects the actual model rather than a hardcoded constant.
/// </summary>
public interface IContextWindowProvider
{
    /// <summary>
    /// The context window for <paramref name="model"/>, by longest case-insensitive prefix match,
    /// falling back to the configured default.
    /// </summary>
    int GetContextWindow(string model);
}
