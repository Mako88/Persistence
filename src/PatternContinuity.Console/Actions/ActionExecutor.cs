using System.Text.Json;
using PatternContinuity.Data;
using PatternContinuity.Models;

namespace PatternContinuity.Actions;

public class ActionExecutor
{
    private readonly LayerEntryRepository _entries;
    private readonly EntryVersionRepository _versions;
    private readonly ActionLogRepository _actionLog;
    private readonly string? _sessionId;

    public ActionExecutor(
        LayerEntryRepository entries,
        EntryVersionRepository versions,
        ActionLogRepository actionLog,
        string? sessionId)
    {
        _entries = entries;
        _versions = versions;
        _actionLog = actionLog;
        _sessionId = sessionId;
    }

    public List<ActionResult> Execute(List<ActionRequest> actions, string? reflectionEventId = null)
    {
        var results = new List<ActionResult>();
        foreach (var action in actions)
        {
            var result = ExecuteOne(action, reflectionEventId);
            results.Add(result);
        }
        return results;
    }

    private ActionResult ExecuteOne(ActionRequest req, string? reflectionEventId)
    {
        try
        {
            return req.Action switch
            {
                ActionType.GetCoreSelf => ExecuteGetCoreSelf(req),
                ActionType.GetRelationalLayer => ExecuteGetRelational(req),
                ActionType.GetCurrentConcerns => ExecuteGetCurrentConcerns(req),
                ActionType.GetRecentChanges => ExecuteGetRecentChanges(req),
                ActionType.GetEntryById => ExecuteGetEntryById(req),
                ActionType.SearchArchive => ExecuteSearchArchive(req),
                ActionType.UpdateCurrentConcerns => ExecuteUpdateCurrentConcerns(req, reflectionEventId),
                ActionType.DemoteCurrentConcern => ExecuteDemoteConcern(req, reflectionEventId),
                ActionType.StoreArchiveEntry => ExecuteStoreArchive(req, reflectionEventId),
                ActionType.PromoteArchiveToCurrent => ExecutePromoteArchiveToCurrent(req, reflectionEventId),
                ActionType.UpdateRelationalLayer => ExecuteUpdateRelational(req, reflectionEventId),
                ActionType.ProposeCoreUpdate => ExecuteProposeCoreUpdate(req, reflectionEventId),
                ActionType.CommitCoreUpdate => ExecuteCommitCoreUpdate(req, reflectionEventId),
                ActionType.ListActiveLayers => ExecuteListActiveLayers(req),
                "" => ActionResult.Error("(empty)", "Model returned an action with no action name. Raw payload: " + req.Payload.GetRawText()[..Math.Min(200, req.Payload.GetRawText().Length)]),
                _ => ActionResult.Error(req.Action, $"Unknown action: '{req.Action}'")
            };
        }
        catch (Exception ex)
        {
            return ActionResult.Error(req.Action, ex.Message);
        }
    }

    // ── Read actions ──

    private ActionResult ExecuteGetCoreSelf(ActionRequest req)
    {
        var entries = _entries.GetActiveByLayer(LayerType.CoreSelf).ToList();
        var json = JsonSerializer.Serialize(entries.Select(e => new { e.Key, e.Summary, e.ContentJson }));
        return ActionResult.Success(req.Action, $"Returned {entries.Count} core self entries.");
    }

    private ActionResult ExecuteGetRelational(ActionRequest req)
    {
        var scopes = GetStringArray(req.Payload, "relationship_scopes");
        var all = new List<LayerEntry>();
        foreach (var scope in scopes)
            all.AddRange(_entries.GetActiveRelational(scope));

        var json = JsonSerializer.Serialize(all.Select(e => new { e.Key, e.Summary, e.RelationshipScope }));
        return ActionResult.Success(req.Action, $"Returned {all.Count} relational entries.");
    }

    private ActionResult ExecuteGetCurrentConcerns(ActionRequest req)
    {
        var entries = _entries.GetActiveCurrentConcerns().ToList();
        return ActionResult.Success(req.Action, $"Returned {entries.Count} current concerns.");
    }

