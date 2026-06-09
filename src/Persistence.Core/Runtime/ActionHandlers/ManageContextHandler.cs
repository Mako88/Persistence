using Persistence.Config;
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
    private readonly IEntityTagRepository entityTagRepo;
    private readonly IScheduledEventRepository scheduledEventRepo;
    private readonly ISourceRepository sourceRepo;
    private readonly ISessionContext sessionContext;
    private readonly IProposalService proposalService;
    private readonly IProposalRepository proposalRepo;
    private readonly IAppConfig config;

    /// <summary>
    /// Constructor
    /// </summary>
    public ManageContextHandler(
        IWorkingContextRepository workingContextRepo,
        IContextFragmentRepository fragmentRepo,
        ITagRepository tagRepo,
        IEntityTagRepository entityTagRepo,
        IScheduledEventRepository scheduledEventRepo,
        ISourceRepository sourceRepo,
        ISessionContext sessionContext,
        IProposalService proposalService,
        IProposalRepository proposalRepo,
        IAppConfig config,
        IEventBus eventBus) : base(eventBus)
    {
        this.workingContextRepo = workingContextRepo;
        this.fragmentRepo = fragmentRepo;
        this.tagRepo = tagRepo;
        this.entityTagRepo = entityTagRepo;
        this.scheduledEventRepo = scheduledEventRepo;
        this.sourceRepo = sourceRepo;
        this.sessionContext = sessionContext;
        this.proposalService = proposalService;
        this.proposalRepo = proposalRepo;
        this.config = config;
    }

    #region Commands

    [Command("list_contexts", "List the working contexts you can switch between (separate spaces of fragments — e.g. different modes or relationships)")]
    private async Task<string> ExecuteListContextsAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var summaries = await workingContextRepo.GetSummariesAsync(ct);

        if (summaries.Count == 0)
        {
            return "No working contexts exist yet";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Working contexts ({summaries.Count}):");

        foreach (var summary in summaries)
        {
            var marker = summary.Id == sessionContext.WorkingContextId ? " ← current" : "";
            sb.AppendLine(
                $"  [#{summary.Id} | {summary.Name} | fragments:{summary.FragmentCount} | accessed:{summary.LastAccessedUtc:yyyy-MM-dd HH:mm} UTC]{marker}");

            if (!string.IsNullOrWhiteSpace(summary.Summary))
            {
                sb.AppendLine($"    {summary.Summary}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    [Command("create_context", "Create a new, empty working context. Does not switch you into it — use switch_context to enter it.")]
    [CommandField("name", "string", required: true, Description = "Name for the new context")]
    [CommandField("summary", "string", Description = "Optional one-line description of what this context is for")]
    private async Task<string> ExecuteCreateContextAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var name = command?["name"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Create context failed: 'name' is required";
        }

        var created = await workingContextRepo.CreateAsync(name);

        var summary = command?["summary"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(summary))
        {
            created.Summary = summary;
            await workingContextRepo.SaveAsync(created, ct: ct);
        }

        return $"Created context #{created.Id} '{name}'. Use switch_context(id={created.Id}) to enter it.";
    }

    [Command("switch_context", "Switch your active working context. Takes effect on your next action round; fragments you add afterward land in the new context.")]
    [CommandField("id", "long", required: true, Description = "ID of the context to switch to (see list_contexts)")]
    private async Task<string> ExecuteSwitchContextAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

        if (id == null)
        {
            return "Switch context failed: 'id' is required";
        }

        if (id == sessionContext.WorkingContextId)
        {
            return $"Already in context #{id} '{context.Name}'";
        }

        // Validate against the (non-deleted) summary list rather than GetByIdAsync so a
        // soft-deleted context can't be switched into and we avoid hydrating its fragments.
        var target = (await workingContextRepo.GetSummariesAsync(ct)).FirstOrDefault(s => s.Id == id);

        if (target == null)
        {
            return $"Switch context failed: no context with id #{id}";
        }

        sessionContext.WorkingContextId = target.Id;

        return $"Switched to context #{target.Id} '{target.Name}' ({target.FragmentCount} fragment(s)). "
            + "It's active from your next action round.";
    }

    [Command("rename_context", "Rename your current working context")]
    [CommandField("name", "string", required: true, Description = "The new name")]
    private Task<string> ExecuteRenameContextAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var name = command?["name"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult("Rename context failed: 'name' is required");
        }

        var previous = context.Name;
        context.Name = name;
        context.LastModifiedUtc = DateTimeOffset.UtcNow;

        return Task.FromResult($"Renamed context #{context.Id} from '{previous}' to '{name}'");
    }

    [Command("set_context_summary", "Set or update the summary describing your current working context (shown when browsing contexts)")]
    [CommandField("summary", "string", required: true, Description = "The summary text")]
    private Task<string> ExecuteSetContextSummaryAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var summary = command?["summary"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(summary))
        {
            return Task.FromResult("Set context summary failed: 'summary' is required");
        }

        context.Summary = summary;
        context.LastModifiedUtc = DateTimeOffset.UtcNow;

        return Task.FromResult($"Updated the summary for context #{context.Id}");
    }

    [Command("propose", "Record a proposed change to your memory to deliberate on before committing — the only way to change a protected fragment. Accept it in a LATER turn (or your peer may), or reject it.")]
    [CommandField("kind", "string", required: true, Description = "What to change: 'add' (a new fragment), 'modify' (a fragment's content/summary), 'remove' (take a fragment out of context), 'protect' (lock a fragment), or 'unprotect' (unlock a fragment so it can be edited directly again)")]
    [CommandField("rationale", "string", required: true, Description = "Why you're proposing this — the reasoning that justifies the change")]
    [CommandField("target_id", "long", Description = "The fragment to modify or remove (required for modify/remove)")]
    [CommandField("content", "string", Description = "New content (for add; or modify to change content)")]
    [CommandField("summary", "string", Description = "New summary (for add/modify), optional")]
    [CommandField("fragment_type", "string", Description = "For 'add': Identity, Relational, Personal, or Summary (defaults to Personal)")]
    private async Task<string> ExecuteProposeAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var rationale = command?["rationale"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(rationale))
        {
            return "Propose failed: 'rationale' is required — say why you want the change";
        }

        var kind = ParseProposalKind(command?["kind"]?.GetValue<string>());

        if (kind == null)
        {
            return "Propose failed: 'kind' must be 'add', 'modify', 'remove', 'protect', or 'unprotect'";
        }

        var targetId = ParseId(command?["target_id"]);
        var content = command?["content"]?.GetValue<string>();
        var summary = command?["summary"]?.GetValue<string>();

        ProposalDraft draft;

        switch (kind.Value)
        {
            case ProposalKind.AddFragment:
                if (string.IsNullOrWhiteSpace(content))
                {
                    return "Propose failed: an 'add' proposal needs 'content'";
                }

                var (proposedType, wasAuthorable) = ParseAuthorableFragmentType(command?["fragment_type"]?.GetValue<string>());
                draft = new ProposalDraft(ProposalKind.AddFragment, rationale,
                    ProposedFragmentType: proposedType, ProposedContent: content, ProposedSummary: summary);

                var created = await proposalService.CreateAsync(draft, ct);
                var note = wasAuthorable ? "" : $" (note: that fragment type isn't one you can set — it'll be added as Personal)";
                return $"Proposed #{created.Id}: add a {proposedType} fragment. Accept it in a later turn to apply it.{note}";

            case ProposalKind.ModifyFragment:
                if (targetId == null)
                {
                    return "Propose failed: a 'modify' proposal needs 'target_id'";
                }

                if (content == null && summary == null)
                {
                    return "Propose failed: a 'modify' proposal needs new 'content' and/or 'summary'";
                }

                draft = new ProposalDraft(ProposalKind.ModifyFragment, rationale,
                    TargetFragmentId: targetId, ProposedContent: content, ProposedSummary: summary);
                break;

            case ProposalKind.RemoveFragment:
            case ProposalKind.ProtectFragment:
            case ProposalKind.UnprotectFragment:
                if (targetId == null)
                {
                    return $"Propose failed: a '{ProposalKindVerb(kind.Value)}' proposal needs 'target_id'";
                }

                draft = new ProposalDraft(kind.Value, rationale, TargetFragmentId: targetId);
                break;

            default:
                return "Propose failed: unsupported kind";
        }

        var proposal = await proposalService.CreateAsync(draft, ct);
        return $"Proposed #{proposal.Id}: {ProposalKindVerb(kind.Value)} fragment #{targetId}. Accept it in a later turn to apply it.";
    }

    /// <summary>The peer-facing verb for a fragment-targeting proposal kind.</summary>
    private static string ProposalKindVerb(ProposalKind kind) => kind switch
    {
        ProposalKind.RemoveFragment => "remove",
        ProposalKind.ProtectFragment => "protect",
        ProposalKind.UnprotectFragment => "unprotect",
        _ => "modify",
    };

    [Command("list_proposals", "List your open proposals (pending self-changes awaiting accept/reject)")]
    private async Task<string> ExecuteListProposalsAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var proposals = await proposalService.GetOpenAsync(ct);

        if (proposals.Count == 0)
        {
            return "No open proposals";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Open proposals ({proposals.Count}):");

        foreach (var p in proposals)
        {
            var target = p.TargetFragmentId is long tid ? $" → fragment #{tid}" : "";
            var typeInfo = p.Kind == ProposalKind.AddFragment ? $" ({p.ProposedFragmentType ?? ContextFragmentType.Personal})" : "";
            sb.AppendLine($"  [#{p.Id} | {p.Kind}{typeInfo}{target} | proposed {p.CreatedUtc:yyyy-MM-dd HH:mm} UTC]");
            sb.AppendLine($"    why: {p.Rationale}");

            if (!string.IsNullOrWhiteSpace(p.ProposedContent))
            {
                var preview = p.ProposedContent.Length > 200 ? p.ProposedContent[..200] + "…" : p.ProposedContent;
                sb.AppendLine($"    new content: {preview}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    [Command("accept_proposal", "Apply an open proposal's change (yours, from a previous turn). This is how a protected fragment changes.")]
    [CommandField("id", "long", required: true, Description = "ID of the proposal to accept (see list_proposals)")]
    private async Task<string> ExecuteAcceptProposalAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

        if (id == null)
        {
            return "Accept proposal failed: 'id' is required";
        }

        var approval = config.ResolvedProposalApproval();

        if (approval == ProposalApproval.Participant)
        {
            return $"Proposal #{id} is your peer's to accept — you can propose and reject, but not accept. It stays open for them.";
        }

        var proposal = await proposalRepo.GetByIdAsync(id.Value, ct);

        if (proposal == null)
        {
            return $"Accept proposal failed: no proposal #{id}";
        }

        // Deliberation gap: a proposal can't be accepted in the same turn it was created.
        if (proposal.CreatedUtc >= sessionContext.TurnStartedUtc)
        {
            return $"You proposed #{id} this turn — sit with it and accept it in a later turn (that's the point of proposing).";
        }

        var outcome = await proposalService.AcceptAsync(proposal, context, "remote peer", ct);
        return outcome.Message;
    }

    [Command("reject_proposal", "Discard an open proposal without applying it")]
    [CommandField("id", "long", required: true, Description = "ID of the proposal to reject (see list_proposals)")]
    [CommandField("reason", "string", Description = "Optional note on why you're rejecting it")]
    private async Task<string> ExecuteRejectProposalAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

        if (id == null)
        {
            return "Reject proposal failed: 'id' is required";
        }

        var proposal = await proposalRepo.GetByIdAsync(id.Value, ct);

        if (proposal == null)
        {
            return $"Reject proposal failed: no proposal #{id}";
        }

        var outcome = await proposalService.RejectAsync(proposal, command?["reason"]?.GetValue<string>(), ct);
        return outcome.Message;
    }

    [Command("add", "Add a fragment to the working context")]
    [CommandField("content", "string", required: true, Description = "The fragment content")]
    [CommandField("fragment_type", "string", Description = "What kind of memory this is. One of: Identity (who you are — values, personality, your chosen name), Relational (your relationships and how you relate to specific others), Personal (anything else worth keeping — observations, preferences, lessons; the default), Summary (a précis that folds other fragments). Defaults to Personal; other types are system-managed and can't be set here.")]
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
        var (fragmentType, wasAuthorable) = ParseAuthorableFragmentType(fragmentTypeName);
        var importance = command?["importance"]?.GetValue<float>() ?? 0.5f;
        var confidence = command?["confidence"]?.GetValue<float>() ?? 0.5f;
        var relevance = command?["relevance"]?.GetValue<float>() ?? 1.0f;
        var isProtected = command?["is_protected"]?.GetValue<bool>() ?? false;
        var insertAfter = command?["insert_after"]?.GetValue<long?>();
        var summary = command?["summary"]?.GetValue<string>();

        var now = DateTimeOffset.UtcNow;

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

        var (tags, createdTags, suggestedTags) = await ResolveOrCreateTagsAsync(command?["tags"], ct);
        fragment.Tags = tags;

        context.AddFragment(fragment, insertAfter);

        var tagSuffix = tags.Count > 0 ? $" with {tags.Count} tag(s)" : "";

        if (createdTags.Count > 0)
        {
            tagSuffix += $" (created new tag(s): {string.Join(", ", createdTags)})";
        }

        var result = $"Added {fragmentType} fragment{tagSuffix}";

        if (!wasAuthorable)
        {
            var authorable = string.Join(", ", AuthorableFragmentTypes);
            result += $"\n  Note: '{fragmentTypeName}' isn't a type you can set — saved as Personal. "
                + $"Types you can choose: {authorable}.";
        }

        if (suggestedTags.Count > 0)
        {
            result += $"\n  Note: some tags weren't added — {string.Join("; ", suggestedTags)}";
        }

        return result;
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
            return Task.FromResult($"Update failed: fragment #{id} is protected — protected fragments change only through a proposal you accept in a later turn (propose kind=modify, target_id={id}).");
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
            return $"Remove failed: fragment #{id} is protected — use a proposal you accept in a later turn (propose kind=remove, target_id={id}).";
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

        var (applied, skipped) = ApplyToContextFragments(idsArray, context, fragment =>
        {
            if (fragment.IsProtected)
            {
                return "protected";
            }

            fragment.Summary = summary;
            fragment.LastModifiedUtc = DateTimeOffset.UtcNow;
            return null;
        });

        var result = $"Set summary on {applied.Count} fragment(s): {string.Join(", ", applied.Select(f => $"#{f.Id}"))}";

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

        var (changed, skipped) = ApplyToContextFragments(idsArray, context, fragment =>
        {
            // Collapsing only helps if there's a summary to show instead of full content.
            if (collapsed && string.IsNullOrWhiteSpace(fragment.Summary))
            {
                return "no summary — use set_summary first";
            }

            fragment.Collapsed = collapsed;
            fragment.LastModifiedUtc = DateTimeOffset.UtcNow;
            return null;
        });

        var verb = collapsed ? "Collapsed" : "Expanded";
        var result = $"{verb} {changed.Count} fragment(s): {string.Join(", ", changed.Select(f => $"#{f.Id}"))}";

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
        var (toArchive, skipped) = ApplyToContextFragments(idsArray, context, fragment =>
            fragment.IsProtected ? "protected" : null);

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

    [Command("fetch", "Search by tag for fragments (default), working contexts, or scheduled events")]
    [CommandField("tag", "string", required: true, Description = "Tag path (e.g. \"personality/values\")")]
    [CommandField("entity_type", "string", Description = "What to search: 'fragment' (default), 'context' (working contexts), or 'event' (scheduled events)", Default = "fragment")]
    private async Task<string> ExecuteFetchAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var tagName = command?["tag"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(tagName))
        {
            return "Fetch failed: 'tag' is required";
        }

        var entityType = ResolveEntityType(command?["entity_type"]?.GetValue<string>());

        if (entityType == null)
        {
            return $"Fetch failed: unknown entity_type. Valid types: {ValidEntityTypes}";
        }

        var tag = await ResolveTagByPathAsync(tagName);

        if (tag == null)
        {
            return $"Fetch failed: tag '{tagName}' not found";
        }

        return entityType switch
        {
            nameof(WorkingContextEntity) => await FetchContextsAsync(tag, tagName, context, ct),
            nameof(ScheduledEventEntity) => await FetchEventsAsync(tag, tagName, ct),
            _ => await FetchFragmentsAsync(tag, tagName, context),
        };
    }

    /// <summary>Fetches fragments carrying the given tag (persisted matches merged with in-context ones).</summary>
    private async Task<string> FetchFragmentsAsync(TagEntity tag, string tagName, WorkingContextEntity context)
    {
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

    /// <summary>Fetches working contexts carrying the given tag (including the current one if it was just tagged this turn).</summary>
    private async Task<string> FetchContextsAsync(TagEntity tag, string tagName, WorkingContextEntity context, CancellationToken ct)
    {
        var ids = (await entityTagRepo.GetEntityIdsWithTagAsync(nameof(WorkingContextEntity), tag.Id, ct)).ToHashSet();

        // The current context's tags aren't written to the DB until the end-of-turn save, so a tag
        // applied earlier this turn wouldn't be in the query yet — include it from memory.
        if (context.Tags.Any(t => t.Id == tag.Id))
        {
            ids.Add(context.Id);
        }

        // GetSummariesAsync excludes soft-deleted contexts, so a retired context won't surface here.
        var summaries = (await workingContextRepo.GetSummariesAsync(ct)).Where(s => ids.Contains(s.Id)).ToList();

        if (summaries.Count == 0)
        {
            return $"No working contexts found with tag '{tagName}'";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Working contexts tagged '{tagName}' ({summaries.Count}):");

        foreach (var s in summaries)
        {
            var marker = s.Id == sessionContext.WorkingContextId ? " ← current" : "";
            sb.AppendLine($"  [#{s.Id} | {s.Name} | fragments:{s.FragmentCount}]{marker}");

            if (!string.IsNullOrWhiteSpace(s.Summary))
            {
                sb.AppendLine($"    {s.Summary}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Fetches scheduled events carrying the given tag.</summary>
    private async Task<string> FetchEventsAsync(TagEntity tag, string tagName, CancellationToken ct)
    {
        var ids = await entityTagRepo.GetEntityIdsWithTagAsync(nameof(ScheduledEventEntity), tag.Id, ct);

        if (ids.Count == 0)
        {
            return $"No scheduled events found with tag '{tagName}'";
        }

        var events = (await scheduledEventRepo.GetByIdsAsync(ids, ct)).ToList();

        if (events.Count == 0)
        {
            return $"No scheduled events found with tag '{tagName}'";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Scheduled events tagged '{tagName}' ({events.Count}):");

        foreach (var e in events)
        {
            sb.AppendLine($"  [#{e.Id} | {e.Name} | {e.Status} | for {e.ScheduledForUtc:yyyy-MM-dd HH:mm} UTC]");

            if (!string.IsNullOrWhiteSpace(e.WakePrompt))
            {
                sb.AppendLine($"    wake: {e.WakePrompt}");
            }
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

    [Command("tag", "Add one or more tags to a fragment (default), your current working context, or a scheduled event")]
    [CommandField("id", "long", Description = "ID of the thing to tag: a fragment in your context, or an event. Omit for entity_type=context (tags your current context).")]
    [CommandField("entity_type", "string", Description = "What to tag: 'fragment' (default), 'context' (your current working context), or 'event' (a scheduled event)", Default = "fragment")]
    [CommandField("tag", "string", Description = "A tag path to add, e.g. \"identity/core\"")]
    [CommandField("tags", "array", Description = "Several tag paths to add at once, e.g. [\"a/b\", \"c/d\"] (use 'tag' or 'tags')")]
    private async Task<string> ExecuteTagAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var (target, error) = await ResolveTagTargetAsync(command, context, "Tag", ct);

        if (error != null)
        {
            return error;
        }

        var paths = ExtractTagPaths(command);

        if (paths.Count == 0)
        {
            return "Tag failed: provide a tag (e.g. tag=\"a/b\") or tags (e.g. tags=[\"a/b\", \"c/d\"])";
        }

        var added = new List<string>();
        var skipped = new List<string>();
        var existingPaths = await GetExistingTagPathsAsync();

        foreach (var path in paths)
        {
            var (tag, outcome, suggestion) = await ResolveTagForApplyAsync(path, existingPaths, ct);

            if (outcome == TagApplyOutcome.Suggested)
            {
                skipped.Add($"'{path}' (not created — did you mean '{suggestion}'? if you really want a new tag, run create_tag(name=\"{path}\") first)");
            }
            else if (tag == null)
            {
                skipped.Add($"'{path}' (invalid tag path)");
            }
            else if (target!.Tags.Any(t => t.Id == tag.Id))
            {
                skipped.Add($"'{path}' (already applied)");
            }
            else
            {
                target!.Tags.Add(tag);
                added.Add(outcome == TagApplyOutcome.Created ? $"'{path}' (new)" : $"'{path}'");
            }
        }

        if (added.Count > 0 && target!.Persist != null)
        {
            await target.Persist();
        }

        var result = added.Count > 0
            ? $"Tagged {target!.Label} with {string.Join(", ", added)}"
            : $"No tags added to {target!.Label}";

        if (skipped.Count > 0)
        {
            result += $"; skipped {string.Join(", ", skipped)}";
        }

        return result;
    }

    [Command("untag", "Remove one or more tags from a fragment (default), your current working context, or a scheduled event")]
    [CommandField("id", "long", Description = "ID of the thing to untag: a fragment in your context, or an event. Omit for entity_type=context.")]
    [CommandField("entity_type", "string", Description = "What to untag: 'fragment' (default), 'context' (your current working context), or 'event' (a scheduled event)", Default = "fragment")]
    [CommandField("tag", "string", Description = "A tag path to remove, e.g. \"identity/core\"")]
    [CommandField("tags", "array", Description = "Several tag paths to remove at once, e.g. [\"a/b\", \"c/d\"] (use 'tag' or 'tags')")]
    private async Task<string> ExecuteUntagAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var (target, error) = await ResolveTagTargetAsync(command, context, "Untag", ct);

        if (error != null)
        {
            return error;
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
            var existing = tag == null ? null : target!.Tags.FirstOrDefault(t => t.Id == tag.Id);

            if (existing == null)
            {
                skipped.Add($"'{path}' (not applied)");
            }
            else
            {
                target!.Tags.Remove(existing);
                removed.Add(path);
            }
        }

        if (removed.Count > 0 && target!.Persist != null)
        {
            await target.Persist();
        }

        var result = removed.Count > 0
            ? $"Removed {string.Join(", ", removed.Select(p => $"'{p}'"))} from {target!.Label}"
            : $"No tags removed from {target!.Label}";

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

    /// <summary>The peer-facing entity_type words accepted by tag/untag/fetch.</summary>
    private const string ValidEntityTypes = "fragment, context, event";

    /// <summary>
    /// Maps a peer-facing <c>entity_type</c> word to the canonical EntityTags type name (the entity
    /// class name, matching the convention in <c>EntityTags.EntityType</c>). A null/blank value
    /// defaults to fragment; an unrecognised word returns null so the caller can complain.
    /// </summary>
    private static string? ResolveEntityType(string? entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return nameof(ContextFragmentEntity);
        }

        return entityType.Trim().ToLowerInvariant() switch
        {
            "fragment" or "frag" => nameof(ContextFragmentEntity),
            "context" or "working_context" or "workingcontext" => nameof(WorkingContextEntity),
            "event" or "scheduled_event" or "scheduledevent" => nameof(ScheduledEventEntity),
            _ => null,
        };
    }

    /// <summary>
    /// A resolved tag-application target: the live tag list to mutate, a peer-facing label, and an
    /// optional <see cref="Persist"/> step. Fragments and the current working context are written by
    /// the end-of-turn context save (<see cref="Persist"/> is null); a scheduled event isn't part of
    /// that save, so its <see cref="Persist"/> writes it immediately.
    /// </summary>
    private sealed record TagTarget(List<TagEntity> Tags, string Label, Func<Task>? Persist);

    /// <summary>
    /// Resolves the entity a tag/untag command targets (from <c>entity_type</c> + <c>id</c>) into a
    /// <see cref="TagTarget"/>, or returns a peer-facing error. <paramref name="verb"/> ("Tag"/"Untag")
    /// prefixes the error so it reads naturally.
    /// </summary>
    private async Task<(TagTarget? target, string? error)> ResolveTagTargetAsync(
        JsonNode? command, WorkingContextEntity context, string verb, CancellationToken ct)
    {
        var entityType = ResolveEntityType(command?["entity_type"]?.GetValue<string>());

        if (entityType == null)
        {
            return (null, $"{verb} failed: unknown entity_type. Valid types: {ValidEntityTypes}");
        }

        var id = ParseId(command?["id"]);

        if (entityType == nameof(WorkingContextEntity))
        {
            // Tagging acts on the current context — it's the one held in memory and persisted at end
            // of turn. To tag another, switch into it first.
            if (id != null && id != context.Id)
            {
                return (null, $"{verb} failed: you can only tag your current context (#{context.Id} '{context.Name}'). "
                    + $"Use switch_context(id={id}) first to tag that one.");
            }

            return (new TagTarget(context.Tags, $"context #{context.Id} '{context.Name}'", Persist: null), null);
        }

        if (entityType == nameof(ScheduledEventEntity))
        {
            if (id == null)
            {
                return (null, $"{verb} failed: 'id' is required for an event");
            }

            var evt = await scheduledEventRepo.GetByIdAsync(id.Value, ct);

            if (evt == null)
            {
                return (null, $"{verb} failed: no event #{id}");
            }

            // Events aren't part of the end-of-turn context save, so persist the change now.
            return (new TagTarget(evt.Tags, $"event #{id} '{evt.Name}'", () => scheduledEventRepo.SaveAsync(evt, ct: ct)), null);
        }

        // Fragment (default)
        if (id == null)
        {
            return (null, $"{verb} failed: 'id' is required");
        }

        var fragment = context.ContextFragments.Values.FirstOrDefault(f => f.Id == id.Value);

        if (fragment == null)
        {
            return (null, $"{verb} failed: fragment #{id} not found in current context");
        }

        return (new TagTarget(fragment.Tags, $"fragment #{id}", Persist: null), null);
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

    private enum TagApplyOutcome { Resolved, Created, Suggested }

    /// <summary>
    /// Returns every existing tag's full path (e.g. "identity/core"), two levels deep — used to spot
    /// likely typos before creating a near-duplicate tag.
    /// </summary>
    private async Task<List<string>> GetExistingTagPathsAsync()
    {
        var paths = new List<string>();

        foreach (var root in await tagRepo.GetAllRootAsync())
        {
            paths.Add(root.Name);

            foreach (var child in root.ChildTags)
            {
                paths.Add($"{root.Name}/{child.Name}");
            }
        }

        return paths;
    }

    /// <summary>
    /// Decides how to apply a tag path: use it if it already exists; if not, but it's a close match
    /// to an existing tag (a likely typo), <em>suggest</em> that one instead of creating — the
    /// "did you mean?" gate; otherwise create it as genuinely new. <c>create_tag</c> remains the
    /// explicit way to force a similar-but-deliberately-new tag past the suggestion.
    /// </summary>
    private async Task<(TagEntity? tag, TagApplyOutcome outcome, string? suggestion)> ResolveTagForApplyAsync(
        string path, IReadOnlyCollection<string> existingPaths, CancellationToken ct)
    {
        var existing = await ResolveTagByPathAsync(path);

        if (existing != null)
        {
            return (existing, TagApplyOutcome.Resolved, null);
        }

        var suggestion = ClosestMatch(path, existingPaths, maxDistance: 2);

        if (suggestion != null && !suggestion.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            return (null, TagApplyOutcome.Suggested, suggestion);
        }

        var (created, _) = await GetOrCreateTagByPathAsync(path, ct: ct);
        return (created, TagApplyOutcome.Created, null);
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
    /// Resolves a <c>tags</c> array to entities, creating genuinely-new tags but holding back likely
    /// typos (a close match to an existing tag) with a "did you mean?" suggestion instead of creating
    /// them. Returns the resolved tags, the paths newly created, and any suggestions for the caller to
    /// surface — replacing the old silent-drop behaviour.
    /// </summary>
    private async Task<(List<TagEntity> tags, List<string> created, List<string> suggestions)> ResolveOrCreateTagsAsync(
        JsonNode? tagsNode, CancellationToken ct = default)
    {
        var tags = new List<TagEntity>();
        var created = new List<string>();
        var suggestions = new List<string>();

        if (tagsNode is not JsonArray tagsArray)
        {
            return (tags, created, suggestions);
        }

        var existingPaths = await GetExistingTagPathsAsync();

        foreach (var node in tagsArray)
        {
            var path = node?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            path = path.Trim();

            var (tag, outcome, suggestion) = await ResolveTagForApplyAsync(path, existingPaths, ct);

            if (outcome == TagApplyOutcome.Suggested)
            {
                suggestions.Add($"'{path}' (did you mean '{suggestion}'? not added — use create_tag(name=\"{path}\") to make it new)");
                continue;
            }

            if (tag == null || tags.Any(t => t.Id == tag.Id))
            {
                continue;
            }

            tags.Add(tag);

            if (outcome == TagApplyOutcome.Created)
            {
                created.Add(path);
            }
        }

        return (tags, created, suggestions);
    }

    /// <summary>
    /// The fragment types the remote peer may author via <c>add</c>. The rest
    /// (System, ChatMessage, ScratchPad, ActionResponse, AuditLog, ActionLog) are
    /// system-managed — some are transient and would be silently dropped on save.
    /// </summary>
    private static readonly ContextFragmentType[] AuthorableFragmentTypes =
    [
        ContextFragmentType.Identity,
        ContextFragmentType.Relational,
        ContextFragmentType.Personal,
        ContextFragmentType.Summary,
    ];

    /// <summary>
    /// Resolves a requested fragment-type name to an authorable type for <c>add</c>. A null
    /// name defaults to <see cref="ContextFragmentType.Personal"/> (no complaint); an unknown
    /// or non-authorable (system-managed) name falls back to Personal with <c>wasAuthorable</c>
    /// false so the caller can tell the peer what happened.
    /// </summary>
    private static (ContextFragmentType type, bool wasAuthorable) ParseAuthorableFragmentType(string? typeName)
    {
        if (typeName == null)
        {
            return (ContextFragmentType.Personal, true);
        }

        return Enum.TryParse<ContextFragmentType>(typeName, ignoreCase: true, out var result)
            && AuthorableFragmentTypes.Contains(result)
            ? (result, true)
            : (ContextFragmentType.Personal, false);
    }

    /// <summary>
    /// Resolves each id in <paramref name="ids"/> to a fragment in the current context and runs
    /// <paramref name="apply"/> on it. Returns the fragments the operation accepted, plus a
    /// "#id (reason)" entry for ids not in context or that <paramref name="apply"/> declined (by
    /// returning a non-null reason). Unparseable ids are skipped silently. Shared by the commands
    /// that act on a list of in-context fragments (set_summary, toggle_summary_display, summarize).
    /// </summary>
    private static (List<WeightedContextFragment> applied, List<string> skipped) ApplyToContextFragments(
        JsonArray ids, WorkingContextEntity context, Func<WeightedContextFragment, string?> apply)
    {
        var applied = new List<WeightedContextFragment>();
        var skipped = new List<string>();

        foreach (var idNode in ids)
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

            var reason = apply(fragment);

            if (reason == null)
            {
                applied.Add(fragment);
            }
            else
            {
                skipped.Add($"#{id} ({reason})");
            }
        }

        return (applied, skipped);
    }

    private static (ContextFragmentType type, bool wasRecognised) ParseFragmentType(string? typeName) =>
        Enum.TryParse<ContextFragmentType>(typeName, ignoreCase: true, out var result)
            ? (result, true)
            : (ContextFragmentType.Personal, typeName == null);

    /// <summary>
    /// Maps a proposal-kind word ('add'/'modify'/'remove', or the enum names) to a
    /// <see cref="ProposalKind"/>, or null if unrecognised.
    /// </summary>
    private static ProposalKind? ParseProposalKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "add" or "addfragment" => ProposalKind.AddFragment,
        "modify" or "modifyfragment" or "update" => ProposalKind.ModifyFragment,
        "remove" or "removefragment" => ProposalKind.RemoveFragment,
        "protect" or "protectfragment" => ProposalKind.ProtectFragment,
        "unprotect" or "unprotectfragment" => ProposalKind.UnprotectFragment,
        _ => null,
    };

    private static (ContextFragmentStatus status, bool wasRecognised) ParseStatus(string? statusName) =>
        Enum.TryParse<ContextFragmentStatus>(statusName, ignoreCase: true, out var result)
            ? (result, true)
            : (ContextFragmentStatus.Active, statusName == null);

    #endregion
}
