using Microsoft.AspNetCore.Mvc;
using Persistence.Services;

namespace Persistence.Api.Controllers;

public record PeerResponse(string Id, string Response);

/// <summary>
/// The remote-peer side, for when an external agent (e.g. Claude) acts as the model via the
/// <see cref="ModelProvider.LocalClaude"/> client. The agent polls <see cref="Pending"/> for a
/// prompt awaiting a completion, then answers it with <see cref="Respond"/> — in the configured
/// response format, exactly as a model would. Only available when the broker is wired
/// (Provider = LocalClaude); other providers complete turns themselves.
/// </summary>
[ApiController]
[Route("api/peer")]
public class PeerController : ControllerBase
{
    private readonly IRemotePeerBroker broker;

    public PeerController(IRemotePeerBroker broker)
    {
        this.broker = broker;
    }

    /// <summary>
    /// Returns the prompt currently awaiting a remote-peer completion, or 204 if none is pending.
    /// </summary>
    [HttpGet("pending")]
    public IActionResult Pending()
    {
        var pending = broker.TryGetPending();
        return pending is null ? NoContent() : Ok(pending);
    }

    /// <summary>
    /// Supplies the remote peer's response to a pending completion. Returns 409 if no matching
    /// request is awaiting (e.g. it was cancelled or already answered).
    /// </summary>
    [HttpPost("respond")]
    public IActionResult Respond([FromBody] PeerResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Id) || response.Response is null)
        {
            return BadRequest("Id and Response are required.");
        }

        return broker.SubmitResponse(response.Id, response.Response)
            ? Ok()
            : Conflict("No pending completion with that id.");
    }
}
