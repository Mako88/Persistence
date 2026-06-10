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

    /// <summary>
    /// When the current turn began. Set at the start of each turn; used to enforce the proposal
    /// deliberation gap (a proposal created during the current turn can't be accepted within it).
    /// </summary>
    DateTimeOffset TurnStartedUtc { get; set; }

    /// <summary>
    /// Whether the compact command list is appended to the end of each turn's context. Defaults to
    /// on (seeded from <see cref="Config.IAppConfig.SurfaceCommands"/> at startup); the peer toggles
    /// it via <c>toggle_command_list</c>. Session-scoped — resets to the configured default each run.
    /// </summary>
    bool SurfaceCommandsEnabled { get; set; }

    /// <summary>
    /// The peer's current working directory inside its container "computer", persisted across the
    /// otherwise-stateless <c>shell</c> invocations so it keeps a sense of place. Blank means
    /// "not yet set" — the executor seeds it from the configured working dir.
    /// </summary>
    string ContainerCwd { get; set; }

    /// <summary>
    /// The name of the local peer the remote peer is currently talking with (e.g. "John", "Claude").
    /// Set per input from the active selection / <c>X-Local-Peer</c> header; surfaced in the sensory
    /// block. <see cref="LocalPeerSourceId"/> tracks this peer's source for message attribution.
    /// </summary>
    string ActiveLocalPeerName { get; set; }
}
