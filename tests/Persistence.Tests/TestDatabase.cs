using Microsoft.Data.Sqlite;
using Persistence.Data;

namespace Persistence.Tests;

/// <summary>
/// Helpers for tearing down a test's temporary SQLite database.
/// </summary>
internal static class TestDatabase
{
    /// <summary>
    /// Releases the connection pool for a single test database and deletes its temp file.
    /// <para>
    /// The pool clear is scoped to just this database's connection string — deliberately NOT the
    /// process-global <see cref="SqliteConnection.ClearAllPools"/>. That global clear is what made the
    /// suite flaky: with test classes running in parallel, one class's teardown would dispose pooled
    /// connections another class was mid-query on, surfacing as "Cannot access a disposed object".
    /// Scoping the clear to this test's own database removes that cross-class race. The pool is keyed
    /// by connection string, so we build it via the same <see cref="SqliteConnectionString"/> the
    /// repositories use, to be sure we target the right pool.
    /// </para>
    /// <para>
    /// Deletion is best-effort (with a finalizer-nudged retry for connections awaiting finalization):
    /// a temp file that still won't delete is harmless — it lives in the OS temp directory and is
    /// reclaimed there — so teardown never throws and fails an unrelated test over it.
    /// </para>
    /// </summary>
    public static void Cleanup(string databasePath)
    {
        using (var connection = new SqliteConnection(SqliteConnectionString.For(databasePath)))
        {
            SqliteConnection.ClearPool(connection);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }

                return;
            }
            catch (IOException)
            {
                // A connection may still be awaiting finalization; release it and retry.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }
}
