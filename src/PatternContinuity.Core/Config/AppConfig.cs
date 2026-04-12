using System.Text.Json;

namespace Persistence.Config;

/// <summary>
/// Global app configuration
/// </summary>
public class AppConfig : IAppConfig
{
    public string DatabasePath { get; set; } = "continuity.db";
    public string ApiProvider { get; set; } = "openai";
    public string ApiKey { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ModelName { get; set; } = "gpt-5.4";
    public int ReflectionFrequency { get; set; } = 1;
    public int MaxCurrentConcerns { get; set; } = 5;
    public int MaxRelationalEntries { get; set; } = 3;
    public int MaxRecentMessages { get; set; } = 8;
    public int MaxArchiveSnippets { get; set; } = 3;
    public int MaxTokenBudget { get; set; } = 8000;
    public string? ActivePersonId { get; set; } = "john";
    public int MaxCompletionTokens { get; set; } = 32000;
    public bool StrictParseMode { get; set; } = true;

    /// <summary>
    /// Load the config from the given filepath
    /// </summary>
    public static IAppConfig Load(string path = "appsettings.json")
    {
        // TODO: Log any errors when loading config

        if (!File.Exists(path))
            return new AppConfig();

        var json = File.ReadAllText(path);

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
