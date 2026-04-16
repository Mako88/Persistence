using Persistence.Actions;
using Persistence.Config;
using Persistence.Models;
using Persistence.Services;

namespace Persistence.Runtime
{
    /// <summary>
    /// Interface for UI display providers
    /// </summary>
    public interface IDisplayProvider
    {
        /// <summary>
        /// Display the session header and restored conversation history
        /// </summary>
        void Initialize(string sessionId, IAppConfig config, List<Message> history);

        /// <summary>
        /// Request input from the user, with support for cancellation
        /// </summary>
        Task<string?> RequestInputAsync(CancellationToken ct);

        /// <summary>
        /// Display a thinking indicator
        /// </summary>
        void ShowThinking(string? label = null);

        /// <summary>
        /// Display the assistant's reply
        /// </summary>
        void ShowReply(string reply);

        /// <summary>
        /// Display the results of executed actions
        /// </summary>
        void ShowActionResults(List<ActionResult> results, string? label = null);

        /// <summary>
        /// Display a truncation recovery result
        /// </summary>
        void ShowRecovery(TurnResult recovery);

        /// <summary>
        /// Display a read follow-up result
        /// </summary>
        void ShowReadFollowUp(TurnResult followUp);

        /// <summary>
        /// Display reflection results
        /// </summary>
        void ShowReflection(ReflectionResult reflection);

        /// <summary>
        /// Display a wake-up event notification
        /// </summary>
        void ShowWakeUpEvent(string reason);

        /// <summary>
        /// Display a wake-up result
        /// </summary>
        void ShowWakeUpResult(WakeUpResult result);

        /// <summary>
        /// Display an error message
        /// </summary>
        void ShowError(string message);

        /// <summary>
        /// Display debug info
        /// </summary>
        void ShowDebugInfo(string info);

        /// <summary>
        /// Display token usage information
        /// </summary>
        void ShowTokenUsage(TokenUsageInfo usage);

        /// <summary>
        /// Display wake-up timer status
        /// </summary>
        void ShowWakeStatus(ScheduledEvent? pending);

        /// <summary>
        /// Display an unknown command message
        /// </summary>
        void ShowUnknownCommand(string command);

        /// <summary>
        /// Display the session ended message
        /// </summary>
        void ShowSessionEnded();

        /// <summary>
        /// Display wake-up debug diagnostics
        /// </summary>
        void ShowWakeDebug(WakeUpDiagnostics diag, string currentInput);

        /// <summary>
        /// Save and clear partial input before a wake-up interruption
        /// </summary>
        void ClearPartialInput(string partialInput);

        /// <summary>
        /// Restore the input prompt after a wake-up interruption
        /// </summary>
        void RestoreInputPrompt(string partialInput);
    }
}
