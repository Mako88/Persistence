namespace Persistence.Data;

/// <summary>
/// Handles all non-query database concerns at startup: applying pending migrations,
/// ensuring seed data is present, and setting <c>ISessionContext.SourceId</c> to the
/// System source's ID. Call <see cref="InitializeAsync"/> once before any repositories
/// are used.
/// </summary>
public interface IDatabaseManager
{
    /// <summary>Runs <see cref="MigrateAsync"/> then <see cref="SeedAsync"/>.</summary>
    Task InitializeAsync();
}
