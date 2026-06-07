namespace Persistence.Services;

/// <summary>
/// A pending completion request awaiting an out-of-band response from an external remote peer.
/// </summary>
public record PendingCompletion(string Id, string Prompt);

/// <summary>
/// Bridges the in-process model client to an external agent (e.g. Claude) that supplies
/// completions out-of-band via the API. When a turn needs a completion, the model client
/// awaits <see cref="RequestCompletionAsync"/>; the agent fetches the pending prompt with
/// <see cref="TryGetPending"/> and answers it with <see cref="SubmitResponse"/>.
///
/// Turns are serialized by the orchestrator's turn lock, so at most one completion is pending
/// at a time — but each carries an id so a late/duplicate response can't satisfy the wrong request.
/// </summary>
public interface IRemotePeerBroker
{
    /// <summary>
    /// Registers a prompt awaiting an external response and returns a task that completes with
    /// that response. Used by the model client in place of an HTTP model call.
    /// </summary>
    Task<string> RequestCompletionAsync(string prompt, CancellationToken ct = default);

    /// <summary>
    /// Returns the prompt currently awaiting a response, or null if none is pending.
    /// </summary>
    PendingCompletion? TryGetPending();

    /// <summary>
    /// Supplies the response for the pending completion with the given id. Returns false if no
    /// matching request is awaiting (e.g. it was cancelled or already answered).
    /// </summary>
    bool SubmitResponse(string id, string response);
}
