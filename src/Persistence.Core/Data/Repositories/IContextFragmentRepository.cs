using Persistence.Data.Entities;

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
    /// Returns the fragments <paramref name="id"/> superseded: the earlier views it replaced
    /// </summary>
    Task<IEnumerable<ContextFragmentEntity>> GetSupersededByAsync(long id);

    /// <summary>
    /// For each of <paramref name="ids"/> that has been superseded, the head of its chain (the view nobody has
    /// replaced) and how many supersessions away it is, resolved in one recursive query however long the chain.
    /// An id that was never superseded is left out. The head may itself be archived or forgotten: the caller decides.
    /// </summary>
    Task<IReadOnlyDictionary<long, (long HeadId, int Hops)>> GetHeadsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>
    /// The ids of the newest <paramref name="count"/> active fragments of <paramref name="type"/>, newest first,
    /// leaving out those said in <paramref name="leaveOut"/> sessions. Ids only: the caller loads the few it shows,
    /// where a whole-type load read every row twice and wrote each one's last-accessed time back.
    /// </summary>
    Task<IReadOnlyList<long>> GetNewestIdsAsync(ContextFragmentType type, int count, IReadOnlyCollection<string> leaveOut);

    /// <summary>The ids of the active fragments of <paramref name="type"/> said in <paramref name="sessions"/>.</summary>
    Task<IReadOnlyList<long>> GetIdsBySessionsAsync(ContextFragmentType type, IReadOnlyCollection<string> sessions);

    /// <summary>Each session that has a fragment of <paramref name="type"/> since <paramref name="since"/>, with its newest.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetSessionsSinceAsync(ContextFragmentType type, DateTimeOffset since);

    /// <summary>
    /// The message ids already kept (deleted ones included, so a forgotten utterance is not kept again), each with
    /// its fragment id and session: what keeping a transcript checks against, without loading the fragments.
    /// </summary>
    Task<IReadOnlyList<(long Id, string MessageId, string? SessionId)>> GetMessageIdsAsync(ContextFragmentType type);

    /// <summary>
    /// Returns up to <paramref name="limit"/> results ordered best-match first.
    /// </summary>
    Task<IEnumerable<ContextFragmentEntity>> SearchRelevantAsync(string query, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes (<c>forget</c>) or restores (<c>unforget</c>) a fragment by flipping its
    /// <see cref="ContextFragmentEntity.IsDeleted"/> flag only — content, tags, and status untouched.
    /// When forgetting, an optional <paramref name="reason"/> is recorded (in Notes) for later recall.
    /// </summary>
    Task SetDeletedAsync(long id, bool deleted, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// Returns soft-deleted (forgotten) fragments, most-recently-forgotten first — the recovery
    /// surface for <c>unforget</c>. Flat rows (no tag hydration); intended for a compact listing.
    /// </summary>
    Task<IReadOnlyList<ContextFragmentEntity>> GetDeletedAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Counts what's been set aside but is still recoverable: forgotten (soft-deleted) fragments and
    /// (separately) archived-but-not-deleted ones. Feeds the sensory block's curation line.
    /// </summary>
    Task<(int Forgotten, int Archived)> CountAsideAsync(CancellationToken ct = default);
}
