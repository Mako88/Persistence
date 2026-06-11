using Persistence.Data.Entities;

namespace Persistence.Services;

/// <summary>
/// Seeds a brand-new store with a peer's authored identity, loaded from a per-database seed file
/// (<c>{SeedsDirectory}/{dbName}.json</c>). Lets a peer arrive already oriented — with its chosen
/// name, values, and relationships — instead of an empty context.
/// </summary>
public interface IPeerSeeder
{
    /// <summary>
    /// Adds the identity fragments from this database's seed file (if one exists) to a freshly-created
    /// context. The caller persists the context afterwards. Returns the number of fragments seeded
    /// (0 when there is no seed file, it's empty, or every entry was skipped).
    /// </summary>
    Task<int> SeedAsync(WorkingContextEntity context, CancellationToken ct = default);
}
