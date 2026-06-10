using Moq;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Runtime.ActionHandlers;
using Persistence.Services.Container;
using System.Data;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

/// <summary>
/// Unit tests for <see cref="ExecuteActionsHandler"/> — validation, branching, formatting, and the
/// friendly audit-target mapping — with the repositories mocked (their SQL is covered by the
/// repository integration tests).
/// </summary>
public class ExecuteActionsHandlerTests
{
    private readonly Mock<IScheduledEventRepository> scheduledEventRepo = new();
    private readonly Mock<IAuditLogRepository> auditLogRepo = new();
    private readonly Mock<IActionLogRepository> actionLogRepo = new();
    private readonly Mock<IContainerExecutor> containerExecutor = new();
    private readonly AppConfig config = new();
    private readonly SessionContext session = new() { SessionId = "S1", WorkingContextId = 7 };
    private readonly List<ToolInvoked> published = [];
    private readonly ExecuteActionsHandler handler;

    public ExecuteActionsHandlerTests()
    {
        // Default happy-path setups so awaited Tasks aren't null; tests override as needed.
        scheduledEventRepo
            .Setup(r => r.SaveAsync(It.IsAny<ScheduledEventEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        scheduledEventRepo
            .Setup(r => r.CancelAsync(It.IsAny<ScheduledEventEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        scheduledEventRepo.Setup(r => r.GetByWorkingContextAsync(It.IsAny<long>())).ReturnsAsync([]);
        auditLogRepo.Setup(r => r.GetByTargetAsync(It.IsAny<string>(), It.IsAny<long>())).ReturnsAsync([]);
        actionLogRepo
            .Setup(r => r.LogAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IDbTransaction?>()))
            .Returns(Task.CompletedTask);
        actionLogRepo.Setup(r => r.GetBySessionAsync(It.IsAny<string>())).ReturnsAsync([]);
        actionLogRepo.Setup(r => r.GetByWorkingContextAsync(It.IsAny<long>())).ReturnsAsync([]);

        var bus = new EventBus();
        bus.Subscribe<ToolInvoked>((_, e) => { published.Add(e); return Task.CompletedTask; });
        handler = new ExecuteActionsHandler(
            scheduledEventRepo.Object, auditLogRepo.Object, actionLogRepo.Object, session,
            containerExecutor.Object, config, bus);
    }

    private static WorkingContextEntity Context() =>
        new() { Id = 7, Name = "c", Summary = "s", CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow };

    private async Task<string> RunAsync(string json)
    {
        published.Clear();
        await handler.HandleAsync(Context(), JsonNode.Parse(json));
        return published.Single().Result;
    }

    [Fact]
    public async Task ShellRequiresACommand()
    {
        config.Container.Enabled = true;
        var result = await RunAsync("""{ "shell": { } }""");
        Assert.Contains("'command' is required", result);
    }

    [Fact]
    public async Task ShellShortCircuitsWhenContainerDisabled()
    {
        config.Container.Enabled = false; // default

        var result = await RunAsync("""{ "shell": { "command": "ls" } }""");

        Assert.Contains("isn't available", result);
        // Never touches the executor when disabled.
        containerExecutor.Verify(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShellRunsCommandAndReturnsOutputAndAudits()
    {
        config.Container.Enabled = true;
        containerExecutor
            .Setup(e => e.ExecuteAsync("ls", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerExecResult(Allowed: true, RejectionReason: null,
                Output: "notes.txt\nresearch/", TimedOut: false, Truncated: false, ExitCode: 0));

        var result = await RunAsync("""{ "shell": { "command": "ls" } }""");

        Assert.Contains("notes.txt", result);
        // Exec is audited to the action log (queryable later, independent of context).
        actionLogRepo.Verify(r => r.LogAsync("shell", "ls", It.IsAny<string?>(), It.IsAny<IDbTransaction?>()), Times.Once);
    }

    [Fact]
    public async Task ShellReturnsRejectionWhenCommandNotAllowed()
    {
        config.Container.Enabled = true;
        containerExecutor
            .Setup(e => e.ExecuteAsync("gcc evil.c", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerExecResult(Allowed: false,
                RejectionReason: "'gcc' is not permitted in your computer. Allowed: ls, python.",
                Output: "", TimedOut: false, Truncated: false, ExitCode: 0));

        var result = await RunAsync("""{ "shell": { "command": "gcc evil.c" } }""");

        Assert.Contains("'gcc' is not permitted", result);
        // A rejected command is still audited, marked as rejected.
        actionLogRepo.Verify(r => r.LogAsync("shell", "gcc evil.c",
            It.Is<string?>(s => s != null && s.Contains("rejected")), It.IsAny<IDbTransaction?>()), Times.Once);
    }

    [Fact]
    public async Task ShellFlagsTimeoutAndTruncation()
    {
        config.Container.Enabled = true;
        containerExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerExecResult(Allowed: true, RejectionReason: null,
                Output: "partial", TimedOut: true, Truncated: true, ExitCode: -1));

        var result = await RunAsync("""{ "shell": { "command": "python slow.py" } }""");

        Assert.Contains("timed out", result);
        Assert.Contains("truncated", result);
    }

    [Fact]
    public async Task ShellSurfacesNonZeroExitWithEmptyOutput()
    {
        config.Container.Enabled = true;
        containerExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerExecResult(Allowed: true, RejectionReason: null,
                Output: "", TimedOut: false, Truncated: false, ExitCode: 2));

        var result = await RunAsync("""{ "shell": { "command": "fetch_url https://x" } }""");

        // A failure with no output must not read as a silent success.
        Assert.Contains("exited with code 2", result);
    }

    [Fact]
    public async Task ScheduleRequiresName()
    {
        var result = await RunAsync("""{ "schedule": { "scheduled_for": "2026-12-01T09:00:00Z" } }""");
        Assert.Contains("'name' is required", result);
    }

    [Fact]
    public async Task ScheduleRejectsAnInvalidDate()
    {
        var result = await RunAsync("""{ "schedule": { "name": "standup", "scheduled_for": "not-a-date" } }""");
        Assert.Contains("must be a valid date/time", result);
    }

    [Fact]
    public async Task ScheduleSavesAndConfirms()
    {
        var result = await RunAsync("""{ "schedule": { "name": "standup", "scheduled_for": "2026-12-01T09:00:00Z" } }""");

        Assert.Contains("Scheduled event", result);
        Assert.Contains("standup", result);
        Assert.Contains("2026-12-01 09:00:00 UTC", result);
        scheduledEventRepo.Verify(
            r => r.SaveAsync(It.Is<ScheduledEventEntity>(e => e.Name == "standup" && e.WorkingContextId == 7),
                It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ScheduleStoresTheWakePrompt()
    {
        var result = await RunAsync("""{ "schedule": { "name": "reflect", "scheduled_for": "2026-12-01T09:00:00Z", "wake_prompt": "revisit my values" } }""");

        Assert.Contains("Scheduled event", result);
        scheduledEventRepo.Verify(
            r => r.SaveAsync(It.Is<ScheduledEventEntity>(e => e.WakePrompt == "revisit my values"),
                It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelReportsNotFound()
    {
        scheduledEventRepo.Setup(r => r.GetByIdAsync(9L, It.IsAny<CancellationToken>())).ReturnsAsync((ScheduledEventEntity?)null);

        var result = await RunAsync("""{ "cancel_event": { "id": 9 } }""");

        Assert.Contains("event #9 not found", result);
    }

    [Fact]
    public async Task CancelRejectsAnAlreadyResolvedEvent()
    {
        scheduledEventRepo.Setup(r => r.GetByIdAsync(3L, It.IsAny<CancellationToken>())).ReturnsAsync(Event(3, "old", ScheduledEventStatus.Triggered));

        var result = await RunAsync("""{ "cancel_event": { "id": 3 } }""");

        Assert.Contains("already Triggered", result);
        scheduledEventRepo.Verify(r => r.CancelAsync(It.IsAny<ScheduledEventEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelSucceedsForPendingEvent()
    {
        scheduledEventRepo.Setup(r => r.GetByIdAsync(3L, It.IsAny<CancellationToken>())).ReturnsAsync(Event(3, "demo", ScheduledEventStatus.Pending));

        var result = await RunAsync("""{ "cancel_event": { "id": 3 } }""");

        Assert.Contains("Cancelled event #3 'demo'", result);
        scheduledEventRepo.Verify(r => r.CancelAsync(It.IsAny<ScheduledEventEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListEventsReportsEmpty()
    {
        var result = await RunAsync("""{ "list_events": {} }""");
        Assert.Contains("No scheduled events", result);
    }

    [Fact]
    public async Task ListEventsFormatsEntries()
    {
        scheduledEventRepo.Setup(r => r.GetByWorkingContextAsync(7)).ReturnsAsync([Event(1, "a", ScheduledEventStatus.Pending), Event(2, "b", ScheduledEventStatus.Cancelled)]);

        var result = await RunAsync("""{ "list_events": {} }""");

        Assert.Contains("Scheduled events (2):", result);
        Assert.Contains("[#1] a", result);
        Assert.Contains("(cancelled)", result);
    }

    [Fact]
    public async Task AuditRequiresTargetId()
    {
        var result = await RunAsync("""{ "audit": {} }""");
        Assert.Contains("'target_id' is required", result);
    }

    [Fact]
    public async Task AuditMapsFriendlyTargetTypeAndFormats()
    {
        auditLogRepo.Setup(r => r.GetByTargetAsync("TagEntity", 5)).ReturnsAsync(
            [new AuditLogEntity { SessionId = "S1", EventType = AuditEventType.Created, TargetType = "TagEntity", TargetId = 5, SourceId = 2, CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow }]);

        var result = await RunAsync("""{ "audit": { "target_id": 5, "target_type": "tag" } }""");

        Assert.Contains("Audit trail for TagEntity #5", result);
        Assert.Contains("Created", result);
        auditLogRepo.Verify(r => r.GetByTargetAsync("TagEntity", 5), Times.Once);
    }

    [Fact]
    public async Task LogRequiresActionType()
    {
        var result = await RunAsync("""{ "log": {} }""");
        Assert.Contains("'action_type' is required", result);
    }

    [Fact]
    public async Task LogRecordsAndConfirms()
    {
        var result = await RunAsync("""{ "log": { "action_type": "noted", "result": "ok" } }""");

        Assert.Contains("Logged action 'noted'", result);
        actionLogRepo.Verify(r => r.LogAsync("noted", It.IsAny<string?>(), "ok", It.IsAny<IDbTransaction?>()), Times.Once);
    }

    [Fact]
    public async Task QueryActionLogByContextFormatsEntries()
    {
        actionLogRepo.Setup(r => r.GetByWorkingContextAsync(7)).ReturnsAsync(
            [new ActionLogEntity { SessionId = "S1", WorkingContextId = 7, ActionType = "did_thing", Result = "done", CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow }]);

        var result = await RunAsync("""{ "query_action_log": { "by": "context" } }""");

        Assert.Contains("Action log (1 entries):", result);
        Assert.Contains("did_thing → done", result);
    }

    [Fact]
    public async Task QueryActionLogBySessionReportsEmpty()
    {
        var result = await RunAsync("""{ "query_action_log": { "by": "session" } }""");
        Assert.Contains("No action log entries", result);
    }

    private static ScheduledEventEntity Event(long id, string name, ScheduledEventStatus status) =>
        new()
        {
            Id = id,
            Name = name,
            WorkingContextId = 7,
            ScheduledForUtc = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero),
            Status = status,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };
}
