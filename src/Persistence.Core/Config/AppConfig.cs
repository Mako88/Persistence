using System.ComponentModel;
using System.Text.Json;

namespace Persistence.Config;

/// <summary>
/// Global app configuration loaded from appsettings.json
/// </summary>
public class AppConfig : IAppConfig
{
    public string DatabasePath { get; set; } = "continuity.db";
    public string ApiKey { get; set; } = "";
    public string ModelProvider { get; set; } = "local";
    public string? ModelName { get; set; }
    public int MaxInputTokens { get; set; } = 8000;
    public int MaxOutputTokens { get; set; } = 32000;
    public bool DebugMode { get; set; } = false;
    public int MaxActionIterations { get; set; } = 5;

    /// <summary>
    /// Loads the config from the given filepath, falling back to defaults on
    /// missing file or parse error
    /// </summary>
    public static async Task<IAppConfig> LoadAsync(string path = "appsettings.json")
    {
        // TODO: Log any errors when loading config

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
/// Supported model providers, resolved by <see cref="AppConfig.ModelName"/>
/// </summary>
public enum ParticipantModels
{
    [Description("local")]
    Local = 0,

    [Description("gpt-5.5")]
    Gpt54 = 1,
}
