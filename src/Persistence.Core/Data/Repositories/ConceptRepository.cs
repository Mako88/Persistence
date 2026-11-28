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
        "Id, SubjectId, ObjectId, Text, Status, IsState, AsOfUtc, RecordedAtUtc, SupersededById, Supersedes, ExtractedBy, Resolution, CreatedUtc, EndedUtc, EndReason, EndedBy";

    private const string DraftColumns = "Id, FactId, EndedBy, Reasons, CreatedUtc, Status, ResolvedUtc, Resolution";

    // A fact that stands: decided or proposed, not replaced, not ended (close-build-plan.md B3).
    private const string Stands = "SupersededById IS NULL AND Status IN ('active', 'proposed') AND EndedUtc IS NULL";

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

    public async Task<ConceptRow> AddConceptAsync(string name, string kind, string? summary, bool kindSaid = true)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        var confirmed = kindSaid ? 0L : -1L;
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO Concepts (Name, Kind, KindConfirmed, Summary, CreatedUtc) VALUES (@name, @kind, @confirmed, @summary, @now); SELECT last_insert_rowid();",
            new { name = name.Trim(), kind, confirmed, summary, now = Now() });
        return new ConceptRow(id, name.Trim(), kind, confirmed, summary);
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

    public async Task MoveAliasAsync(long fromConceptId, long toConceptId, string alias, string note)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        await c.ExecuteAsync("UPDATE ConceptAliases SET ConceptId = @toConceptId, Note = @note WHERE ConceptId = @fromConceptId AND Alias = @alias COLLATE NOCASE",
            new { fromConceptId, toConceptId, alias = alias.Trim(), note });
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
            "SELECT Id, Name, Kind, KindConfirmed, Summary FROM Concepts WHERE KindConfirmed <> 1 ORDER BY Name")).ToList();
    }

    public async Task<IReadOnlyList<ConceptRow>> AllAsync()
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return (await c.QueryAsync<ConceptRow>("SELECT Id, Name, Kind, KindConfirmed, Summary FROM Concepts ORDER BY Name")).ToList();
    }

    public async Task<IReadOnlyList<(long ConceptId, string Label)>> LabelsAsync()
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return (await c.QueryAsync<(long, string)>(
            "SELECT Id, Name FROM Concepts UNION ALL SELECT ConceptId, Alias FROM ConceptAliases")).ToList();
    }

    public async Task<long> AddFactAsync(long subjectId, long? objectId, string text, bool isState, string? asOfUtc,
        string extractedBy, IReadOnlyCollection<long> sources, string status, long? supersedes, string? flag = null)
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
        // A flag given with the fact goes in with it: a fact meant to be flagged is never visible unflagged (#70's gate).
        if (!string.IsNullOrWhiteSpace(flag))
        {
            await c.ExecuteAsync("INSERT INTO FactFlags (FactId, FlaggedBy, Reason, CreatedUtc) VALUES (@id, @by, @reason, @now)",
                new { id, by = extractedBy, reason = flag.Trim(), now = Now() }, tx);
        }

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
        var standing = history ? "" : $"AND {Stands}";
        return (await c.QueryAsync<FactRow>(
            $"SELECT {FactColumns} FROM Facts WHERE (SubjectId = @conceptId OR ObjectId = @conceptId) AND Status <> 'pending' {standing} " +
            "ORDER BY RecordedAtUtc DESC, Id DESC", new { conceptId })).ToList();
    }

    public async Task<IReadOnlyList<FactRow>> FactsByStatusAsync(string status)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return (await c.QueryAsync<FactRow>(
            $"SELECT {FactColumns} FROM Facts WHERE Status = @status AND SupersededById IS NULL AND EndedUtc IS NULL ORDER BY Id", new { status })).ToList();
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

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<FactSourceCloseRow>>> SourceClosesAsync(IReadOnlyCollection<long> factIds)
    {
        if (factIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<FactSourceCloseRow>>();
        }

        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return Grouped(await c.QueryAsync<FactSourceCloseRow>(SourceClosesSql + " WHERE s.FactId IN @ids ORDER BY s.FactId, s.FragmentId", new { ids = factIds }));
    }

    private const string SourceClosesSql =
        "SELECT s.FactId, s.FragmentId, f.ClosedUtc, f.EndedBy, f.Reason FROM FactSources s JOIN ContextFragments f ON f.Id = s.FragmentId";

    private static IReadOnlyDictionary<long, IReadOnlyList<FactSourceCloseRow>> Grouped(IEnumerable<FactSourceCloseRow> rows) =>
        rows.GroupBy(r => r.FactId).ToDictionary(g => g.Key, g => (IReadOnlyList<FactSourceCloseRow>)g.ToList());

    /// <summary>The one ended-by every source shares, or null: no sources, one open, one closed without a ref, or two refs.</summary>
    public static string? SharedEnding(IReadOnlyList<FactSourceCloseRow> sources) =>
        sources.Count > 0 && sources.All(s => s.Closed && s.EndedBy is not null) && sources.Select(s => s.EndedBy).Distinct().Count() == 1
            ? sources[0].EndedBy
            : null;

    public async Task<IReadOnlyList<long>> DraftEndsAsync(long fragmentId, System.Data.IDbTransaction transaction)
    {
        var c = transaction.Connection!;
        var facts = (await c.QueryAsync<long>(
            $"""
            SELECT f.Id FROM Facts f JOIN FactSources s ON s.FactId = f.Id
            WHERE s.FragmentId = @fragmentId AND f.Id IN (SELECT Id FROM Facts WHERE {Stands})
              AND NOT EXISTS (SELECT 1 FROM FactEndDrafts d WHERE d.FactId = f.Id AND d.Status = 'pending')
            ORDER BY f.Id
            """, new { fragmentId }, transaction)).ToList();
        var drafted = new List<long>();
        foreach (var fact in facts)
        {
            var sources = (await c.QueryAsync<FactSourceCloseRow>(SourceClosesSql + " WHERE s.FactId = @fact ORDER BY s.FragmentId", new { fact }, transaction)).ToList();
            // No shared event, no draft (Ember 4): the fact still shows 'sources closed', and the self decides by hand.
            if (SharedEnding(sources) is not { } endedBy)
            {
                continue;
            }

            var reasons = string.Join('\n', sources.Select(x => $"#{x.FragmentId}: {x.Reason}"));
            var id = await c.ExecuteScalarAsync<long>(
                "INSERT INTO FactEndDrafts (FactId, EndedBy, Reasons, CreatedUtc) VALUES (@fact, @endedBy, @reasons, @now); SELECT last_insert_rowid();",
                new { fact, endedBy, reasons, now = Now() }, transaction);
            foreach (var x in sources)
            {
                await c.ExecuteAsync("INSERT INTO FactEndDraftBasis (DraftId, FragmentId, ClosedUtc) VALUES (@id, @f, @at)",
                    new { id, f = x.FragmentId, at = x.ClosedUtc }, transaction);
            }

            drafted.Add(id);
        }

        return drafted;
    }

    public async Task<int> DropEndDraftsRestingOnAsync(long fragmentId, string resolution, System.Data.IDbTransaction transaction) =>
        await transaction.Connection!.ExecuteAsync(
            """
            UPDATE FactEndDrafts SET Status = 'dropped', ResolvedUtc = @now, Resolution = @resolution
            WHERE Status = 'pending' AND Id IN (SELECT DraftId FROM FactEndDraftBasis WHERE FragmentId = @fragmentId)
            """, new { fragmentId, resolution, now = Now() }, transaction);

    public async Task<FactEndDraftRow?> GetEndDraftAsync(long id)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return await c.QuerySingleOrDefaultAsync<FactEndDraftRow>($"SELECT {DraftColumns} FROM FactEndDrafts WHERE Id = @id", new { id });
    }

    public async Task<IReadOnlyList<FactEndDraftRow>> EndDraftsOfAsync(IReadOnlyCollection<long> factIds)
    {
        if (factIds.Count == 0)
        {
            return [];
        }

        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return (await c.QueryAsync<FactEndDraftRow>($"SELECT {DraftColumns} FROM FactEndDrafts WHERE FactId IN @ids ORDER BY FactId, Id DESC",
            new { ids = factIds })).ToList();
    }

    public async Task<IReadOnlyList<FactEndDraftRow>> PendingEndDraftsAsync()
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return (await c.QueryAsync<FactEndDraftRow>($"SELECT {DraftColumns} FROM FactEndDrafts WHERE Status = 'pending' ORDER BY Id")).ToList();
    }

    public async Task<string?> ApproveEndDraftAsync(long id, string reason)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        await using var tx = await c.BeginTransactionAsync();
        var draft = await c.QuerySingleAsync<FactEndDraftRow>($"SELECT {DraftColumns} FROM FactEndDrafts WHERE Id = @id", new { id }, tx);
        var stale = await StaleAsync(c, tx, draft);
        if (stale is not null)
        {
            await c.ExecuteAsync("UPDATE FactEndDrafts SET Status = 'dropped', ResolvedUtc = @now, Resolution = @why WHERE Id = @id",
                new { id, why = $"stale: {stale}", now = Now() }, tx);
            await tx.CommitAsync();
            return stale;
        }

        var now = Now();
        await c.ExecuteAsync("UPDATE Facts SET EndedUtc = @now, EndReason = @reason, EndedBy = @endedBy WHERE Id = @fact",
            new { now, reason, endedBy = draft.EndedBy, fact = draft.FactId }, tx);
        await c.ExecuteAsync("UPDATE FactEndDrafts SET Status = 'approved', ResolvedUtc = @now, Resolution = @reason WHERE Id = @id",
            new { id, reason, now }, tx);
        await tx.CommitAsync();
        return null;
    }

    /// <summary>B2's approval checks, read inside the approving transaction: which one fails, or null when all hold.</summary>
    private static async Task<string?> StaleAsync(System.Data.Common.DbConnection c, System.Data.Common.DbTransaction tx, FactEndDraftRow draft)
    {
        // 1. Still pending, and the fact still stands and hasn't ended.
        if (draft.Status != "pending")
        {
            return $"draft #{draft.Id} is {draft.Status}";
        }

        var fact = await c.QuerySingleOrDefaultAsync<FactRow>($"SELECT {FactColumns} FROM Facts WHERE Id = @id", new { id = draft.FactId }, tx);
        if (fact is null || fact.Ended || fact.SupersededById is not null || fact.Status is not ("active" or "proposed"))
        {
            return fact is { Ended: true } ? $"fact #{draft.FactId} already ended" : $"fact #{draft.FactId} no longer stands";
        }

        // 2. The fact's sources are exactly the basis: none added, none removed.
        var basis = (await c.QueryAsync<(long FragmentId, string ClosedUtc)>(
            "SELECT FragmentId, ClosedUtc FROM FactEndDraftBasis WHERE DraftId = @id ORDER BY FragmentId", new { id = draft.Id }, tx)).ToList();
        var sources = (await c.QueryAsync<FactSourceCloseRow>(SourceClosesSql + " WHERE s.FactId = @fact ORDER BY s.FragmentId", new { fact = draft.FactId }, tx)).ToList();
        var added = sources.Select(s => s.FragmentId).Except(basis.Select(b => b.FragmentId)).ToList();
        var removed = basis.Select(b => b.FragmentId).Except(sources.Select(s => s.FragmentId)).ToList();
        if (sources.Count == 0 || added.Count > 0 || removed.Count > 0)
        {
            return added.Count > 0 ? $"source #{added[0]} was attached after drafting"
                : removed.Count > 0 ? $"source #{removed[0]} was detached after drafting" : $"fact #{draft.FactId} has no sources";
        }

        foreach (var (fragmentId, closedUtc) in basis)
        {
            var now = sources.Single(s => s.FragmentId == fragmentId);
            // 3. Each basis fragment is still closed by the same close (not reopened and re-closed in between).
            if (now.ClosedUtc != closedUtc)
            {
                return now.Closed ? $"#{fragmentId} was reopened and closed again after drafting" : $"#{fragmentId} was reopened";
            }

            // 4. Its ended-by is still the draft's.
            if (now.EndedBy != draft.EndedBy)
            {
                return $"#{fragmentId} is now ended by {now.EndedBy ?? "no ref"}, not {draft.EndedBy}";
            }
        }

        return null;
    }

    public async Task<bool> DropEndDraftAsync(long id, string resolution)
    {
        await using var c = await SqliteConnectionString.OpenAsync(connectionString);
        return await c.ExecuteAsync("UPDATE FactEndDrafts SET Status = 'dropped', ResolvedUtc = @now, Resolution = @resolution WHERE Id = @id AND Status = 'pending'",
            new { id, resolution, now = Now() }) == 1;
    }
}
