using System.Text.Json.Nodes;
using Persistence.Data.Entities;

namespace Persistence.Runtime;

/// <summary>
/// Handles a specific type of action from the remote peer's response.
/// Implementations modify the working context as needed, persist changes, and
/// add transient <see cref="ContextFragmentType.ActionResponse"/> fragments to
/// the context with the results of the action. Throws on failure.
/// </summary>
public interface IActionHandler
{
    /// <summary>
    /// Processes the action using the provided data payload and working context
    /// </summary>
    Task HandleAsync(WorkingContextEntity context, JsonNode? data, CancellationToken ct = default);
}
