using Persistence.Data.Entities;
using Persistence.Runtime;
using System.Text;

namespace Persistence.Console;

/// <summary>
/// The render surface the <see cref="MultiPeerHub"/> drives for the currently-selected peer. Kept as a
/// narrow interface (no Terminal.Gui types) so the hub's per-peer bookkeeping is unit-testable against a
/// fake, and so the concrete <see cref="TerminalGuiDisplayProvider"/> is the only v1-bound piece.
/// </summary>
internal interface IMultiPeerRenderTarget
{
    /// <summary>Replaces the four side panes with the active peer's buffered content.</summary>
    void SetSidePaneContent(string thoughts, string actions, string schedule, string debug);

    /// <summary>Updates the status bar to reflect the active peer (its model, spend, and turn state).</summary>
    void SetPeerStatus(string provider, string model, string session, int proposals, (int Used, int Budget, int Percent)? budget, string state);
}

/// <summary>
/// Aggregates several peer connections into one Terminal.Gui hub. Chat is shared — every peer's messages
/// land in the one conversation pane (see <see cref="PeerScopedDisplay"/>). The side column (thoughts /
/// actions / schedule / debug) and the status bar are <em>per peer</em>: each peer's stream is buffered
/// into its own <see cref="Lane"/>, and a selector chooses which lane the side column shows. Switching is
/// instant and lossless — a lane keeps accumulating in the background while another peer is on screen.
///
/// This is intentionally client-side only: peers do not hear each other here (that cross-peer relay is
/// the room, ADR-0008). The hub just lets one human watch, and talk to, several peers at once.
/// </summary>
internal sealed class MultiPeerHub(IMultiPeerRenderTarget target)
{
    /// <summary>Per-peer side-column + status buffers. Chat is not laned (it aggregates).</summary>
    private sealed class Lane
    {
        public readonly StringBuilder Thoughts = new();
        public readonly StringBuilder Actions = new();
        public readonly StringBuilder Debug = new();
        public string Schedule = "No scheduled events.\n";
        public int Proposals;
        public (int Used, int Budget, int Percent)? Budget;
        public string State = "idle";
        public string Provider = "";
        public string Model = "";
        public string Session = "";
    }

    private readonly object sync = new();
    private readonly Dictionary<string, Lane> lanes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> order = [];
    private string? active;

    /// <summary>The peers, in registration order — the selector's list.</summary>
    public IReadOnlyList<string> PeerNames
    {
        get { lock (sync) { return order.ToArray(); } }
    }

    /// <summary>The peer the side column is currently showing (input is also routed here).</summary>
    public string? ActivePeer
    {
        get { lock (sync) { return active; } }
    }

    /// <summary>
    /// Registers a peer connection (its snapshot model/provider/session for the status bar). The first
    /// registered peer becomes active. Safe to call before the UI loop is ready — the first paint is
    /// driven later by <see cref="Repaint"/>.
    /// </summary>
    public void RegisterPeer(string name, string provider, string model, string session)
    {
        lock (sync)
        {
            if (!lanes.ContainsKey(name))
            {
                lanes[name] = new Lane();
                order.Add(name);
            }

            var lane = lanes[name];
            lane.Provider = provider;
            lane.Model = model;
            lane.Session = session;
            active ??= name;
        }
    }

    /// <summary>The per-peer render facade the connection's event renderer draws through.</summary>
    public IDisplayProvider ScopeFor(string peer, IDisplayProvider chat) => new PeerScopedDisplay(this, peer, chat);

    // --- Recording (called off the UI thread by each connection's renderer via PeerScopedDisplay) ---

    public void RecordThought(string peer, string text) =>
        Mutate(peer, l => l.Thoughts.Append($"{TerminalGuiDisplayProvider.Stamp()}{text}\n\n"));

    public void RecordThoughtDelta(string peer, string delta) =>
        Mutate(peer, l => l.Thoughts.Append(delta));

    public void RecordDebug(string peer, string info) =>
        Mutate(peer, l => l.Debug.Append($"{TerminalGuiDisplayProvider.Stamp()}{info.TrimEnd()}\n\n"));

    public void RecordAction(string peer, string tool, string request, string result) =>
        Mutate(peer, l => l.Actions.Append(FormatAction(tool, request, result)));

    public void RecordSchedule(string peer, IReadOnlyList<ScheduledEventEntity> events) =>
        Mutate(peer, l => l.Schedule = TerminalGuiDisplayProvider.FormatSchedule(events));

    public void RecordProposals(string peer, int count) => Mutate(peer, l => l.Proposals = count);

    public void RecordBudget(string peer, int used, int budget, int percent) =>
        Mutate(peer, l => l.Budget = (used, budget, percent));

    public void RecordState(string peer, string state) => Mutate(peer, l => l.State = state);

    /// <summary>Switches which peer the side column + status bar show (selector callback).</summary>
    public void SetActive(string peer)
    {
        lock (sync)
        {
            if (!lanes.ContainsKey(peer))
            {
                return;
            }

            active = peer;
        }

        Repaint();
    }

    /// <summary>Pushes the active peer's full lane to the render target — call once the UI loop is ready.</summary>
    public void Repaint()
    {
        string thoughts, actions, schedule, debug, provider, model, session, state;
        int proposals;
        (int, int, int)? budget;

        lock (sync)
        {
            if (active is null || !lanes.TryGetValue(active, out var lane))
            {
                return;
            }

            thoughts = lane.Thoughts.ToString();
            actions = lane.Actions.ToString();
            schedule = lane.Schedule;
            debug = lane.Debug.ToString();
            provider = lane.Provider;
            model = lane.Model;
            session = lane.Session;
            proposals = lane.Proposals;
            budget = lane.Budget;
            state = lane.State;
        }

        target.SetSidePaneContent(thoughts, actions, schedule, debug);
        target.SetPeerStatus(provider, model, session, proposals, budget, state);
    }

    /// <summary>Applies a mutation to a peer's lane, then repaints if that peer is the one on screen.</summary>
    private void Mutate(string peer, Action<Lane> mutate)
    {
        bool isActive;

        lock (sync)
        {
            if (!lanes.TryGetValue(peer, out var lane))
            {
                return;
            }

            mutate(lane);
            isActive = string.Equals(peer, active, StringComparison.OrdinalIgnoreCase);
        }

        if (isActive)
        {
            Repaint();
        }
    }

    /// <summary>
    /// Renders one action as a colour-friendly block for the Actions pane: an expanded entry marker +
    /// timestamp + tool, then its request and response. Expanded (not collapsible) in the hub — the
    /// per-peer buffers are read-only snapshots, so there's no interactive toggle here.
    /// </summary>
    private static string FormatAction(string tool, string request, string result)
    {
        var sb = new StringBuilder();
        sb.Append($"▼ {TerminalGuiDisplayProvider.Stamp()}{tool}\n");
        sb.Append("Request:\n");
        sb.Append(Indent(request, 4));
        sb.Append("\nResponse:\n");
        sb.Append(Indent(result, 4));
        sb.Append("\n\n");
        return sb.ToString();
    }

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select(l => l.Length == 0 ? l : pad + l));
    }
}
