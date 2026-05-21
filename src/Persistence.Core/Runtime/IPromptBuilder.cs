using Persistence.Data.Entities;

namespace Persistence.Runtime;

/// <summary>
/// Builds a prompt from a working context for submission to the model. Assembles
/// persisted fragments with uniform metadata headers, any transient fragments on the
/// context, and a sensory block with environmental awareness data. System-type
/// fragments are separated into the system prompt for providers that support it.
/// </summary>
public interface IPromptBuilder
{
    /// <summary>
    /// Builds a prompt string from the working context. Returns a tuple of the main
    /// prompt (all non-System fragments formatted uniformly, plus sensory data) and
    /// an optional system prompt (System fragments only). When
    /// <paramref name="iteration"/> is provided, the sensory block includes
    /// continue-iteration info so the digital colleague is aware of the limit.
    /// <paramref name="availableTags"/> populates the tag catalogue in the sensory block.
    /// </summary>
    (string prompt, string? systemPrompt) Build(
        WorkingContextEntity context,
        IEnumerable<TagEntity> availableTags,
        int iteration = 0,
        int maxIterations = 0);
}
