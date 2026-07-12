using Persistence.Data.Entities;

namespace Persistence.Services;

/// <summary>
/// Formats a working context into an ordered list of <see cref="PromptSegment"/>s.
/// Handles fragment metadata headers, sensory block generation, and content formatting.
/// Fragment order from the working context is preserved in the output list.
/// </summary>
public interface IPromptFormatter
{
    /// <summary>
    /// Formats the working context into an ordered list of prompt segments.
    /// Each fragment becomes a segment with its Section, Source name, and
    /// formatted content (including metadata headers). A sensory block is
    /// appended at the end.
    /// </summary>
    List<PromptSegment> Format(
        WorkingContextEntity context,
        IEnumerable<TagEntity> availableTags,
        int iteration = 0,
        int maxIterations = 0,
        IReadOnlyList<AuditLogEntity>? recentChanges = null,
        IReadOnlyList<string>? recentActions = null,
        string? archiveNote = null,
        IReadOnlyList<ContextFragmentEntity>? surfacedMemories = null);
}
