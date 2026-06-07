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

        // Let the turn finish applying after the completion is supplied.
        await Task.Delay(300);

        return await EventsSinceAsync(client, sinceBefore);
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
                    await client.PostAsJsonAsync("/api/peer/respond",
                        new { id = pending!.Id, response = "<respond>ready</respond><continue>false</continue>" });
                    await Task.Delay(200);
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

    private async Task<long> LatestSeqAsync(HttpClient client)
    {
        var dto = await client.GetFromJsonAsync<EventsDto>("/api/conversation/events?since=0", JsonOpts);
        return dto?.Latest ?? 0;
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
