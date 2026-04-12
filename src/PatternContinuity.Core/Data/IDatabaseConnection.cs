namespace Persistence.Data
{
    /// <summary>
    /// Interface for database operations
    /// </summary>
    public interface IDatabaseConnection
    {
        /// <summary>
        /// Execute a non-query SQL command
        /// </summary>
        Task ExecuteAsync(string sql, object? param = null);

        /// <summary>
        /// Query for a list of results
        /// </summary>
        Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);

        /// <summary>
        /// Query for a single result, or default if not found
        /// </summary>
        Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null);
    }
}
