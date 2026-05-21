using System.Text;
using System.Text.Json.Nodes;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Runtime.ActionHandlers;

/// <summary>
/// Handles <see cref="ModelAction.ExecuteActions"/> by performing side-effect operations
/// (scheduling events, querying logs, recording actions). Like ManageContext, the
/// <c>data</c> payload contains a batch of commands. Results are collected into a single
/// <see cref="ContextFragmentType.ActionResponse"/> fragment.
/// </summary>
[Service(registerAsType: typeof(IActionHandler), key: ModelAction.ExecuteActions)]
public class ExecuteActionsHandler : IActionHandler
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
        ISessionContext sessionContext)
    {
        this.scheduledEventRepo = scheduledEventRepo;
        this.auditLogRepo = auditLogRepo;
        this.actionLogRepo = actionLogRepo;
        this.sessionContext = sessionContext;
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
            results.AppendLine(result);
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
    /// Extracts the commands array from the data payload. Accepts a top-level array,
    /// an object with a "commands" property, or a single command object.
    /// </summary>
    private static List<JsonNode> ParseCommands(JsonNode? data)
    {
        if (data is JsonArray topLevelArray)
        {
            return topLevelArray.Where(n => n != null).Select(n => n!).ToList();
        }

        var commandsNode = data?["commands"];

        if (commandsNode is JsonArray commandsArray)
        {
            return commandsArray.Where(n => n != null).Select(n => n!).ToList();
        }

        if (data != null)
        {
            return [data];
        }

        return [];
    }

    /// <summary>
    /// Routes a single command to the appropriate handler based on its "command" property
    /// </summary>
    private async Task<string> ExecuteCommandAsync(
        WorkingContextEntity context, JsonNode command, CancellationToken ct)
    {
        var commandType = command["command"]?.GetValue<string>()?.ToLowerInvariant();

        try
        {
            return commandType switch
            {
                "schedule" => await ExecuteScheduleAsync(context, command, ct),
                "cancel_event" => await ExecuteCancelEventAsync(command, ct),
                "list_events" => await ExecuteListEventsAsync(context, ct),
                "audit" => await ExecuteAuditAsync(command, ct),
                "log" => await ExecuteLogAsync(command),
                "query_action_log" => await ExecuteQueryActionLogAsync(command),
                _ => $"Unknown command: {commandType ?? "(null)"}",
            };
        }
        catch (Exception ex)
        {
            return $"Error executing '{commandType}': {ex.Message}";
        }
    }

    /// <summary>
    /// Creates a new scheduled event for the current working context
    /// </summary>
    private async Task<string> ExecuteScheduleAsync(
        WorkingContextEntity context, JsonNode command, CancellationToken ct)
    {
        var name = command["name"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Schedule failed: 'name' is required";
        }

        var scheduledForStr = command["scheduledFor"]?.GetValue<string>();

        if (!DateTimeOffset.TryParse(scheduledForStr, out var scheduledFor))
        {
            return $"Schedule failed: 'scheduledFor' is required and must be a valid date/time (got '{scheduledForStr}')";
        }

        // Interpret as UTC if no offset was provided
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
            Notes = command["notes"]?.GetValue<string>(),

            CreatedUtc = now,
            LastModifiedUtc = now,
        };

        await scheduledEventRepo.SaveAsync(scheduledEvent, ct: ct);

        return $"Scheduled event #{scheduledEvent.Id} '{name}' for {scheduledFor.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC";
    }

    /// <summary>
    /// Cancels a pending scheduled event by ID
    /// </summary>
    private async Task<string> ExecuteCancelEventAsync(JsonNode command, CancellationToken ct)
    {
        var id = command["id"]?.GetValue<long>();

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

    /// <summary>
    /// Lists all non-deleted events for the current working context
    /// </summary>
    private async Task<string> ExecuteListEventsAsync(WorkingContextEntity context, CancellationToken ct)
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

    /// <summary>
    /// Queries the audit trail for a specific entity type and ID and formats
    /// the results as text for the model to review
    /// </summary>
    private async Task<string> ExecuteAuditAsync(JsonNode command, CancellationToken ct)
    {
        var targetType = command["targetType"]?.GetValue<string>();
        var targetId = command["targetId"]?.GetValue<long>();

        if (string.IsNullOrWhiteSpace(targetType) || targetId == null)
        {
            return "Audit failed: 'targetType' and 'targetId' are required";
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

    /// <summary>
    /// Records an action log entry for the current session and working context
    /// </summary>
    private async Task<string> ExecuteLogAsync(JsonNode command)
    {
        var actionType = command["actionType"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(actionType))
        {
            return "Log failed: 'actionType' is required";
        }

        var payload = command["payload"]?.GetValue<string>();
        var result = command["result"]?.GetValue<string>();

        await actionLogRepo.LogAsync(actionType, payload, result);

        return $"Logged action '{actionType}'";
    }

    /// <summary>
    /// Queries the action log. Supports querying by session (defaults to current) or
    /// by working context (defaults to current). Returns entries as formatted text.
    /// </summary>
    private async Task<string> ExecuteQueryActionLogAsync(JsonNode command)
    {
        var by = command["by"]?.GetValue<string>()?.ToLowerInvariant() ?? "session";

        IEnumerable<ActionLogEntity> entries;

        if (by == "context")
        {
            var contextId = command["contextId"]?.GetValue<long>() ?? sessionContext.WorkingContextId;
            entries = await actionLogRepo.GetByWorkingContextAsync(contextId);
        }
        else
        {
            var sessionId = command["sessionId"]?.GetValue<string>() ?? sessionContext.SessionId;
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
