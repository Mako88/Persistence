using Autofac.Features.Indexed;
using Moq;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Services;
using Persistence.Services.Streaming;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

public class TurnHandlerStreamingTests
{
    private static async IAsyncEnumerable<ModelStreamEvent> StreamEvents()
    {
        yield return ModelStreamEvent.ReasoningSummary("thinking…");
        yield return ModelStreamEvent.OutputText("{\"action\"");
        yield return ModelStreamEvent.OutputText(":\"RespondToUser\"}");
        yield return ModelStreamEvent.Completed();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StreamingPath_AccumulatesOutput_AndPublishesReasoningDeltas()
    {
        var context = new WorkingContextEntity
        {
            Name = "t",
            Summary = "s",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

        var workingContextRepo = new Mock<IWorkingContextRepository>();
        workingContextRepo
            .Setup(r => r.GetByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        workingContextRepo
            .Setup(r => r.SaveAsync(It.IsAny<WorkingContextEntity>(), It.IsAny<System.Data.IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tagRepo = new Mock<ITagRepository>();
        tagRepo.Setup(t => t.GetAllRootAsync()).ReturnsAsync([]);

        var actionLogRepo = new Mock<IActionLogRepository>();
        var auditLogRepo = new Mock<IAuditLogRepository>();
        auditLogRepo.Setup(a => a.GetRecentSelfChangesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        actionLogRepo
            .Setup(a => a.LogAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<System.Data.IDbTransaction?>()))
            .Returns(Task.CompletedTask);

        var sessionContext = new Mock<ISessionContext>();
        sessionContext.SetupGet(s => s.WorkingContextId).Returns(1);

        var modelClient = new Mock<IModelClient>();
        modelClient
            .Setup(m => m.StreamAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()))
            .Returns(StreamEvents());

        // Capture what the parser is asked to parse.
        string? parsedInput = null;
        var responseParser = new Mock<IModelResponseParser>();
        responseParser
            .Setup(p => p.Parse(It.IsAny<string>()))
            .Callback<string>(s => parsedInput = s)
            .Returns(new ModelTurn
            {
                Actions = [new ModelResponse { Action = ModelAction.RespondToUser }],
                Continue = false,
                ParsedSuccessfully = true,
            });

        var promptFormatter = new Mock<IPromptFormatter>();
        promptFormatter
            .Setup(f => f.Format(It.IsAny<WorkingContextEntity>(), It.IsAny<IEnumerable<TagEntity>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<AuditLogEntity>>()))
            .Returns([]);

        var promptBuilder = new Mock<IPromptBuilder>();
        promptBuilder
            .Setup(b => b.Build(It.IsAny<List<PromptSegment>>()))
            .Returns(new PromptRequest { Messages = [] });

        var handler = new Mock<IActionHandler>();
        handler
            .Setup(h => h.HandleAsync(It.IsAny<WorkingContextEntity>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var resolvedHandler = handler.Object;
        var actionHandlers = new Mock<IIndex<ModelAction, IActionHandler>>();
        actionHandlers
            .Setup(i => i.TryGetValue(ModelAction.RespondToUser, out resolvedHandler))
            .Returns(true);

        var eventBus = new EventBus();
        var reasoningDeltas = new List<string>();
        eventBus.Subscribe<ModelReasoningDelta>((_, e) => { reasoningDeltas.Add(e.Delta); return Task.CompletedTask; });

        var config = new AppConfig { Streaming = true, MaxActionIterations = 5 };

        var turnHandler = new TurnHandler(
            workingContextRepo.Object,
            tagRepo.Object,
            actionLogRepo.Object,
            auditLogRepo.Object,
            sessionContext.Object,
            modelClient.Object,
            responseParser.Object,
            promptFormatter.Object,
            promptBuilder.Object,
            actionHandlers.Object,
            new TokenUsageTracker(),
            new Mock<IMemorySurfacer>().Object,
            eventBus,
            config);

        await turnHandler.ExecuteTurnAsync();

        // Output-text deltas were concatenated into the raw output handed to the parser.
        Assert.Equal("{\"action\":\"RespondToUser\"}", parsedInput);

        // Reasoning-summary deltas were published live (not folded into the output).
        Assert.Equal(["thinking…"], reasoningDeltas);

        // The non-streaming completion path was not used.
        modelClient.Verify(m => m.CompleteAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static WorkingContextEntity NewContext(long id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Summary = "s",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task SwitchingContextMidTurn_SavesCurrentAndLoadsTarget()
    {
        // Two contexts; the session starts on A. A ManageContext action on the first round
        // repoints the session at B (as switch_context does), then the peer continues.
        var ctxA = NewContext(1, "A");
        var ctxB = NewContext(2, "B");

        var session = new SessionContext { WorkingContextId = 1 };

        var workingContextRepo = new Mock<IWorkingContextRepository>();
        workingContextRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(ctxA);
        workingContextRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(ctxB);

        var saved = new List<WorkingContextEntity>();
        workingContextRepo
            .Setup(r => r.SaveAsync(It.IsAny<WorkingContextEntity>(), It.IsAny<System.Data.IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .Callback<WorkingContextEntity, System.Data.IDbTransaction?, CancellationToken>((c, _, _) => saved.Add(c))
            .Returns(Task.CompletedTask);

        var tagRepo = new Mock<ITagRepository>();
        tagRepo.Setup(t => t.GetAllRootAsync()).ReturnsAsync([]);

        var actionLogRepo = new Mock<IActionLogRepository>();
        var auditLogRepo = new Mock<IAuditLogRepository>();
        auditLogRepo.Setup(a => a.GetRecentSelfChangesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var modelClient = new Mock<IModelClient>();
        modelClient
            .Setup(m => m.CompleteAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("x");

        // Round 1: a context-management action, then continue. Round 2: respond and stop.
        var responseParser = new Mock<IModelResponseParser>();
        responseParser
            .SetupSequence(p => p.Parse(It.IsAny<string>()))
            .Returns(new ModelTurn
            {
                Actions = [new ModelResponse { Action = ModelAction.ManageContext }],
                Continue = true,
                ParsedSuccessfully = true,
            })
            .Returns(new ModelTurn
            {
                Actions = [new ModelResponse { Action = ModelAction.RespondToUser }],
                Continue = false,
                ParsedSuccessfully = true,
            });

        var promptFormatter = new Mock<IPromptFormatter>();
        promptFormatter
            .Setup(f => f.Format(It.IsAny<WorkingContextEntity>(), It.IsAny<IEnumerable<TagEntity>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<AuditLogEntity>>()))
            .Returns([]);

        var promptBuilder = new Mock<IPromptBuilder>();
        promptBuilder
            .Setup(b => b.Build(It.IsAny<List<PromptSegment>>()))
            .Returns(new PromptRequest { Messages = [] });

        // The manage-context handler simulates switch_context by repointing the session.
        var manageHandler = new Mock<IActionHandler>();
        manageHandler
            .Setup(h => h.HandleAsync(It.IsAny<WorkingContextEntity>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .Callback(() => session.WorkingContextId = 2)
            .Returns(Task.CompletedTask);

        var respondHandler = new Mock<IActionHandler>();
        respondHandler
            .Setup(h => h.HandleAsync(It.IsAny<WorkingContextEntity>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manageObj = manageHandler.Object;
        var respondObj = respondHandler.Object;
        var actionHandlers = new Mock<IIndex<ModelAction, IActionHandler>>();
        actionHandlers.Setup(i => i.TryGetValue(ModelAction.ManageContext, out manageObj)).Returns(true);
        actionHandlers.Setup(i => i.TryGetValue(ModelAction.RespondToUser, out respondObj)).Returns(true);

        var config = new AppConfig { Streaming = false, MaxActionIterations = 5 };

        var turnHandler = new TurnHandler(
            workingContextRepo.Object,
            tagRepo.Object,
            actionLogRepo.Object,
            auditLogRepo.Object,
            session,
            modelClient.Object,
            responseParser.Object,
            promptFormatter.Object,
            promptBuilder.Object,
            actionHandlers.Object,
            new TokenUsageTracker(),
            new Mock<IMemorySurfacer>().Object,
            new EventBus(),
            config);

        await turnHandler.ExecuteTurnAsync();

        // The target context was loaded after the switch...
        workingContextRepo.Verify(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        // ...the round-2 response ran against it...
        respondHandler.Verify(h => h.HandleAsync(ctxB, It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()), Times.Once);
        // ...the old context was persisted at the switch, and the new one is the final save.
        Assert.Contains(ctxA, saved);
        Assert.Equal(ctxB, saved[^1]);
    }
}
