using Persistence.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using System.Text.Json;

namespace Persistence.Api.Tests;

/// <summary>
/// Boots the real API in-process against an isolated, temporary SQLite database, and drives
/// it the way the live manual testing did: submit local-peer input, fetch the prompt parked
/// for the remote peer, answer it in the tagged format, and read back the conversation events.
///
/// Each fixture instance gets its own database (via the PERSISTENCE_DATABASEPATH override) so tests
/// don't share state.
/// </summary>
public sealed class ApiTestFixture : WebApplicationFactory<Program>
{
    private readonly string dbPath = Path.Combine(
        Path.GetTempPath(), $"persistence-api-test-{Guid.NewGuid():N}.db");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Force the test config via env overrides (these win over any persistence.json the loader
        // finds by walking up to the repo root). Isolated DB + the Claude-as-peer provider so the
        // fixture drives turns itself rather than calling a real model.
        Environment.SetEnvironmentVariable("PERSISTENCE_DATABASEPATH", dbPath);
        Environment.SetEnvironmentVariable("PERSISTENCE_PROVIDER", "LocalClaude");
        Environment.SetEnvironmentVariable("PERSISTENCE_UIMODE", "Api");
        Environment.SetEnvironmentVariable("PERSISTENCE_RESPONSEFORMAT", "Tagged");
        Environment.SetEnvironmentVariable("PERSISTENCE_STREAMING", "false");
        return base.CreateHost(builder);
    }

    /// <summary>
    /// Runs one full turn: submit <paramref name="input"/> as the local peer, wait for the
    /// remote-peer prompt, answer it with <paramref name="peerResponse"/>, and return the
    /// conversation events the turn produced.
    /// </summary>
    public async Task<IReadOnlyList<ConversationEvent>> RunTurnAsync(string input, string peerResponse)
    {
        var client = CreateClient();
        await EnsureReadyAsync(client);

        var sinceBefore = await LatestSeqAsync(client);

        var send = await client.PostAsJsonAsync("/api/conversation/send", new { input });
        send.EnsureSuccessStatusCode();

        var pending = await WaitForPendingAsync(client);
        Assert.NotNull(pending);

        var respond = await client.PostAsJsonAsync("/api/peer/respond",
            new { id = pending!.Id, response = peerResponse });
        respond.EnsureSuccessStatusCode();

        // Wait for the turn to finish applying (rather than betting on a fixed delay), then return
        // everything it produced.
        return await WaitForTurnAsync(client, sinceBefore);
    }

    /// <summary>Submits input without answering, returning the parked remote-peer prompt.</summary>
    public async Task<PendingDto?> SendAndGetPendingAsync(string input)
    {
        var client = CreateClient();
        await EnsureReadyAsync(client);

        var send = await client.PostAsJsonAsync("/api/conversation/send", new { input });
        send.EnsureSuccessStatusCode();
        return await WaitForPendingAsync(client);
    }

    /// <summary>Sends input, waits for the parked prompt, and answers it — without reading events.</summary>
    public async Task DriveTurnAsync(string input, string peerResponse)
    {
        var client = CreateClient();
        await EnsureReadyAsync(client);

        await client.PostAsJsonAsync("/api/conversation/send", new { input });
        var pending = await WaitForPendingAsync(client);
        await client.PostAsJsonAsync("/api/peer/respond", new { id = pending!.Id, response = peerResponse });
    }

    /// <summary>
    /// Runs a full turn with an <c>X-Local-Peer</c> header identifying who's speaking, and returns the
    /// prompt the remote peer saw (so a test can assert the peer was announced). Completes the turn so
    /// the shared fixture is left clean.
    /// </summary>
    public async Task<string> RunTurnAsLocalPeerAsync(string input, string peerResponse, string localPeer)
    {
        var client = CreateClient();
        await EnsureReadyAsync(client);
        var since = await LatestSeqAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversation/send")
        {
            Content = JsonContent.Create(new { input }),
        };
        request.Headers.Add("X-Local-Peer", localPeer);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();

        var pending = await WaitForPendingAsync(client);
        Assert.NotNull(pending);
        var prompt = pending!.Prompt;

        await client.PostAsJsonAsync("/api/peer/respond", new { id = pending.Id, response = peerResponse });
        await WaitForTurnAsync(client, since);
        return prompt;
    }

    /// <summary>Ensures the fixture is initialized (used before opening a stream).</summary>
    public Task EnsureReadyAsync() => EnsureReadyAsync(CreateClient());

    private bool ready;

