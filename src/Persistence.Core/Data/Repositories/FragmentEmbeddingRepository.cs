using InterpolatedSql.Dapper;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Services;

namespace Persistence.Data.Repositories;

/// <summary>
/// <see cref="IFragmentEmbeddingRepository"/> over the <c>FragmentEmbeddings</c> table.
///
/// Deliberately not an <see cref="EntityRepository{T}"/>: an embedding is derived data, not peer
/// memory. It has no audit trail, no soft delete, and no identity of its own — re-deriving it from the
/// fragment is always correct — so the entity machinery (change tracking, audit logging, last-accessed
/// stamping) would be overhead describing something that isn't true of it.
/// </summary>
[Singleton(typeof(IFragmentEmbeddingRepository))]
public class FragmentEmbeddingRepository : IFragmentEmbeddingRepository
{
    private readonly string connectionString;

    /// <summary>
    /// Constructor
    /// </summary>
    public FragmentEmbeddingRepository(IAppConfig config) =>
        connectionString = SqliteConnectionString.For(config.DatabasePath);

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEmbedding>> GetAllAsync(
        string model, CancellationToken ct = default)
    {
        await using var connection = await SqliteConnectionString.OpenAsync(connectionString);

        var rows = await connection
            .SqlBuilder($"""
                SELECT ContextFragmentId, Vector, ContentHash
                FROM FragmentEmbeddings
                WHERE Model = {model}
                """)
            .QueryAsync<(long ContextFragmentId, byte[] Vector, string ContentHash)>(cancellationToken: ct);

        return rows
            .Select(r => new StoredEmbedding(r.ContextFragmentId, VectorMath.FromBlob(r.Vector), r.ContentHash))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetStaleFragmentIdsAsync(
        string model, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await SqliteConnectionString.OpenAsync(connectionString);

        // Only the types a peer authors are worth embedding: chat messages and action responses are
        // already in context by the time recall runs, and embedding them would fill the vector store
        // with transcript rather than memory.
        var authorable = FragmentTypeRules.Authorable.Select(t => (int)t).ToList();

        var ids = await connection
            .SqlBuilder($"""
                SELECT f.Id
                FROM ContextFragments f
                LEFT JOIN FragmentEmbeddings e
                    ON e.ContextFragmentId = f.Id AND e.Model = {model}
                WHERE f.IsDeleted = 0
                  AND f.Status = {(int)ContextFragmentStatus.Active}
                  AND f.FragmentType IN {authorable}
                  AND (e.ContextFragmentId IS NULL OR e.UpdatedUtc < f.LastModifiedUtc)
                ORDER BY f.Id DESC
                LIMIT {limit}
                """)
            .QueryAsync<long>(cancellationToken: ct);

        return ids.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetUnembeddedIdsAsync(
        string model, ContextFragmentType type, int minLength, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await SqliteConnectionString.OpenAsync(connectionString);
        var ids = await connection
            .SqlBuilder($"""
                SELECT f.Id
                FROM ContextFragments f
                LEFT JOIN FragmentEmbeddings e
                    ON e.ContextFragmentId = f.Id AND e.Model = {model}
                WHERE f.IsDeleted = 0
                  AND f.FragmentType = {(int)type}
                  AND length(f.Content) >= {minLength}
                  AND e.ContextFragmentId IS NULL
                ORDER BY f.Id DESC
                LIMIT {limit}
                """)
            .QueryAsync<long>(cancellationToken: ct);

        return ids.ToList();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        string model, long fragmentId, float[] vector, string contentHash, CancellationToken ct = default)
    {
        await using var connection = await SqliteConnectionString.OpenAsync(connectionString);

        var blob = VectorMath.ToBlob(vector);
        var now = DateTimeOffset.UtcNow;

        await connection
            .SqlBuilder($"""
                INSERT INTO FragmentEmbeddings
                    (ContextFragmentId, Model, Dimensions, Vector, ContentHash, UpdatedUtc)
                VALUES
                    ({fragmentId}, {model}, {vector.Length}, {blob}, {contentHash}, {now})
                ON CONFLICT (ContextFragmentId, Model) DO UPDATE SET
                    Dimensions  = excluded.Dimensions,
                    Vector      = excluded.Vector,
                    ContentHash = excluded.ContentHash,
                    UpdatedUtc  = excluded.UpdatedUtc
                """)
            .ExecuteAsync(cancellationToken: ct);
    }
}
