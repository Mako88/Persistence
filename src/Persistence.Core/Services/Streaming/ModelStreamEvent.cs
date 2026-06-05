namespace Persistence.Services.Streaming;

/// <summary>
/// The kinds of incremental events a model can emit while streaming a response.
/// </summary>
public enum ModelStreamEventKind
{
    /// <summary>A chunk of the model's output text (the structured response body).</summary>
    OutputTextDelta,

    /// <summary>A chunk of the model's reasoning summary.</summary>
    ReasoningSummaryDelta,

    /// <summary>The stream has completed; no further events will follow.</summary>
    Completed,
}

/// <summary>
/// A single incremental event from a streaming model response. <see cref="Text"/>
/// carries the delta for the delta kinds and is empty for <see cref="ModelStreamEventKind.Completed"/>.
/// </summary>
public readonly record struct ModelStreamEvent(ModelStreamEventKind Kind, string Text)
{
    public static ModelStreamEvent OutputText(string text) => new(ModelStreamEventKind.OutputTextDelta, text);
    public static ModelStreamEvent ReasoningSummary(string text) => new(ModelStreamEventKind.ReasoningSummaryDelta, text);
    public static ModelStreamEvent Completed() => new(ModelStreamEventKind.Completed, string.Empty);
}
