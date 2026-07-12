namespace Persistence.Services;

/// <summary>
/// One parsed turn from the remote peer: an ordered list of actions to perform and a single
/// <see cref="Continue"/> flag for whether to loop afterward.
///
/// The tagged format can yield several actions in one turn (e.g. think + respond + manage context).
/// The parser produces this shape regardless of format, so the turn pipeline stays format-agnostic and
/// a different response format would slot in unchanged.
/// </summary>
public class ModelTurn
{
    /// <summary>The actions to perform, in order.</summary>
    public required IReadOnlyList<ModelResponse> Actions { get; init; }

    /// <summary>
    /// Whether the remote peer wants the updated context sent back for another iteration
    /// before yielding to the local peer.
    /// </summary>
    public bool Continue { get; init; }

    /// <summary>
    /// Whether the raw output parsed successfully. When false, <see cref="Actions"/> is
    /// empty and the caller falls back (e.g. surfaces the raw text / re-prompts).
    /// </summary>
    public bool ParsedSuccessfully { get; init; }

    /// <summary>A failed parse, carrying no actions.</summary>
    public static ModelTurn Failed() => new() { Actions = [], ParsedSuccessfully = false };
}
