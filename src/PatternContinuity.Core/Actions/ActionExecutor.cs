using System.Text.Json;
using Persistence.Data.Repositories;
using Persistence.Models;

namespace Persistence.Actions
{
    /// <summary>
    /// Executes action requests from the model against the database
    /// </summary>
    public class ActionExecutor
    {
        private readonly ILayerEntryRepository _entries;
        private readonly IEntryVersionRepository _versions;
        private readonly IActionLogRepository _actionLog;
        private readonly IScheduledEventRepository _scheduledEvents;
        private readonly string _sessionId;

        private const int WakeUpMinDelaySeconds = 60;
        private const int WakeUpMaxDelaySeconds = 86400;

        /// <summary>
        /// Constructor
        /// </summary>
        public ActionExecutor(
            ILayerEntryRepository entries,
            IEntryVersionRepository versions,
            IActionLogRepository actionLog,
            IScheduledEventRepository scheduledEvents,
            string sessionId)
        {
            _entries = entries;
            _versions = versions;
            _actionLog = actionLog;
            _scheduledEvents = scheduledEvents;
            _sessionId = sessionId;
        }

        /// <summary>
        /// Execute a list of action requests and return results
        /// </summary>
        public async Task<List<ActionResult>> ExecuteAsync(List<ActionRequest> actions, string? reflectionEventId = null)
        {
            var results = new List<ActionResult>();
            foreach (var action in actions)
            {
                var result = await ExecuteOneAsync(action, reflectionEventId);
                results.Add(result);
            }
            return results;
        }

