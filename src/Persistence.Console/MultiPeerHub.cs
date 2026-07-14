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
    /// <summary>
    /// Replaces the conversation pane with the current scope's chat — one peer's conversation, or every
    /// peer's merged by time under the "all" scope. <paramref name="scopeChanged"/> carries the same
    /// meaning as on <see cref="SetSidePaneContent"/>.
    /// </summary>
    void SetConversation(string chat, bool scopeChanged);

    /// <summary>
    /// Replaces the four side panes with the active peer's buffered content.
    ///
    /// <paramref name="peerSwitched"/> distinguishes the two reasons this is called, which want opposite
    /// scroll behaviour. A <em>switch</em> (or the first paint) puts different content on screen, so the
    /// panes should jump to its newest line. An <em>update</em> to the peer already on screen is a
    /// live append, so it must respect where the reader has scrolled to.
    /// </summary>
    void SetSidePaneContent(string thoughts, string actions, string schedule, string debug, bool peerSwitched);

    /// <summary>Updates the status bar to reflect the active peer (its model, spend, and turn state).</summary>
    void SetPeerStatus(string provider, string model, string session, int proposals, (int Used, int Budget, int Percent)? budget, string state);
}

/// <summary>
/// Aggregates several peer connections into one Terminal.Gui hub, under a single selectable
/// <b>scope</b>: either one peer, or <see cref="AllScope"/>.
///
/// <list type="bullet">
/// <item><b>A peer scope</b> shows that peer alone — its conversation in the main pane, its thoughts /
/// actions / schedule / debug in the side column, its model and spend in the status bar. Input goes only
/// to it.</item>
/// <item><b>The "all" scope</b> is the overview: every peer's conversation merged into one scrollback
/// ordered by time, the side column blanked (there's no single peer to show), and input broadcast to
/// everybody.</item>
/// </list>
///
/// Everything is buffered per peer, so switching scope is instant and lossless — a background peer keeps
/// accumulating while another is on screen.
///
/// This is intentionally client-side only: peers do not hear each other here (that cross-peer relay is
/// the room, ADR-0008). The hub just lets one human watch, and talk to, several peers at once.
/// </summary>
internal sealed class MultiPeerHub(IMultiPeerRenderTarget target)
{
    /// <summary>
    /// The selector entry for the merged overview — every peer's conversation, no single peer's lane.
    /// It's a scope, not a peer: it never appears in <see cref="PeerNames"/> and has no lane of its own.
    /// </summary>
    public const string AllScope = "All";

    /// <summary>What the side column shows under <see cref="AllScope"/>, which has no single peer's lane.</summary>
    private const string AllScopePlaceholder =
        "No peer selected.\nPick a peer in the selector above (click it, or press F6) to see its thoughts,\nactions and schedule.\n";

    /// <summary>Per-peer side-column + status buffers. Chat is laned separately (see <see cref="chat"/>).</summary>
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

    /// <summary>
    /// One rendered conversation line, kept as data rather than appended into a string, because the "all"
    /// scope has to merge several peers' lines <em>by time</em> — which a per-peer string can't do.
    /// </summary>
    /// <param name="Peer">
    /// Whose conversation this belongs to. <see langword="null"/> means "every conversation" — a message
    /// the human broadcast, or a local system line — so it shows whatever scope is selected.
    /// </param>
    /// <param name="At">When it was said. Live lines stamp now; history carries the store's real
    /// timestamp, which is what lets a fresh start interleave peers correctly instead of showing one
    /// peer's history and then the next's.</param>
    /// <param name="FromHuman">Whether the human said it — used only by the "all" scope's duplicate
    /// collapse (see <see cref="RenderChat"/>).</param>
    private sealed record ChatLine(string? Peer, DateTimeOffset At, string Text, bool FromHuman);

    private readonly object sync = new();
    private readonly Dictionary<string, Lane> lanes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> order = [];
    private readonly List<ChatLine> chat = [];

    /// <summary>The selected scope: a peer's name, or null for <see cref="AllScope"/> (the default).</summary>
    private string? active;

    // Composing the conversation means filtering, sorting and re-joining every line, but a repaint fires
    // on every recorded event — including one per streamed reasoning chunk, none of which touch chat. So
    // the composed text is cached and rebuilt only when the conversation or the scope actually changes.
    private string chatRendered = "";
    private string? chatRenderedScope;
    private bool chatRenderedValid;

