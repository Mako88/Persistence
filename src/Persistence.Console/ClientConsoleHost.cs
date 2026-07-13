using Persistence.Client;
using Persistence.Config;
using Persistence.Contracts;
using Persistence.Events;
using Persistence.Runtime;

namespace Persistence.Console;

/// <summary>One peer endpoint the hub connects to: an optional display name, its API base URL, and the
/// local (human) peer identity to speak as.</summary>
public record PeerEndpoint(string? Name, string BaseUrl, string? LocalPeer);

/// <summary>
/// Runs the Console as a thin client of one or more Persistence API servers (ADR-0006): it holds no
/// database, pipeline, or model. With a single unnamed endpoint it's a plain 1:1 client. With several
/// (or named) endpoints it becomes a <b>hub</b> (ADR-0007 Phase 2b): every peer's messages aggregate into
/// one attributed conversation pane, and a selector chooses which peer the side column (thoughts / actions
/// / schedule / debug) and status bar show. Peers do <i>not</i> hear each other here — that cross-peer
/// relay is the room (ADR-0008), built separately; the hub just lets one human watch and talk to several.
/// </summary>
public static class ClientConsoleHost
{
    /// <summary>Back-compat single-peer entry (an unnamed or one named endpoint).</summary>
    public static Task RunAsync(string baseUrl, string? localPeer, CancellationToken ct, string? peerName = null) =>
        RunAsync([new PeerEndpoint(peerName, baseUrl, localPeer)], ct);

    public static async Task RunAsync(IReadOnlyList<PeerEndpoint> peers, CancellationToken ct)
    {
        var config = await AppConfig.LoadAsync();
        var session = new SessionContext();
        var display = new TerminalGuiDisplayProvider(new EventBus(), session, config);

        // A single unnamed endpoint keeps the original 1:1 behaviour (generic label, no selector).
        if (peers is [{ Name: null } only])
        {
            await RunSingleAsync(display, config, session, only, ct);
            return;
        }

        await RunHubAsync(display, config, peers, ct);
    }

    /// <summary>The 1:1 path: one connection driving the panes directly (no hub aggregation).</summary>
    private static async Task RunSingleAsync(TerminalGuiDisplayProvider display, IAppConfig config, SessionContext session, PeerEndpoint peer, CancellationToken ct)
    {
        var client = new PersistenceHttpClient(peer.BaseUrl, peer.LocalPeer);

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

        display.OnInput = text => client.SendAsync(text, ct);

        var renderer = new ConversationEventRenderer(display, peer.Name);
        if (initial is not null)
        {
            renderer.DrawSnapshot(initial);
        }

        var pump = Task.Run(() => PumpAsync(client, renderer, display, initial?.LatestSeq ?? 0, ct), ct);

        await display.LaunchUi(ct);
        await pump;
    }

    /// <summary>The hub path: several connections, one aggregated pane, a per-peer side column + selector.</summary>
    private static async Task RunHubAsync(TerminalGuiDisplayProvider display, IAppConfig config, IReadOnlyList<PeerEndpoint> peers, CancellationToken ct)
    {
        var hub = new MultiPeerHub(display);
        var clients = new Dictionary<string, IPersistenceClient>(StringComparer.OrdinalIgnoreCase);
        var humans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connections = new List<(ConversationEventRenderer Renderer, IPersistenceClient Client, ConversationSnapshot? Snapshot)>();

        foreach (var p in peers)
        {
            var name = p.Name ?? p.BaseUrl;   // hub endpoints are normally named; fall back to the URL
            var client = new PersistenceHttpClient(p.BaseUrl, p.LocalPeer);
            clients[name] = client;
            if (!string.IsNullOrWhiteSpace(p.LocalPeer))
            {
                humans.Add(p.LocalPeer!);
            }

            ConversationSnapshot? snap = null;
            try
            {
                snap = await client.GetSnapshotAsync(ct);
            }
            catch
            {
                // Unreachable peer: still register it so it appears in the selector; its pump retries.
            }

            hub.RegisterPeer(name, snap?.Provider ?? config.Provider, snap?.Model ?? config.Model, snap?.SessionId ?? "—");
            var renderer = new ConversationEventRenderer(hub.ScopeFor(name, display), name);
            connections.Add((renderer, client, snap));
        }

        // Input is sent to whichever peer the selector currently has active.
        display.OnInput = text =>
        {
            var target = hub.ActivePeer;
            return target is not null && clients.TryGetValue(target, out var c) ? c.SendAsync(text, ct) : Task.CompletedTask;
        };

        // Wire the selector (peer list + human names for colouring) and the on-ready first paint.
        display.ConfigurePeerSelector(hub.PeerNames, humans, hub.SetActive, hub.Repaint);

        // Draw each snapshot: chat history aggregates into the shared pane; schedule/proposals lane per peer.
        foreach (var (renderer, _, snap) in connections)
        {
            if (snap is not null)
            {
                renderer.DrawSnapshot(snap);
            }
        }

        var pumps = connections
            .Select(c => Task.Run(() => PumpAsync(c.Client, c.Renderer, display, c.Snapshot?.LatestSeq ?? 0, ct), ct))
            .ToArray();

        await display.LaunchUi(ct);
        await Task.WhenAll(pumps);
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
