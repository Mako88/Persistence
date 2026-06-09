namespace Persistence.Data;

/// <summary>
/// Single source of the SQLite connection string format, so the same options (notably
/// <c>Foreign Keys=True</c>) are used everywhere a connection is opened — repositories, the
/// database manager, and any service that needs to span repositories in one transaction.
/// </summary>
internal static class SqliteConnectionString
{
    public static string For(string databasePath) => $"Data Source={databasePath};Foreign Keys=True;";
}
