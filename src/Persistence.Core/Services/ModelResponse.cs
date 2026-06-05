using System.Text.Json.Nodes;

namespace Persistence.Services;

/// <summary>
/// Structured response from the model, parsed from JSON. Determines what action the
/// remote peer wants to take and whether they want to continue acting before
/// yielding back to the local peer.
/// </summary>
public class ModelResponse
{
    /// <summary>
    /// The type of action the remote peer wants to perform
    /// </summary>
    public required ModelAction Action { get; init; }

    /// <summary>
    /// Whether the remote peer wants to perform additional actions before
    /// yielding back to the local peer. When true, the updated context is
    /// re-sent to the model for another iteration.
    /// </summary>
    public bool Continue { get; init; }

    /// <summary>
    /// Action-specific payload. Shape varies by action type — each
    /// <see cref="Runtime.IActionHandler"/> deserializes what it expects.
    /// </summary>
    public JsonNode? Data { get; init; }

    /// <summary>
    /// Whether the raw model output was successfully parsed as structured JSON.
    /// When false, the response was created from a fallback path (e.g. plain text).
    /// </summary>
    public bool ParsedSuccessfully { get; init; }
}

/// <summary>
/// The types of actions the remote peer can perform in response to input
/// </summary>
public enum ModelAction
{
    /// <summary>
    /// Send a plain-text response to the local peer
    /// </summary>
    RespondToUser = 0,

    /// <summary>
    /// Manage the working context (add, remove, update, rearrange fragments, fetch by tag)
    /// </summary>
    ManageContext = 1,

    /// <summary>
    /// Execute one or more actions (schedule events, fetch logs, etc.)
    /// </summary>
    ExecuteActions = 2,
}