    /// <summary>
    /// Ensures the orchestrator has finished startup (DB init) and subscribed to input before the
    /// first real turn. Input published before the subscription exists is dropped (the event bus
    /// doesn't replay), so we send a throwaway probe, answer any prompt it parks, and only proceed
    /// once a turn round-trips. Runs once per fixture.
    /// </summary>
    private async Task EnsureReadyAsync(HttpClient client)
    {
        if (ready)
        {
            return;
        }

        for (var attempt = 0; attempt < 30 && !ready; attempt++)
        {
            await client.PostAsJsonAsync("/api/conversation/send", new { input = "__ready_probe__" });

            for (var i = 0; i < 10; i++)
            {
                var resp = await client.GetAsync("/api/peer/pending");
                if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    var pending = await resp.Content.ReadFromJsonAsync<PendingDto>(JsonOpts);
                    var since = await LatestSeqAsync(client);
                    await client.PostAsJsonAsync("/api/peer/respond",
                        new { id = pending!.Id, response = "<respond>ready</respond><continue>false</continue>" });
                    // Wait for the probe turn to finish (its reply lands) so the orchestrator is
                    // settled and subscribed before the first real turn — no fixed-delay guess.
                    await WaitForTurnAsync(client, since);
                    ready = true;
                    break;
                }

                await Task.Delay(200);
            }
        }

        Assert.True(ready, "API did not become ready in time.");
    }

    public async Task<IReadOnlyList<ConversationEvent>> EventsSinceAsync(HttpClient client, long since)
    {
        var dto = await client.GetFromJsonAsync<EventsDto>($"/api/conversation/events?since={since}", JsonOpts);
        return dto?.Events ?? [];
    }

    /// <summary>The latest event sequence number — capture before a turn to scope a later wait.</summary>
    public async Task<long> LatestSeqAsync(HttpClient client)
    {
        var dto = await client.GetFromJsonAsync<EventsDto>("/api/conversation/events?since=0", JsonOpts);
        return dto?.Latest ?? 0;
    }

    /// <summary>
    /// Waits for a turn to finish applying after its response is supplied, then returns the events it
    /// produced. The API exposes no explicit "turn complete" signal, and the orchestrator releases its
    /// turn lock only *after* a couple of post-turn refresh queries — later than the last event it
    /// publishes. So we wait for quiescence: poll until the turn has produced output and the event
    /// stream has then gone quiet across a short settle window, which reliably lands after the lock is
    /// released (so the next turn's input can't race into the release gap). Adaptive and bounded —
    /// strictly better than a fixed delay. For turns that re-prompt (continue=true / unparseable),
    /// poll for the new pending prompt instead of calling this.
    /// </summary>
    public async Task<IReadOnlyList<ConversationEvent>> WaitForTurnAsync(HttpClient client, long since)
    {
        const int settlePolls = 4; // ~100ms of quiet after the last event
        var events = await EventsSinceAsync(client, since);
        var lastCount = events.Count;
        var quiet = 0;

        for (var i = 0; i < 200; i++)
        {
            await Task.Delay(25);
            events = await EventsSinceAsync(client, since);

            if (events.Count > 0 && events.Count == lastCount)
            {
                if (++quiet >= settlePolls)
                {
                    return events;
                }
            }
            else
            {
                quiet = 0;
            }

            lastCount = events.Count;
        }

        return events;
    }

    private static async Task<PendingDto?> WaitForPendingAsync(HttpClient client)
    {
        // Generous timeout: the first turn after a cold host boot must wait for the orchestrator's
        // InitializeAsync (DB migrate + seed) to complete before it processes input.
        for (var i = 0; i < 300; i++)
        {
            var resp = await client.GetAsync("/api/peer/pending");
            if (resp.StatusCode == System.Net.HttpStatusCode.OK)
            {
                return await resp.Content.ReadFromJsonAsync<PendingDto>(JsonOpts);
            }

            await Task.Delay(100);
        }

        return null;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("PERSISTENCE_DATABASEPATH", null);
        Environment.SetEnvironmentVariable("PERSISTENCE_PROVIDER", null);
        Environment.SetEnvironmentVariable("PERSISTENCE_UIMODE", null);
        Environment.SetEnvironmentVariable("PERSISTENCE_RESPONSEFORMAT", null);
        Environment.SetEnvironmentVariable("PERSISTENCE_STREAMING", null);
        if (File.Exists(dbPath))
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    public record PendingDto(string Id, string Prompt);
    public record EventsDto(long Latest, List<ConversationEvent> Events);
    public record ConversationEvent(long Seq, string Kind, string Text, string? Detail);
}
