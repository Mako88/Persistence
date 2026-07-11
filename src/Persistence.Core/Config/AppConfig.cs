using System.Text.Json;
using System.Text.Json.Serialization;

namespace Persistence.Config;

/// <summary>
/// Global app configuration loaded from <c>persistence.json</c>.
///
/// Model/provider-coupled settings live in named <see cref="ModelProfile"/> entries in
/// <see cref="Models"/>; <see cref="SelectedModel"/> picks the active one. The model-coupled
/// <see cref="IAppConfig"/> properties (Provider, Model, ApiKey, token limits, …) delegate to that
/// active profile, so consumers read them exactly as before. <see cref="DatabasePath"/> is part of
/// that model-coupled set — each model keeps its own continuity store. The remaining settings here
/// (UI mode, proposal approval, debug, iteration cap) are shared across models.
///
/// The older flat shape (Provider/Model/… at the top level) is still accepted: a config with no
/// <see cref="Models"/> is migrated into a single profile on load.
/// </summary>
public class AppConfig : IAppConfig
{
    // --- Shared (non-model) settings ---

    /// <summary>
    /// Base folder for model stores whose <see cref="ModelProfile.DatabasePath"/> is a bare filename
    /// (or unset). Defaults to <c>dbs</c>; point it at an absolute path (e.g. the repo root) so the
    /// store resolves the same regardless of the process working directory.
    /// </summary>
    public string DatabaseDirectory { get; set; } = ModelProfile.DefaultDatabaseDirectory;

    /// <summary>
    /// Folder of per-peer seed files (<c>{dbName}.json</c> → seeds a new <c>{dbName}.db</c>). Blank
    /// resolves to a <c>seeds</c> folder alongside <see cref="DatabaseDirectory"/> (see
    /// <c>PeerSeeder</c>). Overridable via <c>PERSISTENCE_SEEDSDIRECTORY</c>.
    /// </summary>
    public string SeedsDirectory { get; set; } = "";

    public string UiMode { get; set; } = "Tui";
    public string ProposalApproval { get; set; } = "Self";

    /// <summary>
    /// Whether the compact command list is appended to the end of each turn by default (the peer can
    /// toggle it per session via <c>toggle_command_list</c>). On by default so the peer always knows
    /// what it can do.
    /// </summary>
    public bool SurfaceCommands { get; set; } = true;
    public bool DebugMode { get; set; } = false;
    public int MaxActionIterations { get; set; } = 5;

    /// <summary>
    /// How many recent "raw" fragments (conversation messages + command/tool results) to keep in the
    /// active context. Older ones are archived out (kept in the store, searchable and restorable) to
    /// keep the context lean and turns fast. The peer's authored fragments are never auto-archived.
    /// 0 disables auto-archival.
    /// </summary>
    public int RawContextWindow { get; set; } = 30;

    /// <summary>
    /// How many of the peer's most recent thoughts (<c>&lt;think&gt;</c> blocks) to keep in the active
    /// context before archiving older ones (kept in the store, searchable/restorable). Gives the peer
    /// episodic recall of its recent reasoning across turns. 0 keeps every thought (no thought archival).
    /// </summary>
    public int ThoughtContextWindow { get; set; } = 8;

    /// <summary>
    /// The peer's sandboxed "computer" reached via the <c>shell</c> command. Off by default; see
    /// <see cref="ContainerSettings"/>. Nested values are overridable via <c>PERSISTENCE_CONTAINER_*</c>.
    /// </summary>
    public ContainerSettings Container { get; set; } = new();

    /// <summary>
    /// The active local peer's name — who the remote peer is talking with by default. The Console uses
    /// this; an API caller can override it per request with an <c>X-Local-Peer</c> header.
    /// Defaults to "Local Peer" (back-compat). Overridable via <c>PERSISTENCE_SELECTEDLOCALPEER</c>.
    /// </summary>
    public string SelectedLocalPeer { get; set; } = "Local Peer";

