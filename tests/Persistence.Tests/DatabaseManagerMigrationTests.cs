using Dapper;
using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Repositories;
using Persistence.Runtime;
using Persistence.Utilities;

namespace Persistence.Tests;

/// <summary>
/// Migration application must be idempotent: re-running a migration against a store that already has its
/// changes (e.g. one created out-of-band with the final schema but incomplete migration bookkeeping — the
/// ChatGPT importer did exactly this) must be a no-op, not a crash. Before the fix, the app re-ran
/// <c>001_NarrowSoftDelete</c>'s <c>DROP COLUMN IsDeleted</c> and died with "no such column".
/// </summary>
public sealed class DatabaseManagerMigrationTests : IAsyncLifetime
{
    private string dbPath = null!;

    public Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();
        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-mig-{Guid.NewGuid():N}.db");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        TestDatabase.Cleanup(dbPath);
        return Task.CompletedTask;
    }

    private DatabaseManager NewManager()
    {
        var config = new AppConfig { DatabasePath = dbPath };
        var session = new SessionContext();
        return new DatabaseManager(config, session, new EmbeddedResourceManager(), new SourceRepository(config, session));
    }

    [Fact]
    public async Task ReRunningMigrationsOnAnAlreadyMigratedStoreIsANoOpNotACrash()
    {
        // 1) Fresh store — migrations applied normally.
        await NewManager().InitializeAsync();

        // 2) Simulate a store whose migration bookkeeping doesn't match what the app records (the importer
        //    case): wipe the Migrations rows so the app believes nothing is applied and re-runs everything
        //    against the already-final schema.
        SqliteConnection.ClearAllPools();
        await using (var conn = new SqliteConnection(SqliteConnectionString.For(dbPath)))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM Migrations");
        }
        SqliteConnection.ClearAllPools();

        // 3) Re-initialize. The runner re-runs 001's DROP COLUMN (and every other migration) against a
        //    schema where those changes already exist — this must not throw.
        var ex = await Record.ExceptionAsync(() => NewManager().InitializeAsync());
        Assert.Null(ex);

        // ...and the migrations are recorded again, so they aren't retried on the next boot.
        SqliteConnection.ClearAllPools();
        await using var check = new SqliteConnection(SqliteConnectionString.For(dbPath));
        await check.OpenAsync();
        var applied = await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Migrations");
        Assert.True(applied >= 6, $"expected the 6 migrations re-recorded, saw {applied}");
    }
}
