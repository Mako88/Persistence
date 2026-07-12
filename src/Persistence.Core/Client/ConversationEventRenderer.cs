using Persistence.Contracts;
using Persistence.Data.Entities;
using Persistence.Runtime;
using System.Text.Json;

namespace Persistence.Client;

/// <summary>
/// Maps the API conversation snapshot + event stream onto an <see cref="IDisplayProvider"/>, so a thin
/// client renders exactly what the in-process app renders from its own display calls. Transport-agnostic
/// — give it any display; the mapping is what the client-mode Console (and a future web UI) drive rendering
/// through. One instance per peer connection: it remembers which message ids it has already drawn, so a
/// reply that appears in both the connect-time snapshot and the live stream (the benign overlap left by the
/// persist-before-publish fix) is rendered once, not twice.
/// </summary>
public sealed class ConversationEventRenderer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IDisplayProvider display;
    private readonly HashSet<long> renderedMessageIds = [];

    /// <summary>Constructor</summary>
    public ConversationEventRenderer(IDisplayProvider display) => this.display = display;

    /// <summary>Draws the connect-time snapshot: prior chat, the schedule, and the open-proposal count.</summary>
    public void DrawSnapshot(ConversationSnapshot snapshot)
    {
        // Remember the ids we draw from history so the live stream doesn't redraw the same messages.
        foreach (var m in snapshot.ChatHistory)
        {
            renderedMessageIds.Add(m.Id);
        }

        display.ShowChatHistory(snapshot.ChatHistory);
        display.ShowScheduledEvents(ToEntities(snapshot.ScheduledEvents));
        display.ShowOpenProposalCount(snapshot.OpenProposalCount);
    }

    /// <summary>Applies one streamed <see cref="ConversationEvent"/> to the matching pane.</summary>
    public void Render(ConversationEvent e)
    {
        switch (e.Kind)
        {
            // A reply carries its persisted message id in Detail; skip it if the snapshot already drew it.
            case "reply":
                if (long.TryParse(e.Detail, out var id) && !renderedMessageIds.Add(id))
                {
                    break;
                }
                display.ShowReply(e.Text);
                break;
            case "thought": display.ShowThought(e.Text); break;
            case "reasoning": display.ShowReasoning(e.Text); break;
            case "tool": RenderTool(display, e); break;
            case "wakeup": display.ShowSystemMessage($"⏰ Woke: {e.Text}"); break;
            case "scheduled": RenderScheduled(display, e); break;
            case "proposals" when int.TryParse(e.Text, out var count): display.ShowOpenProposalCount(count); break;
            case "budget": RenderBudget(display, e); break;
            case "thinking": display.ShowThinking(e.Text); break;
            case "system": display.ShowSystemMessage(e.Text); break;
            case "error": display.ShowError(e.Text); break;
            case "queued": display.ShowMessageQueued(e.Text); break;
            case "debug": display.ShowDebugInfo(e.Text); break;
        }
    }

    private static void RenderTool(IDisplayProvider display, ConversationEvent e)
    {
        // The server packs a tool event as text = tool name, detail = "request → result".
        var detail = e.Detail ?? "";
        var sep = detail.IndexOf(" → ", StringComparison.Ordinal);
        var (request, result) = sep >= 0 ? (detail[..sep], detail[(sep + 3)..]) : ("", detail);
        display.ShowToolUse(e.Text, request, result);
    }

    private static void RenderBudget(IDisplayProvider display, ConversationEvent e)
    {
        // Encoded by the server as used/budget/percent.
        var parts = e.Text.Split('/');
        if (parts.Length == 3
            && int.TryParse(parts[0], out var used)
            && int.TryParse(parts[1], out var budget)
            && int.TryParse(parts[2], out var percent))
        {
            display.UpdateBudget(used, budget, percent);
        }
    }

    private static void RenderScheduled(IDisplayProvider display, ConversationEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.Detail))
        {
            return;
        }

        var views = JsonSerializer.Deserialize<List<ScheduledEventView>>(e.Detail, JsonOpts) ?? [];
        display.ShowScheduledEvents(ToEntities(views));
    }

    /// <summary>
    /// Rebuilds the entity the Schedule pane renders from the wire view. Only the shown fields matter;
    /// the rest get harmless defaults (a client has no DB rows behind these).
    /// </summary>
    private static IReadOnlyList<ScheduledEventEntity> ToEntities(IReadOnlyList<ScheduledEventView> views) =>
        views.Select(v => new ScheduledEventEntity
        {
            Id = v.Id,
            Name = v.Name,
            WorkingContextId = 0,
            ScheduledForUtc = v.ScheduledForUtc,
            WakePrompt = v.WakePrompt,
            Status = Enum.TryParse<ScheduledEventStatus>(v.Status, out var s) ? s : ScheduledEventStatus.Pending,
            CreatedUtc = v.ScheduledForUtc,
            LastModifiedUtc = v.ScheduledForUtc,
        }).ToList();
}
