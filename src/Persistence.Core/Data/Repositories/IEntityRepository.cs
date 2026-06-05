using Persistence.Data.Entities;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Base repository interface providing standard CRUD operations for <typeparamref name="T"/>
/// </summary>
public interface IEntityRepository<T> where T : BaseEntity
{
    /// <summary>
    /// Returns the entity with the given ID, or <c>null</c> if not found
    /// </summary>
    Task<T?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Returns all entities matching the given IDs
    /// </summary>
    Task<IEnumerable<T>> GetByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default);

    /// <summary>
    /// Persists a new entity or saves changes to an existing one
    /// </summary>
    Task SaveAsync(T entity, IDbTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the entity by setting <see cref="BaseEntity.IsDeleted"/> and saving
    /// </summary>
    Task DeleteAsync(T entity, IDbTransaction? transaction = null, CancellationToken ct = default);
}
