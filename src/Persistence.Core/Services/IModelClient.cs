using Persistence.Services.Streaming;

namespace Persistence.Services;

/// <summary>
/// The real token usage a provider reported for a completed call. Provider-specific extraction lives
/// in each <see cref="IModelClient"/>; consumers read it uniformly via <see cref="IModelClient.LastUsage"/>.
/// </summary>
public readonly record struct ModelUsage(int InputTokens, int OutputTokens);

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
}
