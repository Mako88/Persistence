using Persistence.Client;
using Persistence.Config;
using Persistence.Contracts;
using Persistence.Events;
using Persistence.Runtime;

namespace Persistence.Console;

/// <summary>
/// Runs the Console as a thin client of a Persistence API server (ADR-0006): it holds no database,
/// pipeline, or model. It draws the same Terminal.Gui panes, but rendering is fed by the API
/// conversation stream (via <see cref="ConversationEventRenderer"/>) and input is sent over HTTP,
/// rather than the in-process event bus.
/// </summary>
public static class ClientConsoleHost
{
    public static async Task RunAsync(string baseUrl, string? localPeer, CancellationToken ct)
    {
        var config = await AppConfig.LoadAsync();
        var session = new SessionContext();
        var client = new PersistenceHttpClient(baseUrl, localPeer);

        // Fetch the connect snapshot BEFORE building the UI so the status-bar labels (read at layout time)
        // show the server's model/provider/session rather than this client's local config.
        ConversationSnapshot? initial = null;
        try
        {
            initial = await client.GetSnapshotAsync(ct);
            config.Provider = initial.Provider;
            config.Model = initial.Model;
            session.SessionId = initial.SessionId;
        }
        catch
        {
            // Server not reachable yet — bring the UI up anyway; the pump retries and reports.
        }

        // A throwaway bus: in client mode nothing publishes domain events locally (we don't call
        // SubscribeToInProcessEvents), so it stays inert — the stream drives the panes instead.
        var display = new TerminalGuiDisplayProvider(new EventBus(), session, config);
        display.OnInput = text => client.SendAsync(text, ct);

        // One renderer per connection — it remembers drawn message ids so the stream doesn't redraw what
        // the snapshot already showed.
        var renderer = new ConversationEventRenderer(display);

        if (initial is not null)
        {
            renderer.DrawSnapshot(initial);
        }

        // The provider buffers pushes that land before the UI loop is ready, so the pump can run alongside.
        var pump = Task.Run(() => PumpAsync(client, renderer, display, initial?.LatestSeq ?? 0, ct), ct);

        await display.LaunchUi(ct);
        await pump;
    }

    /// <summary>
    /// Streams events into the panes, resuming after a drop. Reconnects from the last seq seen, so a
    /// transient disconnect replays only what was missed (no gap, no duplicate). A full server restart —
    /// which resets the in-memory event log — is not auto-recovered; restart the client.
    /// </summary>
    private static async Task PumpAsync(IPersistenceClient client, ConversationEventRenderer renderer, IDisplayProvider display, long since, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var evt in client.StreamAsync(since, ct))
                {
                    renderer.Render(evt);
                    since = evt.Seq;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                display.ShowError($"Disconnected: {ex.Message}. Reconnecting in 2s…");
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
