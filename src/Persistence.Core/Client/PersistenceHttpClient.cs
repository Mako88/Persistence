using Persistence.Contracts;
using SimpleHttpClient;
using SimpleHttpClient.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Persistence.Client;

/// <summary>
/// <see cref="IPersistenceClient"/> over HTTP, built on <see cref="SimpleClient"/> (the same transport
/// the model clients use). The client identifies as one local peer via an <c>X-Local-Peer</c> default
/// header, so every request it makes speaks for that peer.
/// </summary>
public class PersistenceHttpClient : IPersistenceClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ISimpleClient client;

    /// <summary>
    /// Builds a client against the API at <paramref name="baseUrl"/>, identifying as
    /// <paramref name="localPeer"/> (e.g. "John") on every request.
    /// </summary>
    public PersistenceHttpClient(string baseUrl, string? localPeer = null)
        : this(CreateClient(baseUrl, localPeer))
    {
    }

    /// <summary>
    /// Test seam: accepts a pre-configured <see cref="ISimpleClient"/> (e.g. a fake) instead of
    /// constructing one from a base URL.
    /// </summary>
    internal PersistenceHttpClient(ISimpleClient client) => this.client = client;

    private static ISimpleClient CreateClient(string baseUrl, string? localPeer)
    {
        var client = new SimpleClient(baseUrl.TrimEnd('/'));

        if (!string.IsNullOrWhiteSpace(localPeer))
        {
            client.DefaultHeaders["X-Local-Peer"] = localPeer;
        }

        // The event stream is a long-lived SSE connection; the default request timeout would cut it (and
        // force a reconnect) every ~30s. Use a week — long enough that reconnect only fires on genuine
        // drops, but safely under the ~24-day cap where the underlying millisecond delay overflows.
        // (send/snapshot return near-instantly, so the large ceiling doesn't affect them.)
        client.Timeout = 7 * 24 * 60 * 60;

        return client;
    }

    /// <inheritdoc />
    public async Task SendAsync(string input, CancellationToken ct = default)
    {
        var response = await client.MakeRequest(
            new SimpleRequest("/api/conversation/send", HttpMethod.Post, new { input }));

        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException($"Send failed ({response.StatusCode}): {response.StringBody}");
        }
    }

    /// <inheritdoc />
    public async Task<ConversationSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var response = await client.MakeRequest(
            new SimpleRequest("/api/conversation/snapshot", HttpMethod.Get));

        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException($"Snapshot failed ({response.StatusCode}): {response.StringBody}");
        }

        return JsonSerializer.Deserialize<ConversationSnapshot>(response.StringBody, JsonOpts)
            ?? throw new InvalidOperationException("The snapshot response was empty.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ConversationEvent> StreamAsync(
        long since = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var response = await client.MakeStreamRequest(
            new SimpleRequest($"/api/conversation/stream?since={since}", HttpMethod.Get), ct);

        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException($"Stream failed ({response.StatusCode}).");
        }

        // SimpleHttpClient owns the SSE wire-format parsing; each event's Data line is the full
        // ConversationEvent JSON the server serialized.
        await foreach (var sse in response.ReadServerSentEventsAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(sse.Data))
            {
                continue;
            }

            var evt = JsonSerializer.Deserialize<ConversationEvent>(sse.Data, JsonOpts);
            if (evt is not null)
            {
                yield return evt;
            }
        }
    }
}
