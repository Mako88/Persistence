using System.Net.Http.Json;
using System.Text.Json;

namespace Persistence.Api.Tests;

/// <summary>
/// Tests the Server-Sent Events stream: live push of conversation events, backlog replay,
/// resume via `since`, and camelCase JSON payloads (for browser clients).
/// </summary>
public class StreamingTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture api;

    public StreamingTests(ApiTestFixture api) => this.api = api;

    private sealed record SseEvent(long Seq, string Kind, string Text, string? Detail);

    /// <summary>
    /// Reads SSE frames from the stream until <paramref name="count"/> data events arrive or the
    /// timeout elapses. Parses the `data:` JSON of each frame.
    /// </summary>
    private static async Task<List<SseEvent>> ReadEventsAsync(
        Stream stream, int count, TimeSpan timeout, CancellationToken ct, List<string>? rawJson = null)
    {
        using var reader = new StreamReader(stream);
        var events = new List<SseEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (events.Count < count && await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.StartsWith("data:"))
                {
                    var json = line["data:".Length..].Trim();
                    var e = JsonSerializer.Deserialize<SseEvent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (e is not null)
                    {
                        events.Add(e);
                        rawJson?.Add(json);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting for more events — return what we have.
        }

        return events;
    }

    // NOTE on harness limits: ASP.NET's in-memory TestServer does not return response headers
    // mid-stream for an open-ended SSE response (HttpCompletionOption.ResponseHeadersRead blocks
    // until the body completes), so a true "open stream, then push live" test would hang. Live
    // push is verified manually via `curl -N` against a real Kestrel host. These tests cover what
    // TestServer can: the backlog the stream emits up front, its SSE framing, camelCase payloads,
    // ordering, dedup, and `since` resume — by reading until the backlog is drained then timing out.

    [Fact]
    public async Task Stream_ReplaysBacklog_WithSseFramingAndCamelCase()
    {
        await api.DriveTurnAsync(
            "history",
            "<think>noting</think><respond>noted</respond><continue>false</continue>");

        var client = api.CreateClient();
        using var streamResp = await client.GetAsync(
            "/api/conversation/stream?since=0", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", streamResp.Content.Headers.ContentType?.MediaType);

        var stream = await streamResp.Content.ReadAsStreamAsync();
        var raw = new List<string>();
        var events = await ReadEventsAsync(stream, count: 1000, TimeSpan.FromSeconds(4), CancellationToken.None, raw);

        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.Kind == "thought" && e.Text == "noting");
        Assert.Contains(events, e => e.Kind == "reply" && e.Text == "noted");

        // Sequences strictly increasing and unique — no duplicates across the replay boundary.
        var seqs = events.Select(e => e.Seq).ToList();
        Assert.Equal(seqs.OrderBy(s => s).Distinct().ToList(), seqs);

        // Browser clients depend on camelCase keys. The Web deserializer above is case-insensitive,
        // so it can't prove casing — assert against the raw `data:` JSON directly: camelCase keys
        // present, PascalCase absent.
        var payload = raw[0];
        Assert.Contains("\"seq\":", payload);
        Assert.Contains("\"kind\":", payload);
        Assert.Contains("\"text\":", payload);
        Assert.DoesNotContain("\"Seq\":", payload);
        Assert.DoesNotContain("\"Kind\":", payload);
        Assert.DoesNotContain("\"Text\":", payload);
    }

    [Fact]
    public async Task Stream_ResumeWithSince_OmitsEarlierEvents()
    {
        await api.DriveTurnAsync("first", "<respond>one</respond><continue>false</continue>");

        var client = api.CreateClient();
        var since = (await client.GetFromJsonAsync<ApiTestFixture.EventsDto>(
            "/api/conversation/events?since=0",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.Latest;

        await api.DriveTurnAsync("second", "<respond>two</respond><continue>false</continue>");

        using var streamResp = await client.GetAsync(
            $"/api/conversation/stream?since={since}", HttpCompletionOption.ResponseHeadersRead);
        var stream = await streamResp.Content.ReadAsStreamAsync();
        var events = await ReadEventsAsync(stream, count: 1000, TimeSpan.FromSeconds(4), CancellationToken.None);

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.True(e.Seq > since));
        Assert.Contains(events, e => e.Text == "two");
        Assert.DoesNotContain(events, e => e.Text == "one");
    }
}
