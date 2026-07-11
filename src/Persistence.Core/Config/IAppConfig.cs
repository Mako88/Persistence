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
    /// Path to the SQLite database file (resolved per active model under <see cref="DatabaseDirectory"/>)
    /// </summary>
    string DatabasePath { get; set; }

    /// <summary>
    /// Base folder for model stores addressed by a bare filename; point at an absolute path (e.g. the
    /// repo root) to make the store location independent of the process working directory.
    /// </summary>
    string DatabaseDirectory { get; set; }

    /// <summary>
    /// Folder of per-peer seed files. Each file is named for the database it seeds
    /// (<c>{name}.json</c> seeds <c>{DatabaseDirectory}/{name}.db</c>) and holds an array of identity
    /// fragments authored into a brand-new store. Blank (the default) resolves to a <c>seeds</c> folder
    /// alongside <see cref="DatabaseDirectory"/>. Overridable via <c>PERSISTENCE_SEEDSDIRECTORY</c>.
    /// </summary>
    string SeedsDirectory { get; set; }

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
    /// Who may accept a proposal (a self-change, including to protected fragments):
    /// "Self" (remote peer, after a deliberation gap), "Participant" (local peer only),
    /// or "Both".
    /// </summary>
    string ProposalApproval { get; set; }

    /// <summary>
    /// When true, displays raw prompts and model responses in the console
    /// </summary>
    bool DebugMode { get; set; }

    /// <summary>
    /// Maximum number of action iterations per turn before the loop is forcibly ended
    /// </summary>
    int MaxActionIterations { get; set; }

    /// <summary>
    /// How many recent raw fragments (conversation + tool results) to keep in the active context
    /// before archiving older ones (kept in the store, searchable/restorable). 0 disables it.
    /// </summary>
    int RawContextWindow { get; set; }

    /// <summary>
    /// How many of the peer's most recent thoughts (<c>&lt;think&gt;</c> blocks) to keep in the
    /// active context before archiving older ones (kept in the store, searchable/restorable). Gives
    /// the peer episodic recall of its recent reasoning across turns without the context ballooning.
    /// 0 keeps every thought (no thought archival).
    /// </summary>
    int ThoughtContextWindow { get; set; }

    /// <summary>
    /// Whether the compact command list is appended to the end of each turn by default. The peer can
    /// toggle it per session via <c>toggle_command_list</c>; full schemas remain available via <c>list()</c>.
    /// </summary>
    bool SurfaceCommands { get; set; }

    /// <summary>
    /// HTTP request timeout (seconds) for model calls; -1 disables it. Generous by default because
    /// slow local models can spend a long time just ingesting a large prompt before responding.
    /// </summary>
    int RequestTimeoutSeconds { get; set; }

    /// <summary>
    /// The peer's sandboxed "computer" reached via the <c>shell</c> command (off by default).
    /// </summary>
    ContainerSettings Container { get; set; }

    /// <summary>
    /// The active local peer's name (default; an <c>X-Local-Peer</c> header overrides it per API request).
    /// </summary>
    string SelectedLocalPeer { get; set; }

    /// <summary>
    /// Optional descriptions for known local peers, surfaced to the remote peer.
    /// </summary>
    List<LocalPeerProfile> LocalPeers { get; set; }
}
