using Persistence.Data.Entities;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Utilities;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Persistence.Runtime;

/// <summary>
/// Base class for action handlers that dispatch commands discovered via <see cref="CommandAttribute"/>.
/// Subclasses define command methods with the attribute — the base handles parsing, dispatch,
/// listing, and error formatting. A built-in "list" command returns all available commands and schemas.
/// </summary>
public abstract class CommandHandler : IActionHandler
{
    private static readonly ConcurrentDictionary<Type, Dictionary<string, CommandInfo>> Cache = new();

    private readonly Dictionary<string, CommandInfo> commands;
    private readonly IEventBus eventBus;

    /// <summary>
    /// Constructor
    /// </summary>
    protected CommandHandler(IEventBus eventBus)
    {
        commands = Cache.GetOrAdd(GetType(), DiscoverCommands);
        this.eventBus = eventBus;
    }

    /// <summary>
    /// Parses and dispatches each command in the data payload in order, publishing a tool-invoked
    /// event per command and recording the combined results as an action-response fragment
    /// </summary>
    public async Task HandleAsync(WorkingContextEntity context, JsonNode? data, CancellationToken ct = default)
    {
        var parsed = CommandParser.Parse(data);
        var results = new StringBuilder();

        foreach (var (type, fields) in parsed)
        {
            var result = await DispatchAsync(context, type, fields, ct);
            results.AppendLine(result);
            await eventBus.PublishAsync(this, new ToolInvoked(type, fields?.ToJsonString() ?? "{}", result));
        }

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ActionResponse,
            Status = ContextFragmentStatus.Active,
            Content = results.ToString().TrimEnd(),
            Importance = 1.0f,
            Confidence = 1.0f,
            Relevance = 1.0f,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Parses an ID from a JsonNode, accepting a numeric value or a string like "#123" or "123"
    /// </summary>
    protected static long? ParseId(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var id))
            {
                return id;
            }

