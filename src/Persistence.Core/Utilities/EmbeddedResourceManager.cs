using Persistence.DI;
using System.Reflection;

namespace Persistence.Utilities;

/// <summary>
/// Manages embedded resources
/// </summary>
[Singleton]
public class EmbeddedResourceManager : IEmbeddedResourceManager
{
    /// <summary>
    /// Get all sql files in the Data/Migrations folder, except Bootstrap.sql
    /// </summary>
    public async Task<OrderedDictionary<string, string>> GetMigrationsAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Sort by name so migrations apply in their numeric-prefix order (000_, 001_, ...).
        // GetManifestResourceNames() order is unspecified, so this ordering is load-bearing.
        var migrationNames = assembly.GetManifestResourceNames()
            .Where(x =>
                x.Contains("Data.Migrations") &&
                !x.EndsWith("Bootstrap.sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.Ordinal);

        var migrations = new OrderedDictionary<string, string>();

        foreach (var name in migrationNames)
        {
            using var stream = assembly.GetManifestResourceStream(name);

            if (stream == null)
            {
                // This should never happen
                continue;
            }

            using var reader = new StreamReader(stream);

            migrations.Add(name, await reader.ReadToEndAsync());
        }

        return migrations;
    }

    /// <summary>
    /// Get the DB bootstrap script
    /// </summary>
    public async Task<string?> GetBootstrapScriptAsync() =>
        await GetStringContentByFilenameAsync("Bootstrap.sql");

    /// <summary>
    /// Get the fragment seeds json string
    /// </summary>
    public async Task<string?> GetFragmentSeedsAsync() =>
        await GetStringContentByFilenameAsync("fragment_seeds.json");

    /// <summary>
    /// Get the file content of the given embedded resource as a string
    /// </summary>
    private async Task<string?> GetStringContentByFilenameAsync(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();

        var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith(filename, StringComparison.OrdinalIgnoreCase));

        using var stream = assembly.GetManifestResourceStream(name);

        if (stream == null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync();
    }
}
