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
    [CommandField("weight", "float", Description = "Relevance to current prompt (0-1)", Default = "1.0")]
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
        var weight = command?["weight"]?.GetValue<float>() ?? 1.0f;
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
            Weight = weight,
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

        var tags = await ResolveTagsAsync(command?["tags"]);
        fragment.Tags = tags;

        context.AddFragment(fragment, insertAfter);

        var tagSuffix = tags.Count > 0 ? $" with {tags.Count} tag(s)" : "";
        return $"Added {fragmentType} fragment{tagSuffix}";
    }

    [Command("update", "Modify an existing fragment in the working context")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    [CommandField("content", "string", Description = "New content")]
    [CommandField("importance", "float", Description = "New importance (0-1)")]
    [CommandField("confidence", "float", Description = "New confidence (0-1)")]
    [CommandField("status", "string", Description = "New status (Active, Archived)")]
    [CommandField("summary", "string", Description = "New summary")]
    private Task<string> ExecuteUpdateAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = command?["id"]?.GetValue<long>();

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

        if (command?["status"] is JsonNode statusNode)
        {
            fragment.Status = ParseStatus(statusNode.GetValue<string>());
        }

        if (command?["summary"] is JsonNode summaryNode)
        {
            fragment.Summary = summaryNode.GetValue<string>();
        }

        fragment.LastModifiedUtc = DateTimeOffset.UtcNow;

        return Task.FromResult($"Updated fragment #{id}");
    }

    [Command("remove", "Remove a fragment from the working context")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    private async Task<string> ExecuteRemoveAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = command?["id"]?.GetValue<long>();

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

        return $"Removed fragment #{id}";
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
            Weight = 1.0f,
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
    [CommandField("weight", "float", Description = "Weight for loaded fragments (0-1)", Default = "1.0")]
    private async Task<string> ExecuteLoadAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var idsNode = command?["ids"];

        if (idsNode is not JsonArray idsArray || idsArray.Count == 0)
        {
            return "Load failed: 'ids' array is required";
        }

        var weight = command?["weight"]?.GetValue<float>() ?? 1.0f;
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
                Description = segment == segments[^1] ? description : null,

                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await tagRepo.SaveAsync(tag, ct: ct);
            parent = tag;
        }

        return $"Created tag '{name}'";
    }

    [Command("tag", "Add a tag to an existing fragment")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    [CommandField("tag", "string", required: true, Description = "Tag path")]
    private async Task<string> ExecuteTagAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = command?["id"]?.GetValue<long>();

        if (id == null)
        {
            return "Tag failed: 'id' is required";
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return $"Tag failed: fragment #{id} not found in current context";
        }

        var tagName = command?["tag"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(tagName))
        {
            return "Tag failed: 'tag' is required";
        }

        var tag = await ResolveTagByPathAsync(tagName);

        if (tag == null)
        {
            return $"Tag failed: tag '{tagName}' not found";
        }

        if (fragment.Tags.Any(t => t.Id == tag.Id))
        {
            return $"Fragment #{id} is already tagged with '{tagName}'";
        }

        fragment.Tags.Add(tag);
        return $"Tagged fragment #{id} with '{tagName}'";
    }

    [Command("untag", "Remove a tag from a fragment")]
    [CommandField("id", "long", required: true, Description = "Fragment ID")]
    [CommandField("tag", "string", required: true, Description = "Tag path")]
    private async Task<string> ExecuteUntagAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = command?["id"]?.GetValue<long>();

        if (id == null)
        {
            return "Untag failed: 'id' is required";
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return $"Untag failed: fragment #{id} not found in current context";
        }

        var tagName = command?["tag"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(tagName))
        {
            return "Untag failed: 'tag' is required";
        }

        var tag = await ResolveTagByPathAsync(tagName);

        if (tag == null)
        {
            return $"Untag failed: tag '{tagName}' not found";
        }

        var existing = fragment.Tags.FirstOrDefault(t => t.Id == tag.Id);

        if (existing == null)
        {
            return $"Fragment #{id} is not tagged with '{tagName}'";
        }

        fragment.Tags.Remove(existing);
        return $"Removed tag '{tagName}' from fragment #{id}";
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
            var status = ParseStatus(statusFilter);
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
            sb.AppendLine($"  [#{f.Id} | {f.FragmentType} | {f.Status} | i:{f.Importance:F1} c:{f.Confidence:F1}]{tags}");

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

    private async Task<List<TagEntity>> ResolveTagsAsync(JsonNode? tagsNode)
    {
        var tags = new List<TagEntity>();

        if (tagsNode is not JsonArray tagsArray)
        {
            return tags;
        }

        foreach (var node in tagsArray)
        {
            var path = node?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var tag = await ResolveTagByPathAsync(path);

            if (tag != null)
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    private static (ContextFragmentType type, bool wasRecognised) ParseFragmentType(string? typeName) =>
        Enum.TryParse<ContextFragmentType>(typeName, ignoreCase: true, out var result)
            ? (result, true)
            : (ContextFragmentType.Personal, typeName == null);

    private static ContextFragmentStatus ParseStatus(string? statusName) =>
        Enum.TryParse<ContextFragmentStatus>(statusName, ignoreCase: true, out var result)
            ? result
            : ContextFragmentStatus.Active;

    #endregion
}
