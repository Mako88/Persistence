namespace Persistence.Services;

/// <summary>
/// Converts an ordered list of <see cref="PromptSegment"/>s into a provider-specific
/// <see cref="PromptRequest"/>. Each provider has its own builder that maps sections
/// and source names to the API's message roles and structure.
/// </summary>
public interface IPromptBuilder
{
    /// <summary>
    /// Builds a provider-ready request from the formatted segments. Maps each segment's
    /// Section and Source to provider-specific roles and decides how to structure
    /// the messages array (e.g. separate system message, collapsing adjacent same-role
    /// segments, etc.).
    /// </summary>
    PromptRequest Build(List<PromptSegment> segments);
}
