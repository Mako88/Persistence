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
        connectionString = $"Data Source={config.DatabasePath};Foreign Keys=True;";
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
        await sourceRepository.CreateLocalPeerSourceIfNotExists();
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
    /// Apply the given migration with the given name
    /// </summary>
    private async Task ApplyMigrationAsync(string name, string sql, SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(sql, transaction: transaction);

        await connection
            .SqlBuilder($"INSERT INTO Migrations (Name, AppliedUtc) VALUES ({name}, {DateTimeOffset.UtcNow})")
            .ExecuteAsync(transaction);

        transaction.Commit();
    }

    /// <summary>
    /// Open a connection to the database
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

}
