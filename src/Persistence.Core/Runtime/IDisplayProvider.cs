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
    /// Shows the session header and begins accepting user input
    /// </summary>
    void Start(CancellationToken ct);

    /// <summary>Shows a thinking/working indicator before a model call.</summary>
    void ShowThinking(string? label = null);

    /// <summary>Shows the digital colleague's reply text.</summary>
    void ShowReply(string reply);

    /// <summary>Shows a wake-up event notification.</summary>
    void ShowWakeUpEvent(ScheduledEventEntity evt);

    /// <summary>Shows an error message.</summary>
    void ShowError(string message);

    /// <summary>Shows a debug info string.</summary>
    void ShowDebugInfo(string info);

    /// <summary>
    /// Stops the input loop and shows final session information. Called once when
    /// the session is shutting down.
    /// </summary>
    void Stop();

    /// <summary>Shows an unrecognised slash-command message.</summary>
    void ShowUnknownCommand(string command);
}
