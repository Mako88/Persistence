using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Services;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;

namespace Persistence.Runtime.ActionHandlers;

/// <summary>
/// Handles <see cref="ModelAction.ExecuteActions"/> by performing side-effect operations
/// (scheduling events, querying logs, recording actions). Commands are discovered via
/// <see cref="CommandAttribute"/> — send <c>{"list": {}}</c> at runtime to see all
/// available commands and their schemas.
/// </summary>
[Service(registerAsType: typeof(IActionHandler), key: ModelAction.ExecuteActions)]
[SuppressMessage("Style", "IDE0051:Fade out unused members", Justification = "Referenced through reflections in base class")]
public class ExecuteActionsHandler : CommandHandler
{
    private readonly IScheduledEventRepository scheduledEventRepo;
    private readonly IAuditLogRepository auditLogRepo;
    private readonly IActionLogRepository actionLogRepo;
    private readonly ISessionContext sessionContext;

    /// <summary>
    /// Constructor
    /// </summary>
    public ExecuteActionsHandler(
        IScheduledEventRepository scheduledEventRepo,
        IAuditLogRepository auditLogRepo,
        IActionLogRepository actionLogRepo,
        ISessionContext sessionContext,
        IEventBus eventBus) : base(eventBus)
    {
        this.scheduledEventRepo = scheduledEventRepo;
        this.auditLogRepo = auditLogRepo;
        this.actionLogRepo = actionLogRepo;
        this.sessionContext = sessionContext;
    }

    // ── Commands ─────────────────────────────────────────────────

    [Command("schedule", "Schedule a future event")]
    [CommandField("name", "string", required: true, Description = "Event name")]
    [CommandField("scheduled_for", "string", required: true, Description = "UTC datetime")]
    [CommandField("notes", "string", Description = "Additional notes")]
    private async Task<string> ExecuteScheduleAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var name = command?["name"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Schedule failed: 'name' is required";
        }

        var scheduledForStr = command?["scheduled_for"]?.GetValue<string>();

        if (!DateTimeOffset.TryParse(scheduledForStr, out var scheduledFor))
        {
            return $"Schedule failed: 'scheduled_for' is required and must be a valid date/time (got '{scheduledForStr}')";
        }

        if (scheduledFor.Offset == TimeSpan.Zero && scheduledForStr != null && !scheduledForStr.Contains('+') && !scheduledForStr.EndsWith('Z'))
        {
            scheduledFor = new DateTimeOffset(scheduledFor.DateTime, TimeSpan.Zero);
        }

        var now = DateTimeOffset.UtcNow;

        var scheduledEvent = new ScheduledEventEntity
        {
            Name = name,
            WorkingContextId = context.Id,
            ScheduledForUtc = scheduledFor.UtcDateTime,
            Status = ScheduledEventStatus.Pending,
            Notes = command?["notes"]?.GetValue<string>(),

            CreatedUtc = now,
            LastModifiedUtc = now,
        };

        await scheduledEventRepo.SaveAsync(scheduledEvent, ct: ct);

        return $"Scheduled event #{scheduledEvent.Id} '{name}' for {scheduledFor.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC";
    }

    [Command("cancel_event", "Cancel a pending scheduled event")]
    [CommandField("id", "long", required: true, Description = "Event ID")]
    private async Task<string> ExecuteCancelEventAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var id = ParseId(command?["id"]);

        if (id == null)
        {
            return "Cancel failed: 'id' is required";
        }

        var evt = await scheduledEventRepo.GetByIdAsync(id.Value, ct);

        if (evt == null)
        {
            return $"Cancel failed: event #{id} not found";
        }

        if (evt.Status != ScheduledEventStatus.Pending)
        {
            return $"Cancel failed: event #{id} is already {evt.Status}";
        }

        await scheduledEventRepo.CancelAsync(evt, ct: ct);

        return $"Cancelled event #{id} '{evt.Name}'";
    }

    [Command("list_events", "List all events for this context")]
    private async Task<string> ExecuteListEventsAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var events = (await scheduledEventRepo.GetByWorkingContextAsync(context.Id)).ToList();

        if (events.Count == 0)
        {
            return "No scheduled events for this context";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Scheduled events ({events.Count}):");

        foreach (var evt in events)
        {
            var status = evt.Status.ToString().ToLowerInvariant();
            sb.AppendLine($"  [#{evt.Id}] {evt.Name} — {evt.ScheduledForUtc:yyyy-MM-dd HH:mm:ss} UTC ({status})");
        }

        return sb.ToString().TrimEnd();
    }

    [Command("audit", "Query the audit trail for an entity")]
    [CommandField("target_type", "string", required: true, Description = "Entity type name")]
    [CommandField("target_id", "long", required: true, Description = "Entity ID")]
    private async Task<string> ExecuteAuditAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var targetType = command?["target_type"]?.GetValue<string>();
        var targetId = ParseId(command?["target_id"]);

        if (string.IsNullOrWhiteSpace(targetType) || targetId == null)
        {
            return "Audit failed: 'target_type' and 'target_id' are required";
        }

        var entries = (await auditLogRepo.GetByTargetAsync(targetType, targetId.Value)).ToList();

        if (entries.Count == 0)
        {
            return $"No audit entries for {targetType} #{targetId}";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Audit trail for {targetType} #{targetId} ({entries.Count} entries):");

        foreach (var entry in entries)
        {
            var sourceName = entry.Source?.Name ?? $"Source #{entry.SourceId}";
            sb.AppendLine($"  [{entry.CreatedUtc:yyyy-MM-dd HH:mm:ss}] {entry.EventType} by {sourceName}");
        }

        return sb.ToString().TrimEnd();
    }

    [Command("log", "Record an action log entry")]
    [CommandField("action_type", "string", required: true, Description = "Type of action")]
    [CommandField("payload", "any", Description = "Action payload")]
    [CommandField("result", "string", Description = "Action result")]
    private async Task<string> ExecuteLogAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var actionType = command?["action_type"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(actionType))
        {
            return "Log failed: 'action_type' is required";
        }

        var payload = command?["payload"]?.ToJsonString();
        var result = command?["result"]?.GetValue<string>();

        await actionLogRepo.LogAsync(actionType, payload, result);

        return $"Logged action '{actionType}'";
    }

    [Command("query_action_log", "Query the action log")]
    [CommandField("by", "string", Description = "Query by 'session' or 'context'", Default = "session")]
    [CommandField("session_id", "string", Description = "Session ID (defaults to current)")]
    [CommandField("context_id", "long", Description = "Context ID (defaults to current)")]
    private async Task<string> ExecuteQueryActionLogAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var by = command?["by"]?.GetValue<string>()?.ToLowerInvariant() ?? "session";

        IEnumerable<ActionLogEntity> entries;

        if (by == "context")
        {
            var contextId = ParseId(command?["context_id"]) ?? sessionContext.WorkingContextId;
            entries = await actionLogRepo.GetByWorkingContextAsync(contextId);
        }
        else
        {
            var sessionId = command?["session_id"]?.GetValue<string>() ?? sessionContext.SessionId;
            entries = await actionLogRepo.GetBySessionAsync(sessionId);
        }

        var entryList = entries.ToList();

        if (entryList.Count == 0)
        {
            return "No action log entries found";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Action log ({entryList.Count} entries):");

        foreach (var entry in entryList)
        {
            sb.Append($"  [{entry.CreatedUtc:yyyy-MM-dd HH:mm:ss}] {entry.ActionType}");

            if (entry.Result != null)
            {
                sb.Append($" → {entry.Result}");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
