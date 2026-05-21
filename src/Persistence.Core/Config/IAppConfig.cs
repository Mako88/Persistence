namespace Persistence.Config;

/// <summary>
/// Application configuration settings
/// </summary>
public interface IAppConfig
{
    /// <summary>API key for the model provider</summary>
    string ApiKey { get; set; }

    /// <summary>Path to the SQLite database file</summary>
    string DatabasePath { get; set; }

    /// <summary>Maximum tokens the model is allowed to generate per completion</summary>
    int MaxOutputTokens { get; set; }

    /// <summary>Maximum input tokens available for the prompt (context budget)</summary>
    int MaxInputTokens { get; set; }

    /// <summary>Model provider identifier used to resolve the correct client at startup</summary>
    string ModelProvider { get; set; }

    /// <summary>Display name of the model (also used to resolve the keyed client)</summary>
    string? ModelName { get; set; }

    /// <summary>When true, displays raw prompts and model responses in the console</summary>
    bool DebugMode { get; set; }

    /// <summary>Maximum number of action iterations per turn before the loop is forcibly ended</summary>
    int MaxActionIterations { get; set; }
}
