using Persistence.DI;

namespace Persistence.Runtime;

/// <summary>
/// Singleton implementation of <see cref="ISessionContext"/>. Holds mutable session state
/// that is set during Orchestrator startup and updated throughout the session lifetime.
/// </summary>
[Singleton]
public class SessionContext : ISessionContext
{
    /// <summary>
    /// The unique identifier for the current session.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// The ID of the WorkingContext currently loaded in this session.
    /// </summary>
    public long WorkingContextId { get; set; }

    /// <summary>
    /// The ID for the 'System' source type.
    /// </summary>
    public long SystemSourceId { get; set; }

    /// <summary>
    /// The ID for the 'LocalPeer' source type.
    /// </summary>
    public long LocalPeerSourceId { get; set; }

    /// <summary>
    /// The ID for the 'RemotePeer' source type.
    /// </summary>
    public long RemotePeerSourceId { get; set; }

    /// <summary>
    /// When the current turn began. Set at the start of each turn; used to enforce the proposal
    /// deliberation gap (a proposal created during the current turn can't be accepted within it).
    /// </summary>
    public DateTimeOffset TurnStartedUtc { get; set; }
}
