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
    /// Runs <paramref name="work"/> inside a single transaction on this repository's connection,
    /// committing if it completes and rolling back if it throws. The transaction is handed to the
    /// callback so it can be passed to <see cref="SaveAsync"/> on this <em>or other</em>
    /// repositories — the way to make a multi-repository write atomic while connection ownership
    /// stays in the repository layer.
    /// </summary>
    Task<TResult> RunInTransactionAsync<TResult>(
        Func<IDbTransaction, Task<TResult>> work, CancellationToken ct = default);
}
