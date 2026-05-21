using Persistence.Data.Entities;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for managing sources
/// </summary>
public interface ISourceRepository : IEntityRepository<SourceEntity>
{
    /// <summary>
    /// Create a source with the System type if none exist
    /// </summary>
    Task CreateSystemSourceIfNotExists();
}
