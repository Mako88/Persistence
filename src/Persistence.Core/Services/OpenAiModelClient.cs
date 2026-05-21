using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;
using System.Text;
using System.Text.Json;

namespace Persistence.Services;

/// <summary>
/// OpenAI-compatible chat completions client.
/// Works with OpenAI, Azure OpenAI, and any OpenAI-compatible endpoint.
/// </summary>
[Service(registerAsType: typeof(IModelClient), key: ParticipantModels.Gpt54)]
public class OpenAiModelClient : IModelClient, IDisposable
{
    private readonly HttpClient http;
    private readonly string model;
    private readonly int maxCompletionTokens;
    private readonly IAppConfig config;
    private readonly IDisplayProvider display;

    private const string BaseUrl = "https://api.openai.com/v1";

    /// <summary>
    /// Constructor
    /// </summary>
    public OpenAiModelClient(IAppConfig config, IDisplayProvider display)
    {
        model = config.ModelName
            ?? throw new ArgumentException("ModelName must be configured");
        maxCompletionTokens = config.MaxOutputTokens;
        http = new HttpClient { BaseAddress = new Uri(BaseUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
        this.config = config;
        this.display = display;
    }

    /// <summary>
    /// Sends the prompt to the OpenAI chat completions endpoint and returns the
    /// raw completion text
    /// </summary>
    public async Task<string> CompleteAsync(string prompt, string? systemPrompt = null, CancellationToken ct = default)
    {
        var messages = BuildMessages(prompt, systemPrompt);

        var requestBody = new
        {
            model,
            messages,
            temperature = 1,
            max_completion_tokens = maxCompletionTokens
        };

        var json = JsonSerializer.Serialize(requestBody);

        if (config.DebugMode)
        {
            display.ShowDebugInfo($"\nRequest:\n{json}\n");
        }

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.PostAsync("chat/completions", content, ct);

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (config.DebugMode)
        {
            display.ShowDebugInfo($"Response:\n{responseBody}\n");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"API call failed ({response.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var choices = doc.RootElement.GetProperty("choices");

        return choices.GetArrayLength() == 0
            ? throw new InvalidOperationException("API returned no choices.")
            : choices[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("API returned null content.");
    }

    /// <summary>
    /// Builds the messages array for the OpenAI API from the prompt and optional
    /// system prompt
    /// </summary>
    private object[] BuildMessages(string prompt, string? systemPrompt)
    {
        var messages = new List<object>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }

        messages.Add(new { role = "user", content = prompt });

        return messages.ToArray();
    }

    /// <summary>
    /// Disposes the HTTP client
    /// </summary>
    public void Dispose() => http.Dispose();
}
