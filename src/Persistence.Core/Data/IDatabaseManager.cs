namespace Persistence.Data;

/// <summary>
/// Handles all non-query database concerns at startup: applying pending migrations
/// and ensuring seed data is present. Call <see cref="InitializeAsync"/> once before
/// any repositories are used.
/// </summary>
public interface IDatabaseManager
{
    /// <summary>
    /// Runs migrations and creates seed sources.
    /// </summary>
    Task InitializeAsync();
}
