using Persistence.Services.Streaming;

namespace Persistence.Services;

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
}