    /// <summary>
    /// Optional descriptions for known local peers, surfaced to the remote peer. Names not listed are
    /// still accepted (just without a description).
    /// </summary>
    public List<LocalPeerProfile> LocalPeers { get; set; } = [];

    // --- Model selection ---

    /// <summary>
    /// The <see cref="ModelProfile.Name"/> of the active profile in <see cref="Models"/>.
    /// Null/blank or unmatched selects the first profile.
    /// </summary>
    public string? SelectedModel { get; set; }

    /// <summary>
    /// The configured model profiles. Each bundles a provider/model and its coupled settings;
    /// switch which one is live via <see cref="SelectedModel"/>.
    /// </summary>
    public List<ModelProfile> Models { get; set; } = [];

    /// <summary>
    /// The resolved active profile that the flat <see cref="IAppConfig"/> model properties read
    /// from. Always non-null after <see cref="ResolveActiveModel"/> (run during load).
    /// </summary>
    [JsonIgnore]
    public ModelProfile ActiveModel { get; private set; } = new();

    // --- Model-coupled IAppConfig properties: delegate to the active profile. ---
    // [JsonIgnore] so they neither serialize (no duplication with Models) nor deserialize from the
    // new shape; legacy flat configs are migrated separately in LoadFromFileAsync.

    [JsonIgnore] public string DatabasePath { get => ActiveModel.ResolveDatabasePath(DatabaseDirectory); set => ActiveModel.DatabasePath = value; }
    [JsonIgnore] public string Provider { get => ActiveModel.Provider; set => ActiveModel.Provider = value; }
    [JsonIgnore] public string Model { get => ActiveModel.Model; set => ActiveModel.Model = value; }
    [JsonIgnore] public string ApiKey { get => ActiveModel.ApiKey; set => ActiveModel.ApiKey = value; }
    [JsonIgnore] public string? ApiBaseUrl { get => ActiveModel.ApiBaseUrl; set => ActiveModel.ApiBaseUrl = value; }
    [JsonIgnore] public int MaxInputTokens { get => ActiveModel.MaxInputTokens; set => ActiveModel.MaxInputTokens = value; }
    [JsonIgnore] public int MaxOutputTokens { get => ActiveModel.MaxOutputTokens; set => ActiveModel.MaxOutputTokens = value; }
    [JsonIgnore] public string ReasoningEffort { get => ActiveModel.ReasoningEffort; set => ActiveModel.ReasoningEffort = value; }
    [JsonIgnore] public bool Streaming { get => ActiveModel.Streaming; set => ActiveModel.Streaming = value; }
    [JsonIgnore] public int RequestTimeoutSeconds { get => ActiveModel.RequestTimeoutSeconds; set => ActiveModel.RequestTimeoutSeconds = value; }

    /// <summary>
    /// The shared <see cref="ContainerSettings.Name"/> as originally configured, captured before any
    /// per-profile <see cref="ModelProfile.ContainerName"/> override is applied. Lets a profile
    /// without an override fall back to this base rather than inheriting the previous profile's box
    /// across a re-resolve (e.g. a <c>PERSISTENCE_SELECTEDMODEL</c> switch).
    /// </summary>
    private string? baseContainerName;

    /// <summary>The shared <see cref="ContainerSettings.AllowAllCommands"/> as originally configured,
    /// captured before any per-profile <see cref="ModelProfile.ContainerAllowAll"/> override — same
    /// base-capture role as <see cref="baseContainerName"/>.</summary>
    private bool? baseContainerAllowAll;

