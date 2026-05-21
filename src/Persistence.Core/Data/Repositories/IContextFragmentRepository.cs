using Persistence.Data.Entities;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for managing context fragments
/// </summary>
public interface IContextFragmentRepository : IEntityRepository<ContextFragmentEntity>
{
    /// <summary>
    /// Returns fragments of the given type. Defaults to active only.
    /// Pass <c>activeOnly: false</c> to include all statuses.
    /// </summary>
    Task<IEnumerable<ContextFragmentEntity>> GetByTypeAsync(ContextFragmentType type, bool activeOnly = true);

    /// <summary>
    /// Returns all fragments tagged with the given tag ID, regardless of status
    /// </summary>
    Task<IEnumerable<ContextFragmentEntity>> GetByTagAsync(long tagId);

    /// <summary>
    /// Returns all active <see cref="ContextFragmentType.System"/> fragments. Used by the
    /// startup sequence to seed a new <see cref="WorkingContextEntity"/> with the system
    /// fragment set.
    /// </summary>
    Task<IEnumerable<ContextFragmentEntity>> GetSystemFragmentsAsync();

}
