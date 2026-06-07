namespace Persistence.Config;

/// <summary>
/// Application configuration settings
/// </summary>
public interface IAppConfig
{
    /// <summary>
    /// API key for the model provider
    /// </summary>
    string ApiKey { get; set; }

    /// <summary>
    /// Path to the SQLite database file
    /// </summary>
    string DatabasePath { get; set; }

    /// <summary>
    /// Maximum tokens the model is allowed to generate per completion
    /// </summary>
    int MaxOutputTokens { get; set; }

    /// <summary>
    /// Maximum input tokens available for the prompt (context budget)
    /// </summary>
    int MaxInputTokens { get; set; }

    /// <summary>
    /// API provider that determines the request/response shape (e.g. "OpenAI", "Local")
    /// </summary>
    string Provider { get; set; }

    /// <summary>
    /// Model identifier sent to the provider API. Use "custom" with ApiBaseUrl
    /// to hit a custom endpoint using the selected provider's API shape.
    /// </summary>
    string Model { get; set; }

    /// <summary>
    /// Base URL override for the provider API. When set with Model = "custom",
    /// requests use the selected provider's API shape but target this URL instead.
    /// </summary>
    string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Reasoning effort for reasoning-capable models (e.g. "minimal", "low",
    /// "medium", "high"). Sent to providers that support a reasoning parameter.
    /// </summary>
    string ReasoningEffort { get; set; }

    /// <summary>
    /// When true, model responses are streamed incrementally (live reasoning and
    /// output) instead of awaited as a single completion.
    /// </summary>
    bool Streaming { get; set; }

    /// <summary>
    /// Selects the display provider / front-end (e.g. "Console", "Tui").
    /// </summary>
    string UiMode { get; set; }

    /// <summary>
    /// Selects the remote-peer response wire format ("Json" or "Tagged").
    /// </summary>
    string ResponseFormat { get; set; }

    /// <summary>
    /// When true, displays raw prompts and model responses in the console
    /// </summary>
    bool DebugMode { get; set; }

    /// <summary>
    /// Maximum number of action iterations per turn before the loop is forcibly ended
    /// </summary>
    int MaxActionIterations { get; set; }
}