    /// <summary>
    /// Resolves <see cref="ActiveModel"/> from <see cref="SelectedModel"/> (case-insensitive name
    /// match), falling back to the first profile. Ensures at least one profile exists, syncs
    /// <see cref="SelectedModel"/> to the resolved profile's name, and points the shared container at
    /// the active profile's <see cref="ModelProfile.ContainerName"/> when it sets one. Idempotent.
    /// </summary>
    public AppConfig ResolveActiveModel()
    {
        if (Models.Count == 0)
        {
            Models.Add(new ModelProfile());
        }

        ActiveModel = Models.FirstOrDefault(m =>
            string.Equals(m.Name, SelectedModel, StringComparison.OrdinalIgnoreCase)) ?? Models[0];

        SelectedModel = ActiveModel.Name;

        // Bind the active peer to its own computer, if it names one. Capture the configured base once
        // so a profile with no override resolves back to it (an explicit PERSISTENCE_CONTAINER_NAME
        // still wins — it's applied later, in ApplyContainerEnvironmentOverrides).
        baseContainerName ??= Container.Name;
        Container.Name = string.IsNullOrWhiteSpace(ActiveModel.ContainerName)
            ? baseContainerName
            : ActiveModel.ContainerName.Trim();

        baseContainerAllowAll ??= Container.AllowAllCommands;
        Container.AllowAllCommands = ActiveModel.ContainerAllowAll ?? baseContainerAllowAll.Value;

        return this;
    }

    /// <summary>
    /// Loads the config from the given filepath, falling back to defaults on missing file or
    /// parse error, then applies environment-variable overrides. Any setting can be overridden by
    /// a <c>PERSISTENCE_&lt;PROPERTY&gt;</c> env var (e.g. <c>PERSISTENCE_PROVIDER</c>,
    /// <c>PERSISTENCE_DATABASEPATH</c>, <c>PERSISTENCE_MAXINPUTTOKENS</c>) — case-insensitive,
    /// matching the property name. Model-coupled overrides apply to the active profile;
    /// <c>PERSISTENCE_SELECTEDMODEL</c> switches which profile is active first. Useful for tests,
    /// deploys, and ops without editing the file.
    /// </summary>
    public static async Task<IAppConfig> LoadAsync(string? path = null)
    {
        var config = await LoadFromFileAsync(path ?? ResolveConfigPath());
        ApplyEnvironmentOverrides(config);
        return config;
    }

    /// <summary>
    /// Finds the single shared <c>persistence.json</c> config. Both entry points (Console, API)
    /// read the same file — named distinctly so it never collides with ASP.NET's own
    /// <c>appsettings.json</c>. Searched for in the current directory first, then walking up from
    /// the app's base directory, so the one solution-root config is found whether launched via
    /// <c>dotnet run</c> (cwd = project dir) or as a published exe (cwd = output dir). Falls back
    /// to defaults if none is found.
    /// </summary>
    private static string ResolveConfigPath()
    {
        const string fileName = "persistence.json";

        if (File.Exists(fileName))
        {
            return fileName;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return fileName;
    }

    private const string EnvPrefix = "PERSISTENCE_";

    /// <summary>
    /// Overrides config properties from <c>PERSISTENCE_*</c> environment variables. Matches each
    /// public settable property by name (case-insensitive); silently skips unknown vars and values
    /// that can't be converted to the property's type.
    /// </summary>
    private static void ApplyEnvironmentOverrides(AppConfig config)
    {
        // Switch the active profile first, so any model-coupled overrides below land on it.
        var selected = Environment.GetEnvironmentVariable(EnvPrefix + nameof(SelectedModel));
        if (!string.IsNullOrEmpty(selected))
        {
            config.SelectedModel = selected;
            config.ResolveActiveModel();
        }

        var properties = typeof(AppConfig)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (!key.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = key[EnvPrefix.Length..];
            if (!properties.TryGetValue(name, out var prop))
            {
                continue;
            }

            var raw = entry.Value?.ToString();
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            try
            {
                var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                prop.SetValue(config, Convert.ChangeType(raw, targetType, System.Globalization.CultureInfo.InvariantCulture));
            }
            catch
            {
                // Unconvertible value — leave the file/default value in place.
            }
        }

        ApplyContainerEnvironmentOverrides(config);
    }

    /// <summary>
    /// Overrides the nested <see cref="ContainerSettings"/> ops knobs from <c>PERSISTENCE_CONTAINER_*</c>
    /// env vars. The generic reflection loop above only handles scalar props directly on
    /// <see cref="AppConfig"/>, so the high-value container settings are wired explicitly here
    /// (the allowlist stays file-configured — an array doesn't map cleanly to a single env value).
    /// </summary>
    private static void ApplyContainerEnvironmentOverrides(AppConfig config)
    {
        string? Env(string suffix) =>
            Environment.GetEnvironmentVariable($"{EnvPrefix}CONTAINER_{suffix}");

        if (Env("ENABLED") is { } enabled && bool.TryParse(enabled, out var en)) config.Container.Enabled = en;
        if (Env("ALLOWALLCOMMANDS") is { } all && bool.TryParse(all, out var aa)) config.Container.AllowAllCommands = aa;
        if (Env("NAME") is { Length: > 0 } name) config.Container.Name = name;
        if (Env("DOCKERHOST") is { Length: > 0 } host) config.Container.DockerHost = host;
        if (Env("WORKINGDIR") is { Length: > 0 } dir) config.Container.WorkingDir = dir;
        if (Env("TIMEOUTSECONDS") is { } t && int.TryParse(t, out var ts)) config.Container.TimeoutSeconds = ts;
        if (Env("MAXOUTPUTBYTES") is { } m && int.TryParse(m, out var mb)) config.Container.MaxOutputBytes = mb;
    }

    private static async Task<AppConfig> LoadFromFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new AppConfig().ResolveActiveModel();
        }

