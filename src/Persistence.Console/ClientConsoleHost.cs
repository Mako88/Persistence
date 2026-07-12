using Persistence.Client;
using Persistence.Config;
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

        // A throwaway bus: in client mode nothing publishes domain events locally (we don't call
        // SubscribeToInProcessEvents), so it stays inert — the stream drives the panes instead.
        var display = new TerminalGuiDisplayProvider(new EventBus(), new SessionContext(), config);
        var client = new PersistenceHttpClient(baseUrl, localPeer);

        display.OnInput = text => client.SendAsync(text, ct);

        // Feed the panes from the server: draw the connect snapshot, then stream live. Runs alongside the
        // UI loop; the provider buffers any push that lands before the loop is ready.
        var pump = Task.Run(() => PumpAsync(client, display, ct), ct);

        await display.LaunchUi(ct);
        await pump;
    }

    private static async Task PumpAsync(IPersistenceClient client, IDisplayProvider display, CancellationToken ct)
    {
        try
        {
            var snapshot = await client.GetSnapshotAsync(ct);
            ConversationEventRenderer.DrawSnapshot(display, snapshot);

            // Resume exactly at the snapshot's sequence — no gap, no duplicate.
            await foreach (var evt in client.StreamAsync(snapshot.LatestSeq, ct))
            {
                ConversationEventRenderer.Render(display, evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            display.ShowError($"Client connection error: {ex.Message}");
        }
    }
}
