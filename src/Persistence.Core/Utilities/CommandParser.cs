using System.Text.Json.Nodes;

namespace Persistence.Utilities;

public static class CommandParser
{
    /// <summary>
    /// Extracts commands from the data payload. Accepts an array of command objects,
    /// an object with a "commands" property that is an array of command objects, or a single command object.
    /// </summary>
    public static IEnumerable<(string Command, JsonObject? Fields)> Parse(JsonNode? data)
    {
        JsonArray? commandArray = null;

        if (data is JsonArray topLevelArray)
        {
            commandArray = topLevelArray;
        }
        else if (data?["commands"] is JsonArray nodeArray)
        {
            commandArray = nodeArray;
        }
        else if (data != null)
        {
            // Single command passed as the data object itself
            commandArray = [data];
        }

        if (commandArray is not null)
        {
            return commandArray.Where(x => x != null).Select((Func<JsonNode?, (string, JsonObject?)>)(commandParent =>
            {
                // A command is an object with a single property: { "commandName": { ...fields } }.
                // Anything else (empty object, array, bare value) is unparseable.
                var command = commandParent is JsonObject { Count: > 0 } obj ? obj[0] : null;

                if (command is null)
                {
                    return ("error", null);
                }

                var commandType = command.GetPropertyName().ToLowerInvariant();

                return (commandType, command.AsObject());
            }));
        }

        return [("error", null)];
    }
}
