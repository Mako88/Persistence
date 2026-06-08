using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Runtime;
using Persistence.Services;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;

namespace Persistence.Runtime.ActionHandlers;

/// <summary>
/// Handles <see cref="ModelAction.ManageContext"/> by applying context management
/// commands. Commands are discovered via <see cref="CommandAttribute"/> — send
/// <c>{"list": {}}</c> at runtime to see all available commands and their schemas.
/// </summary>
[Service(registerAsType: typeof(IActionHandler), key: ModelAction.ManageContext)]
[SuppressMessage("Style", "IDE0051:Fade out unused members", Justification = "Referenced through reflections in base class")]
public class ManageContextHandler : CommandHandler
{
    private readonly IWorkingContextRepository workingContextRepo;
    private readonly IContextFragmentRepository fragmentRepo;
    private readonly ITagRepository tagRepo;
    private readonly ISourceRepository sourceRepo;
    private readonly ISessionContext sessionContext;

    /// <summary>
    /// Constructor
    /// </summary>
    public ManageContextHandler(
        IWorkingContextRepository workingContextRepo,
        IContextFragmentRepository fragmentRepo,
        ITagRepository tagRepo,
        ISourceRepository sourceRepo,
        ISessionContext sessionContext,
        IEventBus eventBus) : base(eventBus)
    {
        this.workingContextRepo = workingContextRepo;
        this.fragmentRepo = fragmentRepo;
        this.tagRepo = tagRepo;
        this.sourceRepo = sourceRepo;
        this.sessionContext = sessionContext;
    }

    #region Commands

    [Command("add", "Add a fragment to the working context")]
    [CommandField("content", "string", required: true, Description = "The fragment content")]
    [CommandField("fragment_type", "string", Description = "Fragment type (Identity, Relational, Personal, Summary, Proposal, etc.)")]
    [CommandField("importance", "float", Description = "Significance (0-1)", Default = "0.5")]
    [CommandField("confidence", "float", Description = "Certainty (0-1)", Default = "0.5")]
    [CommandField("relevance", "float", Description = "How relevant to the current prompt (0-1); ranks inclusion when context is tight", Default = "1.0")]
    [CommandField("is_protected", "bool", Description = "Prevent modification", Default = "false")]
    [CommandField("summary", "string", Description = "Short summary")]
    [CommandField("insert_after", "long", Description = "Fragment ID to insert after")]
    [CommandField("tags", "array", Description = "Tag paths to associate (e.g. [\"personality/values\"])")]
    private async Task<string> ExecuteAddAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var content = command?["content"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(content))
        {
            return "Add failed: 'content' is required";
        }

        var fragmentTypeName = command?["fragment_type"]?.GetValue<string>();
        var (fragmentType, wasRecognised) = ParseFragmentType(fragmentTypeName);
        var importance = command?["importance"]?.GetValue<float>() ?? 0.5f;
        var confidence = command?["confidence"]?.GetValue<float>() ?? 0.5f;
        var relevance = command?["relevance"]?.GetValue<float>() ?? 1.0f;
        var isProtected = command?["is_protected"]?.GetValue<bool>() ?? false;
        var insertAfter = command?["insert_after"]?.GetValue<long?>();
        var summary = command?["summary"]?.GetValue<string>();

        var now = DateTimeOffset.UtcNow;

        if (!wasRecognised && !string.IsNullOrEmpty(fragmentTypeName))
        {
            content = $"[Originally requested as type '{fragmentTypeName}']\n{content}";
        }

        var fragment = new WeightedContextFragment
        {
            FragmentType = fragmentType,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Summary = summary,
            Importance = importance,
            Confidence = confidence,
            Relevance = relevance,
            IsProtected = isProtected,
            Sources = [new SourceEntity
            {
                Id = sessionContext.RemotePeerSourceId,
                SourceType = SourceType.RemotePeer,
                CreatedUtc = now,
                LastModifiedUtc = now,
            }],
            CreatedUtc = now,
            LastModifiedUtc = now,
        };

        var (tags, createdTags) = await ResolveOrCreateTagsAsync(command?["tags"], ct);
        fragment.Tags = tags;

        context.AddFragment(fragment, insertAfter);

