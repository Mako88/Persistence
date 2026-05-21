namespace Persistence.Runtime;

/// <summary>
/// Processes a single conversational turn — builds the prompt from the current working
/// context, calls the model, persists the exchange, and publishes downstream events.
/// </summary>
public interface ITurnHandler
{
    /// <summary>
    /// Executes a full turn for the given user input
    /// </summary>
    Task ExecuteTurnAsync(string input, CancellationToken ct = default);
}
