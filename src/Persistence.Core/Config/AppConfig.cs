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
    /// Resolves <see cref="ActiveModel"/> from <see cref="SelectedModel"/> (case-insensitive name
    /// match), falling back to the first profile. Ensures at least one profile exists, and syncs
    /// <see cref="SelectedModel"/> to the resolved profile's name. Idempotent.
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
}

/// <summary>
/// Supported front-ends. Selects which <c>IDisplayProvider</c> implementation
/// renders the session and accepts input.
/// </summary>
public enum UiMode
{
    Tui = 1,
    Api = 2,
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