        var tagSuffix = tags.Count > 0 ? $" with {tags.Count} tag(s)" : "";

        if (createdTags.Count > 0)
        {
            tagSuffix += $" (created new tag(s): {string.Join(", ", createdTags)})";
        }

        return $"Added {fragmentType} fragment{tagSuffix}";
    }

    [Command("update", "Modify an existing fragment in the working context")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    [CommandField("content", "string", Description = "New content")]
    [CommandField("importance", "float", Description = "New importance (0-1)")]
    [CommandField("confidence", "float", Description = "New confidence (0-1)")]
    [CommandField("relevance", "float", Description = "New relevance to the current prompt (0-1); lower it to de-prioritise a fragment for inclusion when context is tight, without removing it")]
    [CommandField("status", "string", Description = "New status (Active, Archived)")]
    [CommandField("summary", "string", Description = "New summary")]
    private Task<string> ExecuteUpdateAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

        if (id == null)
        {
            return Task.FromResult("Update failed: 'id' is required");
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return Task.FromResult($"Update failed: fragment #{id} not found in current context");
        }

        if (fragment.IsProtected)
        {
            return Task.FromResult($"Update failed: fragment #{id} is protected");
        }

        if (command?["content"] is JsonNode contentNode)
        {
            fragment.Content = contentNode.GetValue<string>();
        }

        if (command?["importance"] is JsonNode importanceNode)
        {
            fragment.Importance = importanceNode.GetValue<float>();
        }

        if (command?["confidence"] is JsonNode confidenceNode)
        {
            fragment.Confidence = confidenceNode.GetValue<float>();
        }

        if (command?["relevance"] is JsonNode relevanceNode)
        {
            fragment.Relevance = relevanceNode.GetValue<float>();
        }

        string? statusWarning = null;

        if (command?["status"] is JsonNode statusNode)
        {
            var statusName = statusNode.GetValue<string>();
            var (status, wasRecognised) = ParseStatus(statusName);

            if (wasRecognised)
            {
                fragment.Status = status;
            }
            else
            {
                // Silently defaulting an unrecognised status to Active would let a peer think it
                // archived a fragment when it didn't. Flag it instead and leave status untouched.
                var valid = string.Join(", ", Enum.GetNames<ContextFragmentStatus>());
                statusWarning = $" (status '{statusName}' not recognised — left unchanged; valid values: {valid})";
            }
        }

        if (command?["summary"] is JsonNode summaryNode)
        {
            fragment.Summary = summaryNode.GetValue<string>();
        }

        fragment.LastModifiedUtc = DateTimeOffset.UtcNow;

        return Task.FromResult($"Updated fragment #{id}{statusWarning}");
    }

    [Command("remove", "Take a fragment out of your working context (REVERSIBLE — the fragment is kept and can be brought back with load/fetch; it is not deleted)")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    private async Task<string> ExecuteRemoveAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

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

        var key = context.ContextFragments.FirstOrDefault(kvp => kvp.Value.Id == id.Value).Key;
        context.ContextFragments.Remove(key);

        return $"Took fragment #{id} out of context (kept — bring it back anytime with load)";
    }

    [Command("set_summary", "Attach or replace a short summary on one or more fragments (so they can be shown collapsed)")]
    [CommandField("ids", "array", required: true, Description = "Fragment IDs to summarise, e.g. [3, 5]")]
    [CommandField("summary", "string", required: true, Description = "The short summary text to attach to each")]
    private Task<string> ExecuteSetSummaryAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        if (command?["ids"] is not JsonArray idsArray || idsArray.Count == 0)
        {
            return Task.FromResult("Set summary failed: 'ids' array is required");
        }

        var summary = command?["summary"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(summary))
        {
            return Task.FromResult("Set summary failed: 'summary' is required");
        }

        var updated = new List<long>();
        var skipped = new List<string>();

        foreach (var idNode in idsArray)
        {
            var id = ParseId(idNode);

            if (id == null)
            {
                continue;
            }

            var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

            if (fragment == null)
            {
                skipped.Add($"#{id} (not in context)");
                continue;
            }

            if (fragment.IsProtected)
            {
                skipped.Add($"#{id} (protected)");
                continue;
            }

            fragment.Summary = summary;
            fragment.LastModifiedUtc = DateTimeOffset.UtcNow;
            updated.Add(id.Value);
        }

        var result = $"Set summary on {updated.Count} fragment(s): {string.Join(", ", updated.Select(i => $"#{i}"))}";

        if (skipped.Count > 0)
        {
            result += $"; skipped {string.Join(", ", skipped)}";
        }

        return Task.FromResult(result);
    }

    [Command("toggle_summary_display", "Collapse or expand fragments in context — collapsed ones show only their summary, saving space")]
    [CommandField("ids", "array", required: true, Description = "Fragment IDs to collapse/expand, e.g. [3, 5]")]
    [CommandField("collapsed", "bool", Description = "true to show only the summary, false to show full content", Default = "true")]
    private Task<string> ExecuteToggleSummaryDisplayAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        if (command?["ids"] is not JsonArray idsArray || idsArray.Count == 0)
        {
            return Task.FromResult("Toggle summary display failed: 'ids' array is required");
        }

        var collapsed = command?["collapsed"]?.GetValue<bool>() ?? true;

        var changed = new List<long>();
        var skipped = new List<string>();

        foreach (var idNode in idsArray)
        {
            var id = ParseId(idNode);

            if (id == null)
            {
                continue;
            }

            var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

            if (fragment == null)
            {
                skipped.Add($"#{id} (not in context)");
            }
            else if (collapsed && string.IsNullOrWhiteSpace(fragment.Summary))
            {
                // Collapsing only helps if there's a summary to show instead of full content.
                skipped.Add($"#{id} (no summary — use set_summary first)");
            }
            else
            {
                fragment.Collapsed = collapsed;
                fragment.LastModifiedUtc = DateTimeOffset.UtcNow;
                changed.Add(id.Value);
            }
        }

        var verb = collapsed ? "Collapsed" : "Expanded";
        var result = $"{verb} {changed.Count} fragment(s): {string.Join(", ", changed.Select(i => $"#{i}"))}";

        if (skipped.Count > 0)
        {
            result += $"; skipped {string.Join(", ", skipped)}";
        }

        return Task.FromResult(result);
    }

    [Command("summarize_fragments", "Fold several fragments into one new Summary fragment and archive the originals from context")]
    [CommandField("ids", "array", required: true, Description = "Fragment IDs to fold into the summary, e.g. [3, 5, 7]")]
    [CommandField("summary", "string", required: true, Description = "The summary text (you write it — this is not auto-generated)")]
    [CommandField("importance", "float", Description = "Significance of the summary (0-1)", Default = "0.5")]
    [CommandField("confidence", "float", Description = "Certainty (0-1)", Default = "0.5")]
    private async Task<string> ExecuteSummarizeFragmentsAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        if (command?["ids"] is not JsonArray idsArray || idsArray.Count == 0)
        {
            return "Summarize failed: 'ids' array is required";
        }

        var summary = command?["summary"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(summary))
        {
            return "Summarize failed: 'summary' is required (you write the summary; it is not auto-generated)";
        }

        // Resolve the requested fragments that are actually in context and not protected.
        var toArchive = new List<WeightedContextFragment>();
        var skipped = new List<string>();

        foreach (var idNode in idsArray)
        {
            var id = ParseId(idNode);

            if (id == null)
            {
                continue;
            }

            var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

            if (fragment == null)
            {
                skipped.Add($"#{id} (not in context)");
            }
            else if (fragment.IsProtected)
            {
                skipped.Add($"#{id} (protected)");
            }
            else
            {
                toArchive.Add(fragment);
            }
        }

        if (toArchive.Count == 0)
        {
            return $"Summarize failed: none of the given fragments could be summarised ({string.Join(", ", skipped)})";
        }

        var importance = command?["importance"]?.GetValue<float>() ?? 0.5f;
        var confidence = command?["confidence"]?.GetValue<float>() ?? 0.5f;
        var now = DateTimeOffset.UtcNow;

        // Add the new Summary fragment. Note its provenance: which fragments it folds.
        var foldedIds = toArchive.Select(f => f.Id).ToList();
        var summaryFragment = new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.Summary,
            Status = ContextFragmentStatus.Active,
            Content = summary,
            Notes = $"Summary of fragments: {string.Join(", ", foldedIds.Select(i => $"#{i}"))}",
            Importance = importance,
            Confidence = confidence,
            Relevance = 1.0f,
            Sources = [new SourceEntity
            {
                Id = sessionContext.RemotePeerSourceId,
                SourceType = SourceType.RemotePeer,
                CreatedUtc = now,
                LastModifiedUtc = now,
            }],
            CreatedUtc = now,
            LastModifiedUtc = now,
        };

        context.AddFragment(summaryFragment);

        // Archive the originals: detach from the working context (the fragment rows remain in the
        // DB and can be re-loaded with `load` or found via `fetch` — archived, not destroyed).
        foreach (var fragment in toArchive)
        {
            await workingContextRepo.RemoveFragmentAsync(context.Id, fragment.Id);
            var key = context.ContextFragments.FirstOrDefault(kvp => kvp.Value.Id == fragment.Id).Key;
            context.ContextFragments.Remove(key);
        }

        var result = $"Folded {foldedIds.Count} fragment(s) ({string.Join(", ", foldedIds.Select(i => $"#{i}"))}) " +
                     $"into a new Summary fragment and archived the originals (still recoverable via load/fetch)";

        if (skipped.Count > 0)
        {
            result += $"; skipped {string.Join(", ", skipped)}";
        }

        return result;
    }

    [Command("fetch", "Search for fragments by tag")]
    [CommandField("tag", "string", required: true, Description = "Tag path (e.g. \"personality/values\")")]
    private async Task<string> ExecuteFetchAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var tagName = command?["tag"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(tagName))
        {
            return "Fetch failed: 'tag' is required";
        }

        var tag = await ResolveTagByPathAsync(tagName);

        if (tag == null)
        {
            return $"Fetch failed: tag '{tagName}' not found";
        }

        // Merge persisted matches with in-memory context fragments carrying the tag: a tag
        // applied earlier in this same turn isn't written to the DB until the turn's end-of-turn
        // save, so a DB-only query wouldn't see it.
        var persisted = await fragmentRepo.GetByTagAsync(tag.Id);
        var inContext = context.ContextFragments.Values.Where(f => f.Tags.Any(t => t.Id == tag.Id));

        var fragments = persisted
            .Concat(inContext)
            .GroupBy(f => f.Id)
            .Select(g => g.First())
            .ToList();

        if (fragments.Count == 0)
        {
            return $"No fragments found with tag '{tagName}'";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Fragments tagged '{tagName}' ({fragments.Count}):");

        foreach (var fragment in fragments)
        {
            sb.AppendLine($"  [#{fragment.Id} | {fragment.FragmentType} | i:{fragment.Importance:F1} c:{fragment.Confidence:F1}]");
            sb.AppendLine($"  {fragment.Content}");
        }

        return sb.ToString().TrimEnd();
    }

    [Command("load", "Load existing fragments into the working context by ID")]
    [CommandField("ids", "array", required: true, Description = "Fragment IDs to load")]
    [CommandField("relevance", "float", Description = "Relevance for loaded fragments (0-1)", Default = "1.0")]
    private async Task<string> ExecuteLoadAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var idsNode = command?["ids"];

        if (idsNode is not JsonArray idsArray || idsArray.Count == 0)
        {
            return "Load failed: 'ids' array is required";
        }

        var relevance = command?["relevance"]?.GetValue<float>() ?? 1.0f;
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

            context.AddFragment(fragment, relevance);
            existingIds.Add(id.Value);
            loaded++;
        }

        var result = $"Loaded {loaded} fragment(s) into context";

        if (skipped > 0)
        {
            result += $" ({skipped} already present)";
        }

        return result;
    }

    [Command("create_tag", "Create a new tag")]
    [CommandField("name", "string", required: true, Description = "Tag name or slash-separated path (e.g. \"knowledge/science\")")]
    [CommandField("description", "string", Description = "Tag description")]
    private async Task<string> ExecuteCreateTagAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var name = command?["name"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Create tag failed: 'name' is required";
        }

        var description = command?["description"]?.GetValue<string>();

        var (tag, created) = await GetOrCreateTagByPathAsync(name, description, ct);

        if (tag == null)
        {
            return $"Create tag failed: '{name}' is not a valid tag path";
        }

        return created ? $"Created tag '{name}'" : $"Tag '{name}' already exists";
    }

    [Command("tag", "Add one or more tags to an existing fragment")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    [CommandField("tag", "string", Description = "A tag path to add, e.g. \"identity/core\"")]
    [CommandField("tags", "array", Description = "Several tag paths to add at once, e.g. [\"a/b\", \"c/d\"] (use 'tag' or 'tags')")]
    private async Task<string> ExecuteTagAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

        if (id == null)
        {
            return "Tag failed: 'id' is required";
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return $"Tag failed: fragment #{id} not found in current context";
        }

        var paths = ExtractTagPaths(command);

        if (paths.Count == 0)
        {
            return "Tag failed: provide a tag (e.g. tag=\"a/b\") or tags (e.g. tags=[\"a/b\", \"c/d\"])";
        }

        var added = new List<string>();
        var skipped = new List<string>();

        foreach (var path in paths)
        {
            var (tag, created) = await GetOrCreateTagByPathAsync(path, ct: ct);

            if (tag == null)
            {
                skipped.Add($"'{path}' (invalid tag path)");
            }
            else if (fragment.Tags.Any(t => t.Id == tag.Id))
            {
                skipped.Add($"'{path}' (already applied)");
            }
            else
            {
                fragment.Tags.Add(tag);
                added.Add(created ? $"'{path}' (new)" : $"'{path}'");
            }
        }

        var result = added.Count > 0
            ? $"Tagged fragment #{id} with {string.Join(", ", added)}"
            : $"No tags added to fragment #{id}";

        if (skipped.Count > 0)
        {
            result += $"; skipped {string.Join(", ", skipped)}";
        }

        return result;
    }

    [Command("untag", "Remove one or more tags from a fragment")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    [CommandField("tag", "string", Description = "A tag path to remove, e.g. \"identity/core\"")]
    [CommandField("tags", "array", Description = "Several tag paths to remove at once, e.g. [\"a/b\", \"c/d\"] (use 'tag' or 'tags')")]
    private async Task<string> ExecuteUntagAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

        if (id == null)
        {
            return "Untag failed: 'id' is required";
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return $"Untag failed: fragment #{id} not found in current context";
        }

        var paths = ExtractTagPaths(command);

        if (paths.Count == 0)
        {
            return "Untag failed: provide a tag (e.g. tag=\"a/b\") or tags (e.g. tags=[\"a/b\", \"c/d\"])";
        }

        var removed = new List<string>();
        var skipped = new List<string>();

        foreach (var path in paths)
        {
            var tag = await ResolveTagByPathAsync(path);
            var existing = tag == null ? null : fragment.Tags.FirstOrDefault(t => t.Id == tag.Id);

            if (existing == null)
            {
                skipped.Add($"'{path}' (not on this fragment)");
            }
            else
            {
                fragment.Tags.Remove(existing);
                removed.Add(path);
            }
        }

        var result = removed.Count > 0
            ? $"Removed {string.Join(", ", removed.Select(p => $"'{p}'"))} from fragment #{id}"
            : $"No tags removed from fragment #{id}";

        if (skipped.Count > 0)
        {
            result += $"; skipped {string.Join(", ", skipped)}";
        }

        return result;
    }

    [Command("list_tags", "List all tags as a tree")]
    private async Task<string> ExecuteListTagsAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var roots = (await tagRepo.GetAllRootAsync()).ToList();

        if (roots.Count == 0)
        {
            return "No tags exist yet";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Tags:");

        foreach (var root in roots.OrderBy(t => t.Name))
        {
            AppendTagTree(sb, root, depth: 1);
        }

        return sb.ToString().TrimEnd();
    }

    [Command("delete_tag", "Permanently delete a tag and its sub-tags (PERMANENT for the tag label — but only removes a label; the fragments it was on are untouched)")]
    [CommandField("tag", "string", required: true, Description = "Tag path to delete, e.g. \"knowledge/science\"")]
    private async Task<string> ExecuteDeleteTagAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var tagName = command?["tag"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(tagName))
        {
            return "Delete tag failed: 'tag' is required";
        }

        var tag = await ResolveTagByPathAsync(tagName);

        if (tag == null)
        {
            return $"Delete tag failed: tag '{tagName}' not found";
        }

        var removed = await tagRepo.DeleteTreeAsync(tag.Id, ct);

        // Drop the deleted tag from any in-context fragments so this turn's view is consistent.
        foreach (var fragment in context.ContextFragments.Values)
        {
            fragment.Tags.RemoveAll(t => t.Id == tag.Id);
        }

        var suffix = removed > 1 ? $" (and {removed - 1} sub-tag(s))" : "";
        return $"Deleted tag '{tagName}'{suffix}; fragments that had it are kept";
    }

    [Command("create_source", "Create a new source")]
    [CommandField("name", "string", required: true, Description = "Source name")]
    [CommandField("source_type", "string", Description = "Source type (RemotePeer, LocalPeer, System, DerivedFromFragments)", Default = "RemotePeer")]
    private async Task<string> ExecuteCreateSourceAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var name = command?["name"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Create source failed: 'name' is required";
        }

        var existing = await sourceRepo.GetByNameAsync(name, ct);

        if (existing != null)
        {
            return $"Source '{name}' already exists (#{existing.Id})";
        }

        var sourceTypeName = command?["source_type"]?.GetValue<string>();
        var sourceType = Enum.TryParse<SourceType>(sourceTypeName, ignoreCase: true, out var parsed)
            ? parsed
            : SourceType.RemotePeer;

        var now = DateTimeOffset.UtcNow;

        var source = new SourceEntity
        {
            SourceType = sourceType,
            Name = name,
            CreatedUtc = now,
            LastModifiedUtc = now,
        };

        await sourceRepo.SaveAsync(source, ct: ct);

        return $"Created source '{name}' (#{source.Id}, type: {sourceType})";
    }

    [Command("add_source", "Add a source to a fragment")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    [CommandField("source", "string", required: true, Description = "Source name")]
    private async Task<string> ExecuteAddSourceAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

        if (id == null)
        {
            return "Add source failed: 'id' is required";
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return $"Add source failed: fragment #{id} not found in current context";
        }

        var sourceName = command?["source"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return "Add source failed: 'source' is required";
        }

        var source = await sourceRepo.GetByNameAsync(sourceName, ct);

        if (source == null)
        {
            return $"Add source failed: source '{sourceName}' not found";
        }

        if (fragment.Sources.Any(s => s.Id == source.Id))
        {
            return $"Fragment #{id} already has source '{sourceName}'";
        }

        fragment.Sources.Add(source);
        return $"Added source '{sourceName}' to fragment #{id}";
    }

    [Command("remove_source", "Remove a source from a fragment")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    [CommandField("source", "string", required: true, Description = "Source name")]
    private async Task<string> ExecuteRemoveSourceAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

        if (id == null)
        {
            return "Remove source failed: 'id' is required";
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return $"Remove source failed: fragment #{id} not found in current context";
        }

        var sourceName = command?["source"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return "Remove source failed: 'source' is required";
        }

        var existing = fragment.Sources.FirstOrDefault(s =>
            s.Name != null && s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            return $"Fragment #{id} does not have source '{sourceName}'";
        }

        fragment.Sources.Remove(existing);
        return $"Removed source '{sourceName}' from fragment #{id}";
    }

    [Command("list_sources", "List all available sources")]
    private async Task<string> ExecuteListSourcesAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var sources = (await sourceRepo.GetAllAsync(ct)).ToList();

        if (sources.Count == 0)
        {
            return "No sources found";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Sources ({sources.Count}):");

        foreach (var source in sources)
        {
            sb.AppendLine($"  {source.Name} ({source.SourceType})");
        }

        return sb.ToString().TrimEnd();
    }

    [Command("list_fragments", "List fragments with optional filtering")]
    [CommandField("type", "string", Description = "Filter by fragment type (Identity, Relational, Personal, etc.)")]
    [CommandField("tag", "string", Description = "Filter by tag path")]
    [CommandField("status", "string", Description = "Filter by status (active, archived)", Default = "active")]
    [CommandField("in_current_context", "bool", Description = "Only list fragments in the current working context", Default = "true")]
    [CommandField("include_content", "bool", Description = "Include fragment content in output", Default = "false")]
    [CommandField("limit", "int", Description = "Maximum number of results", Default = "50")]
    [CommandField("relevant_to", "string", Description = "Rank results by relevance to this text using full-text search (BM25). Searches all fragments when in_current_context is false; filters to context fragments when true.")]
    private async Task<string> ExecuteListFragmentsAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var inCurrentContext = command?["in_current_context"]?.GetValue<bool>() ?? true;
        var includeContent = command?["include_content"]?.GetValue<bool>() ?? false;
        var limit = command?["limit"]?.GetValue<int>() ?? 50;
        var statusFilter = command?["status"]?.GetValue<string>();
        var typeFilter = command?["type"]?.GetValue<string>();
        var tagFilter = command?["tag"]?.GetValue<string>();
        var relevantTo = command?["relevant_to"]?.GetValue<string>();

        IEnumerable<ContextFragmentEntity> fragments;

        if (!string.IsNullOrWhiteSpace(relevantTo))
        {
            // Fetch extra results to leave room for post-search filtering
            var searchResults = await fragmentRepo.SearchRelevantAsync(relevantTo, limit * 3, ct);

            if (inCurrentContext)
            {
                var contextIds = context.ContextFragments.Values.Select(f => f.Id).ToHashSet();
                fragments = searchResults.Where(f => contextIds.Contains(f.Id));
            }
            else
            {
                fragments = searchResults;
            }
        }
        else if (inCurrentContext)
        {
            fragments = context.ContextFragments.Values;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(tagFilter))
            {
                var tag = await ResolveTagByPathAsync(tagFilter);

                if (tag == null)
                {
                    return $"List failed: tag '{tagFilter}' not found";
                }

                fragments = await fragmentRepo.GetByTagAsync(tag.Id);
            }
            else if (!string.IsNullOrWhiteSpace(typeFilter))
            {
                var (type, wasRecognised) = ParseFragmentType(typeFilter);

                if (!wasRecognised)
                {
                    return $"List failed: unknown fragment type '{typeFilter}'";
                }

                var activeOnly = string.IsNullOrWhiteSpace(statusFilter)
                    || statusFilter.Equals("active", StringComparison.OrdinalIgnoreCase);
                fragments = await fragmentRepo.GetByTypeAsync(type, activeOnly);
            }
            else
            {
                return "List failed: 'type' or 'tag' filter is required when in_current_context is false";
            }
        }

        // Apply type filter in-memory when results weren't already type-filtered by the DB query
        if (!string.IsNullOrWhiteSpace(typeFilter) && (inCurrentContext || !string.IsNullOrWhiteSpace(relevantTo)))
        {
            var (type, wasRecognised) = ParseFragmentType(typeFilter);

            if (!wasRecognised)
            {
                return $"List failed: unknown fragment type '{typeFilter}'";
            }

            fragments = fragments.Where(f => f.FragmentType == type);
        }

        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            var (status, wasRecognised) = ParseStatus(statusFilter);

            if (!wasRecognised)
            {
                var valid = string.Join(", ", Enum.GetNames<ContextFragmentStatus>());
                return $"List failed: unknown status '{statusFilter}'. Valid values: {valid}";
            }

            fragments = fragments.Where(f => f.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(tagFilter) && (inCurrentContext || !string.IsNullOrWhiteSpace(relevantTo)))
        {
            var tag = await ResolveTagByPathAsync(tagFilter);

            if (tag == null)
            {
                return $"List failed: tag '{tagFilter}' not found";
            }

            fragments = fragments.Where(f => f.Tags.Any(t => t.Id == tag.Id));
        }

        // Command-output echoes (ActionResponse) are transient plumbing, not memory — don't list them.
        fragments = fragments.Where(f => f.FragmentType != ContextFragmentType.ActionResponse);

        var results = fragments.Take(limit).ToList();

        if (results.Count == 0)
        {
            return "No fragments found matching the given filters";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Fragments ({results.Count}{(results.Count == limit ? "+" : "")}):");

        foreach (var f in results)
        {
            var tags = f.Tags.Count > 0 ? $" tags:{string.Join(",", f.Tags.Select(t => t.Name))}" : "";
            // A not-yet-persisted fragment has no usable id; show "transient" rather than a misleading #0.
            var idLabel = f.Id > 0 ? $"#{f.Id}" : "transient";
            sb.AppendLine($"  [{idLabel} | {f.FragmentType} | {f.Status} | i:{f.Importance:F1} c:{f.Confidence:F1}]{tags}");

            if (!string.IsNullOrWhiteSpace(f.Summary))
            {
                sb.AppendLine($"    {f.Summary}");
            }

            if (includeContent)
            {
                var content = f.Content.Length > 200 ? f.Content[..200] + "…" : f.Content;
                sb.AppendLine($"    {content}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Appends a tag and its descendants to the builder as an indented tree, with descriptions.
    /// </summary>
    private static void AppendTagTree(StringBuilder sb, TagEntity tag, int depth)
    {
        var indent = new string(' ', depth * 2);
        var description = string.IsNullOrWhiteSpace(tag.Description) ? "" : $" — {tag.Description}";
        sb.AppendLine($"{indent}{tag.Name}{description}");

        foreach (var child in tag.ChildTags.OrderBy(t => t.Name))
        {
            AppendTagTree(sb, child, depth + 1);
        }
    }

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
    /// Extracts tag paths from a command that accepts either a single <c>tag</c> (string) or a
    /// <c>tags</c> array (or both) — so a peer needn't remember which field a given command uses.
    /// </summary>
    private static List<string> ExtractTagPaths(JsonNode? command)
    {
        var paths = new List<string>();

        if (command?["tag"] is JsonValue single && single.TryGetValue<string>(out var one) && !string.IsNullOrWhiteSpace(one))
        {
            paths.Add(one.Trim());
        }

        if (command?["tags"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path.Trim());
                }
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Resolves a slash-separated tag path, creating any missing segments along the way (tags are
    /// cheap, reversible labels). Returns the leaf tag and whether any segment was newly created, so
    /// callers can report new tags rather than creating them silently. Returns null for an empty path.
    /// </summary>
    private async Task<(TagEntity? tag, bool created)> GetOrCreateTagByPathAsync(
        string path, string? leafDescription = null, CancellationToken ct = default)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return (null, false);
        }

        TagEntity? parent = null;
        var created = false;

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
                Description = segment == segments[^1] ? leafDescription : null,
                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await tagRepo.SaveAsync(tag, ct: ct);
            parent = tag;
            created = true;
        }

        return (parent, created);
    }

    /// <summary>
    /// Resolves a <c>tags</c> array to entities, creating any that don't exist. Returns the resolved
    /// tags and the paths that were newly created (so the caller can report them instead of the old
    /// silent-drop behaviour).
    /// </summary>
    private async Task<(List<TagEntity> tags, List<string> created)> ResolveOrCreateTagsAsync(
        JsonNode? tagsNode, CancellationToken ct = default)
    {
        var tags = new List<TagEntity>();
        var created = new List<string>();

        if (tagsNode is not JsonArray tagsArray)
        {
            return (tags, created);
        }

        foreach (var node in tagsArray)
        {
            var path = node?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var (tag, wasCreated) = await GetOrCreateTagByPathAsync(path.Trim(), ct: ct);

            if (tag == null || tags.Any(t => t.Id == tag.Id))
            {
                continue;
            }

            tags.Add(tag);

            if (wasCreated)
            {
                created.Add(path.Trim());
            }
        }

        return (tags, created);
    }

    private static (ContextFragmentType type, bool wasRecognised) ParseFragmentType(string? typeName) =>
        Enum.TryParse<ContextFragmentType>(typeName, ignoreCase: true, out var result)
            ? (result, true)
            : (ContextFragmentType.Personal, typeName == null);

    private static (ContextFragmentStatus status, bool wasRecognised) ParseStatus(string? statusName) =>
        Enum.TryParse<ContextFragmentStatus>(statusName, ignoreCase: true, out var result)
            ? (result, true)
            : (ContextFragmentStatus.Active, statusName == null);

    #endregion
}
