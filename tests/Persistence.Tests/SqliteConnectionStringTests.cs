using Microsoft.Data.Sqlite;
using Persistence.Data;

namespace Persistence.Tests;

/// <summary>
/// Connections opened for the store must carry the concurrency PRAGMAs so a second process (Console +
/// API server) rides out a lock instead of hard-failing. This is interim hardening, not the
/// single-owner model — but the lock-failure edge is what actually bit in practice.
/// </summary>
public class SqliteConnectionStringTests
{
    [Fact]
    public async Task OpenAsyncPutsTheDatabaseInWalModeWithABusyTimeout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"persistence-wal-{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = await SqliteConnectionString.OpenAsync(SqliteConnectionString.For(path));

            await using var journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", ((string?)await journal.ExecuteScalarAsync())?.ToLowerInvariant());

            await using var busy = connection.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout;";
            Assert.Equal(5000L, Assert.IsType<long>(await busy.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(f);
            }
        }
    }
}
