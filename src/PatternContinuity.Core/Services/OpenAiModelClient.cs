using System.Text;
using System.Text.Json;

namespace PatternContinuity.Services;

/// <summary>
/// OpenAI-compatible chat completions client.
/// Works with OpenAI, Azure OpenAI, and any OpenAI-compatible endpoint.
/// </summary>
public class OpenAiModelClient : IModelClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxCompletionTokens;

    public OpenAiModelClient(string apiKey, string baseUrl, string model, int maxCompletionTokens = 8192)
    {
        _model = model;
        _maxCompletionTokens = maxCompletionTokens;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<string> CompleteAsync(List<ChatMessage> messages, CancellationToken ct = default)
    {
        var requestBody = new
        {
            model = _model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            temperature = 0.7,
            max_completion_tokens = _maxCompletionTokens
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("chat/completions", content, ct);

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"API call failed ({response.StatusCode}): {responseBody}");

        using var doc = JsonDocument.Parse(responseBody);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new InvalidOperationException("API returned no choices.");

        return choices[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("API returned null content.");
    }

    public void Dispose() => _http.Dispose();
}