            if (value.TryGetValue<string>(out var str))
            {
                str = str.TrimStart('#').Trim();

                if (long.TryParse(str, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    #region Private

    private async Task<string> DispatchAsync(
        WorkingContextEntity context, string type, JsonObject? fields, CancellationToken ct)
    {
        if (type == "list")
        {
            return FormatCommandList();
        }

        if (type == "error")
        {
            return "That didn't look like a command. Each command is a function call like " +
                   "name(field=value). Send a `list` command to see everything available and its fields.";
        }

        // A call the parser couldn't read (e.g. a typo'd bracket or a missing value). The synthetic
        // command carries a plain-language reason and the offending text so the peer can correct it.
        if (type == FunctionCallParser.ErrorCommandName)
        {
            return FormatParseError(fields);
        }

        if (!commands.TryGetValue(type, out var info))
        {
            var available = string.Join(", ", commands.Keys.Order());
            var suggestion = ClosestMatch(type, commands.Keys);
            var didYouMean = suggestion != null ? $" Did you mean '{suggestion}'?" : "";
            return $"Unknown command: '{type}'.{didYouMean} Available: {available}. " +
                   "Send a `list` command for full schemas.";
        }

        string result;

        try
        {
            result = await (Task<string>)info.Method.Invoke(this, [context, (JsonNode?)fields, ct])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            result = FormatError(type, info, fields, Humanize(ex.InnerException.Message));
        }
        catch (Exception ex)
        {
            result = FormatError(type, info, fields, Humanize(ex.Message));
        }

        return AppendUnknownFieldHint(result, info, fields);
    }

    /// <summary>
    /// Translates implementation-jargon exception messages into the peer's vocabulary. The most
    /// common is a JSON type mismatch (e.g. passing a string where a number is expected), whose
    /// raw message names CLR types ("System.String cannot be converted to 'System.Int64'") that
    /// mean nothing to the remote peer.
    /// </summary>
    private static string Humanize(string message)
    {
        // Matches the System.Text.Json message: "... type 'System.String' ... to a 'System.Int64'."
        var match = System.Text.RegularExpressions.Regex.Match(
            message, @"type '(?<from>System\.\w+)'.*to a '(?<to>System\.\w+)'");

        if (match.Success)
        {
            return $"a value of type {FriendlyType(match.Groups["from"].Value)} can't be used where " +
                   $"{FriendlyType(match.Groups["to"].Value)} is expected";
        }

        return message;
    }

    private static string FriendlyType(string clrType) => clrType switch
    {
        "System.Int64" or "System.Int32" => "a whole number",
        "System.Double" or "System.Single" => "a number",
        "System.Boolean" => "true/false",
        "System.String" => "text",
        _ => clrType,
    };

    /// <summary>
    /// Formats the feedback for a call the parser couldn't read, surfacing the plain-language
    /// reason and the offending text the peer wrote so it can fix that specific call.
    /// </summary>
    private static string FormatParseError(JsonObject? fields)
    {
        var message = fields?["message"]?.GetValue<string>() ?? "it could not be read";
        var text = fields?["text"]?.GetValue<string>();

        var sb = new StringBuilder();
        sb.AppendLine($"Couldn't parse a command: {message}.");

        if (!string.IsNullOrWhiteSpace(text))
        {
            sb.AppendLine($"  In: {text}");
        }

        sb.Append("  Commands are function calls like name(field=value), one per line. " +
                  "Send a `list` command to see them all.");
        return sb.ToString();
    }

    /// <summary>
    /// Appends a note when a command was given field names it doesn't define — these are silently
    /// ignored otherwise, so a typo'd field looks like it worked. Offers a closest-match suggestion
    /// per unknown field and lists what the command actually accepts.
    /// </summary>
    private static string AppendUnknownFieldHint(string result, CommandInfo info, JsonObject? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return result;
        }

        var known = info.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = fields.Select(kvp => kvp.Key).Where(k => !known.Contains(k)).ToList();

        if (unknown.Count == 0)
        {
            return result;
        }

        var hints = unknown.Select(u =>
        {
            var match = ClosestMatch(u, info.Fields.Select(f => f.Name));
            return match != null ? $"'{u}' (did you mean '{match}'?)" : $"'{u}'";
        });

        var accepts = info.Fields.Length > 0
            ? string.Join(", ", info.Fields.Select(f => f.Name))
            : "no fields";

        return $"{result}\n  Note: ignored unknown field(s): {string.Join(", ", hints)}. " +
               $"{info.Name} accepts: {accepts}.";
    }

    /// <summary>
    /// Returns the candidate closest to <paramref name="input"/> by edit distance, or null when
    /// nothing is near enough to be a likely typo (guards against misleading suggestions).
    /// </summary>
    private static string? ClosestMatch(string input, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = LevenshteinDistance(input.ToLowerInvariant(), candidate.ToLowerInvariant());

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        // Only suggest when the edit distance is small relative to the input — a far-off match is
        // noise, not a helpful "did you mean".
        return bestDistance <= Math.Max(2, input.Length / 2) ? best : null;
    }

    /// <summary>
    /// Standard Levenshtein edit distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private string FormatCommandList()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Available commands ({commands.Count}):");

        foreach (var (name, info) in commands.OrderBy(c => c.Key))
        {
            sb.AppendLine();

            // Lead with a call-signature line so the shape is obvious at a glance, then detail
            // each field. Required fields come first in the signature.
            var signatureFields = info.Fields
                .OrderByDescending(f => f.Required)
                .Select(f => f.Required ? f.Name : $"{f.Name}?");
            sb.AppendLine($"  {name}({string.Join(", ", signatureFields)}) — {info.Description}");

            foreach (var field in info.Fields)
            {
                var parts = new List<string> { field.Type };
                if (field.Required) parts.Add("required");
                if (field.Default != null) parts.Add($"default: {field.Default}");
                var desc = field.Description != null ? $" — {field.Description}" : "";
                sb.AppendLine($"    {field.Name} ({string.Join(", ", parts)}){desc}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatError(string commandName, CommandInfo info, JsonObject? received, string message)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Error executing '{commandName}': {message}");

        // Describe the command's fields format-neutrally (the peer may be writing function-call
        // or JSON syntax — don't presume one), and echo what was actually received so the peer
        // can see the mismatch.
        if (info.Fields.Length > 0)
        {
            var fields = string.Join(", ", info.Fields.Select(f =>
            {
                var req = f.Required ? ", required" : "";
                return $"{f.Name} ({f.Type}{req})";
            }));
            sb.AppendLine($"  Accepts: {commandName}({fields})");
        }
        else
        {
            sb.AppendLine($"  Accepts: {commandName}() — no fields");
        }

        sb.AppendLine($"  You sent: {FormatReceivedFields(received)}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatReceivedFields(JsonObject? received)
    {
        if (received is null || received.Count == 0)
        {
            return "(no fields)";
        }

        return string.Join(", ", received.Select(kvp => $"{kvp.Key}={kvp.Value?.ToJsonString() ?? "null"}"));
    }

    private static Dictionary<string, CommandInfo> DiscoverCommands(Type handlerType)
    {
        var result = new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);

        var methods = handlerType.GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<CommandAttribute>();

            if (attr == null)
            {
                continue;
            }

            var fields = method.GetCustomAttributes<CommandFieldAttribute>().ToArray();

            result[attr.Name] = new CommandInfo(attr.Name, attr.Description, fields, method);
        }

        return result;
    }

    private record CommandInfo(string Name, string Description, CommandFieldAttribute[] Fields, MethodInfo Method);

    #endregion
}
