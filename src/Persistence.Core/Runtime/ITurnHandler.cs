namespace Persistence.Runtime;

/// <summary>
/// Processes a single conversational turn — builds the prompt from the current working
/// context, calls the model, persists the exchange, and publishes downstream events.
/// </summary>
public interface ITurnHandler
{
    /// <summary>
    /// Executes a full turn. When <paramref name="input"/> is provided, it is
    /// persisted as the initial message. When null, pending queued input is
    /// drained as the starting context instead.
    /// </summary>
    Task ExecuteTurnAsync(string? input = null, CancellationToken ct = default);

    /// <summary>
    /// Queues input from the local peer to be injected into the working context
    /// before the next model call within the current turn's iteration loop.
    /// </summary>
    void EnqueueInput(string input);

    /// <summary>
    /// Whether there are any pending input messages waiting to be processed.
    /// </summary>
    bool HasPendingInput { get; }
}
