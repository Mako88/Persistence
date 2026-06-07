namespace Persistence.Services;

/// <summary>
/// Parses raw model output into a structured <see cref="ModelTurn"/> (one or more actions
/// plus a continue flag). Implementations define the wire format the remote peer responds in.
/// </summary>
public interface IModelResponseParser
{
    /// <summary>
    /// Parses the raw model output into a <see cref="ModelTurn"/>. Returns a turn with
    /// <see cref="ModelTurn.ParsedSuccessfully"/> = false when the output can't be parsed.
    /// </summary>
    ModelTurn Parse(string rawOutput);
}