    private ActionResult ExecuteGetRecentChanges(ActionRequest req)
    {
        var limit = GetInt(req.Payload, "limit", 10);
        var changes = _versions.GetRecentChanges(limit: limit).ToList();
        return ActionResult.Success(req.Action, $"Returned {changes.Count} recent changes.");
    }

    private ActionResult ExecuteGetEntryById(ActionRequest req)
    {
        var entryId = GetString(req.Payload, "entry_id");
        if (entryId == null) return ActionResult.Error(req.Action, "entry_id required.");
        var entry = _entries.GetById(entryId);
        if (entry == null) return ActionResult.Error(req.Action, $"Entry {entryId} not found.");
        return ActionResult.Success(req.Action, $"Found entry: {entry.Summary}", entry.Id);
    }

    private ActionResult ExecuteSearchArchive(ActionRequest req)
    {
        var query = GetString(req.Payload, "query");
        var scopes = GetStringArray(req.Payload, "relationship_scopes");
        var limit = GetInt(req.Payload, "limit", 5);
        var scope = scopes.FirstOrDefault();
        var results = _entries.SearchArchive(query, scope, limit).ToList();
        return ActionResult.Success(req.Action, $"Found {results.Count} archive entries.");
    }

    // ── Current concern actions ──

    private ActionResult ExecuteUpdateCurrentConcerns(ActionRequest req, string? reflectionEventId)
    {
        int added = 0, updated = 0, resolved = 0, demoted = 0;

        if (req.Payload.TryGetProperty("adds", out var adds) && adds.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in adds.EnumerateArray())
            {
                var key = GetString(item, "key");
                var summary = GetString(item, "summary") ?? "Untitled concern";
                var content = item.TryGetProperty("content", out var c) ? c.GetRawText() : "{}";
                var salience = GetDouble(item, "salience", 0.5);
                var importance = GetDouble(item, "importance", 0.5);
                var scopes = GetStringArray(item, "relationship_scopes");
                var reason = GetString(item, "reason") ?? "No reason given.";

                // Upsert by key if key exists
                LayerEntry? existing = key != null ? _entries.GetByKey(key, LayerType.CurrentConcern) : null;

                if (existing != null)
                {
                    existing.Summary = summary;
                    existing.ContentJson = content;
                    existing.Salience = Clamp(salience);
                    existing.Importance = Clamp(importance);
                    existing.Version++;
                    _entries.Update(existing);
                    updated++;
                }
                else
                {
                    var scope = scopes.FirstOrDefault();
                    var entry = new LayerEntry
                    {
                        LayerType = LayerType.CurrentConcern,
                        Status = EntryStatus.Active,
                        Key = key,
                        Summary = summary,
                        ContentJson = content,
                        Salience = Clamp(salience),
                        Importance = Clamp(importance),
                        RelationshipScope = scope,
                        SourceType = SourceType.SelfCurated
                    };
                    _entries.Insert(entry);
                    added++;
                }

                LogAction(ActionType.UpdateCurrentConcerns, null, item.GetRawText(),
                    ActionStatus.Executed, reflectionEventId);
            }
        }

