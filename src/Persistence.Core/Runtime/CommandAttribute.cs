namespace Persistence.Runtime;

/// <summary>
/// Marks a method as a discoverable command within a <see cref="CommandHandler"/>.
/// The method must return Task&lt;string&gt; and accept (WorkingContextEntity, JsonNode?, CancellationToken).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class CommandAttribute(string name, string description) : Attribute
{
    public string Name => name;
    public string Description => description;
}

/// <summary>
/// Declares a field in a command's expected schema. Applied alongside <see cref="CommandAttribute"/>
/// to describe the command's input format for discovery and error reporting.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class CommandFieldAttribute(string name, string type, bool required = false) : Attribute
{
    public string Name => name;
    public string Type => type;
    public bool Required => required;
    public string? Description { get; init; }
    public string? Default { get; init; }
}
