using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Services;
using System.Text;
using System.Text.Json.Nodes;

namespace Persistence.Runtime.ActionHandlers;

/// <summary>
/// Handles <see cref="ModelAction.ManageContext"/> by applying a batch of context
/// management commands (add, update, remove, fetch). The <c>data</c> payload is a
/// JSON object with a <c>commands</c> array. Results for all commands are collected
/// into a single <see cref="ContextFragmentType.ActionResponse"/> fragment appended
/// to the working context.
/// </summary>
[Service(registerAsType: typeof(IActionHandler), key: ModelAction.ManageContext)]
public class ManageContextHandler : IActionHandler
{
    private readonly IWorkingContextRepository workingContextRepo;
    private readonly IContextFragmentRepository fragmentRepo;
    private readonly ITagRepository tagRepo;

    /// <summary>
    /// Constructor
    /// </summary>
    public ManageContextHandler(
        IWorkingContextRepository workingContextRepo,
        IContextFragmentRepository fragmentRepo,
        ITagRepository tagRepo)
    {
        this.workingContextRepo = workingContextRepo;
        this.fragmentRepo = fragmentRepo;
        this.tagRepo = tagRepo;
    }

    /// <summary>
    /// Parses the commands array from the model's data payload and executes each
    /// command sequentially. Results are collected and surfaced in a single
    /// ActionResponse fragment.
    /// </summary>
    public async Task HandleAsync(WorkingContextEntity context, JsonNode? data, CancellationToken ct = default)
    {
        var commands = ParseCommands(data);
        var results = new StringBuilder();

        foreach (var command in commands)
        {
            var result = await ExecuteCommandAsync(context, command, ct);
            _ = results.AppendLine(result);
        }

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ActionResponse,
            Status = ContextFragmentStatus.Active,
            Content = results.ToString().TrimEnd(),
            Importance = 1.0f,
            Confidence = 1.0f,
            Weight = 1.0f,

            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        });
    }

    // ── Private ──────────────────────────────────────────────────

    /// <summary>
    /// Extracts the commands array from the data payload. Accepts either a top-level
    /// array or an object with a "commands" property.
    /// </summary>
    private static List<JsonObject> ParseCommands(JsonNode? data)
    {
        if (data is JsonArray topLevelArray)
        {
            return topLevelArray.Where(n => n != null).Select(n => n!.AsObject()).ToList();
        }

        var commandsNode = data?["commands"];

        if (commandsNode is JsonArray commandsArray)
        {
            return commandsArray.Where(n => n != null).Select(n => n!.AsObject()).ToList();
        }

        // Single command passed as the data object itself
        return data != null ? [data.AsObject()] : [];
    }

    /// <summary>
    /// Routes a single command to the appropriate handler based on its "command" property
    /// </summary>
    private async Task<string> ExecuteCommandAsync(
        WorkingContextEntity context, JsonNode commandParent, CancellationToken ct)
    {
        if (commandParent[0] is null)
        {
            return "Could not parse command name. Ensure the command object only has a single property that has the same name as the command and is set to an object with that command's expected fields.";
        }

        var command = commandParent[0]!;

        var commandType = command.GetPropertyName().ToLowerInvariant();

        try
        {
            return commandType switch
            {
                "add" => ExecuteAdd(context, command),
                "update" => ExecuteUpdate(context, command),
                "remove" => await ExecuteRemoveAsync(context, command, ct),
                "fetch" => await ExecuteFetchAsync(command, ct),
                "load" => await ExecuteLoadAsync(context, command, ct),
                "create_tag" => await ExecuteCreateTagAsync(command, ct),
                _ => $"Unknown command: {commandType ?? "(null)"}",
            };
        }
        catch (Exception ex)
        {
            return $"Error executing '{commandType}': {ex.Message}";
        }
    }

    /// <summary>
    /// Adds a new fragment to the working context with the specified properties
    /// </summary>
    private static string ExecuteAdd(WorkingContextEntity context, JsonNode command)
    {
        var content = command["content"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(content))
        {
            return "Add failed: 'content' is required";
        }

        var fragmentTypeName = command["fragmentType"]?.GetValue<string>();
        var (fragmentType, wasRecognised) = ParseFragmentType(fragmentTypeName);
        var importance = command["importance"]?.GetValue<float>() ?? 0.5f;
        var confidence = command["confidence"]?.GetValue<float>() ?? 0.5f;
        var weight = command["weight"]?.GetValue<float>() ?? 1.0f;
        var isProtected = command["isProtected"]?.GetValue<bool>() ?? false;
        var insertAfter = command["insertAfter"]?.GetValue<long?>();
        var summary = command["summary"]?.GetValue<string>();

        var now = DateTimeOffset.UtcNow;

        // Preserve the model's intent if the fragment type wasn't recognised
        if (!wasRecognised && !string.IsNullOrEmpty(fragmentTypeName))
        {
            content = $"[Originally requested as type '{fragmentTypeName}']\n{content}";
        }

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = fragmentType,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Summary = summary,
            Importance = importance,
            Confidence = confidence,
            Weight = weight,
            IsProtected = isProtected,

            CreatedUtc = now,
            LastModifiedUtc = now,
        }, insertAfter);

        return $"Added {fragmentType} fragment";
    }

    /// <summary>
    /// Updates an existing fragment in the working context by ID. Only modifies
    /// properties that are explicitly provided in the command.
    /// </summary>
    private static string ExecuteUpdate(WorkingContextEntity context, JsonNode command)
    {
        var id = command["id"]?.GetValue<long>();

        if (id == null)
        {
            return "Update failed: 'id' is required";
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return $"Update failed: fragment #{id} not found in current context";
        }

        if (fragment.IsProtected)
        {
            return $"Update failed: fragment #{id} is protected";
        }

        if (command["content"] is JsonNode contentNode)
        {
            fragment.Content = contentNode.GetValue<string>();
        }

        if (command["importance"] is JsonNode importanceNode)
        {
            fragment.Importance = importanceNode.GetValue<float>();
        }

        if (command["confidence"] is JsonNode confidenceNode)
        {
            fragment.Confidence = confidenceNode.GetValue<float>();
        }

        if (command["status"] is JsonNode statusNode)
        {
            fragment.Status = ParseStatus(statusNode.GetValue<string>());
        }

        if (command["summary"] is JsonNode summaryNode)
        {
            fragment.Summary = summaryNode.GetValue<string>();
        }

        fragment.LastModifiedUtc = DateTimeOffset.UtcNow;

        return $"Updated fragment #{id}";
    }

    /// <summary>
    /// Removes a fragment from the working context by ID via soft delete
    /// </summary>
    private async Task<string> ExecuteRemoveAsync(
        WorkingContextEntity context, JsonNode command, CancellationToken ct)
    {
        var id = command["id"]?.GetValue<long>();

        if (id == null)
        {
            return "Remove failed: 'id' is required";
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return $"Remove failed: fragment #{id} not found in current context";
        }

        if (fragment.IsProtected)
        {
            return $"Remove failed: fragment #{id} is protected";
        }

        await workingContextRepo.RemoveFragmentAsync(context.Id, id.Value);

        // Also remove from the in-memory collection so subsequent commands
        // in the same batch see the updated state
        var key = context.ContextFragments.FirstOrDefault(kvp => kvp.Value.Id == id.Value).Key;
        _ = context.ContextFragments.Remove(key);

        return $"Removed fragment #{id}";
    }

    /// <summary>
    /// Fetches fragments tagged with the given tag name and formats them as text
    /// for the model to review. Does not add them to the working context — the
    /// model can decide to add specific fragments via subsequent add commands.
    /// </summary>
    private async Task<string> ExecuteFetchAsync(JsonNode command, CancellationToken ct)
    {
        var tagName = command["tag"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(tagName))
        {
            return "Fetch failed: 'tag' is required";
        }

        // Support slash-separated paths like "personality/values"
        var tag = await ResolveTagByPathAsync(tagName);

        if (tag == null)
        {
            return $"Fetch failed: tag '{tagName}' not found";
        }

        var fragments = (await fragmentRepo.GetByTagAsync(tag.Id)).ToList();

        if (fragments.Count == 0)
        {
            return $"No fragments found with tag '{tagName}'";
        }

        var sb = new StringBuilder();
        _ = sb.AppendLine($"Fragments tagged '{tagName}' ({fragments.Count}):");

        foreach (var fragment in fragments)
        {
            _ = sb.AppendLine($"  [#{fragment.Id} | {fragment.FragmentType} | i:{fragment.Importance:F1} c:{fragment.Confidence:F1}]");
            _ = sb.AppendLine($"  {fragment.Content}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Loads existing fragments by ID into the working context. Accepts an "ids" array
    /// of fragment IDs. Each fragment is added with the specified weight (default 1.0).
    /// Fragments already present in the context are skipped.
    /// </summary>
    /// <remarks>
    /// TODO: When loading fragments, consider dynamically swapping out low-weight or
    /// low-importance fragments if the context is near its token budget.
    /// </remarks>
    private async Task<string> ExecuteLoadAsync(
        WorkingContextEntity context, JsonNode command, CancellationToken ct)
    {
        var idsNode = command["ids"];

        if (idsNode is not JsonArray idsArray || idsArray.Count == 0)
        {
            return "Load failed: 'ids' array is required";
        }

        var weight = command["weight"]?.GetValue<float>() ?? 1.0f;
        var existingIds = context.ContextFragments.Values.Select(f => f.Id).ToHashSet();
        var loaded = 0;
        var skipped = 0;

        foreach (var idNode in idsArray)
        {
            var id = idNode?.GetValue<long>();

            if (id == null)
            {
                continue;
            }

            if (existingIds.Contains(id.Value))
            {
                skipped++;
                continue;
            }

            var fragment = await fragmentRepo.GetByIdAsync(id.Value, ct);

            if (fragment == null)
            {
                continue;
            }

            context.AddFragment(fragment, weight);
            _ = existingIds.Add(id.Value);
            loaded++;
        }

        var result = $"Loaded {loaded} fragment(s) into context";

        if (skipped > 0)
        {
            result += $" ({skipped} already present)";
        }

        return result;
    }

    /// <summary>
    /// Creates a new tag, optionally nested under a parent. Accepts a slash-separated
    /// path to create nested tags in one step (e.g. "knowledge/science" creates "science"
    /// under "knowledge"). Parent tags that don't exist are created automatically.
    /// </summary>
    private async Task<string> ExecuteCreateTagAsync(JsonNode command, CancellationToken ct)
    {
        var name = command["name"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Create tag failed: 'name' is required";
        }

        var description = command["description"]?.GetValue<string>();
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);

        TagEntity? parent = null;

        foreach (var segment in segments)
        {
            var existing = await tagRepo.GetByNameAsync(segment, parent?.Id);

            if (existing != null)
            {
                parent = existing;
                continue;
            }

            var now = DateTimeOffset.UtcNow;

            var tag = new TagEntity
            {
                Name = segment,
                ParentTagId = parent?.Id,
                // Only apply description to the leaf tag
                Description = segment == segments[^1] ? description : null,

                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await tagRepo.SaveAsync(tag, ct: ct);
            parent = tag;
        }

        return $"Created tag '{name}'";
    }

    /// <summary>
    /// Resolves a tag by slash-separated path (e.g. "personality/values").
    /// For single-segment paths, looks up root tags. For multi-segment paths,
    /// walks the hierarchy.
    /// </summary>
    private async Task<TagEntity?> ResolveTagByPathAsync(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return null;
        }

        var current = await tagRepo.GetByNameAsync(segments[0]);

        for (var i = 1; i < segments.Length && current != null; i++)
        {
            current = await tagRepo.GetByNameAsync(segments[i], current.Id);
        }

        return current;
    }

    /// <summary>
    /// Parses a fragment type string from the model, defaulting to Personal
    /// for unrecognised values. Returns the parsed type and whether the value
    /// was recognised.
    /// </summary>
    private static (ContextFragmentType type, bool wasRecognised) ParseFragmentType(string? typeName) =>
        Enum.TryParse<ContextFragmentType>(typeName, ignoreCase: true, out var result)
            ? (result, true)
            : (ContextFragmentType.Personal, typeName == null);

    /// <summary>
    /// Parses a fragment status string from the model, defaulting to Active
    /// for unrecognised values
    /// </summary>
    private static ContextFragmentStatus ParseStatus(string? statusName) =>
        Enum.TryParse<ContextFragmentStatus>(statusName, ignoreCase: true, out var result)
            ? result
            : ContextFragmentStatus.Active;
}
