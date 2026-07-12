using Persistence.Contracts;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Persistence.Client;

/// <summary>
/// <see cref="IPersistenceClient"/> over HTTP. The <see cref="HttpClient"/> is supplied by the caller
/// (its <c>BaseAddress</c> points at the server), so the same client works against a real server or an
/// in-memory test host.
/// </summary>
public class PersistenceHttpClient : IPersistenceClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;

    public PersistenceHttpClient(HttpClient http) => this.http = http;

    /// <inheritdoc />
    public async Task SendAsync(string input, string? localPeer = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversation/send")
        {
            Content = JsonContent.Create(new { input }),
        };

        if (!string.IsNullOrWhiteSpace(localPeer))
        {
            request.Headers.Add("X-Local-Peer", localPeer);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async Task<ConversationSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ConversationSnapshot>("/api/conversation/snapshot", JsonOpts, ct)
        ?? throw new InvalidOperationException("The snapshot response was empty.");

    /// <inheritdoc />
    public async IAsyncEnumerable<ConversationEvent> StreamAsync(
        long since = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/conversation/stream?since={since}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Minimal SSE parse: each event is a block of `id:`/`event:`/`data:` lines ending in a blank
        // line. The server puts the full ConversationEvent JSON on the `data:` line, so that's all we
        // need to reconstruct the event.
        string? dataLine = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break; // stream closed
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLine = line["data:".Length..].TrimStart();
            }
            else if (line.Length == 0 && dataLine is not null)
            {
                var evt = JsonSerializer.Deserialize<ConversationEvent>(dataLine, JsonOpts);
                dataLine = null;

                if (evt is not null)
                {
                    yield return evt;
                }
            }
        }
    }
}
