using Dapper;
using Persistence.Config;
using Persistence.DI;

namespace Persistence.Data.Repositories;

/// <summary>
/// <see cref="IConceptRepository"/> over the concept-graph tables. Not an <see cref="EntityRepository{T}"/>: a fact
/// keeps its own history (supersede, resolution, flags), so the generic audit trail would say it twice.
/// </summary>
[Singleton(typeof(IConceptRepository))]
public class ConceptRepository : IConceptRepository
{
    private const string FactColumns =
        "Id, SubjectId, ObjectId, Text, Status, IsState, AsOfUtc, RecordedAtUtc, SupersededById, Supersedes, ExtractedBy, Resolution, CreatedUtc";

    private readonly string connectionString;

    public ConceptRepository(IAppConfig config) =>
        connectionString = SqliteConnectionString.For(config.DatabasePath);

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffzzz");

    public async Task<ConceptRow?> FindAsync(string nameOrAlias)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return await c.QuerySingleOrDefaultAsync<ConceptRow>(
            """
            SELECT Id, Name, Kind, KindConfirmed, Summary FROM Concepts WHERE Name = @n COLLATE NOCASE
            UNION ALL
            SELECT c.Id, c.Name, c.Kind, c.KindConfirmed, c.Summary FROM Concepts c
              JOIN ConceptAliases a ON a.ConceptId = c.Id WHERE a.Alias = @n COLLATE NOCASE
            LIMIT 1
            """, new { n = nameOrAlias.Trim() });
    }

    public async Task<ConceptRow?> GetAsync(long id)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return await c.QuerySingleOrDefaultAsync<ConceptRow>(
            "SELECT Id, Name, Kind, KindConfirmed, Summary FROM Concepts WHERE Id = @id", new { id });
    }

    public async Task<ConceptRow> AddConceptAsync(string name, string kind, string? summary)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO Concepts (Name, Kind, KindConfirmed, Summary, CreatedUtc) VALUES (@name, @kind, 0, @summary, @now); SELECT last_insert_rowid();",
            new { name = name.Trim(), kind, summary, now = Now() });
        return new ConceptRow(id, name.Trim(), kind, 0, summary);
    }

    public async Task SetKindAsync(long id, string kind)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        await c.ExecuteAsync("UPDATE Concepts SET Kind = @kind, KindConfirmed = 1 WHERE Id = @id", new { id, kind });
    }

    public async Task AddAliasAsync(long conceptId, string alias, string? note)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        await c.ExecuteAsync("INSERT INTO ConceptAliases (ConceptId, Alias, Note) VALUES (@conceptId, @alias, @note)",
            new { conceptId, alias = alias.Trim(), note });
    }

    public async Task<IReadOnlyList<(string Alias, string? Note)>> AliasesAsync(long conceptId)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return (await c.QueryAsync<(string, string?)>(
            "SELECT Alias, Note FROM ConceptAliases WHERE ConceptId = @conceptId ORDER BY Alias", new { conceptId })).ToList();
    }

    public async Task<IReadOnlyList<ConceptRow>> UnconfirmedAsync()
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return (await c.QueryAsync<ConceptRow>(
            "SELECT Id, Name, Kind, KindConfirmed, Summary FROM Concepts WHERE KindConfirmed = 0 ORDER BY Name")).ToList();
    }

    public async Task<long> AddFactAsync(long subjectId, long? objectId, string text, bool isState, string? asOfUtc,
        string extractedBy, IReadOnlyCollection<long> sources, string status, long? supersedes)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        await using var tx = await c.BeginTransactionAsync();
        var id = await c.ExecuteScalarAsync<long>(
            """
            INSERT INTO Facts (SubjectId, ObjectId, Text, Status, IsState, AsOfUtc, ExtractedBy, Supersedes, CreatedUtc)
            VALUES (@subjectId, @objectId, @text, 'pending', @isState, @asOfUtc, @extractedBy, @supersedes, @now);
            SELECT last_insert_rowid();
            """, new { subjectId, objectId, text = text.Trim(), isState, asOfUtc, extractedBy, supersedes, now = Now() }, tx);
        foreach (var source in sources.Distinct())
        {
            await c.ExecuteAsync("INSERT INTO FactSources (FactId, FragmentId) VALUES (@id, @source)", new { id, source }, tx);
        }

        await c.ExecuteAsync(
            """
            UPDATE Facts SET Status = @status,
              RecordedAtUtc = (SELECT MAX(f.CreatedUtc) FROM FactSources s JOIN ContextFragments f ON f.Id = s.FragmentId WHERE s.FactId = @id)
            WHERE Id = @id
            """, new { id, status }, tx);
        await tx.CommitAsync();
        return id;
    }

    public async Task<FactRow?> GetFactAsync(long id)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return await c.QuerySingleOrDefaultAsync<FactRow>($"SELECT {FactColumns} FROM Facts WHERE Id = @id", new { id });
    }

    public async Task<IReadOnlyList<FactRow>> FactsOfAsync(long conceptId, bool history)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        var standing = history ? "" : "AND SupersededById IS NULL AND Status IN ('active', 'proposed')";
        return (await c.QueryAsync<FactRow>(
            $"SELECT {FactColumns} FROM Facts WHERE (SubjectId = @conceptId OR ObjectId = @conceptId) AND Status <> 'pending' {standing} " +
            "ORDER BY RecordedAtUtc DESC, Id DESC", new { conceptId })).ToList();
    }

    public async Task<IReadOnlyList<FactRow>> FactsByStatusAsync(string status)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return (await c.QueryAsync<FactRow>(
            $"SELECT {FactColumns} FROM Facts WHERE Status = @status AND SupersededById IS NULL ORDER BY Id", new { status })).ToList();
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> SourcesAsync(IReadOnlyCollection<long> factIds)
    {
        if (factIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<long>>();
        }

        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        var rows = await c.QueryAsync<(long FactId, long FragmentId)>(
            "SELECT FactId, FragmentId FROM FactSources WHERE FactId IN @ids ORDER BY FragmentId", new { ids = factIds });
        return rows.GroupBy(r => r.FactId).ToDictionary(g => g.Key, g => (IReadOnlyList<long>)g.Select(r => r.FragmentId).ToList());
    }

    public async Task SetFactStatusAsync(long id, string status, string? resolution)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        await c.ExecuteAsync("UPDATE Facts SET Status = @status, Resolution = COALESCE(@resolution, Resolution) WHERE Id = @id",
            new { id, status, resolution });
    }

    public async Task SupersedeFactAsync(long oldId, long newId, string? resolution)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        await c.ExecuteAsync("UPDATE Facts SET SupersededById = @newId, Resolution = COALESCE(@resolution, Resolution) WHERE Id = @oldId",
            new { oldId, newId, resolution });
    }

    public async Task<long> FlagAsync(long factId, string by, string reason)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return await c.ExecuteScalarAsync<long>(
            "INSERT INTO FactFlags (FactId, FlaggedBy, Reason, CreatedUtc) VALUES (@factId, @by, @reason, @now); SELECT last_insert_rowid();",
            new { factId, by, reason, now = Now() });
    }

    public async Task<IReadOnlyList<FactFlagRow>> OpenFlagsAsync()
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return (await c.QueryAsync<FactFlagRow>(
            "SELECT Id, FactId, FlaggedBy, Reason, CreatedUtc FROM FactFlags WHERE ResolvedUtc IS NULL ORDER BY Id")).ToList();
    }

    public async Task ResolveFlagsAsync(long factId)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        await c.ExecuteAsync("UPDATE FactFlags SET ResolvedUtc = @now WHERE FactId = @factId AND ResolvedUtc IS NULL",
            new { factId, now = Now() });
    }

    public async Task<IReadOnlyDictionary<long, int>> FragmentTypesAsync(IReadOnlyCollection<long> fragmentIds)
    {
        if (fragmentIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        var rows = await c.QueryAsync<(long Id, int FragmentType)>(
            "SELECT Id, FragmentType FROM ContextFragments WHERE Id IN @ids AND IsDeleted = 0", new { ids = fragmentIds });
        return rows.ToDictionary(r => r.Id, r => r.FragmentType);
    }
}
