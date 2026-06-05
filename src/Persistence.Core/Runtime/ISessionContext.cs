namespace Persistence.Runtime;

/// <summary>
/// Holds the mutable runtime state for the current session. Updated by the Orchestrator
/// as the session progresses (e.g. when a new WorkingContext is loaded).
/// </summary>
public interface ISessionContext
{
    /// <summary>
    /// The unique identifier for the current session.
    /// </summary>
    string SessionId { get; set; }

    /// <summary>
    /// The ID of the WorkingContext currently loaded in this session.
    /// </summary>
    long WorkingContextId { get; set; }

    /// <summary>
    /// The ID for the 'System' source type.
    /// </summary>
    long SystemSourceId { get; set; }

    /// <summary>
    /// The ID for the 'LocalPeer' source type.
    /// </summary>
    long LocalPeerSourceId { get; set; }

    /// <summary>
    /// The ID for the 'RemotePeer' source type.
    /// </summary>
    long RemotePeerSourceId { get; set; }
}
