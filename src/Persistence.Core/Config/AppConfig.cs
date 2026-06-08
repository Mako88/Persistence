using System.Text.Json;

namespace Persistence.Config;

/// <summary>
/// Global app configuration loaded from appsettings.json
/// </summary>
public class AppConfig : IAppConfig
{
    public string DatabasePath { get; set; } = "dbs/continuity.db";
    public string ApiKey { get; set; } = "";
    public string Provider { get; set; } = "local";
    public string Model { get; set; } = "local";
    public string? ApiBaseUrl { get; set; }
    public int MaxInputTokens { get; set; } = 8000;
    public int MaxOutputTokens { get; set; } = 32000;
    public string ReasoningEffort { get; set; } = "high";
    public bool Streaming { get; set; } = true;
    public string UiMode { get; set; } = "Tui";
    public string ResponseFormat { get; set; } = "Tagged";
    public bool DebugMode { get; set; } = false;
    public int MaxActionIterations { get; set; } = 5;
    public int RequestTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Loads the config from the given filepath, falling back to defaults on missing file or
    /// parse error, then applies environment-variable overrides. Any setting can be overridden by
    /// a <c>PERSISTENCE_&lt;PROPERTY&gt;</c> env var (e.g. <c>PERSISTENCE_PROVIDER</c>,
    /// <c>PERSISTENCE_DATABASEPATH</c>, <c>PERSISTENCE_MAXINPUTTOKENS</c>) — case-insensitive,
    /// matching the property name. Useful for tests, deploys, and ops without editing the file.
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
            return new AppConfig();
        }

        var json = await File.ReadAllTextAsync(path);

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
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
