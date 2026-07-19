using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Services;
using Persistence.Services.Container;
using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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
    private readonly IContainerExecutor containerExecutor;
    private readonly IAppConfig config;

    /// <summary>
    /// Constructor
    /// </summary>
    public ExecuteActionsHandler(
        IScheduledEventRepository scheduledEventRepo,
        IAuditLogRepository auditLogRepo,
        IActionLogRepository actionLogRepo,
        ISessionContext sessionContext,
        IContainerExecutor containerExecutor,
        IAppConfig config,
        IEventBus eventBus) : base(eventBus)
    {
        this.scheduledEventRepo = scheduledEventRepo;
        this.auditLogRepo = auditLogRepo;
        this.actionLogRepo = actionLogRepo;
        this.sessionContext = sessionContext;
        this.containerExecutor = containerExecutor;
        this.config = config;
    }

    #region Commands

    [Command("exec", "Run a command in your computer — a sandboxed container with web tools (web_search, fetch_url, agent-browser), scripting (python, bash, node), and file/navigation utilities. Your working directory persists between calls; files in your working area survive across sessions. Only allowlisted programs run; send exec(command=\"ls\") to look around. (Same as shell.)")]
    [CommandField("command", "string", required: true, Description = "The command line to run, e.g. web_search \"rust async\", or cd notes && ls, or python script.py")]
    private Task<string> ExecuteExecAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct) =>
        RunInComputerAsync("exec", command?["command"]?.GetValue<string>(), ct);

    [Command("shell", "Run a command in your computer — a sandboxed container with web tools (web_search, fetch_url, agent-browser), scripting (python, bash, node), and file/navigation utilities. Your working directory persists between calls; files in your working area survive across sessions. Only allowlisted programs run; send shell(command=\"ls\") to look around. (Same as exec.)")]
    [CommandField("command", "string", required: true, Description = "The command line to run, e.g. web_search \"rust async\", or cd notes && ls, or python script.py")]
    private Task<string> ExecuteShellAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct) =>
        RunInComputerAsync("shell", command?["command"]?.GetValue<string>(), ct);

    /// <summary>
    /// Shared body for <c>exec</c>/<c>shell</c>: guards that the computer is enabled, runs the command
    /// line through the allowlisted container executor, audits it, and formats the outcome so success
    /// and every failure mode (rejected, non-zero exit, timeout, truncation) reads unambiguously.
    /// </summary>
    private async Task<string> RunInComputerAsync(string label, string? commandLine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return $"{label} failed: 'command' is required";
        }

        if (!config.Container.Enabled)
        {
            return ComputerUnavailable;
        }

        var result = await containerExecutor.ExecuteAsync(commandLine, ct);

        // Audit every exec so it's queryable later via query_action_log, independent of context.
        await actionLogRepo.LogAsync(label, commandLine,
            result.Allowed ? Summarize(result.Output) : $"rejected: {result.RejectionReason}");

        if (!result.Allowed)
        {
            return result.RejectionReason!;
        }

        // Always make the outcome legible — success OR failure — so no command is ambiguous.
        string output;
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            output = result.Output;
        }
        else if (result.ExitCode == 0 && !result.TimedOut)
        {
            output = "(command completed, no output)";
        }
        else
        {
            output = "(no output)";
        }

        // Surface failures so the peer is never left guessing — a non-zero exit with no output would
        // otherwise read as a silent success.
        if (result.ExitCode != 0 && !result.TimedOut)
        {
            output += $"\n[exited with code {result.ExitCode}]";
        }

        if (result.TimedOut)
        {
            output += "\n[timed out — the command ran longer than the allowed time and was stopped]";
        }

        if (result.Truncated)
        {
            output += "\n[output truncated]";
        }

        return output;
    }

    [Command("read_file", "Read a file from your computer, a window at a time so it never floods your context. Reads 'limit' lines starting at line 'offset' (0-based); relative paths resolve against your current working directory. The header tells you the range and total lines so you can page with a larger offset.")]
    [CommandField("path", "string", required: true, Description = "File to read, e.g. notes/plan.md or /work/data.json")]
    [CommandField("offset", "int", Description = "0-based line to start at", Default = "0")]
    [CommandField("limit", "int", Description = "How many lines to read", Default = "200")]
    private async Task<string> ExecuteReadFileAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var path = command?["path"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(path))
        {
            return "read_file failed: 'path' is required";
        }

        if (!config.Container.Enabled)
        {
            return ComputerUnavailable;
        }

        var offset = Math.Max(0, command?["offset"]?.GetValue<int>() ?? 0);
        var limit = command?["limit"]?.GetValue<int>() ?? 200;

        if (limit <= 0)
        {
            return "read_file failed: 'limit' must be at least 1";
        }

        var result = await containerExecutor.ReadFileAsync(path, offset, limit, ct);

        await actionLogRepo.LogAsync("read_file", $"{path} [offset {offset}, limit {limit}]",
            result.Found ? $"{result.FirstLine}-{result.LastLine}/{result.TotalLines}" : result.Error);

        if (result.TimedOut)
        {
            return $"read_file '{path}' timed out.";
        }

        if (!result.Found)
        {
            return $"read_file failed: {result.Error}";
        }

        if (offset >= result.TotalLines)
        {
            return $"[{path} — {result.TotalLines} lines total; offset {offset} is past the end]";
        }

        var more = result.LastLine < result.TotalLines
            ? $"; more below — read with offset={result.LastLine}"
            : "";
        var header = $"[{path} — lines {result.FirstLine}-{result.LastLine} of {result.TotalLines}{more}]";
        var body = result.Content.Length == 0 ? "(empty)" : result.Content;
        var truncatedNote = result.Truncated ? "\n[output truncated — narrow the window with a smaller limit]" : "";

        return $"{header}\n{body}{truncatedNote}";
    }

    [Command("write_file", "Write text to a file in your computer (creating parent folders). Overwrites by default, or set append=true to add to the end — handy for building a file up incrementally without resending it. Relative paths resolve against your current working directory.")]
    [CommandField("path", "string", required: true, Description = "File to write, e.g. notes/plan.md or /work/out.txt")]
    [CommandField("content", "string", required: true, Description = "The text to write")]
    [CommandField("append", "bool", Description = "Append to the end instead of overwriting", Default = "false")]
    private async Task<string> ExecuteWriteFileAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var path = command?["path"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(path))
        {
            return "write_file failed: 'path' is required";
        }

        // Accept an empty string (e.g. to truncate/create a file) but require the field to be present.
        if (command?["content"] is not JsonNode contentNode)
        {
            return "write_file failed: 'content' is required";
        }

        if (!config.Container.Enabled)
        {
            return ComputerUnavailable;
        }

        var content = contentNode.GetValue<string>();
        var append = command?["append"]?.GetValue<bool>() ?? false;

        var result = await containerExecutor.WriteFileAsync(path, content, append, ct);

        var verb = append ? "Appended to" : "Wrote";
        await actionLogRepo.LogAsync("write_file", $"{path} [{(append ? "append" : "overwrite")}]",
            result.ExitCode == 0 && !result.TimedOut ? Summarize(result.Output) : "failed");

        if (result.TimedOut)
        {
            return $"write_file '{path}' timed out.";
        }

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Output) ? "" : $"\n{result.Output}";
            return $"write_file failed (exit code {result.ExitCode}).{detail}";
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        return $"{verb} {path} ({bytes} bytes).";
    }

    private const string ComputerUnavailable =
        "Your computer isn't available yet (the container is disabled). Your peer needs to start it and enable it in config.";

    private static string Summarize(string output)
    {
        var firstLine = output.Split('\n', 2)[0];
        return firstLine.Length <= 200 ? firstLine : firstLine[..200];
    }

    [Command("snapshot_db", "Write a consistent read-only snapshot of your database into your workspace so you can query your own data directly (e.g. with python3's sqlite3). Overwrites the previous snapshot each time; it's a copy, not the live file.")]
    private async Task<string> ExecuteSnapshotDbAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        // Where the peer will be able to *read* the snapshot from depends on where it lives.
        //
        // Local mode (ADR-0007): the peer's runtime runs inside its own container, so its shell and its
        // database share a filesystem — the snapshot goes into its workspace and it just opens the file.
        // The old sidecar model had the peer reaching into a separate container, so the only common
        // ground was the /shared host bridge; that folder does NOT exist inside a peer's own container,
        // which is why this used to refuse outright for every containerised peer.
        var local = config.Container.Local;
        var directory = local ? config.Container.WorkingDir : config.SharedDirectory;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return local
                ? "snapshot_db isn't available — no workspace directory is configured for your computer."
                : "snapshot_db isn't available — no shared folder is configured (the /shared bridge to your computer isn't set up).";
        }

        var sourcePath = config.DatabasePath;

        if (!File.Exists(sourcePath))
        {
            return $"snapshot_db failed: your database wasn't found at {sourcePath}.";
        }

        // Distinguish the copy from the live database, which in local mode is a real file the peer can
        // also see — without the suffix, "glm.db" in the workspace and "glm.db" in /data/db are easy to
        // confuse, and only one of them is safe to poke at.
        var fileName = local
            ? $"{Path.GetFileNameWithoutExtension(sourcePath)}-snapshot{Path.GetExtension(sourcePath)}"
            : Path.GetFileName(sourcePath);
        var destPath = Path.Combine(directory, fileName);

        try
        {
            Directory.CreateDirectory(directory);

            // Read-only source connection (no write lock on the live DB) → a consistent online-backup copy.
            await using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
            await using var dest = new SqliteConnection($"Data Source={destPath}");
            await source.OpenAsync(ct);
            await dest.OpenAsync(ct);
            source.BackupDatabase(dest);
        }
        catch (Exception ex)
        {
            return $"snapshot_db failed: {ex.Message}";
        }

        var kb = new FileInfo(destPath).Length / 1024;
        await actionLogRepo.LogAsync("snapshot_db", fileName, $"{kb} KB");

        // Report the path the peer's own shell will see.
        var shownPath = local ? destPath.Replace('\\', '/') : $"/shared/{fileName}";

        return $"Wrote a snapshot of your database to {shownPath} ({kb} KB). Query it read-only "
            + $"in your computer with python3's sqlite3 (open \"{shownPath}\").";
    }

    [Command("container_logs", "View recent logs from your computer's containers, to troubleshoot it yourself. 'computer' is your box; 'search' is the search service (useful when web_search misbehaves).")]
    [CommandField("service", "string", Description = "Which container: 'computer' (default) or 'search'", Default = "computer")]
    [CommandField("lines", "int", Description = "How many recent log lines to show", Default = "50")]
    private async Task<string> ExecuteContainerLogsAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        if (!config.Container.Enabled)
        {
            return "Your computer isn't available yet (the container is disabled). Your peer needs to start it and enable it in config.";
        }

        var service = command?["service"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "computer";
        var lines = command?["lines"]?.GetValue<int>() ?? 50;

        var containerName = service switch
        {
            "computer" => config.Container.Name,
            "search" => config.Container.SearchContainerName,
            _ => null,
        };

        if (containerName is null)
        {
            return $"Unknown service '{service}'. Available: computer, search.";
        }

        var logs = await containerExecutor.GetLogsAsync(containerName, lines, ct);
        await actionLogRepo.LogAsync("container_logs", service, Summarize(logs));

        return $"[{service} logs — last {lines} lines]\n{logs}";
    }

    [Command("schedule", "Schedule a future event that will wake you for an autonomous turn at the given time")]
    [CommandField("name", "string", required: true, Description = "Event name")]
    [CommandField("scheduled_for", "string", required: true, Description = "UTC datetime in ISO 8601 format, e.g. \"2026-12-01T09:00:00Z\"")]
    [CommandField("wake_prompt", "string", Description = "A note to yourself, surfaced to you when this wakes you (e.g. \"reconsider whether I still value X\")")]
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
            WakePrompt = command?["wake_prompt"]?.GetValue<string>(),

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

    [Command("audit", "Query the change history of one of your fragments (or another entity)")]
    [CommandField("target_id", "long", required: true, Description = "The #ID of the fragment (or entity) to inspect")]
    [CommandField("target_type", "string", Description = "What kind of thing target_id refers to: \"fragment\" (default), \"tag\", \"source\", or \"event\"")]
    private async Task<string> ExecuteAuditAsync(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
    {
        var targetId = ParseId(command?["target_id"]);

        if (targetId == null)
        {
            return "Audit failed: 'target_id' is required";
        }

        // Accept friendly names ("fragment", "tag", …) and map to the stored entity type name,
        // so the peer never has to know internal class names. Defaults to fragment.
        var friendly = command?["target_type"]?.GetValue<string>();
        var targetType = ResolveAuditTargetType(friendly);

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

    #endregion

    #region Helpers

    /// <summary>
    /// Maps a peer-friendly target name ("fragment", "tag", "source", "event") to the stored
    /// audit entity-type name. Defaults to fragment — the common case — and passes through an
    /// already-exact entity type name unchanged.
    /// </summary>
    private static string ResolveAuditTargetType(string? friendly) => friendly?.Trim().ToLowerInvariant() switch
    {
        null or "" or "fragment" => nameof(ContextFragmentEntity),
        "tag" => nameof(TagEntity),
        "source" => nameof(SourceEntity),
        "event" or "scheduledevent" => nameof(ScheduledEventEntity),
        _ => friendly!, // assume the caller already passed an exact entity type name
    };

    #endregion
}
