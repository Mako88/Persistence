namespace Persistence.DI;

/// <summary>
/// Marks a class for automatic DI registration. Optionally specifies the service
/// type to register as and/or a keyed registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ServiceAttribute : Attribute, IServiceAttribute
{
    /// <summary>
    /// Constructor
    /// </summary>
    public ServiceAttribute(Type? registerAsType = null, string? name = null, object? key = null)
    {
        RegisterAsType = registerAsType;
        Name = name;
        Key = key;
    }

    /// <summary>
    /// The interface or base type this service should be registered as
    /// </summary>
    public Type? RegisterAsType { get; }

    /// <summary>
    /// Named registration key (for named service resolution)
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Keyed registration value (for keyed/indexed service resolution)
    /// </summary>
    public object? Key { get; }
}
