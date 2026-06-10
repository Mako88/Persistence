using Persistence.Runtime;
using System.Reflection;

namespace Persistence.Tests;

public class CommandCatalogTests
{
    private static readonly string Listing = new CommandCatalog().GetCompactListing();

    [Fact]
    public void ListingAggregatesCommandsFromBothHandlers()
    {
        // 'add' lives on ManageContextHandler; 'schedule' on ExecuteActionsHandler — both present
        // proves the catalog spans every command handler, not just one.
        Assert.Contains("add(", Listing);
        Assert.Contains("schedule(", Listing);
        Assert.StartsWith("[Commands]", Listing);
    }

    [Fact]
    public void ListingIsCompactOneLinePerCommandNotFullSchema()
    {
        // The compact form shows a signature line but NOT the per-field detail blocks that list()
        // emits (e.g. "(string, required)"). This is the whole point of "compact".
        var addLine = Listing.Split('\n').Single(l => l.TrimStart().StartsWith("add("));

        Assert.Contains("add(content", addLine);          // signature includes the required field name
        Assert.DoesNotContain("(string, required)", Listing); // but not the full field schema
    }

    [Fact]
    public void RequiredFieldsPrecedeOptionalInTheSignature()
    {
        // 'add' has one required field (content) and several optional ones — required first, optionals
        // suffixed with '?'.
        var addLine = Listing.Split('\n').Single(l => l.TrimStart().StartsWith("add("));

        var requiredField = addLine.IndexOf("content", StringComparison.Ordinal);
        var firstOptional = addLine.IndexOf("?", StringComparison.Ordinal);

        Assert.True(requiredField >= 0 && firstOptional >= 0);
        Assert.DoesNotContain("content?", addLine); // the required field is NOT marked optional
        Assert.True(requiredField < firstOptional, "required field should appear before optional ('?') fields");
    }

    [Fact]
    public void EveryConcreteCommandHandlerSubclassIsRepresented()
    {
        // Guards the explicit HandlerTypes array in CommandCatalog: if a new CommandHandler subclass
        // is added but not registered in the catalog, this fails loudly.
        var handlerTypes = typeof(CommandHandler).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false } && typeof(CommandHandler).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(handlerTypes);

        foreach (var handler in handlerTypes)
        {
            var commands = CommandHandler.DescribeCommands(handler);
            // Each handler exposes at least one command, and at least one of its commands appears
            // in the aggregated listing.
            Assert.NotEmpty(commands);
            Assert.Contains(commands, c => Listing.Contains($"{c.Name}("));
        }
    }
}
