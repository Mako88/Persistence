using Persistence.Services.Streaming;

namespace Persistence.Services;

/// <summary>
/// The real token usage a provider reported for a completed call. Provider-specific extraction lives
/// in each <see cref="IModelClient"/>; consumers read it uniformly via <see cref="IModelClient.LastUsage"/>.
/// <see cref="InputTokens"/> is the uncached (full-price) input; the cache fields are the prompt-cache
/// portions (read = billed cheap, creation = billed at a write premium) and are 0 for providers without
/// caching.
/// </summary>
public readonly record struct ModelUsage(
    int InputTokens, int OutputTokens, int CacheReadTokens = 0, int CacheCreationTokens = 0);

/// <summary>
/// Sends a structured prompt to the model provider and returns the raw completion text
/// </summary>
public interface IModelClient
{
    /// <summary>
    /// Sends the prompt request to the model and returns the raw completion text
    /// </summary>
    Task<string> CompleteAsync(PromptRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sends the prompt request to the model and streams the response incrementally as
    /// <see cref="ModelStreamEvent"/>s (output-text and reasoning-summary deltas, ending
    /// with <see cref="ModelStreamEventKind.Completed"/>). Concatenating the output-text
    /// deltas yields the same raw completion text that <see cref="CompleteAsync"/> returns.
    /// </summary>
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(PromptRequest request, CancellationToken ct = default);

    /// <summary>
    /// The provider-reported token usage of the most recent call (streaming or not), or null when the
    /// provider reports none (e.g. local/out-of-band clients) or before the first call. Turns are
    /// serialized, so this is unambiguously "the last call's" usage when read right after it returns.
    /// </summary>
    ModelUsage? LastUsage { get; }

    /// <summary>
    /// Why the provider stopped generating on the most recent call — the raw provider string
    /// (Anthropic: <c>end_turn</c>, <c>max_tokens</c>, <c>tool_use</c>, <c>refusal</c>; OpenAI-family:
    /// <c>stop</c>, <c>length</c>). Null when the provider reports none, or before the first call.
    ///
    /// <para>Kept deliberately as the provider's own string rather than normalised to an enum: the
    /// interesting values differ by provider and grow over time, and a value we don't recognise is
    /// still worth showing a human verbatim. The one case the pipeline acts on is "the output was cut
    /// off", which <see cref="ModelStopReason.IsTruncation"/> decides.</para>
    ///
    /// <para>Read right after the call returns; turns are serialized, so it unambiguously belongs to
    /// the call that just finished. Deliberately a separate property rather than a field on
    /// <see cref="ModelUsage"/> — that record is about token counts, and it is being edited
    /// concurrently elsewhere.</para>
    /// </summary>
    string? LastStopReason { get; }
}
