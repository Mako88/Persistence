using System.Text.Json;

namespace PatternContinuity.Config;

public class AppConfig
{
    public string DatabasePath { get; set; } = "continuity.db";
    public string ApiProvider { get; set; } = "openai";
    public string ApiKey { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ModelName { get; set; } = "gpt-4o";
    public int ReflectionFrequency { get; set; } = 1;
    public int MaxCurrentConcerns { get; set; } = 5;
    public int MaxRelationalEntries { get; set; } = 3;
    public int MaxRecentMessages { get; set; } = 8;
    public int MaxArchiveSnippets { get; set; } = 3;
    public int MaxTokenBudget { get; set; } = 8000;
    public string? ActivePersonId { get; set; } = "john";

    public static AppConfig Load(string path = "appsettings.json")
    {
        if (!File.Exists(path))
            return new AppConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new AppConfig();
    }
}