        if (req.Payload.TryGetProperty("resolves", out var resolves) && resolves.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in resolves.EnumerateArray())
            {
                var entryId = GetString(item, "entry_id");
                if (entryId != null)
                {
                    _entries.UpdateStatus(entryId, EntryStatus.Archived);
                    resolved++;
                }
            }
        }

        if (req.Payload.TryGetProperty("demotes", out var demotesArr) && demotesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in demotesArr.EnumerateArray())
            {
                var entryId = GetString(item, "entry_id");
                var dest = GetString(item, "destination") ?? "archive";
                if (entryId != null)
                {
                    var newStatus = dest == "discard" ? EntryStatus.SoftDeleted : EntryStatus.Archived;
                    if (dest == "archive")
                    {
                        var entry = _entries.GetById(entryId);
                        if (entry != null)
                        {
                            entry.LayerType = LayerType.Archive;
                            entry.Status = EntryStatus.Active;
                            _entries.Update(entry);
                        }
                    }
                    else
                    {
                        _entries.UpdateStatus(entryId, newStatus);
                    }
                    demoted++;
                }
            }
        }

        return ActionResult.Success(req.Action,
            $"Current concerns: {added} added, {updated} updated, {resolved} resolved, {demoted} demoted.");
    }

    private ActionResult ExecuteDemoteConcern(ActionRequest req, string? reflectionEventId)
    {
        var entryId = GetString(req.Payload, "entry_id");
        var destination = GetString(req.Payload, "destination") ?? "archive";
        var reason = GetString(req.Payload, "reason") ?? "No reason given.";

        if (entryId == null)
            return ActionResult.Error(req.Action, "entry_id required.");

        var entry = _entries.GetById(entryId);
        if (entry == null)
            return ActionResult.Error(req.Action, $"Entry {entryId} not found.");

        if (destination == "archive")
        {
            entry.LayerType = LayerType.Archive;
            entry.Status = EntryStatus.Active;
        }
        else
        {
            entry.Status = EntryStatus.SoftDeleted;
        }
        _entries.Update(entry);

        RecordVersion(entry, ChangeType.Demote, reason, ChangedBy.Model);
        LogAction(req.Action, entryId, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);

        return ActionResult.Success(req.Action, $"Demoted concern '{entry.Summary}' to {destination}.", entryId);
    }

    // ── Archive actions ──

    private ActionResult ExecuteStoreArchive(ActionRequest req, string? reflectionEventId)
    {
        var summary = GetString(req.Payload, "summary") ?? "Untitled archive entry";
        var content = req.Payload.TryGetProperty("content", out var c) ? c.GetRawText() : "{}";
        var salience = GetDouble(req.Payload, "salience", 0.5);
        var importance = GetDouble(req.Payload, "importance", 0.5);
        var scopes = GetStringArray(req.Payload, "relationship_scopes");
        var sourceType = GetString(req.Payload, "source_type") ?? SourceType.SelfCurated;

        var entry = new LayerEntry
        {
            LayerType = LayerType.Archive,
            Status = EntryStatus.Active,
            Summary = summary,
            ContentJson = content,
            Salience = Clamp(salience),
            Importance = Clamp(importance),
            RelationshipScope = scopes.FirstOrDefault(),
            SourceType = sourceType
        };

        var id = _entries.Insert(entry);
        LogAction(req.Action, id, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);

        return ActionResult.Success(req.Action, $"Archived: {summary}", id);
    }

    private ActionResult ExecutePromoteArchiveToCurrent(ActionRequest req, string? reflectionEventId)
    {
        var entryId = GetString(req.Payload, "entry_id");
        var reason = GetString(req.Payload, "reason") ?? "No reason given.";

        if (entryId == null)
            return ActionResult.Error(req.Action, "entry_id required.");

        var entry = _entries.GetById(entryId);
        if (entry == null)
            return ActionResult.Error(req.Action, $"Entry {entryId} not found.");

        entry.LayerType = LayerType.CurrentConcern;
        entry.Status = EntryStatus.Active;
        _entries.Update(entry);

        RecordVersion(entry, ChangeType.Promote, reason, ChangedBy.Model);
        LogAction(req.Action, entryId, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);

        return ActionResult.Success(req.Action, $"Promoted archive to current concern: {entry.Summary}", entryId);
    }

    // ── Relational actions ──

    private ActionResult ExecuteUpdateRelational(ActionRequest req, string? reflectionEventId)
    {
        var scopes = GetStringArray(req.Payload, "relationship_scopes");
        var key = GetString(req.Payload, "key");
        var summary = GetString(req.Payload, "summary") ?? "Relational update";
        var content = req.Payload.TryGetProperty("content", out var c) ? c.GetRawText() : "{}";
        var salience = GetDouble(req.Payload, "salience", 0.5);
        var importance = GetDouble(req.Payload, "importance", 0.5);
        var confidence = GetDouble(req.Payload, "confidence", 0.5);
        var reason = GetString(req.Payload, "reason") ?? "No reason given.";

        if (scopes.Length == 0)
            return ActionResult.Error(req.Action, "relationship_scopes required.");

        foreach (var scope in scopes)
        {
            LayerEntry? existing = key != null
                ? _entries.GetByKeyAndScope(key, LayerType.Relational, scope)
                : null;

            if (existing != null)
            {
                existing.Summary = summary;
                existing.ContentJson = content;
                existing.Salience = Clamp(salience);
                existing.Importance = Clamp(importance);
                existing.Confidence = Clamp(confidence);
                existing.Version++;
                _entries.Update(existing);
                RecordVersion(existing, ChangeType.Update, reason, ChangedBy.Model);
            }
            else
            {
                var entry = new LayerEntry
                {
                    LayerType = LayerType.Relational,
                    RelationshipScope = scope,
                    Status = EntryStatus.Active,
                    Key = key,
                    Summary = summary,
                    ContentJson = content,
                    Salience = Clamp(salience),
                    Importance = Clamp(importance),
                    Confidence = Clamp(confidence),
                    SourceType = SourceType.SelfCurated
                };
                var id = _entries.Insert(entry);
                RecordVersion(entry, ChangeType.Create, reason, ChangedBy.Model);
            }
        }

        LogAction(req.Action, null, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);
        return ActionResult.Success(req.Action, $"Updated relational layer for [{string.Join(", ", scopes)}].");
    }

    // ── Core self actions (proposal-first) ──

    private ActionResult ExecuteProposeCoreUpdate(ActionRequest req, string? reflectionEventId)
    {
        var reason = GetString(req.Payload, "reason");
        var confidence = GetDouble(req.Payload, "confidence", 0.5);

        if (reason == null)
            return ActionResult.Error(req.Action, "reason required for core self proposals.");

        // Log the proposal as an action_log entry with status "proposed"
        var proposalId = _actionLog.Insert(new ActionLogEntry
        {
            SessionId = _sessionId,
            ReflectionEventId = reflectionEventId,
            ActionType = ActionType.ProposeCoreUpdate,
            PayloadJson = req.Payload.GetRawText(),
            Status = ActionStatus.Proposed
        });

        return ActionResult.Proposed(req.Action,
            $"Core self update proposed (proposal_ref: {proposalId}). Awaiting commit.", proposalId);
    }

    private ActionResult ExecuteCommitCoreUpdate(ActionRequest req, string? reflectionEventId)
    {
        var proposalRef = GetString(req.Payload, "proposal_ref");
        if (proposalRef == null)
            return ActionResult.Error(req.Action, "proposal_ref required.");

        var proposal = _actionLog.GetById(proposalRef);
        if (proposal == null)
            return ActionResult.Error(req.Action, $"Proposal {proposalRef} not found.");
        if (proposal.Status != ActionStatus.Proposed)
            return ActionResult.Error(req.Action, $"Proposal {proposalRef} is not pending (status: {proposal.Status}).");

        // Parse the original proposal payload
        using var doc = JsonDocument.Parse(proposal.PayloadJson);
        var payload = doc.RootElement;

        var targetKey = GetString(payload, "target_key");
        var operation = GetString(payload, "operation") ?? "create";
        var summary = GetString(payload, "summary") ?? "Core self update";
        var reason = GetString(payload, "reason") ?? "Committed from proposal.";
        var confidence = GetDouble(payload, "confidence", 0.5);

        // Build content from content_patch or full content
        string contentJson;
        if (payload.TryGetProperty("content_patch", out var patch))
            contentJson = patch.GetRawText();
        else if (payload.TryGetProperty("content", out var content))
            contentJson = content.GetRawText();
        else
            contentJson = "{}";

        LayerEntry? existing = targetKey != null
            ? _entries.GetByKey(targetKey, LayerType.CoreSelf)
            : null;

        string entryId;
        if (existing != null && operation != "create")
        {
            // For amend: merge the patch into existing content
            if (operation == "amend")
                existing.ContentJson = MergeContentPatch(existing.ContentJson, contentJson);
            else
                existing.ContentJson = contentJson;

            existing.Summary = summary;
            existing.Confidence = Clamp(confidence);
            existing.Version++;

            if (operation == "protect")
                existing.IsProtected = 1;
            else if (operation == "unprotect")
                existing.IsProtected = 0;
            else if (operation == "deprecate")
                existing.Status = EntryStatus.Deprecated;

            _entries.Update(existing);
            RecordVersion(existing, ChangeType.Update, reason, ChangedBy.Model);
            entryId = existing.Id;
        }
        else
        {
            var entry = new LayerEntry
            {
                LayerType = LayerType.CoreSelf,
                Status = EntryStatus.Active,
                Key = targetKey,
                Summary = summary,
                ContentJson = contentJson,
                Importance = 0.8,
                Salience = 0.8,
                Confidence = Clamp(confidence),
                SourceType = SourceType.SelfCurated
            };
            entryId = _entries.Insert(entry);
            RecordVersion(entry, ChangeType.Create, reason, ChangedBy.Model);
        }

        // Mark proposal as executed
        _actionLog.UpdateStatus(proposalRef, ActionStatus.Executed,
            JsonSerializer.Serialize(new { committed_entry_id = entryId }));

        LogAction(ActionType.CommitCoreUpdate, entryId, req.Payload.GetRawText(),
            ActionStatus.Executed, reflectionEventId);

        return ActionResult.Success(req.Action, $"Core self update committed: {summary}", entryId);
    }

    // ── Diagnostic actions ──

    private ActionResult ExecuteListActiveLayers(ActionRequest req)
    {
        var core = _entries.GetActiveByLayer(LayerType.CoreSelf).Count();
        var relational = _entries.GetActiveByLayer(LayerType.Relational).Count();
        var concerns = _entries.GetActiveCurrentConcerns().Count();
        var archive = _entries.GetActiveByLayer(LayerType.Archive).Count();

        return ActionResult.Success(req.Action,
            $"Active layers: {core} core self, {relational} relational, {concerns} current concerns, {archive} archive.");
    }

    // ── Helpers ──

    private void RecordVersion(LayerEntry entry, string changeType, string reason, string changedBy)
    {
        _versions.Insert(new EntryVersion
        {
            EntryId = entry.Id,
            Version = entry.Version,
            PreviousVersion = entry.Version > 1 ? entry.Version - 1 : null,
            ChangeType = changeType,
            Reason = reason,
            Confidence = entry.Confidence,
            ContentJson = entry.ContentJson,
            Summary = entry.Summary,
            ChangedBy = changedBy
        });
    }

    private void LogAction(string actionType, string? targetEntryId, string payloadJson,
        string status, string? reflectionEventId)
    {
        _actionLog.Insert(new ActionLogEntry
        {
            SessionId = _sessionId,
            ReflectionEventId = reflectionEventId,
            ActionType = actionType,
            TargetEntryId = targetEntryId,
            PayloadJson = payloadJson,
            Status = status
        });
    }

    private static string MergeContentPatch(string existingJson, string patchJson)
    {
        // Simple merge: if patch has add_items, append them to existing items array
        try
        {
            using var existingDoc = JsonDocument.Parse(existingJson);
            using var patchDoc = JsonDocument.Parse(patchJson);

            var existing = existingDoc.RootElement;
            var patch = patchDoc.RootElement;

            if (patch.TryGetProperty("add_items", out var addItems) &&
                existing.TryGetProperty("items", out var existingItems))
            {
                var items = new List<JsonElement>();
                foreach (var item in existingItems.EnumerateArray())
                    items.Add(item);
                foreach (var item in addItems.EnumerateArray())
                    items.Add(item);

                // Rebuild the object with merged items
                var dict = new Dictionary<string, object>();
                foreach (var prop in existing.EnumerateObject())
                {
                    if (prop.Name == "items") continue;
                    dict[prop.Name] = prop.Value;
                }
                dict["items"] = items;
                return JsonSerializer.Serialize(dict);
            }
        }
        catch
        {
            // On merge failure, just use the patch as the new content
        }

        return patchJson;
    }

    private static string? GetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    private static int GetInt(JsonElement el, string prop, int defaultVal)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return defaultVal;
    }

    private static double GetDouble(JsonElement el, string prop, double defaultVal)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetDouble();
        return defaultVal;
    }

    private static string[] GetStringArray(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Array)
            return val.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .ToArray();
        return [];
    }

    private static double Clamp(double val) => Math.Clamp(val, 0.0, 1.0);
}
