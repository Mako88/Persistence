using Microsoft.AspNetCore.Mvc;
using Persistence.Events;
using Persistence.Notifications;

namespace Persistence.Api.Controllers;

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
    private readonly IEventBus eventBus;
    private readonly ApiDisplayProvider display;

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
}
