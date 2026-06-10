namespace Persistence.Config;

/// <summary>
/// A named bundle of the provider/model-coupled settings. <see cref="AppConfig"/> holds a list of
/// these and activates one via <see cref="AppConfig.SelectedModel"/>, so several models (e.g. a
/// cloud model and a local llama.cpp server) can be configured side by side and switched between.
/// The shared, non-model settings (database path, UI mode, proposal approval, etc.) stay on
/// <see cref="AppConfig"/> — memory and behaviour are the same whichever model is driving.
/// </summary>
public class ModelProfile
{
    /// <summary>
    /// Default directory for continuity stores when a profile's <see cref="DatabasePath"/> is a bare
    /// filename (or unset). Keeps each model's database tidily under one folder.
    /// </summary>
    public const string DefaultDatabaseDirectory = "dbs";

    /// <summary>
    /// Friendly name used to select this profile (see <see cref="AppConfig.SelectedModel"/>).
    /// </summary>
    public string Name { get; set; } = "default";

    /// <summary>
    /// Path to this model's SQLite continuity store, so each model keeps its own memory. A bare
    /// filename (no directory) is resolved under <see cref="DefaultDatabaseDirectory"/>; a path with
    /// a directory (relative or absolute) is used as-is. When null/blank, defaults to
    /// <c>{DefaultDatabaseDirectory}/{Name}.db</c>. See <see cref="ResolveDatabasePath"/>.
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// The effective database path for this profile: an explicit <see cref="DatabasePath"/> (bare
    /// filenames placed under <paramref name="baseDirectory"/>), or <c>{baseDirectory}/{Name}.db</c>
    /// when none is set. A <see cref="DatabasePath"/> that already carries a directory (relative or
    /// absolute) wins as-is — the base directory only applies to bare filenames.
    /// </summary>
    /// <param name="baseDirectory">Folder for bare-filename stores; blank falls back to
    /// <see cref="DefaultDatabaseDirectory"/>. Pass <see cref="AppConfig.DatabaseDirectory"/>.</param>
    public string ResolveDatabasePath(string baseDirectory = DefaultDatabaseDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(baseDirectory) ? DefaultDatabaseDirectory : baseDirectory.Trim();
        var raw = string.IsNullOrWhiteSpace(DatabasePath) ? $"{Name}.db" : DatabasePath.Trim();
        return string.IsNullOrEmpty(Path.GetDirectoryName(raw))
            ? Path.Combine(dir, raw)
            : raw;
    }

    /// <summary>
    /// API provider that determines the request/response shape (e.g. "OpenAI", "OpenAiChat",
    /// "LocalClaude", "local").
    /// </summary>
    public string Provider { get; set; } = "local";

    /// <summary>
    /// Model identifier sent to the provider API. (Local servers like llama.cpp ignore it.)
    /// </summary>
    public string Model { get; set; } = "local";

    /// <summary>
    /// API key for the provider. Blank is fine for local servers that don't authenticate.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Base URL override for the provider API — e.g. a local llama.cpp server's
    /// <c>http://127.0.0.1:8080/v1</c> endpoint. Null uses the provider's default.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Maximum input tokens available for the prompt — the context budget surfaced to the peer.
    /// </summary>
    public int MaxInputTokens { get; set; } = 8000;

    /// <summary>
    /// Maximum tokens the model may generate per completion.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 32000;

    /// <summary>
    /// Reasoning effort for reasoning-capable models ("minimal", "low", "medium", "high").
    /// </summary>
    public string ReasoningEffort { get; set; } = "high";

    /// <summary>
    /// When true, responses are streamed incrementally (live reasoning/output) rather than
    /// awaited as a single completion.
    /// </summary>
    public bool Streaming { get; set; } = true;

    /// <summary>
    /// HTTP request timeout (seconds) for model calls; -1 disables it. Generous by default
    /// because slow local models can spend a long time ingesting a large prompt before responding.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 600;
}
