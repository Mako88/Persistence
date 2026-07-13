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
    /// How long a connection waits for a held write lock before giving up with SQLITE_BUSY. Even under the
    /// single-owner model (one API process per store, ADR-0006/0007), the store is touched by more than one
    /// connection at once — the web server's concurrent reads, the wake monitor, and the out-of-process
    /// online backup (<c>scripts/backup-peer.ps1</c>) — so a short wait lets a reader ride out a brief write
    /// rather than failing immediately. This does NOT make concurrent writes safe from lost updates — that
    /// is the single-owner model's job, not this.
    /// </summary>
    private const int BusyTimeoutMs = 5000;

    public static string For(string databasePath) => $"Data Source={databasePath};Foreign Keys=True;";

    /// <summary>
    /// Opens a connection and applies the concurrency PRAGMAs: WAL journaling (readers don't block the
    /// writer, and vice versa) and a busy timeout.
    /// <para>
    /// WAL still earns its place even though this is now single-writer per store (the old two-owner
    /// Console+API race is gone, ADR-0006): (1) the API is a web server, so read paths — the connect-time
    /// snapshot/history, the wake monitor, post-turn refresh queries — can run concurrently with a writing
    /// turn, and WAL lets readers and the one writer proceed without blocking (no SQLITE_BUSY); (2) the
    /// live backup (<c>backup-peer.ps1</c>) opens the store from a separate process while the peer may be
    /// writing, which relies on WAL's readers-don't-block-writer guarantee; (3) it suits the small,
    /// frequent-write turn workload. So this is a "single-writer + concurrent readers + live backup"
    /// default, NOT interim multi-owner scaffolding — don't strip it out as obsolete.
    /// </para>
    /// WAL persists in the database file; the busy timeout is per-connection, so both are (re)applied on
    /// every open. Prefer this over <c>new SqliteConnection(...).OpenAsync()</c>.
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
