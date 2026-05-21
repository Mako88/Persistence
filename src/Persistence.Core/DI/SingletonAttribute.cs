namespace Persistence.DI;

/// <summary>
/// Marks a class for DI registration as a singleton
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class SingletonAttribute : ServiceAttribute
{
    /// <summary>
    /// Constructor
    /// </summary>
    public SingletonAttribute() : base() { }

    /// <summary>
    /// Constructor with explicit service type registration
    /// </summary>
    public SingletonAttribute(Type registerAsType) : base(registerAsType) { }
}
