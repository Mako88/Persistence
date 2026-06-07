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

    /// <summary>
    /// Constructor
    /// </summary>
    public ConversationController(IEventBus eventBus, ApiDisplayProvider display)
    {
        this.eventBus = eventBus;
        this.display = display;
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

        eventBus.FireAndForget(this, new DisplayInputReceived(request.Input));
        return Accepted();
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
