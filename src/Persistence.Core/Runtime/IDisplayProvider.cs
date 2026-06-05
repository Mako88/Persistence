using Persistence.Data.Entities;

namespace Persistence.Runtime;

/// <summary>
/// UI-layer interface for all user-visible output and input. Implementations are
/// responsible for rendering session state and accepting human input. The interface is
/// intentionally narrow — complex UI interactions (e.g. wake-up interrupt handling) are
/// left to implementations.
/// </summary>
public interface IDisplayProvider
{
    /// <summary>
    /// Shows the session header, begins accepting user input, and returns a task
    /// that completes when the display has shut down (via <see cref="Stop"/> or
    /// cancellation). Callers await this to keep the session alive.
    /// </summary>
    Task Start(CancellationToken ct);

    /// <summary>
    /// Shows a thinking/working indicator before a model call.
    /// </summary>
    void ShowThinking(string? label = null);

    /// <summary>
    /// Shows the remote peer's reply text.
    /// </summary>
    void ShowReply(string reply);

    /// <summary>
    /// Shows the model's reasoning summary (when the provider returns one).
    /// </summary>
    void ShowReasoning(string summary);

    /// <summary>
    /// Appends an incremental chunk of the reasoning summary while streaming. Unlike
    /// <see cref="ShowReasoning"/>, this is called repeatedly with small deltas and
    /// should not add its own framing between chunks.
    /// </summary>
    void ShowReasoningDelta(string delta);

    /// <summary>
    /// Shows a tool/command invocation: its name, the request it was given, and its result.
    /// </summary>
    void ShowToolUse(string tool, string request, string result);

    /// <summary>
    /// Shows a wake-up event notification.
    /// </summary>
    void ShowWakeUpEvent(ScheduledEventEntity evt);

    /// <summary>
    /// Shows an error message.
    /// </summary>
    void ShowError(string message);

    /// <summary>
    /// Shows a debug info string.
    /// </summary>
    void ShowDebugInfo(string info);

    /// <summary>
    /// Stops the input loop and shows final session information. Called once when
    /// the session is shutting down.
    /// </summary>
    void Stop();

    /// <summary>
    /// Shows recent chat history on startup.
    /// </summary>
    void ShowChatHistory(IReadOnlyList<(string Role, string Content, DateTimeOffset Timestamp)> messages);

    /// <summary>
    /// Shows an unrecognised slash-command message.
    /// </summary>
    void ShowUnknownCommand(string command);

    /// <summary>
    /// Shows a notification that a message has been queued for the next iteration.
    /// </summary>
    void ShowMessageQueued(string input);
}
