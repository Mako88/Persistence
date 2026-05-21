using Persistence.DI;

namespace Persistence.Runtime;

/// <summary>
/// Singleton implementation of <see cref="ISessionContext"/>. Holds mutable session state
/// that is set during Orchestrator startup and updated throughout the session lifetime.
/// </summary>
[Singleton]
public class SessionContext : ISessionContext
{
    /// <inheritdoc/>
    public string SessionId { get; set; } = string.Empty;

    /// <inheritdoc/>
    public long WorkingContextId { get; set; }

    /// <inheritdoc/>
    public long SystemSourceId { get; set; }
}
