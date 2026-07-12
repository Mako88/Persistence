using Microsoft.Data.Sqlite;

namespace Persistence.Data;

/// <summary>
/// Single source of the SQLite connection string format, so the same options (notably
/// <c>Foreign Keys=True</c>) are used everywhere a connection is opened — repositories, the
/// database manager, and any service that needs to span repositories in one transaction.
/// </summary>
internal static class SqliteConnectionString
{
    /// <summary>
    /// How long a connection waits for a held write lock before giving up with SQLITE_BUSY. With
    /// more than one process touching the store (e.g. the Console and the API server), a short wait
    /// lets a second writer ride out the first's transaction instead of throwing. This does NOT make
    /// concurrent writes safe from lost updates — that needs the single-owner model (see docs/TODO.md).
    /// </summary>
    private const int BusyTimeoutMs = 5000;

    public static string For(string databasePath) => $"Data Source={databasePath};Foreign Keys=True;";

    /// <summary>
    /// Opens a connection and applies the concurrency PRAGMAs: WAL journaling (readers don't block the
    /// writer, and vice versa) and a busy timeout (a second writer waits for the lock rather than
    /// failing immediately). WAL persists in the database file; the busy timeout is per-connection, so
    /// both are (re)applied on every open. Prefer this over <c>new SqliteConnection(...).OpenAsync()</c>.
    /// </summary>
    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={BusyTimeoutMs};";
        await pragma.ExecuteNonQueryAsync(ct);

        return connection;
    }
}
