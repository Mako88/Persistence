namespace Persistence.Data.Repositories;

/// <summary>A concept in the graph: a thing, person, self or value that facts are about.</summary>
public record ConceptRow(long Id, string Name, string Kind, long KindConfirmed, string? Summary)
{
    /// <summary>Whether the self has said what this concept is (SQLite keeps it as 0/1).</summary>
    public bool Confirmed => KindConfirmed != 0;
}

/// <summary>A fact about a concept (and, with an object, a link to another): a view of the fragments it cites.</summary>
public record FactRow(
    long Id, long SubjectId, long? ObjectId, string Text, string Status, long IsState,
    string? AsOfUtc, string? RecordedAtUtc, long? SupersededById, long? Supersedes, string ExtractedBy, string? Resolution, string CreatedUtc)
{
    /// <summary>A state ("running", "held") rather than an event: only as true as when it was known.</summary>
    public bool State => IsState != 0;
}

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

    Task<ConceptRow> AddConceptAsync(string name, string kind, string? summary);

    Task SetKindAsync(long id, string kind);

    Task AddAliasAsync(long conceptId, string alias, string? note);

    Task<IReadOnlyList<(string Alias, string? Note)>> AliasesAsync(long conceptId);

    Task<IReadOnlyList<ConceptRow>> UnconfirmedAsync();

    /// <summary>
    /// Writes a fact pending, then its sources, then sets its RecordedAt to the latest source's date and its
    /// status: in one transaction, so a fact never stands without its sources.
    /// </summary>
    Task<long> AddFactAsync(long subjectId, long? objectId, string text, bool isState, string? asOfUtc,
        string extractedBy, IReadOnlyCollection<long> sources, string status, long? supersedes);

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
}
