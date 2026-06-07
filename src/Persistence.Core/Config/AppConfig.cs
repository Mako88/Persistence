using System.Text.Json;

namespace Persistence.Config;

/// <summary>
/// Global app configuration loaded from appsettings.json
/// </summary>
public class AppConfig : IAppConfig
{
    public string DatabasePath { get; set; } = "continuity.db";
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

    /// <summary>
    /// Loads the config from the given filepath, falling back to defaults on
    /// missing file or parse error
    /// </summary>
    public static async Task<IAppConfig> LoadAsync(string path = "appsettings.json")
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
}

/// <summary>
/// Supported front-ends. Selects which <c>IDisplayProvider</c> implementation
/// renders the session and accepts input.
/// </summary>
public enum UiMode
{
    Console = 0,
    Tui = 1,
    Api = 2,
}
