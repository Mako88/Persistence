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
        RunAsync([new PeerEndpoint(peerName, baseUrl, localPeer)], localPeer, ct);

    public static async Task RunAsync(IReadOnlyList<PeerEndpoint> cliPeers, string? cliLocalPeer, CancellationToken ct)
    {
        var config = await AppConfig.LoadAsync();
        var session = new SessionContext();
        var display = new TerminalGuiDisplayProvider(new EventBus(), session, config);

        var peers = ResolvePeers(cliPeers, cliLocalPeer, config);

        // A single unnamed endpoint keeps the original 1:1 behaviour (generic label, no selector).
        if (peers is [{ Name: null } only])
        {
            await RunSingleAsync(display, config, session, only, ct);
            return;
        }

        await RunHubAsync(display, config, peers, ct);
    }

    /// <summary>
    /// Resolves which peers to connect to: explicit <c>--peer</c>/<c>--client</c> CLI endpoints win;
    /// otherwise the config's <see cref="IAppConfig.HubPeers"/> (ADR-0007 Phase 2b); otherwise the
    /// default single local server. Config peers with no local identity inherit the CLI <c>--as</c>.
    /// </summary>
    internal static IReadOnlyList<PeerEndpoint> ResolvePeers(IReadOnlyList<PeerEndpoint> cliPeers, string? cliLocalPeer, IAppConfig config)
    {
        if (cliPeers.Count > 0)
        {
            return cliPeers;
        }

        var configured = config.HubPeers
            .Where(h => !string.IsNullOrWhiteSpace(h.BaseUrl))
            .Select(h => new PeerEndpoint(string.IsNullOrWhiteSpace(h.Name) ? null : h.Name, h.BaseUrl, h.LocalPeer ?? cliLocalPeer))
            .ToList();

        return configured.Count > 0 ? configured : [new PeerEndpoint(null, "http://localhost:5000", cliLocalPeer)];
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

    /// <summary>The hub path: several connections, a scope selector, and per-peer lanes behind it.</summary>
    private static async Task RunHubAsync(TerminalGuiDisplayProvider display, IAppConfig config, IReadOnlyList<PeerEndpoint> peers, CancellationToken ct)
    {
        var hub = new MultiPeerHub(display);
        var clients = new Dictionary<string, IPersistenceClient>(StringComparer.OrdinalIgnoreCase);
        var humans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connections = new List<(ConversationEventRenderer Renderer, IPersistenceClient Client, IDisplayProvider Scoped, ConversationSnapshot? Snapshot)>();

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

            // The scoped display is this connection's whole surface — the pump reports through it too, so
            // a disconnect notice lands in the right peer's conversation rather than on the raw display,
            // whose own buffer the hub doesn't render in hub mode.
            var scoped = hub.ScopeFor(name);
            connections.Add((new ConversationEventRenderer(scoped, name), client, scoped, snap));
        }

        // Input goes to the selected peer — or, under the "all" scope, to every peer. Broadcasting is the
        // human opening the floor to the room, which ADR-0008 §1 explicitly anticipates; the no-autofan
        // guard there is about *peers* relaying to each other unmediated, and a human turn is what resets
        // the reply-chain breaker rather than tripping it.
        display.OnInput = text =>
        {
            var target = hub.ActivePeer;

            // A relay is handled here rather than sent on: it needs the several connections and the
            // current selection, which only the hub has. The server never sees the command.
            if (RelayCommand.IsRelay(text))
            {
                return RelayAsync(hub, clients, target, text, ct);
            }

            if (target is null)
            {
                return Task.WhenAll(clients.Values.Select(c => c.SendAsync(text, ct)));
            }

            return clients.TryGetValue(target, out var c) ? c.SendAsync(text, ct) : Task.CompletedTask;
        };

        // The hub composes the conversation from per-peer lanes, so the echo of what you just sent has to
        // be filed against the current scope rather than appended to the display's own buffer.
        display.OnLocalChat = hub.RecordLocalChat;

        // Wire the selector ("all" + the peers, human names for colouring) and the on-ready first paint.
        display.ConfigurePeerSelector(hub.SelectorEntries, humans, hub.SetActive, hub.Repaint);

        // Draw each snapshot into its peer's lane. History carries the store's timestamps, so the "all"
        // scope interleaves the peers into one chronology instead of one backlog after another.
        foreach (var (renderer, _, _, snap) in connections)
        {
            if (snap is not null)
            {
                renderer.DrawSnapshot(snap);
            }
        }

        var pumps = connections
            .Select(c => Task.Run(() => PumpAsync(c.Client, c.Renderer, c.Scoped, c.Snapshot?.LatestSeq ?? 0, ct), ct))
            .ToArray();

        await display.LaunchUi(ct);
        await Task.WhenAll(pumps);
    }

    /// <summary>
    /// Streams events into the panes, resuming after a drop. Reconnects from the last seq seen, so a
    /// transient disconnect replays only what was missed (no gap, no duplicate). A full server restart —
    /// which resets the in-memory event log — is not auto-recovered; restart the client.
    /// </summary>
    /// <summary>
    /// Carries the peer you're watching's most recent message to another peer (ADR-0008 §4).
    ///
    /// <para>The source message is read back from the origin peer's <em>store</em> rather than from the
    /// hub's rendered lines: the store is where the utterance's cross-peer id and hop depth live, and a
    /// relay that invented either would defeat the point of persisting them. The rendered pane holds
    /// text, not identity.</para>
    ///
    /// <para>Everything that could get the provenance wrong is delegated to <see cref="RelayComposer"/>.
    /// This method only decides <em>which</em> message and <em>where to</em>, then reports back.</para>
    /// </summary>
    private static async Task RelayAsync(MultiPeerHub hub, IReadOnlyDictionary<string, IPersistenceClient> clients,
        string? sourcePeer, string input, CancellationToken ct)
    {
        // Under "all" there's no one conversation to take the last message from, and guessing across
        // peers would relay something the human wasn't looking at.
        if (sourcePeer is null)
        {
            hub.RecordLocalChat("Select a peer first (F6) — /relay carries that peer's last message.\n\n");
            return;
        }

        var targetPeer = RelayCommand.ParseTarget(input);

        if (targetPeer is null)
        {
            hub.RecordChatNotice(sourcePeer, $"Usage: /relay <peer>   (carries {sourcePeer}'s last message onward)\n\n");
            return;
        }

        if (!clients.TryGetValue(targetPeer, out var destination))
        {
            var known = string.Join(", ", clients.Keys);
            hub.RecordChatNotice(sourcePeer, $"No peer called \"{targetPeer}\". Connected: {known}\n\n");
            return;
        }

        if (string.Equals(targetPeer, sourcePeer, StringComparison.OrdinalIgnoreCase))
        {
            hub.RecordChatNotice(sourcePeer, $"{sourcePeer} already said that — a relay carries it to someone else.\n\n");
            return;
        }

        try
        {
            var snapshot = await clients[sourcePeer].GetSnapshotAsync(ct);
            var message = RelayCommand.ResolveLastRelayable(snapshot.ChatHistory);

            if (message is null)
            {
                hub.RecordChatNotice(sourcePeer, $"{sourcePeer} hasn't said anything to carry yet.\n\n");
                return;
            }

            var relay = RelayComposer.Compose(message, targetPeer);
            await destination.RelayAsync(relay, ct);

            // Echoed into the *origin* peer's conversation, not the destination's: that's where the human
            // was reading when they decided to forward, and it's what stops them losing track of what
            // they've already carried and relaying it twice. The destination shows the message itself.
            hub.RecordChatNotice(sourcePeer, RelayCommand.Describe(message, targetPeer, relay.RelayDepth) + "\n\n");
        }
        catch (Exception ex)
        {
            // Usually the depth breaker refusing, whose message explains itself and says how to restart
            // the chain — so show what came back rather than a generic failure.
            hub.RecordChatNotice(sourcePeer, $"[Relay failed: {ex.Message}]\n\n");
        }
    }

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
