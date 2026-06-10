namespace Persistence.Config;

/// <summary>
/// A named local peer (a human or agent who talks to the remote peer). Optional — used to give the
/// remote peer a short description of who it's speaking with. The active local peer is chosen by
/// <see cref="IAppConfig.SelectedLocalPeer"/> (or an <c>X-Local-Peer</c> header on the API); names not
/// listed here are still accepted, just without a description.
/// </summary>
public class LocalPeerProfile
{
    /// <summary>The peer's name, matched against the active selection (case-insensitive).</summary>
    public string Name { get; set; } = "Local Peer";

    /// <summary>Optional one-line description surfaced to the remote peer (e.g. "the human steward").</summary>
    public string? Description { get; set; }
}