        var json = await File.ReadAllTextAsync(path);

        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var config = JsonSerializer.Deserialize<AppConfig>(json, opts) ?? new AppConfig();

            // Backward-compat: an older flat config has no Models array — migrate its top-level
            // model fields (Provider/Model/ApiKey/token limits/…) into a single profile.
            if (config.Models.Count == 0)
            {
                config.Models = [JsonSerializer.Deserialize<ModelProfile>(json, opts) ?? new ModelProfile()];
            }

            return config.ResolveActiveModel();
        }
        catch
        {
            return new AppConfig().ResolveActiveModel();
        }
    }
}

/// <summary>
/// Supported API providers. Each provider defines an API shape — the model client
/// uses this to know how to structure requests and parse responses.
/// </summary>
public enum ModelProvider
{
    Local = 0,
    OpenAI = 1,

    /// <summary>
    /// The remote peer is an external agent (e.g. Claude) supplying completions out-of-band
    /// via the API, rather than an HTTP model endpoint. See <c>LocalClaudeModelClient</c>.
    /// </summary>
    LocalClaude = 2,

    /// <summary>
    /// OpenAI-compatible Chat Completions API (<c>/chat/completions</c>) — for local servers like
    /// llama.cpp/Ollama/LM Studio/vLLM (point <c>ApiBaseUrl</c> at them), or OpenAI's chat endpoint.
    /// See <c>OpenAiChatModelClient</c>.
    /// </summary>
    OpenAiChat = 3,

    /// <summary>
    /// The Anthropic Claude Messages API (<c>/v1/messages</c>) — talks directly to
    /// <c>api.anthropic.com</c> (or a compatible endpoint via <c>ApiBaseUrl</c>). See
    /// <c>AnthropicModelClient</c>.
    /// </summary>
    Anthropic = 4,
}

/// <summary>
/// Supported front-ends. Selects which <c>IDisplayProvider</c> implementation
/// renders the session and accepts input.
/// </summary>
public enum UiMode
{
    Tui = 1,
    Api = 2,

    /// <summary>
    /// No front-end — a no-op display for headless runs (e.g. the scheduled wake-runner that fires
    /// due events and exits). See <c>HeadlessDisplayProvider</c>.
    /// </summary>
    Headless = 3,
}

/// <summary>
/// Who may accept a proposal (a change to the peer's own memory, including protected fragments).
/// </summary>
public enum ProposalApproval
{
    /// <summary>The remote peer accepts its own proposals (after a deliberation gap — it can't
    /// accept one in the same turn it proposed it).</summary>
    Self = 0,

    /// <summary>Only the local peer accepts, via their own controls; the remote peer can propose
    /// and reject but not accept.</summary>
    Participant = 1,

    /// <summary>Either the remote peer (with the deliberation gap) or the local peer may accept.</summary>
    Both = 2,
}
