using Persistence.Data.Entities;

namespace Persistence.Services;

/// <summary>
/// A single segment of a formatted prompt. The ordered list of segments produced by
/// <see cref="IPromptFormatter"/> preserves fragment order from the working context.
/// Each <see cref="IPromptBuilder"/> maps the segment to the appropriate API-specific
/// message structure.
/// </summary>
public record PromptSegment
{
    /// <summary>
    /// The display name of who produced this content (a peer's name, or "System"). This is what the
    /// reader sees; role mapping should prefer <see cref="AuthorType"/> where set.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// The provenance of this segment, used by builders to map to provider-specific roles
    /// (digital peer → assistant, human peer → user, system → developer/system). Null for
    /// framework-injected segments (protocol instructions, the sensory block) that aren't a
    /// peer's authored content — builders treat those as system. Mapping by type rather than by
    /// the <see cref="Source"/> string keeps role assignment correct as peers gain arbitrary names.
    /// </summary>
    public SourceType? AuthorType { get; init; }

    /// <summary>
    /// The fully formatted content string, including any fragment metadata headers
    /// </summary>
    public required string Content { get; init; }
}
