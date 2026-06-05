namespace Persistence.Services;

/// <summary>
/// API-ready prompt representation produced by an <see cref="IPromptBuilder"/>.
/// Contains the messages array that model clients send directly to the provider API.
/// </summary>
public record PromptRequest
{
    /// <summary>
    /// Ordered list of messages ready for the provider API. Each message has a
    /// provider-specific role and content string.
    /// </summary>
    public required List<PromptMessage> Messages { get; init; }
}

/// <summary>
/// A single message in a provider-ready prompt. The role string is provider-specific
/// (e.g. "system", "user", "assistant" for OpenAI).
/// </summary>
public record PromptMessage(string Role, string Content);
