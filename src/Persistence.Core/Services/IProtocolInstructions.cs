namespace Persistence.Services;

/// <summary>
/// Supplies the response-protocol instructions injected into every prompt — how the remote
/// peer must structure its output. Kept out of the persisted seed (which holds identity, not
/// scaffolding) so the wire format stays a code concern and never needs reseeding the database.
/// </summary>
public interface IProtocolInstructions
{
    /// <summary>The protocol description for the configured response format.</summary>
    string GetInstructions();
}
