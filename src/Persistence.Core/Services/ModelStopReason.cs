namespace Persistence.Services;

/// <summary>
/// Reads meaning out of a provider's raw stop-reason string (<see cref="IModelClient.LastStopReason"/>).
///
/// <para>Exists because the one thing the turn pipeline must act on — "was this output cut off?" — is
/// spelled differently by every provider, and because for a long time nothing read the stop reason at
/// all. The API had been reporting on every single call whether the model finished or hit the ceiling,
/// and we discarded it; a peer lost three turns' replies while we inferred the cause from event
/// timings instead of reading the answer the provider had already given us.</para>
/// </summary>
public static class ModelStopReason
{
    /// <summary>Anthropic's "I ran out of room" value.</summary>
    public const string MaxTokens = "max_tokens";

    /// <summary>The OpenAI-family spelling of the same thing.</summary>
    public const string Length = "length";

    /// <summary>
    /// Whether the model was cut off mid-generation rather than finishing what it meant to say.
    ///
    /// <para>Matters because a truncated turn is <em>silently</em> wrong: the peer believes it said
    /// something it never finished saying, and from the outside the reply is merely absent or short.
    /// Nothing else about the response distinguishes it from a deliberate one.</para>
    ///
    /// <para>Unknown or unrecognised values are <em>not</em> treated as truncation — a provider we
    /// haven't taught this about should not have every turn flagged.</para>
    /// </summary>
    public static bool IsTruncation(string? stopReason) =>
        string.Equals(stopReason, MaxTokens, StringComparison.OrdinalIgnoreCase)
        || string.Equals(stopReason, Length, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A short human-facing explanation of a truncated turn, naming the setting that caused it and what
    /// to do about it — so whoever sees it can act without first having to learn the pipeline.
    /// </summary>
    public static string DescribeTruncation(int maxOutputTokens) =>
        $"The model was cut off at the {maxOutputTokens:N0}-token output ceiling (MaxOutputTokens) — "
        + "what it was saying is incomplete, and anything it had not yet written (including a reply "
        + "it intended to send) was lost. Raise MaxOutputTokens for this peer if this recurs.";
}
