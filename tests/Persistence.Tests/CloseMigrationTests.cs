using Dapper;
using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Repositories;
using Persistence.Runtime;
using Persistence.Utilities;

namespace Persistence.Tests;

/// <summary>
/// Close's migrations (build plan A1) continue through a column that is already there. The runner records a
/// migration that raises "duplicate column name" as applied without running the rest of it, so one file holding
/// all six ADD COLUMNs would, on a store that already had one, silently leave the other five out (Ember, room
/// 1575337029846179952). With one ADD COLUMN per file, only that file is recorded unrun; every later one really
/// runs. Mutation: merge 014 to 019 into one file and both tests go red (columns missing, migration recorded).
/// In the serial collection because it reads the runner's stderr line, which is process-global.
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class CloseMigrationTests : IAsyncLifetime
{
    private static readonly string[] Columns = ["ClosedUtc", "Reason", "EndedBy", "ReopenReason", "IsState", "SplitFromId"];
    private static readonly string[] Indexes = ["IX_ContextFragments_SplitFromId", "IX_ContextFragments_ClosedUtc"];
    private static readonly string[] CloseMigrations =
        ["014_AddClosedUtc", "015_AddCloseReason", "016_AddEndedBy", "017_AddReopenReason", "018_AddIsState", "019_AddSplitFromId", "020_AddCloseIndexes"];

    private string dbPath = null!;

    public Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();
        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-close-mig-{Guid.NewGuid():N}.db");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        TestDatabase.Cleanup(dbPath);
        return Task.CompletedTask;
    }

    /// <summary>The app's migrations, up to and including <paramref name="through"/> (by numeric prefix).</summary>
    private sealed class MigrationsThrough(int through) : IEmbeddedResourceManager
    {
        private readonly EmbeddedResourceManager inner = new();

        public async Task<OrderedDictionary<string, string>> GetMigrationsAsync()
        {
            var kept = new OrderedDictionary<string, string>();
            foreach (var (name, sql) in await inner.GetMigrationsAsync())
            {
                var file = name[(name.IndexOf("Data.Migrations.", StringComparison.Ordinal) + "Data.Migrations.".Length)..];
                if (int.Parse(file[..3]) <= through)
                {
                    kept.Add(name, sql);
                }
            }

            return kept;
        }

        public Task<string?> GetBootstrapScriptAsync() => inner.GetBootstrapScriptAsync();

        public Task<string?> GetFragmentSeedsAsync() => inner.GetFragmentSeedsAsync();
    }

    private DatabaseManager Manager(IEmbeddedResourceManager resources)
    {
        var config = new AppConfig { DatabasePath = dbPath };
        var session = new SessionContext();
        return new DatabaseManager(config, session, resources, new SourceRepository(config, session));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new SqliteConnection(SqliteConnectionString.For(dbPath));
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql);
    }

    [Theory]
    [InlineData("ClosedUtc TEXT NULL", "014_AddClosedUtc")]
    [InlineData("SplitFromId INTEGER NULL", "019_AddSplitFromId")]
    public async Task A_column_already_present_records_only_its_own_migration_and_the_rest_really_run(string column, string present)
    {
        // A store at 013, then one of close's columns added by hand, then the app's migrations.
        await Manager(new MigrationsThrough(13)).InitializeAsync();
        await ExecuteAsync($"ALTER TABLE ContextFragments ADD COLUMN {column};");

        var stderr = new StringWriter();
        var was = System.Console.Error;
        System.Console.SetError(stderr);
        try
        {
            await Manager(new EmbeddedResourceManager()).InitializeAsync();
        }
        finally
        {
            System.Console.SetError(was);
        }

        await using var conn = new SqliteConnection(SqliteConnectionString.For(dbPath));
        await conn.OpenAsync();
        var columns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('ContextFragments')")).ToHashSet();
        var indexes = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'index'")).ToHashSet();
        var recorded = (await conn.QueryAsync<string>("SELECT Name FROM Migrations")).ToList();

        Assert.All(Columns, c => Assert.Contains(c, columns));
        Assert.All(Indexes, i => Assert.Contains(i, indexes));
        Assert.All(CloseMigrations, m => Assert.Single(recorded, r => r.Contains(m)));

        // Only the file whose column was there is logged as recorded unrun; every other one ran.
        var unrun = stderr.ToString().Split('\n').Where(l => l.Contains("already applied")).ToList();
        Assert.Single(unrun);
        Assert.Contains(present, unrun[0]);
    }
}
