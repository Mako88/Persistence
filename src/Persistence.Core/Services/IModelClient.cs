namespace Persistence.Services;

/// <summary>
/// Sends a prompt to the model and returns the raw completion text
/// </summary>
public interface IModelClient
{
    /// <summary>
    /// Sends the prompt to the model and returns the raw completion text. When
    /// <paramref name="systemPrompt"/> is provided, clients that support a separate
    /// system channel should use it; otherwise it is prepended to
    /// <paramref name="prompt"/>.
    /// </summary>
    Task<string> CompleteAsync(string prompt, string? systemPrompt = null, CancellationToken ct = default);
}
