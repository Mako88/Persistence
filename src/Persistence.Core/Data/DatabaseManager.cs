using Dapper;
using InterpolatedSql.Dapper;
using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Runtime;
using Persistence.Utilities;

namespace Persistence.Data;

/// <summary>
/// Handles all non-query database concerns
/// </summary>
[Singleton]
public class DatabaseManager : IDatabaseManager
{
    private readonly string connectionString;
    private readonly ISessionContext sessionContext;
    private readonly IEmbeddedResourceManager resourceManager;
    private readonly ISourceRepository sourceRepository;

    /// <summary>
    /// Constructor
    /// </summary>
    public DatabaseManager(IAppConfig config,
        ISessionContext sessionContext,
        IEmbeddedResourceManager resourceManager,
        ISourceRepository sourceRepository)
    {
        connectionString = SqliteConnectionString.For(config.DatabasePath);

        // SQLite won't create missing parent directories, so a DatabasePath like "dbs/qwen.db"
        // would fail on a fresh checkout. Ensure the folder exists.
        var dbDir = Path.GetDirectoryName(config.DatabasePath);
        if (!string.IsNullOrEmpty(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

        this.sessionContext = sessionContext;
        this.resourceManager = resourceManager;
        this.sourceRepository = sourceRepository;
    }

    /// <summary>
    /// Creates / Migrates / Seeds the DB
    /// </summary>
    public async Task InitializeAsync()
    {
        await MigrateAsync();
        await sourceRepository.CreateSystemSourceIfNotExists();
        await sourceRepository.CreateRemotePeerSourceIfNotExists();
    }

    /// <summary>
    /// Migrates the DB
    /// </summary>
    private async Task MigrateAsync()
    {
        await using var connection = await OpenConnectionAsync();

        // Bootstrap the Migrations tracking table before querying it.
        var bootstrapScript = await resourceManager.GetBootstrapScriptAsync();

        if (bootstrapScript is not null)
        {
            await connection.ExecuteAsync(bootstrapScript);
        }

        var applied = await GetAppliedMigrationsAsync(connection);

        foreach (var (migrationName, migrationScript) in await resourceManager.GetMigrationsAsync())
        {
            if (applied.Contains(migrationName))
            {
                continue;
            }

            await ApplyMigrationAsync(migrationName, migrationScript, connection);
        }
    }

    /// <summary>
    /// Get already-applied migrations
    /// </summary>
    private static async Task<HashSet<string>> GetAppliedMigrationsAsync(SqliteConnection connection) =>
        (await connection.QueryAsync<string>("SELECT Name FROM Migrations")).ToHashSet();

    /// <summary>
    /// Applies a migration, then records it. Idempotent: if the migration's changes are already present
    /// (its DDL raises a benign "already applied" error — duplicate column / no such column / already
    /// exists), it's recorded without re-running rather than crashing. This matters for a store created
    /// out-of-band with the final schema but incomplete migration bookkeeping — e.g. the ChatGPT importer,
    /// which built the full schema but recorded migration names the app didn't recognise, so the app
    /// re-ran them and died on <c>001</c>'s <c>DROP COLUMN</c>. SQLite can't express <c>IF EXISTS</c> for
    /// columns and migrations are append-only, so this re-run safety lives here, not in the scripts.
    /// </summary>
    private async Task ApplyMigrationAsync(string name, string sql, SqliteConnection connection)
    {
        using (var transaction = connection.BeginTransaction())
        {
            try
            {
                await connection.ExecuteAsync(sql, transaction: transaction);
                await connection
                    .SqlBuilder($"INSERT INTO Migrations (Name, AppliedUtc) VALUES ({name}, {DateTimeOffset.UtcNow})")
                    .ExecuteAsync(transaction);
                transaction.Commit();
                return;
            }
            catch (SqliteException ex) when (IsAlreadyApplied(ex))
            {
                transaction.Rollback();
                Console.Error.WriteLine(
                    $"[migrations] '{name}' already applied to this store (schema present); recording without re-running — {ex.Message.Split('\n')[0]}");
            }
        }

        // Record the already-applied migration outside the rolled-back transaction so it isn't retried.
        using var record = connection.BeginTransaction();
        await connection
            .SqlBuilder($"INSERT INTO Migrations (Name, AppliedUtc) VALUES ({name}, {DateTimeOffset.UtcNow})")
            .ExecuteAsync(record);
        record.Commit();
    }

    /// <summary>
    /// Whether a migration failure means its changes are already present — the schema is in the target
    /// state — so re-applying is a no-op. SQLite raises these when re-running already-applied DDL:
    /// re-adding a column ("duplicate column name"), re-dropping one ("no such column"), or re-creating a
    /// table/index/trigger ("already exists"). Anything else is a real error and propagates.
    /// </summary>
    private static bool IsAlreadyApplied(SqliteException ex) =>
        ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Open a connection to the database
    /// </summary>
    private Task<SqliteConnection> OpenConnectionAsync() =>
        SqliteConnectionString.OpenAsync(connectionString);

}
