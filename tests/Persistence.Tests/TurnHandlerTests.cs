using Autofac.Features.Indexed;
using Moq;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Services;
using System.Data;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

/// <summary>
/// Unit tests for <see cref="TurnHandler"/>'s loop behaviour (non-streaming path, parse-error retry,
/// no-response notice, user-input persistence, handler-exception handling) with all collaborators
/// mocked. The streaming path and mid-turn context switch are covered in
/// <see cref="TurnHandlerStreamingTests"/>.
/// </summary>
public class TurnHandlerTests
{
    private sealed class Harness
    {
        public readonly Mock<IWorkingContextRepository> ContextRepo = new();
        public readonly Mock<ITagRepository> TagRepo = new();
        public readonly Mock<IActionLogRepository> ActionLog = new();
        public readonly Mock<IAuditLogRepository> AuditLog = new();
        public readonly SessionContext Session = new() { WorkingContextId = 1, LocalPeerSourceId = 5, RemotePeerSourceId = 6 };
        public readonly Mock<IModelClient> Model = new();
        public readonly Mock<IModelResponseParser> Parser = new();
        public readonly Mock<IPromptFormatter> Formatter = new();
        public readonly Mock<IPromptBuilder> Builder = new();
        public readonly Mock<IIndex<ModelAction, IActionHandler>> Handlers = new();
        public readonly EventBus Bus = new();
        public readonly AppConfig Config = new() { Streaming = false, MaxActionIterations = 5 };
        public readonly WorkingContextEntity Context;
        public readonly List<string> Replies = [];