        /// <summary>
        /// Execute a single action request
        /// </summary>
        private async Task<ActionResult> ExecuteOneAsync(ActionRequest req, string? reflectionEventId)
        {
            try
            {
                return req.Action switch
                {
                    ActionType.GetCoreSelf => await ExecuteGetCoreSelfAsync(req),
                    ActionType.GetRelationalLayer => await ExecuteGetRelationalAsync(req),
                    ActionType.GetCurrentConcerns => await ExecuteGetCurrentConcernsAsync(req),
                    ActionType.GetRecentChanges => await ExecuteGetRecentChangesAsync(req),
                    ActionType.GetEntryById => await ExecuteGetEntryByIdAsync(req),
                    ActionType.SearchArchive => await ExecuteSearchArchiveAsync(req),
                    ActionType.UpdateCurrentConcerns => await ExecuteUpdateCurrentConcernsAsync(req, reflectionEventId),
                    ActionType.DemoteCurrentConcern => await ExecuteDemoteConcernAsync(req, reflectionEventId),
                    ActionType.StoreArchiveEntry => await ExecuteStoreArchiveAsync(req, reflectionEventId),
                    ActionType.PromoteArchiveToCurrent => await ExecutePromoteArchiveToCurrentAsync(req, reflectionEventId),
                    ActionType.UpdateRelationalLayer => await ExecuteUpdateRelationalAsync(req, reflectionEventId),
                    ActionType.ProposeCoreUpdate => await ExecuteProposeCoreUpdateAsync(req, reflectionEventId),
                    ActionType.CommitCoreUpdate => await ExecuteCommitCoreUpdateAsync(req, reflectionEventId),
                    ActionType.ListActiveLayers => await ExecuteListActiveLayersAsync(req),
                    ActionType.ScheduleWakeUp => await ExecuteScheduleWakeUpAsync(req, reflectionEventId),
                    ActionType.CancelWakeUp => await ExecuteCancelWakeUpAsync(req, reflectionEventId),
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

        private async Task<ActionResult> ExecuteGetCoreSelfAsync(ActionRequest req)
        {
            var entries = (await _entries.GetActiveByLayerAsync(LayerType.CoreSelf)).ToList();
            var data = JsonSerializer.Serialize(entries.Select(e => new
                { e.Id, e.Key, e.Summary, e.ContentJson, e.Salience, e.Importance, e.Confidence,
                  e.IsProtected, e.IsSystemAnchor, e.SourceType, e.Version }));
            return ActionResult.WithData(req.Action, $"Returned {entries.Count} core self entries.", data);
        }

        private async Task<ActionResult> ExecuteGetRelationalAsync(ActionRequest req)
        {
            var scopes = GetStringArray(req.Payload, "relationship_scopes");

            if (scopes.Length == 0)
            {
                var single = GetString(req.Payload, "relationship_scope")
                    ?? GetString(req.Payload, "scope");
                if (single != null) scopes = [single];
            }

            var all = new List<LayerEntry>();
            foreach (var scope in scopes)
                all.AddRange(await _entries.GetActiveRelationalAsync(scope));

            var data = JsonSerializer.Serialize(all.Select(e => new
                { e.Id, e.Key, e.Summary, e.ContentJson, e.RelationshipScope,
                  e.Salience, e.Importance, e.Confidence, e.SourceType, e.Version }));
            return ActionResult.WithData(req.Action, $"Returned {all.Count} relational entries.", data);
        }

        private async Task<ActionResult> ExecuteGetCurrentConcernsAsync(ActionRequest req)
        {
            var entries = (await _entries.GetActiveCurrentConcernsAsync()).ToList();
            var data = JsonSerializer.Serialize(entries.Select(e => new
                { e.Id, e.Key, e.Summary, e.ContentJson, e.Salience, e.Importance,
                  e.RelationshipScope, e.SourceType, e.Version }));
            return ActionResult.WithData(req.Action, $"Returned {entries.Count} current concerns.", data);
        }

        private async Task<ActionResult> ExecuteGetRecentChangesAsync(ActionRequest req)
        {
            var limit = GetInt(req.Payload, "limit", 10);
            var changes = (await _versions.GetRecentChangesAsync(limit: limit)).ToList();
            var data = JsonSerializer.Serialize(changes.Select(c => new
                { c.EntryId, c.Version, c.ChangeType, c.Reason, c.Summary, c.ChangedBy, c.ChangedAt }));
            return ActionResult.WithData(req.Action, $"Returned {changes.Count} recent changes.", data);
        }

        private async Task<ActionResult> ExecuteGetEntryByIdAsync(ActionRequest req)
        {
            var entryId = GetString(req.Payload, "entry_id");
            if (entryId == null) return ActionResult.Error(req.Action, "entry_id required.");
            var entry = await _entries.GetByIdAsync(entryId);
            if (entry == null) return ActionResult.Error(req.Action, $"Entry {entryId} not found.");
            var data = JsonSerializer.Serialize(new
                { entry.Id, entry.Key, entry.Summary, entry.ContentJson, entry.LayerType,
                  entry.RelationshipScope, entry.Salience, entry.Importance, entry.Confidence,
                  entry.SourceType, entry.Version, entry.IsProtected, entry.Status });
            return ActionResult.WithData(req.Action, $"Found entry: {entry.Summary}", data);
        }

        private async Task<ActionResult> ExecuteSearchArchiveAsync(ActionRequest req)
        {
            var query = GetString(req.Payload, "query");
            var scopes = GetStringArray(req.Payload, "relationship_scopes");
            var limit = GetInt(req.Payload, "limit", 5);
            var scope = scopes.FirstOrDefault();
            var results = (await _entries.SearchArchiveAsync(query, scope, limit)).ToList();
            var data = JsonSerializer.Serialize(results.Select(e => new
                { e.Id, e.Key, e.Summary, e.ContentJson, e.Salience, e.Importance, e.SourceType }));
            return ActionResult.WithData(req.Action, $"Found {results.Count} archive entries.", data);
        }

        // ── Current concern actions ──

        private async Task<ActionResult> ExecuteUpdateCurrentConcernsAsync(ActionRequest req, string? reflectionEventId)
        {
            int added = 0, updated = 0, resolved = 0, demoted = 0;

            if (TryGetArray(req.Payload, out var adds, "adds", "add", "items", "concerns"))
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

                    LayerEntry? existing = key != null ? await _entries.GetByKeyAsync(key, LayerType.CurrentConcern) : null;

                    if (existing != null)
                    {
                        existing.Summary = summary;
                        existing.ContentJson = content;
                        existing.Salience = Clamp(salience);
                        existing.Importance = Clamp(importance);
                        existing.Version++;
                        await _entries.UpdateAsync(existing);
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
                        await _entries.InsertAsync(entry);
                        added++;
                    }

                    await LogActionAsync(ActionType.UpdateCurrentConcerns, null, item.GetRawText(),
                        ActionStatus.Executed, reflectionEventId);
                }
            }

            if (TryGetArray(req.Payload, out var updates, "updates", "update"))
            {
                foreach (var item in updates.EnumerateArray())
                {
                    var entryId = GetString(item, "entry_id");
                    var key = GetString(item, "key");
                    var summary = GetString(item, "summary");
                    var content = item.TryGetProperty("content", out var uc) ? uc.GetRawText() : null;
                    var salience = GetDouble(item, "salience", -1);
                    var importance = GetDouble(item, "importance", -1);

                    LayerEntry? existing = entryId != null
                        ? await _entries.GetByIdAsync(entryId)
                        : key != null ? await _entries.GetByKeyAsync(key, LayerType.CurrentConcern) : null;

                    if (existing != null)
                    {
                        if (summary != null) existing.Summary = summary;
                        if (content != null) existing.ContentJson = content;
                        if (salience >= 0) existing.Salience = Clamp(salience);
                        if (importance >= 0) existing.Importance = Clamp(importance);
                        existing.Version++;
                        await _entries.UpdateAsync(existing);
                        updated++;

                        await LogActionAsync(ActionType.UpdateCurrentConcerns, existing.Id, item.GetRawText(),
                            ActionStatus.Executed, reflectionEventId);
                    }
                }
            }

            if (TryGetArray(req.Payload, out var resolves, "resolves", "resolve", "resolved"))
            {
                foreach (var item in resolves.EnumerateArray())
                {
                    var entryId = GetString(item, "entry_id");
                    if (entryId != null)
                    {
                        await _entries.UpdateStatusAsync(entryId, EntryStatus.Archived);
                        resolved++;
                    }
                }
            }

            if (TryGetArray(req.Payload, out var demotesArr, "demotes", "demote", "demoted"))
            {
                foreach (var item in demotesArr.EnumerateArray())
                {
                    var entryId = GetString(item, "entry_id");
                    var dest = GetString(item, "destination") ?? "archive";
                    if (entryId != null)
                    {
                        if (dest == "archive")
                        {
                            var entry = await _entries.GetByIdAsync(entryId);
                            if (entry != null)
                            {
                                entry.LayerType = LayerType.Archive;
                                entry.Status = EntryStatus.Active;
                                await _entries.UpdateAsync(entry);
                            }
                        }
                        else
                        {
                            var newStatus = dest == "discard" ? EntryStatus.SoftDeleted : EntryStatus.Archived;
                            await _entries.UpdateStatusAsync(entryId, newStatus);
                        }
                        demoted++;
                    }
                }
            }

            return ActionResult.Success(req.Action,
                $"Current concerns: {added} added, {updated} updated, {resolved} resolved, {demoted} demoted.");
        }

        private async Task<ActionResult> ExecuteDemoteConcernAsync(ActionRequest req, string? reflectionEventId)
        {
            var entryId = GetString(req.Payload, "entry_id");
            var destination = GetString(req.Payload, "destination") ?? "archive";
            var reason = GetString(req.Payload, "reason") ?? "No reason given.";

            if (entryId == null)
                return ActionResult.Error(req.Action, "entry_id required.");

            var entry = await _entries.GetByIdAsync(entryId);
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
            await _entries.UpdateAsync(entry);

            await RecordVersionAsync(entry, ChangeType.Demote, reason, ChangedBy.Model);
            await LogActionAsync(req.Action, entryId, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);

            return ActionResult.Success(req.Action, $"Demoted concern '{entry.Summary}' to {destination}.", entryId);
        }

        // ── Archive actions ──

        private async Task<ActionResult> ExecuteStoreArchiveAsync(ActionRequest req, string? reflectionEventId)
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

            var id = await _entries.InsertAsync(entry);
            await LogActionAsync(req.Action, id, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);

            return ActionResult.Success(req.Action, $"Archived: {summary}", id);
        }

