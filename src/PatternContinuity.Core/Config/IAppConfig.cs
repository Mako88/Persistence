namespace Persistence.Config
{
    /// <summary>
    /// Global app configuration
    /// </summary>
    public interface IAppConfig
    {
        string? ActivePersonId { get; set; }
        string ApiBaseUrl { get; set; }
        string ApiKey { get; set; }
        string ApiProvider { get; set; }
        string DatabasePath { get; set; }
        int MaxArchiveSnippets { get; set; }
        int MaxCompletionTokens { get; set; }
        int MaxCurrentConcerns { get; set; }
        int MaxRecentMessages { get; set; }
        int MaxRelationalEntries { get; set; }
        int MaxTokenBudget { get; set; }
        string ModelName { get; set; }
        int ReflectionFrequency { get; set; }
        bool StrictParseMode { get; set; }
        bool DebugMode { get; set; }
    }
}