        public Harness()
        {
            var now = DateTimeOffset.UtcNow;
            Context = new WorkingContextEntity { Id = 1, Name = "c", Summary = "s", CreatedUtc = now, LastModifiedUtc = now };

            ContextRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Context);
            ContextRepo.Setup(r => r.SaveAsync(It.IsAny<WorkingContextEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            TagRepo.Setup(t => t.GetAllRootAsync()).ReturnsAsync([]);
            ActionLog.Setup(a => a.LogAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IDbTransaction?>())).Returns(Task.CompletedTask);
            AuditLog.Setup(a => a.GetRecentSelfChangesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Model.Setup(m => m.CompleteAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync("raw");
            Formatter.Setup(f => f.Format(It.IsAny<WorkingContextEntity>(), It.IsAny<IEnumerable<TagEntity>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<AuditLogEntity>>())).Returns([]);
            Builder.Setup(b => b.Build(It.IsAny<List<PromptSegment>>())).Returns(new PromptRequest { Messages = [] });
            Bus.Subscribe<RemotePeerReplied>((_, e) => { Replies.Add(e.Reply); return Task.CompletedTask; });
        }

        public void RegisterHandler(ModelAction action, Action? onHandle = null)
        {
            var handler = new Mock<IActionHandler>();
            handler.Setup(h => h.HandleAsync(It.IsAny<WorkingContextEntity>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
                .Callback(() => onHandle?.Invoke())
                .Returns(Task.CompletedTask);
            var obj = handler.Object;
            Handlers.Setup(i => i.TryGetValue(action, out obj)).Returns(true);
        }

        public void ParseReturns(params ModelTurn[] turns)
        {
            var seq = Parser.SetupSequence(p => p.Parse(It.IsAny<string>()));
            foreach (var turn in turns)
            {
                seq = seq.Returns(turn);
            }
        }

        public static ModelTurn Turn(ModelAction action, bool cont = false) =>
            new() { Actions = [new ModelResponse { Action = action }], Continue = cont, ParsedSuccessfully = true };

        public static ModelTurn Unparsed() => new() { Actions = [], Continue = false, ParsedSuccessfully = false };

        public TurnHandler Build() => new(
            ContextRepo.Object, TagRepo.Object, ActionLog.Object, AuditLog.Object, Session, TestResolvers.For(Model.Object), Parser.Object,
            Formatter.Object, Builder.Object, Handlers.Object, new TokenUsageTracker(),
            new Mock<IMemorySurfacer>().Object, Bus, Config);
    }

    [Fact]
    public async Task NonStreamingPathDispatchesActionAndSavesContext()
    {
        var h = new Harness();
        var dispatched = false;
        h.RegisterHandler(ModelAction.RespondToUser, onHandle: () => dispatched = true);
        h.ParseReturns(Harness.Turn(ModelAction.RespondToUser));

        await h.Build().ExecuteTurnAsync();

        Assert.True(dispatched, "the registered handler's HandleAsync should have run");
        h.ActionLog.Verify(a => a.LogAsync("RespondToUser", It.IsAny<string?>(), "success", It.IsAny<IDbTransaction?>()), Times.Once);
        h.Model.Verify(m => m.CompleteAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Model.Verify(m => m.StreamAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        h.ContextRepo.Verify(r => r.SaveAsync(h.Context, It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnparseableResponseIsRetriedWithFeedback()
    {
        var h = new Harness();
        h.RegisterHandler(ModelAction.RespondToUser);
        h.ParseReturns(Harness.Unparsed(), Harness.Turn(ModelAction.RespondToUser));

        await h.Build().ExecuteTurnAsync();

        // First parse failed → fed back and retried; second succeeded.
        h.Model.Verify(m => m.CompleteAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        // ...and the feedback was actually injected into the context (carrying the unparseable raw
        // output back to the model), not just a bare retry. The harness's model returns "raw".
        Assert.Contains(h.Context.ContextFragments.Values, f =>
            f.FragmentType == ContextFragmentType.ActionResponse
            && f.Content.Contains("could not be parsed")
            && f.Content.Contains("raw"));
    }

    [Fact]
    public async Task RepeatedParseFailureTerminatesAtTheIterationCap()
    {
        // Regression: an always-unparseable model response must stop after MaxActionIterations
        // retries rather than loop forever. (This test would hang before the iteration-count fix.)
        var h = new Harness();
        h.Config.MaxActionIterations = 3;
        h.RegisterHandler(ModelAction.RespondToUser);
        h.Parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(Harness.Unparsed());

        await h.Build().ExecuteTurnAsync();

        // One call per iteration 0..Max inclusive, then it gives up.
        h.Model.Verify(m => m.CompleteAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
        Assert.Contains(h.Replies, r => r.Contains("could not parse"));
    }

    [Fact]
    public async Task TurnWithoutAResponsePublishesANotice()
    {
        var h = new Harness();
        h.RegisterHandler(ModelAction.ManageContext);
        h.ParseReturns(Harness.Turn(ModelAction.ManageContext));

        await h.Build().ExecuteTurnAsync();

        Assert.Contains(h.Replies, r => r.Contains("no response to user"));
    }

    [Fact]
    public async Task UserInputIsPersistedAsAChatMessage()
    {
        var h = new Harness();
        h.RegisterHandler(ModelAction.RespondToUser);
        h.ParseReturns(Harness.Turn(ModelAction.RespondToUser));

        await h.Build().ExecuteTurnAsync("hello there");

        Assert.Contains(h.Context.ContextFragments.Values,
            f => f.FragmentType == ContextFragmentType.ChatMessage && f.Content == "hello there");
    }

    [Fact]
    public async Task AWakeNoteIsInjectedAsATransientFragmentForAnAutonomousTurn()
    {
        var h = new Harness();
        h.RegisterHandler(ModelAction.RespondToUser);
        h.ParseReturns(Harness.Turn(ModelAction.RespondToUser));

        await h.Build().ExecuteTurnAsync(wakeNote: "Your scheduled event fired.");

        // The wake framing is present as a transient fragment, with no persisted ChatMessage (no
        // local-peer message was sent).
        Assert.Contains(h.Context.ContextFragments.Values,
            f => f.FragmentType == ContextFragmentType.ActionResponse && f.Content == "Your scheduled event fired.");
        Assert.DoesNotContain(h.Context.ContextFragments.Values, f => f.FragmentType == ContextFragmentType.ChatMessage);
    }

    [Fact]
    public async Task AQueuedSystemNoteSurfacesInTheNextTurn()
    {
        var h = new Harness();
        h.RegisterHandler(ModelAction.RespondToUser);
        h.ParseReturns(Harness.Turn(ModelAction.RespondToUser));
        var handler = h.Build();

        handler.EnqueueSystemNote("Your peer accepted your proposal #5.");
        await handler.ExecuteTurnAsync();

        Assert.Contains(h.Context.ContextFragments.Values,
            f => f.FragmentType == ContextFragmentType.ActionResponse && f.Content == "Your peer accepted your proposal #5.");
    }

    [Fact]
    public async Task AHandlerExceptionIsCaughtAndLoggedAsError()
    {
        var h = new Harness();
        h.RegisterHandler(ModelAction.RespondToUser, onHandle: () => throw new InvalidOperationException("boom"));
        h.ParseReturns(Harness.Turn(ModelAction.RespondToUser));

        await h.Build().ExecuteTurnAsync();

        h.ActionLog.Verify(
            a => a.LogAsync("RespondToUser", It.IsAny<string?>(), It.Is<string?>(s => s != null && s.StartsWith("error:")), It.IsAny<IDbTransaction?>()),
            Times.Once);
    }
}
