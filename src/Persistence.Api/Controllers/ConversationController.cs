using Microsoft.AspNetCore.Mvc;
using Persistence.Events;
using Persistence.Notifications;
using System.Text.Json;

namespace Persistence.Api.Controllers;

/// <summary>
/// Request body for submitting local-peer input: the text to send to the conversation.
/// </summary>
public record SendRequest(string Input);

/// <summary>
/// The local-peer side of the conversation: submit input, then poll for what the system emits.
/// Input is dispatched through the same <see cref="DisplayInputReceived"/> event the other
/// front-ends use, so the orchestrator/turn pipeline runs unchanged. Turns run in the
/// background; results arrive via <see cref="Poll"/>.
/// </summary>
[ApiController]
[Route("api/conversation")]
public class ConversationController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IEventBus eventBus;
    private readonly ApiDisplayProvider display;
    private readonly Persistence.Services.IConversationHistoryProvider history;

    /// <summary>
    /// Constructor
    /// </summary>
    public ConversationController(IEventBus eventBus, ApiDisplayProvider display, Persistence.Services.IConversationHistoryProvider history)
    {
        this.eventBus = eventBus;
        this.display = display;
        this.history = history;
    }

    /// <summary>
    /// Submits local-peer input and returns immediately. The turn runs in the background
    /// (it may block awaiting an out-of-band remote peer); poll <c>events</c> for output.
    /// </summary>
    [HttpPost("send")]
    public IActionResult Send([FromBody] SendRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
        {
            return BadRequest("Input is required.");
        }

        // An optional X-Local-Peer header lets a caller identify who's speaking (John / Claude / Ember);
        // absent, the turn falls back to the configured SelectedLocalPeer.
        var localPeer = Request.Headers["X-Local-Peer"].ToString();
        localPeer = string.IsNullOrWhiteSpace(localPeer) ? null : localPeer.Trim();

        // Report a turn that fails outright. The turn runs detached, so without an error callback
        // FireAndForget drops the exception on the floor and the client sits on "thinking…" forever —
        // a misconfigured peer (a missing or wrong API key, say) looks hung rather than broken, with
        // nothing in the log either. Surfacing it as a conversation error is how the human finds out.
        eventBus.FireAndForget(this, new DisplayInputReceived(request.Input, localPeer),
            ex => display.ShowError(RootCause(ex).Message));
        return Accepted();
    }

    /// <summary>
    /// The innermost exception in a chain — the one that actually says what went wrong.
    ///
    /// Worth the unwrap: a turn failure usually surfaces through a DI activation, and the outer message
    /// is "An exception was thrown while activating Persistence.Services.SomeModelClient", which tells
    /// the human nothing they can act on. The cause underneath is the useful part ("no API key is set;
    /// add your OpenRouter key…").
    /// </summary>
    private static Exception RootCause(Exception ex)
    {
        while (ex.InnerException is { } inner)
        {
            ex = inner;
        }

        return ex;
    }

    /// <summary>
    /// Returns the standing state a freshly-connected client draws before subscribing: pending scheduled
    /// events, open-proposal count, recent chat, and the latest sequence. The client then streams from
    /// <c>?since=LatestSeq</c> so nothing is missed or duplicated.
    /// </summary>
    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot(CancellationToken ct)
    {
        // Capture the stream cut (LatestSeq) BEFORE reading history, not after. History and the event log
        // are separate sources, so a turn committing between them isn't an atomic snapshot: reading seq
        // first means a message that lands in that window is included in history AND re-delivered by the
        // stream (a harmless duplicate) rather than falling into a gap between them and being lost. A
        // fully atomic cut would need a per-message reconciliation key shared by history and events
        // (turn-pipeline change) — tracked as a follow-up.
        var latestSeq = display.LatestSeq;
        var chat = await history.GetRecentAsync(ct: ct);
        return Ok(display.Snapshot(latestSeq, chat));
    }

    /// <summary>
    /// Returns conversation events with sequence greater than <paramref name="since"/>.
    /// Clients track the highest seq they've seen and pass it back to get only new events.
    /// </summary>
    [HttpGet("events")]
    public IActionResult Poll([FromQuery] long since = 0) =>
        Ok(new
        {
            latest = display.LatestSeq,
            events = display.EventsSince(since),
        });

    /// <summary>
    /// Streams conversation events live as Server-Sent Events, starting after
    /// <paramref name="since"/> and continuing until the client disconnects. Each event is sent as
    /// <c>id: &lt;seq&gt;</c> + <c>event: &lt;kind&gt;</c> + a JSON <c>data:</c> line. Clients can
    /// resume after a drop by passing the last id they saw (also honoured via the standard
    /// <c>Last-Event-ID</c> header).
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream([FromQuery] long since = 0, CancellationToken ct = default)
    {
        if (long.TryParse(Request.Headers["Last-Event-ID"], out var resumeFrom) && resumeFrom > since)
        {
            since = resumeFrom;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering

        await foreach (var e in display.StreamAsync(since, ct))
        {
            var json = JsonSerializer.Serialize(e, JsonOpts);
            await Response.WriteAsync($"id: {e.Seq}\nevent: {e.Kind}\ndata: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