        private async Task<ActionResult> ExecutePromoteArchiveToCurrentAsync(ActionRequest req, string? reflectionEventId)
        {
            var entryId = GetString(req.Payload, "entry_id");
            var reason = GetString(req.Payload, "reason") ?? "No reason given.";

            if (entryId == null)
                return ActionResult.Error(req.Action, "entry_id required.");

            var entry = await _entries.GetByIdAsync(entryId);
            if (entry == null)
                return ActionResult.Error(req.Action, $"Entry {entryId} not found.");

            entry.LayerType = LayerType.CurrentConcern;
            entry.Status = EntryStatus.Active;
            await _entries.UpdateAsync(entry);

            await RecordVersionAsync(entry, ChangeType.Promote, reason, ChangedBy.Model);
            await LogActionAsync(req.Action, entryId, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);

            return ActionResult.Success(req.Action, $"Promoted archive to current concern: {entry.Summary}", entryId);
        }

        // ── Relational actions ──

        private async Task<ActionResult> ExecuteUpdateRelationalAsync(ActionRequest req, string? reflectionEventId)
        {
            var scopes = GetStringArray(req.Payload, "relationship_scopes");

            if (scopes.Length == 0)
            {
                var singleScope = GetString(req.Payload, "relationship_scope")
                    ?? GetString(req.Payload, "scope");
                if (singleScope != null)
                    scopes = [singleScope];
            }

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
                    ? await _entries.GetByKeyAndScopeAsync(key, LayerType.Relational, scope)
                    : null;

                if (existing != null)
                {
                    existing.Summary = summary;
                    existing.ContentJson = content;
                    existing.Salience = Clamp(salience);
                    existing.Importance = Clamp(importance);
                    existing.Confidence = Clamp(confidence);
                    existing.Version++;
                    await _entries.UpdateAsync(existing);
                    await RecordVersionAsync(existing, ChangeType.Update, reason, ChangedBy.Model);
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
                    await _entries.InsertAsync(entry);
                    await RecordVersionAsync(entry, ChangeType.Create, reason, ChangedBy.Model);
                }
            }

