using Persistence.DI;
using Persistence.Runtime.ActionHandlers;
using System.Text;

namespace Persistence.Runtime;

/// <summary>
/// Aggregates the commands across every command handler into one compact listing. Built from the
/// same <see cref="CommandAttribute"/> metadata that backs dispatch and <c>list()</c>, so a new
/// command surfaces automatically — no central list to maintain.
/// </summary>
[Singleton(typeof(ICommandCatalog))]
public class CommandCatalog : ICommandCatalog
{
    // The concrete command-handler types. Listed explicitly (rather than resolved from DI) because
    // the catalog is a pure metadata view: it must not construct handlers — which would pull in
    // repositories/DB — just to read their attributes. A unit test guards that every concrete
    // CommandHandler subclass in the assembly appears here, so an omission fails loudly.
    private static readonly Type[] HandlerTypes =
    [
        typeof(ManageContextHandler),
        typeof(ExecuteActionsHandler),
    ];

    // Command attributes are static, so the listing is computed once.
    private static readonly Lazy<string> Listing = new(BuildListing);

    /// <inheritdoc />
    public string GetCompactListing() => Listing.Value;

    private static string BuildListing()
    {
        var commands = HandlerTypes
            .SelectMany(CommandHandler.DescribeCommands)
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase) // de-dupe if two handlers share a name
            .Select(g => g.First())
            .OrderBy(c => c.Name, StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine("[Commands] (compact — send list() for full field schemas)");

        foreach (var command in commands)
        {
            // Required fields first, optional suffixed with '?'. Same signature shape as list().
            var signature = string.Join(", ", command.Fields
                .OrderByDescending(f => f.Required)
                .Select(f => f.Required ? f.Name : $"{f.Name}?"));

            sb.AppendLine($"  {command.Name}({signature}) — {command.Description}");
        }

        return sb.ToString().TrimEnd();
    }
}
