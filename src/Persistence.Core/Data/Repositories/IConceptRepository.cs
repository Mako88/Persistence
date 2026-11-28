namespace Persistence.Data.Repositories;

/// <summary>A concept in the graph: a thing, person, self or value that facts are about.</summary>
public record ConceptRow(long Id, string Name, string Kind, long KindConfirmed, string? Summary)
{
    /// <summary>Whether the self has said what this concept is. KindConfirmed: 1 confirmed by the self, 0 a kind said
    /// when it was made but not confirmed, -1 no kind said at all (its Kind is the column's placeholder, "thing").</summary>
    public bool Confirmed => KindConfirmed == 1;

    /// <summary>Whether whoever made the concept said a kind; a review must not show "(said thing)" for one nobody said (Arden).</summary>
    public bool KindSaid => KindConfirmed >= 0;
}

/// <summary>A fact about a concept (and, with an object, a link to another): a view of the fragments it cites.</summary>
public record FactRow(
    long Id, long SubjectId, long? ObjectId, string Text, string Status, long IsState,
    string? AsOfUtc, string? RecordedAtUtc, long? SupersededById, long? Supersedes, string ExtractedBy, string? Resolution, string CreatedUtc,
    string? EndedUtc = null, string? EndReason = null, string? EndedBy = null)
{
    /// <summary>A state ("running", "held") rather than an event: only as true as when it was known.</summary>
    public bool State => IsState != 0;

    /// <summary>Ended by approving an end draft (close-build-plan.md B3): it was true and stopped being true.</summary>
    public bool Ended => EndedUtc is not null;
}

/// <summary>One source fragment of a fact, with its close (all null while the fragment is open).</summary>
public record FactSourceCloseRow(long FactId, long FragmentId, string? ClosedUtc, string? EndedBy, string? Reason)
{
    public bool Closed => ClosedUtc is not null;
}

/// <summary>A drafted end of a fact whose sources are all closed under one ended-by (close-build-plan.md B2).</summary>
public record FactEndDraftRow(long Id, long FactId, string EndedBy, string Reasons, string CreatedUtc, string Status,
    string? ResolvedUtc, string? Resolution);

/// <summary>A flag on a fact: someone (anyone) says it may be wrong; the self resolves it.</summary>
public record FactFlagRow(long Id, long FactId, string FlaggedBy, string Reason, string CreatedUtc);

/// <summary>
/// The concept graph's tables (migration 013). The rules about what a fact may be (cited, proposed, superseded)
/// live in the store above this and in the migration's triggers; this only reads and writes rows.
/// </summary>
public interface IConceptRepository
{
    Task<ConceptRow?> FindAsync(string nameOrAlias);

    Task<ConceptRow?> GetAsync(long id);

    Task<ConceptRow> AddConceptAsync(string name, string kind, string? summary, bool kindSaid = true);

    Task SetKindAsync(long id, string kind);

    Task AddAliasAsync(long conceptId, string alias, string? note);

    Task<IReadOnlyList<(string Alias, string? Note)>> AliasesAsync(long conceptId);

    /// <summary>Moves an alias from one concept to another, its note replaced by <paramref name="note"/> (which carries the move).</summary>
    Task MoveAliasAsync(long fromConceptId, long toConceptId, string alias, string note);

    Task<IReadOnlyList<ConceptRow>> UnconfirmedAsync();

    /// <summary>Every concept, by name.</summary>
    Task<IReadOnlyList<ConceptRow>> AllAsync();

    /// <summary>Every label a concept answers to: its name and each alias, for matching names in text.</summary>
    Task<IReadOnlyList<(long ConceptId, string Label)>> LabelsAsync();

    /// <summary>
    /// Writes a fact pending, then its sources, then sets its RecordedAt to the latest source's date and its
    /// status: in one transaction, so a fact never stands without its sources.
    /// </summary>
    Task<long> AddFactAsync(long subjectId, long? objectId, string text, bool isState, string? asOfUtc,
        string extractedBy, IReadOnlyCollection<long> sources, string status, long? supersedes, string? flag = null);

    Task<FactRow?> GetFactAsync(long id);

    /// <summary>Facts with the concept as subject or object; superseded and rejected ones only with <paramref name="history"/>.</summary>
    Task<IReadOnlyList<FactRow>> FactsOfAsync(long conceptId, bool history);

    Task<IReadOnlyList<FactRow>> FactsByStatusAsync(string status);

    Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> SourcesAsync(IReadOnlyCollection<long> factIds);

    Task SetFactStatusAsync(long id, string status, string? resolution);

    Task SupersedeFactAsync(long oldId, long newId, string? resolution);

    Task<long> FlagAsync(long factId, string by, string reason);

    Task<IReadOnlyList<FactFlagRow>> OpenFlagsAsync();

    Task ResolveFlagsAsync(long factId);

    /// <summary>The fragment types of <paramref name="fragmentIds"/>, and which of them do not exist.</summary>
    Task<IReadOnlyDictionary<long, int>> FragmentTypesAsync(IReadOnlyCollection<long> fragmentIds);

    /// <summary>Every source of each fact, with its close, ordered by fragment id.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<FactSourceCloseRow>>> SourceClosesAsync(IReadOnlyCollection<long> factIds);

    /// <summary>
    /// Inside the close's own <paramref name="transaction"/>, after <paramref name="fragmentId"/> was closed: drafts an
    /// end for each standing fact citing it whose sources are now all closed under one shared ended-by, and that has no
    /// pending draft. Returns the new draft ids.
    /// </summary>
    Task<IReadOnlyList<long>> DraftEndsAsync(long fragmentId, System.Data.IDbTransaction transaction);

    /// <summary>Inside the reopen's <paramref name="transaction"/>: drops exactly the pending drafts whose basis holds
    /// <paramref name="fragmentId"/>, with <paramref name="resolution"/>.</summary>
    Task<int> DropEndDraftsRestingOnAsync(long fragmentId, string resolution, System.Data.IDbTransaction transaction);

    Task<FactEndDraftRow?> GetEndDraftAsync(long id);

    /// <summary>The drafts of <paramref name="factIds"/> (every status), newest first per fact.</summary>
    Task<IReadOnlyList<FactEndDraftRow>> EndDraftsOfAsync(IReadOnlyCollection<long> factIds);

    Task<IReadOnlyList<FactEndDraftRow>> PendingEndDraftsAsync();

    /// <summary>
    /// Approves draft <paramref name="id"/> in one transaction that first revalidates it (B2's four checks). On success
    /// the fact gets EndedUtc, EndReason (= <paramref name="reason"/>) and EndedBy, and null is returned. If a check
    /// fails, nothing on the fact changes, the draft is dropped as stale, and the failed check is returned.
    /// </summary>
    Task<string?> ApproveEndDraftAsync(long id, string reason);

    /// <summary>Drops a pending draft with the self's reason. False if it wasn't pending.</summary>
    Task<bool> DropEndDraftAsync(long id, string resolution);
}