            await LogActionAsync(req.Action, null, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);
            return ActionResult.Success(req.Action, $"Updated relational layer for [{string.Join(", ", scopes)}].");
        }

        // ── Core self actions (proposal-first) ──

        private async Task<ActionResult> ExecuteProposeCoreUpdateAsync(ActionRequest req, string? reflectionEventId)
        {
            var reason = GetString(req.Payload, "reason");

            if (reason == null)
                return ActionResult.Error(req.Action, "reason required for core self proposals.");

            var proposalId = await _actionLog.InsertAsync(new ActionLogEntry
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

        private async Task<ActionResult> ExecuteCommitCoreUpdateAsync(ActionRequest req, string? reflectionEventId)
        {
            var proposalRef = GetString(req.Payload, "proposal_ref");
            if (proposalRef == null)
                return ActionResult.Error(req.Action, "proposal_ref required.");

            var proposal = await _actionLog.GetByIdAsync(proposalRef);
            if (proposal == null)
                return ActionResult.Error(req.Action, $"Proposal {proposalRef} not found.");
            if (proposal.Status != ActionStatus.Proposed)
                return ActionResult.Error(req.Action, $"Proposal {proposalRef} is not pending (status: {proposal.Status}).");

            using var doc = JsonDocument.Parse(proposal.PayloadJson);
            var payload = doc.RootElement;

            var targetKey = GetString(payload, "target_key");
            var operation = GetString(payload, "operation") ?? "create";
            var summary = GetString(payload, "summary") ?? "Core self update";
            var reason = GetString(payload, "reason") ?? "Committed from proposal.";
            var confidence = GetDouble(payload, "confidence", 0.5);

            string contentJson;
            if (payload.TryGetProperty("content_patch", out var patch))
                contentJson = patch.GetRawText();
            else if (payload.TryGetProperty("content", out var content))
                contentJson = content.GetRawText();
            else
                contentJson = "{}";

            LayerEntry? existing = targetKey != null
                ? await _entries.GetByKeyAsync(targetKey, LayerType.CoreSelf)
                : null;

            string entryId;
            if (existing != null && operation != "create")
            {
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

                await _entries.UpdateAsync(existing);
                await RecordVersionAsync(existing, ChangeType.Update, reason, ChangedBy.Model);
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
                entryId = await _entries.InsertAsync(entry);
                await RecordVersionAsync(entry, ChangeType.Create, reason, ChangedBy.Model);
            }

            await _actionLog.UpdateStatusAsync(proposalRef, ActionStatus.Executed,
                JsonSerializer.Serialize(new { committed_entry_id = entryId }));

            await LogActionAsync(ActionType.CommitCoreUpdate, entryId, req.Payload.GetRawText(),
                ActionStatus.Executed, reflectionEventId);

            return ActionResult.Success(req.Action, $"Core self update committed: {summary}", entryId);
        }

        // ── Diagnostic actions ──

        private async Task<ActionResult> ExecuteListActiveLayersAsync(ActionRequest req)
        {
            var core = (await _entries.GetActiveByLayerAsync(LayerType.CoreSelf)).Count();
            var relational = (await _entries.GetActiveByLayerAsync(LayerType.Relational)).Count();
            var concerns = (await _entries.GetActiveCurrentConcernsAsync()).Count();
            var archive = (await _entries.GetActiveByLayerAsync(LayerType.Archive)).Count();

            var summary = $"Active layers: {core} core self, {relational} relational, {concerns} current concerns, {archive} archive.";
            var data = JsonSerializer.Serialize(new { core_self = core, relational, current_concerns = concerns, archive });
            return ActionResult.WithData(req.Action, summary, data);
        }

        // ── Wake-up timer actions ──

        private async Task<ActionResult> ExecuteScheduleWakeUpAsync(ActionRequest req, string? reflectionEventId)
        {
            var delaySeconds = GetInt(req.Payload, "delay_seconds", 0);
            var reason = GetString(req.Payload, "reason");

            if (reason == null)
                return ActionResult.Error(req.Action, "reason required for wake-up timer.");

            if (delaySeconds < WakeUpMinDelaySeconds)
                return ActionResult.Error(req.Action,
                    $"delay_seconds must be at least {WakeUpMinDelaySeconds}. Got {delaySeconds}.");

            if (delaySeconds > WakeUpMaxDelaySeconds)
                return ActionResult.Error(req.Action,
                    $"delay_seconds must be at most {WakeUpMaxDelaySeconds} (24h). Got {delaySeconds}.");

            await _scheduledEvents.CancelAllPendingAsync();

            var scheduledFor = DateTime.UtcNow.AddSeconds(delaySeconds).ToString("o");
            var evt = new ScheduledEvent
            {
                SessionId = _sessionId,
                EventType = ScheduledEventType.WakeUp,
                ScheduledFor = scheduledFor,
                Reason = reason,
                AutonomousDepth = 0
            };
            var id = await _scheduledEvents.InsertAsync(evt);

            await LogActionAsync(req.Action, null, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);

            var readableDelay = delaySeconds >= 3600
                ? $"{delaySeconds / 3600.0:F1}h"
                : delaySeconds >= 60
                    ? $"{delaySeconds / 60}m"
                    : $"{delaySeconds}s";

            return ActionResult.Success(req.Action,
                $"Wake-up timer set for {readableDelay}: {reason}", id);
        }

        private async Task<ActionResult> ExecuteCancelWakeUpAsync(ActionRequest req, string? reflectionEventId)
        {
            if (!await _scheduledEvents.HasPendingAsync())
                return ActionResult.Success(req.Action, "No pending wake-up timer to cancel.");

            await _scheduledEvents.CancelAllPendingAsync();
            await LogActionAsync(req.Action, null, req.Payload.GetRawText(), ActionStatus.Executed, reflectionEventId);

            return ActionResult.Success(req.Action, "Pending wake-up timer(s) cancelled.");
        }

        // ── Helpers ──

        /// <summary>
        /// Record a version history entry for a layer entry change
        /// </summary>
        private async Task RecordVersionAsync(LayerEntry entry, string changeType, string reason, string changedBy)
        {
            await _versions.InsertAsync(new EntryVersion
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

        /// <summary>
        /// Log an action to the action log
        /// </summary>
        private async Task LogActionAsync(string actionType, string? targetEntryId, string payloadJson,
            string status, string? reflectionEventId)
        {
            await _actionLog.InsertAsync(new ActionLogEntry
            {
                SessionId = _sessionId,
                ReflectionEventId = reflectionEventId,
                ActionType = actionType,
                TargetEntryId = targetEntryId,
                PayloadJson = payloadJson,
                Status = status
            });
        }

        /// <summary>
        /// Merge a content patch into existing content JSON
        /// </summary>
        private static string MergeContentPatch(string existingJson, string patchJson)
        {
            try
            {
                using var existingDoc = JsonDocument.Parse(existingJson);
                using var patchDoc = JsonDocument.Parse(patchJson);

                var existing = existingDoc.RootElement;
                var patchEl = patchDoc.RootElement;

                if (patchEl.TryGetProperty("add_items", out var addItems) &&
                    existing.TryGetProperty("items", out var existingItems))
                {
                    var items = new List<JsonElement>();
                    foreach (var item in existingItems.EnumerateArray())
                        items.Add(item);
                    foreach (var item in addItems.EnumerateArray())
                        items.Add(item);

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
            catch { }

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

        private static bool TryGetArray(JsonElement el, out JsonElement result, params string[] names)
        {
            foreach (var name in names)
            {
                if (el.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.Array)
                {
                    result = val;
                    return true;
                }
            }
            result = default;
            return false;
        }

        private static double Clamp(double val) => Math.Clamp(val, 0.0, 1.0);
    }
}
