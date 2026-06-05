namespace Persistence.DI;

/// <summary>
/// Contract for attributes that mark a class for automatic DI registration
/// </summary>
public interface IServiceAttribute
{
    /// <summary>
    /// The interface or base type this service should be registered as
    /// </summary>
    Type? RegisterAsType { get; }

    /// <summary>
    /// Named registration key (for named service resolution)
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// Keyed registration value (for keyed/indexed service resolution)
    /// </summary>
    object? Key { get; }
}
