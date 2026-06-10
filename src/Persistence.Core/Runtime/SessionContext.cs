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

    /// <summary>
    /// Whether the compact command list is appended to the end of each turn's context. Defaults to
    /// on; seeded from config at startup and toggled by the peer via <c>toggle_command_list</c>.
    /// </summary>
    public bool SurfaceCommandsEnabled { get; set; } = true;

    /// <summary>
    /// The peer's current working directory inside its container; persisted across <c>shell</c> calls.
    /// </summary>
    public string ContainerCwd { get; set; } = string.Empty;
}
