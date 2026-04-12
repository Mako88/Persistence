using Dapper;
using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.DI;

namespace Persistence.Data
{
    /// <summary>
    /// SQLite implementation of database operations
    /// </summary>
    [Singleton]
    public class DatabaseConnection : IDatabaseConnection
    {
        private readonly string _connectionString;

        /// <summary>
        /// Constructor
        /// </summary>
        public DatabaseConnection(IAppConfig config)
        {
            _connectionString = $"Data Source={config.DatabasePath}";
        }

        /// <summary>
        /// Execute a non-query SQL command
        /// </summary>
        public async Task ExecuteAsync(string sql, object? param = null)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(sql, param);
        }

        /// <summary>
        /// Query for a list of results
        /// </summary>
        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            return await connection.QueryAsync<T>(sql, param);
        }

        /// <summary>
        /// Query for a single result, or default if not found
        /// </summary>
        public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            return await connection.QueryFirstOrDefaultAsync<T?>(sql, param);
        }
    }
}
