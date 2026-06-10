using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Published by the display provider when the user submits input. <paramref name="localPeerName"/>
/// optionally identifies which local peer is speaking (e.g. from an API <c>X-Local-Peer</c> header);
/// null falls back to the configured <see cref="Config.IAppConfig.SelectedLocalPeer"/>.
/// </summary>
public class DisplayInputReceived(string? input, string? localPeerName = null) : BaseEvent
{
    public string? Input { get; } = input;

    public string? LocalPeerName { get; } = localPeerName;
}
