namespace Persistence.Config;

/// <summary>
/// One peer the Console <b>hub</b> connects to (ADR-0007 Phase 2b). Lets the hub's peer list live in
/// config — "point at these containers" — instead of a repeated <c>--peer</c> flag per launch. A config
/// with two or more of these opens the multi-peer hub; <c>--peer</c> flags on the command line override it.
/// </summary>
public class HubPeerProfile
{
    /// <summary>Display name used to attribute this peer's messages and label it in the selector (e.g. "Arden").</summary>
    public string Name { get; set; } = "";

    /// <summary>The peer's API server base URL (e.g. <c>http://localhost:5001</c>).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Optional local (human) identity to speak as to this peer; null uses the server's default.</summary>
    public string? LocalPeer { get; set; }
}
