using System.Text.Json.Nodes;

namespace Persistence.Services;

/// <summary>
/// A single action the remote peer wants to perform within a <see cref="ModelTurn"/>.
/// </summary>
public class ModelResponse
{
    /// <summary>
    /// The type of action the remote peer wants to perform
    /// </summary>
    public required ModelAction Action { get; init; }

    /// <summary>
    /// Action-specific payload. Shape varies by action type — each
    /// <see cref="Runtime.IActionHandler"/> deserializes what it expects.
    /// </summary>
    public JsonNode? Data { get; init; }
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

    /// <summary>
    /// Reason in the open: record a thought into the working context (as a transient
    /// ScratchPad fragment) without sending anything to the local peer. Typically paired
    /// with <c>continue: true</c> so the remote peer can act on its own thinking.
    /// </summary>
    Think = 3,
}
