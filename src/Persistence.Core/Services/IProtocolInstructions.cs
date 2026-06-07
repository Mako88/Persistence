namespace Persistence.Services;

/// <summary>
/// Supplies the response-protocol instructions injected into every prompt — how the remote
/// peer must structure its output. Kept out of the persisted seed (which holds identity, not
/// scaffolding) and resolved by <see cref="ResponseFormat"/>, so the wire format can be
/// switched purely via config without reseeding the database.
/// </summary>
public interface IProtocolInstructions
{
    /// <summary>The protocol description for the configured response format.</summary>
    string GetInstructions();
}
