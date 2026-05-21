namespace Persistence.Utilities;

/// <summary>
/// Manages embedded resources
/// </summary>
public interface IEmbeddedResourceManager
{
    /// <summary>
    /// Get all sql files in the Data/Migrations folder, except Bootstrap.sql
    /// </summary>
    Task<OrderedDictionary<string, string>> GetMigrationsAsync();

    /// <summary>
    /// Get the DB bootstrap script
    /// </summary>
    Task<string?> GetBootstrapScriptAsync();

    /// <summary>
    /// Get the fragment seeds json string
    /// </summary>
    Task<string?> GetFragmentSeedsAsync();
}