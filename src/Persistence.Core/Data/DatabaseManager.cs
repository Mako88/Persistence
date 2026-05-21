using Dapper;
using InterpolatedSql.Dapper;
using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Extensions;
using Persistence.Runtime;
using Persistence.Utilities;
using System.Text.Json;

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
    private readonly IContextFragmentRepository contextFragmentRepository;

    /// <summary>
    /// Constructor
    /// </summary>
    public DatabaseManager(IAppConfig config,
        ISessionContext sessionContext,
        IEmbeddedResourceManager resourceManager,
        ISourceRepository sourceRepository,
        IContextFragmentRepository contextFragmentRepository)
    {
        connectionString = $"Data Source={config.DatabasePath};Foreign Keys=True;";
        this.sessionContext = sessionContext;
        this.resourceManager = resourceManager;
        this.sourceRepository = sourceRepository;
        this.contextFragmentRepository = contextFragmentRepository;
    }

    /// <summary>
    /// Creates / Migrates / Seeds the DB
    /// </summary>
    public async Task InitializeAsync()
    {
        await MigrateAsync();
        await SeedAsync();
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
            _ = await connection.ExecuteAsync(bootstrapScript);
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

        _ = await connection.ExecuteAsync(sql, transaction: transaction);

        _ = await connection
            .SqlBuilder($"INSERT INTO Migrations (Name, AppliedUtc) VALUES ({name}, {DateTimeOffset.UtcNow})")
            .ExecuteAsync(transaction);

        transaction.Commit();
    }

    /// <summary>
    /// Seed initial data into the database if not already present
    /// </summary>
    private async Task SeedAsync()
    {
        await sourceRepository.CreateSystemSourceIfNotExists();

        await SeedInitialFragmentsAsync();
    }

    /// <summary>
    /// Seed any initial fragments defined in embedded resources
    /// </summary>
    private async Task SeedInitialFragmentsAsync()
    {
        var json = await resourceManager.GetFragmentSeedsAsync();

        if (!json.HasValue())
        {
            return;
        }

        var seeds = JsonSerializer.Deserialize<List<FragmentSeed>>(json);

        if (seeds == null || seeds.Count == 0)
        {
            return;
        }

        var existing = await contextFragmentRepository.GetByTypeAsync(ContextFragmentType.System);
        var existingContent = existing.Select(f => f.Content).ToHashSet();

        foreach (var seed in seeds)
        {
            if (existingContent.Contains(seed.Content))
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;

            await contextFragmentRepository.SaveAsync(new ContextFragmentEntity
            {
                FragmentType = ContextFragmentType.System,
                Status = ContextFragmentStatus.Active,
                Content = seed.Content,
                Importance = 1.0f,
                Confidence = 1.0f,
                IsProtected = true,
                CreatedUtc = now,
                LastModifiedUtc = now,
            });
        }
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

    /// <summary>
    /// DTO for deserialising seed_fragments.json entries
    /// </summary>
    private sealed class FragmentSeed
    {
        public required string Content { get; set; }
    }
}
