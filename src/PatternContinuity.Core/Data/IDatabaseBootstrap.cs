namespace Persistence.Data
{
    /// <summary>
    /// Interface for database schema initialization and seed data
    /// </summary>
    public interface IDatabaseBootstrap
    {
        /// <summary>
        /// Initialize the database schema, run migrations, and seed data
        /// </summary>
        Task InitializeAsync();
    }
}
