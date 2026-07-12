using Persistence.Contracts;
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
    /// Shows an open thought the remote peer recorded via a Think action.
    /// </summary>
    void ShowThought(string thought);

    /// <summary>
    /// Shows a tool/command invocation: its name, the request it was given, and its result.
    /// </summary>
    void ShowToolUse(string tool, string request, string result);

    /// <summary>
    /// Shows a wake-up event notification.
    /// </summary>
    void ShowWakeUpEvent(ScheduledEventEntity evt);

    /// <summary>
    /// Shows the current set of pending scheduled events (a snapshot, pushed whenever it changes).
    /// </summary>
    void ShowScheduledEvents(IReadOnlyList<ScheduledEventEntity> events);

    /// <summary>
    /// Shows how many proposals are currently open/awaiting a decision (a count, pushed whenever it
    /// changes) — e.g. a status-bar indicator.
    /// </summary>
    void ShowOpenProposalCount(int count);

    /// <summary>
    /// Updates the context-window budget gauge (used/total tokens and percent full), pushed whenever a
    /// turn recomputes it.
    /// </summary>
    void UpdateBudget(int usedTokens, int budgetTokens, int percentFull);

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
    /// Shows recent chat history on startup — each message attributed to its author (a peer's name),
    /// carrying the fragment id so a client can reconcile it against the live stream.
    /// </summary>
    void ShowChatHistory(IReadOnlyList<ChatHistoryItem> messages);

    /// <summary>
    /// Shows a system/local message to the local peer — e.g. the result of a local slash command
    /// (not a remote-peer reply, not an error).
    /// </summary>
    void ShowSystemMessage(string message);

    /// <summary>
    /// Shows an unrecognised slash-command message.
    /// </summary>
    void ShowUnknownCommand(string command);

    /// <summary>
    /// Shows a notification that a message has been queued for the next iteration.
    /// </summary>
    void ShowMessageQueued(string input);
}
