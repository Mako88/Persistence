namespace Persistence.Services;

/// <summary>
/// Parses raw model output into a structured <see cref="ModelResponse"/>
/// </summary>
public interface IModelResponseParser
{
    /// <summary>
    /// Parses the raw model output into a <see cref="ModelResponse"/>. Returns a
    /// <see cref="ModelAction.RespondToUser"/> response if the output is not valid JSON.
    /// </summary>
    ModelResponse Parse(string rawOutput);
}