    /// <summary>The peers, in registration order — the selector's list.</summary>
    public IReadOnlyList<string> PeerNames
    {
        get { lock (sync) { return order.ToArray(); } }
    }

    /// <summary>
    /// The selected peer — the one the side column shows and input is routed to — or <see langword="null"/>
    /// under <see cref="AllScope"/>, where the conversation is merged and input is broadcast.
    /// </summary>
    public string? ActivePeer
    {
        get { lock (sync) { return active; } }
    }

    /// <summary>The selector's entries: <see cref="AllScope"/> first, then the peers in registration order.</summary>
    public IReadOnlyList<string> SelectorEntries
    {
        get { lock (sync) { return [AllScope, .. order]; } }
    }

    /// <summary>
    /// Registers a peer connection (its snapshot model/provider/session for the status bar). Safe to call
    /// before the UI loop is ready — the first paint is driven later by <see cref="Repaint"/>. The scope
    /// starts at <see cref="AllScope"/>, so a fresh start opens on the merged conversation.
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
        }
    }

    /// <summary>The per-peer render facade the connection's event renderer draws through.</summary>
    public IDisplayProvider ScopeFor(string peer) => new PeerScopedDisplay(this, peer);

    // --- Chat (laned per peer; the "all" scope merges them by time) ---

    /// <summary>Records a peer's reply into its own conversation.</summary>
    public void RecordReply(string peer, string text, string? speaker = null)
    {
        var name = speaker ?? peer;
        AddChat(new ChatLine(peer, DateTimeOffset.Now, $"{TerminalGuiDisplayProvider.Stamp()}{name}: {text}\n\n", FromHuman: false));
    }

    /// <summary>
    /// Records a peer's connect-time history into its conversation, keeping each message's <em>store</em>
    /// timestamp. That's what lets the "all" scope interleave several peers' histories into one true
    /// chronology, rather than showing one peer's backlog and then the next's.
    /// </summary>
    public void RecordHistory(string peer, IReadOnlyList<Persistence.Contracts.ChatHistoryItem> messages)
    {
        var lines = messages.Select(m => new ChatLine(
            peer,
            m.Timestamp,
            $"{TerminalGuiDisplayProvider.Stamp(m.Timestamp)}{m.Author}: {m.Content}\n\n",
            IsHuman(m.Role)));

        lock (sync)
        {
            chat.AddRange(lines);
            chatRenderedValid = false;
        }

        Paint(scopeChanged: false);
    }

    /// <summary>A line tied to one peer's conversation but not spoken by it (an error, a queued notice).</summary>
    public void RecordChatNotice(string peer, string text) =>
        AddChat(new ChatLine(peer, DateTimeOffset.Now, text, FromHuman: false));

    /// <summary>
    /// Records a line that belongs to <em>every</em> conversation — the local echo of something the human
    /// sent, or a system/error line raised by the client itself. Under a peer scope it's attributed to
    /// that peer (it's what you said to them); under <see cref="AllScope"/> it was broadcast, so it
    /// belongs to all of them.
    /// </summary>
    public void RecordLocalChat(string text)
    {
        string? scope;
        lock (sync)
        {
            scope = active;
        }

        AddChat(new ChatLine(scope, DateTimeOffset.Now, $"{TerminalGuiDisplayProvider.Stamp()}{text}\n\n", FromHuman: true));
    }

    /// <summary>The store's coarse role for a human-authored message.</summary>
    private static bool IsHuman(string role) => string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);

    private void AddChat(ChatLine line)
    {
        lock (sync)
        {
            chat.Add(line);
            chatRenderedValid = false;
        }

        Paint(scopeChanged: false);
    }

    /// <summary>The conversation for <paramref name="scope"/>, composed only when it could have changed.</summary>
    private string RenderChatCached(string? scope)
    {
        if (chatRenderedValid && string.Equals(chatRenderedScope, scope, StringComparison.OrdinalIgnoreCase))
        {
            return chatRendered;
        }

        chatRendered = RenderChat(scope);
        chatRenderedScope = scope;
        chatRenderedValid = true;
        return chatRendered;
    }

    /// <summary>
    /// Renders the conversation for <paramref name="scope"/>: one peer's lines (plus anything addressed to
    /// everyone), or — under "all" — every peer's, ordered by time. OrderBy is stable, so lines sharing a
    /// timestamp keep the order they arrived in.
    /// </summary>
    private string RenderChat(string? scope)
    {
        var visible = chat
            .Where(l => scope is null || l.Peer is null || string.Equals(l.Peer, scope, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.At);

        var sb = new StringBuilder();
        ChatLine? previous = null;

        foreach (var line in visible)
        {
            // Under "all", one thing the human broadcast is stored once per peer, so the merge sees it
            // once per peer and would print it N times. Collapse a human line that is byte-identical to
            // the one just before it (same stamp, same author, same text) — after sorting, those copies
            // are necessarily adjacent. This is deliberately narrow: it can't touch a peer's own words,
            // and it can't merge anything a minute apart. The principled fix is the cross-peer message id
            // that ADR-0007 Phase 0 / ADR-0008 call for, which doesn't exist yet — see TODO.md.
            var duplicateBroadcast = scope is null
                && previous is not null
                && line.FromHuman
                && previous.FromHuman
                && string.Equals(previous.Text, line.Text, StringComparison.Ordinal);

            if (!duplicateBroadcast)
            {
                sb.Append(line.Text);
            }

            previous = line;
        }

        return sb.ToString();
    }

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

    /// <summary>
    /// Switches scope (selector callback): <see cref="AllScope"/> for the merged overview, or a peer's
    /// name to focus that peer alone. An unknown name is ignored.
    /// </summary>
    public void SetActive(string scope)
    {
        lock (sync)
        {
            if (string.Equals(scope, AllScope, StringComparison.OrdinalIgnoreCase))
            {
                active = null;
            }
            else if (lanes.ContainsKey(scope))
            {
                active = scope;
            }
            else
            {
                return;
            }
        }

        Paint(scopeChanged: true);
    }

    /// <summary>Pushes the current scope to the render target — call once the UI loop is ready.</summary>
    public void Repaint() => Paint(scopeChanged: true);

    /// <summary>
    /// Pushes the current scope to the render target. <paramref name="scopeChanged"/> is true when the
    /// content is changing scope (a switch, or the first paint) and false when it's a live update to the
    /// scope already on screen — the target uses it to decide whether to jump to the newest line.
    /// </summary>
    private void Paint(bool scopeChanged)
    {
        string chatText, thoughts, actions, schedule, debug, provider, model, session, state;
        int proposals;
        (int, int, int)? budget;

        lock (sync)
        {
            chatText = RenderChatCached(active);

            if (active is null)
            {
                // The "all" scope: the side column has no single peer to show, so it's blanked (John's
                // call). The status bar still carries what's meaningful across peers — the total open
                // proposals, and whether *anyone* is working — but not a model or spend, which are
                // per peer. An empty model collapses the bar's "/" separator (see SetPeerStatus).
                thoughts = actions = schedule = debug = AllScopePlaceholder;
                provider = lanes.Count == 1 ? "1 peer" : $"{lanes.Count} peers";
                model = "";
                session = "—";
                proposals = lanes.Values.Sum(l => l.Proposals);
                budget = null;
                state = lanes.Values.Any(l => IsBusy(l.State)) ? "thinking…" : "idle";
            }
            else if (lanes.TryGetValue(active, out var lane))
            {
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
            else
            {
                return;
            }
        }

        target.SetConversation(chatText, scopeChanged);
        target.SetSidePaneContent(thoughts, actions, schedule, debug, scopeChanged);
        target.SetPeerStatus(provider, model, session, proposals, budget, state);
    }

    /// <summary>
    /// Whether a lane's state means "working". Mirrors how the status chip decides its colour: a settled
    /// state is a bare word, a working one carries the trailing ellipsis.
    /// </summary>
    private static bool IsBusy(string state) => state.Contains('…');

    /// <summary>
    /// Applies a mutation to a peer's lane, then repaints if it would change what's on screen — that's
    /// the peer's own scope, or "all" (whose status aggregates every lane's proposals and busy-ness).
    /// </summary>
    private void Mutate(string peer, Action<Lane> mutate)
    {
        bool onScreen;

        lock (sync)
        {
            if (!lanes.TryGetValue(peer, out var lane))
            {
                return;
            }

            mutate(lane);
            onScreen = active is null || string.Equals(peer, active, StringComparison.OrdinalIgnoreCase);
        }

        if (onScreen)
        {
            Paint(scopeChanged: false);
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
