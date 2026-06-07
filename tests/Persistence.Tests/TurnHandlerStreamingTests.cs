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
            .Setup(f => f.Format(It.IsAny<WorkingContextEntity>(), It.IsAny<IEnumerable<TagEntity>>(), It.IsAny<int>(), It.IsAny<int>()))
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
            sessionContext.Object,
            modelClient.Object,
            responseParser.Object,
            promptFormatter.Object,
            promptBuilder.Object,
            actionHandlers.Object,
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
}
