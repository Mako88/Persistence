namespace Persistence.Services;

/// <summary>
/// A single segment of a formatted prompt. The ordered list of segments produced by
/// <see cref="IPromptFormatter"/> preserves fragment order from the working context.
/// Each <see cref="IPromptBuilder"/> maps Source to the appropriate API-specific
/// message structure.
/// </summary>
public record PromptSegment
{
    /// <summary>
    /// The name of the source that created this content (e.g. "Local Peer",
    /// "Remote Peer", "System"). Builders use this to map to provider-specific
    /// roles (e.g. "user", "assistant" for OpenAI).
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// The fully formatted content string, including any fragment metadata headers
    /// </summary>
    public required string Content { get; init; }
}
