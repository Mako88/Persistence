using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="SourceEntity"/>
/// </summary>
[Singleton]
public class SourceRepository : EntityRepository<SourceEntity>, ISourceRepository
{
    private readonly ISessionContext sessionContext;

    /// <summary>
    /// Constructor
    /// </summary>
    public SourceRepository(IAppConfig config, ISessionContext sessionContext)
        : base(config, sessionContext)
    {
        this.sessionContext = sessionContext;
    }

    /// <summary>
    /// Creates a System source if none exists and stores its ID in the session context
    /// </summary>
    public async Task CreateSystemSourceIfNotExists()
    {
        var systemSourceId = await ExecuteScalarAsync<long?>(
            $"SELECT Id FROM Sources WHERE SourceType = {SourceType.System} LIMIT 1");

        if (systemSourceId == null)
        {
            var now = DateTimeOffset.UtcNow;

            var source = new SourceEntity
            {
                SourceType = SourceType.System,
                Name = "System",
                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await SaveAsync(source);
            systemSourceId = source.Id;
        }

        sessionContext.SystemSourceId = systemSourceId.Value;
    }

    // ── Base overrides ───────────────────────────────────────────

    /// <summary>
    /// Returns the INSERT statement for a source entity
    /// </summary>
    protected override FormattableString GetInsertSql(SourceEntity entity) =>
        $"""
        INSERT INTO Sources (SourceType, Name, LastAccessedUtc, IsDeleted, CreatedUtc, LastModifiedUtc, Notes)
        VALUES ({entity.SourceType}, {entity.Name}, {entity.LastAccessedUtc}, {entity.IsDeleted}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.Notes})
        """;

    /// <summary>
    /// Returns the UPDATE statement for a source entity
    /// </summary>
    protected override FormattableString GetUpdateSql(SourceEntity entity) =>
        $"""
        UPDATE Sources
        SET SourceType = {entity.SourceType}, Name = {entity.Name},
            LastAccessedUtc = {entity.LastAccessedUtc}, IsDeleted = {entity.IsDeleted},
            LastModifiedUtc = {entity.LastModifiedUtc}, Notes = {entity.Notes}
        WHERE Id = {entity.Id}
        """;
}
