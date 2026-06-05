using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Raised after an action handler executes a command. Subscribers should display
/// the tool name, the request it was given, and its result (e.g. in a tool-usage pane).
/// </summary>
public class ToolInvoked(string tool, string request, string result) : BaseEvent
{
    /// <summary>
    /// The command/tool name that was invoked
    /// </summary>
    public string Tool { get; } = tool;

    /// <summary>
    /// The request payload (command fields) the tool was invoked with
    /// </summary>
    public string Request { get; } = request;

    /// <summary>
    /// The result text the command produced
    /// </summary>
    public string Result { get; } = result;
}
