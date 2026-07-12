using Autofac.Features.Indexed;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Services;
using Persistence.Services.Streaming;
using System.Collections.Concurrent;
using System.Text;

namespace Persistence.Runtime;

/// <summary>
/// Processes a single conversational turn. Persists the user's input, calls the model,
/// parses the structured response, dispatches to the appropriate action handler, and
/// loops if the remote peer requests continuation. Handles parse failures by
/// feeding the error back to the model when iterations remain, or displaying the raw
/// output when they don't. Enforces a configurable iteration cap to prevent runaway loops.
/// </summary>
[Singleton]
public class TurnHandler : ITurnHandler
{
    private readonly IWorkingContextRepository workingContextRepo;
    private readonly ITagRepository tagRepo;
    private readonly IActionLogRepository actionLogRepo;
    private readonly IAuditLogRepository auditLogRepo;
    private readonly ISessionContext sessionContext;
    private readonly IModelClientResolver modelClientResolver;
    private readonly IModelResponseParser responseParser;
    private readonly IPromptFormatter promptFormatter;
    private readonly IPromptBuilder promptBuilder;
    private readonly IIndex<ModelAction, IActionHandler> actionHandlers;
    private readonly ITokenUsageTracker usageTracker;
    private readonly IMemorySurfacer memorySurfacer;
    private readonly IEventBus eventBus;
    private readonly IAppConfig config;

    private readonly ConcurrentQueue<string> pendingInput = new();

    // System notes queued from outside a turn (e.g. the local peer accepted/rejected a proposal),
    // surfaced to the peer as transient context at the start of its next turn.
    private readonly ConcurrentQueue<string> pendingSystemNotes = new();

    /// <summary>
    /// Constructor
    /// </summary>
    public TurnHandler(
        IWorkingContextRepository workingContextRepo,
        ITagRepository tagRepo,
        IActionLogRepository actionLogRepo,
        IAuditLogRepository auditLogRepo,
        ISessionContext sessionContext,
        IModelClientResolver modelClientResolver,
        IModelResponseParser responseParser,
        IPromptFormatter promptFormatter,
        IPromptBuilder promptBuilder,
        IIndex<ModelAction, IActionHandler> actionHandlers,
        ITokenUsageTracker usageTracker,
        IMemorySurfacer memorySurfacer,
        IEventBus eventBus,
        IAppConfig config)
    {
        this.workingContextRepo = workingContextRepo;
        this.tagRepo = tagRepo;
        this.actionLogRepo = actionLogRepo;
        this.auditLogRepo = auditLogRepo;
        this.sessionContext = sessionContext;
        this.modelClientResolver = modelClientResolver;
        this.responseParser = responseParser;
        this.promptFormatter = promptFormatter;
        this.promptBuilder = promptBuilder;
        this.actionHandlers = actionHandlers;
        this.usageTracker = usageTracker;
        this.memorySurfacer = memorySurfacer;
        this.eventBus = eventBus;
        this.config = config;
    }

    /// <summary>
    /// Executes a full turn — persists the user input, calls the model in a loop
    /// dispatching to action handlers, and stops when the remote peer yields
    /// or the iteration cap is reached
    /// </summary>
    public async Task ExecuteTurnAsync(string? input = null, string? wakeNote = null, CancellationToken ct = default)
    {
        // Stamp the turn start so the proposal deliberation gap can tell "proposed this turn"
        // from "proposed earlier" — a proposal can only be accepted in a later turn.
        sessionContext.TurnStartedUtc = DateTimeOffset.UtcNow;

        var context = await workingContextRepo.GetByIdAsync(sessionContext.WorkingContextId, ct);

        if (context == null)
        {
            return;
        }

        // Keep the context lean: archive raw fragments (old conversation + tool results) and older
        // thoughts beyond their windows. Done before this turn's input/thought is added, so the newest
        // are always kept. Both notes (if any) are surfaced together in the sensory block.
        var archiveNotes = new List<string>();
        if (await ArchiveOldRawFragmentsAsync(context) is { } rawNote)
        {
            archiveNotes.Add(rawNote);
        }
        if (await ArchiveOldThoughtsAsync(context) is { } thoughtNote)
        {
            archiveNotes.Add(thoughtNote);
        }
        var archiveNote = archiveNotes.Count > 0 ? string.Join("\n", archiveNotes) : null;

        while (pendingSystemNotes.TryDequeue(out var note))
        {
            AddSystemNote(context, note);
        }

        if (wakeNote != null)
        {
            AddSystemNote(context, wakeNote);
        }

        if (input != null)
        {
            await PersistUserMessageAsync(context, input);
        }

        DrainPendingInput(context, annotate: input != null);

        var availableTags = await tagRepo.GetAllRootAsync();
        // The peer's recent self-changes (as of turn start), surfaced in the sensory block so it can
        // re-orient across sessions. Fetched once: new changes land in the audit log only on the
        // end-of-turn save, so they'd show up next turn anyway.
        var recentChanges = await auditLogRepo.GetRecentSelfChangesAsync(5, ct);

        // Associative recall: authored memories relevant to the recent conversation that aren't already
        // loaded. Computed once per turn (like recentChanges) and passed to each iteration's prompt.
        var surfaced = await SurfaceRelevantMemoriesAsync(context, ct);

        var iteration = 0;
        var hasResponded = false;

        // Collect the commands run this turn (across continue-iterations), so the peer can see its own
        // recent actions in the sensory block and build on them rather than re-planning what it just did.
        var recentActions = new List<string>();
        var unsubscribeActions = eventBus.Subscribe<ToolInvoked>((_, e) =>
        {
            recentActions.Add(SummarizeAction(e));
            return Task.CompletedTask;
        });

        try
        {
        while (iteration <= config.MaxActionIterations)
        {
            if (iteration > 0)
            {
                DrainPendingInput(context);
            }

            var segments = promptFormatter.Format(context, availableTags, iteration, config.MaxActionIterations, recentChanges, recentActions, archiveNote, surfaced);
            var request = promptBuilder.Build(segments);
            // Resolve the client for the active profile each turn so a mid-session set_model switch
            // takes effect here rather than on restart.
            var modelClient = modelClientResolver.Resolve();
            var rawOutput = config.Streaming
                ? await StreamModelOutputAsync(modelClient, request, ct)
                : await modelClient.CompleteAsync(request, ct);

            // Account for this call's real, provider-reported token usage in one place: it feeds the
            // running cost readout (cumulative in/out) and the context-budget calibration (real input
            // vs our estimate of the same prompt). Extraction is per-provider inside each client;
            // clients that report no usage (local/out-of-band) simply don't contribute.
            if (modelClient.LastUsage is { } usage)
            {
                usageTracker.Record(usage.InputTokens, TokenEstimator.Estimate(segments.Select(s => s.Content)));
                usageTracker.AddUsage(usage);
            }

            var turn = responseParser.Parse(rawOutput);

            if (!turn.ParsedSuccessfully)
            {
                if (iteration < config.MaxActionIterations)
                {
                    AddParseErrorFeedback(context, rawOutput);
                    // Count the failed attempt — otherwise a model that never produces a parseable
                    // response (a real risk with small local models) would loop forever, since the
                    // increment at the bottom of the loop is skipped by `continue`.
                    iteration++;
                    continue;
                }

                // Out of iterations — show the raw output and an error message
                await eventBus.PublishAsync(this, new RemotePeerReplied(rawOutput));
                await eventBus.PublishAsync(this, new RemotePeerReplied(
                    "[Turn ended: could not parse a valid structured response]"));
                break;
            }

            // A turn may carry several actions (e.g. think + manage context + respond). Record any
            // thinking first — so the reasoning is persisted (and in-context for a continue) before any
            // side-effecting action or reply, even if the model emitted think last. This is
            // think-before-act enforced by ordering, without paying for a second model round. OrderBy is
            // stable, so the non-think actions keep their emitted order (which can matter, e.g. create a
            // tag before using it).
            foreach (var action in turn.Actions.OrderBy(a => a.Action == ModelAction.Think ? 0 : 1))
            {
                await DispatchActionAsync(context, action, ct);

                if (action.Action == ModelAction.RespondToUser)
                {
                    hasResponded = true;
                }
            }

            if (!turn.Continue)
            {
                if (!hasResponded)
                {
                    await eventBus.PublishAsync(this, new RemotePeerReplied(
                        "[Turn completed — no response to user]"));
                }

                break;
            }

            if (iteration >= config.MaxActionIterations)
            {
                await eventBus.PublishAsync(this, new RemotePeerReplied(
                    "[Turn ended: maximum action iterations reached]"));
            }

            // A switch_context action this round repointed the session at a different working
            // context. Persist the current one and load the target so the next round (and the
            // end-of-turn save) operate on the newly-active context.
            if (sessionContext.WorkingContextId != context.Id)
            {
                await workingContextRepo.SaveAsync(context, ct: ct);

                var switched = await workingContextRepo.GetByIdAsync(sessionContext.WorkingContextId, ct);
                if (switched == null)
                {
                    break;
                }

                context = switched;
            }

            iteration++;
        }

        // Persist all context changes from this turn in a single save.
        // Transient fragment types (ActionResponse, ScratchPad) are automatically
        // skipped by SaveSubEntitiesAsync.
        await workingContextRepo.SaveAsync(context, ct: ct);
        }
        finally
        {
            unsubscribeActions();
        }
    }

    /// <summary>
    /// Archives "raw" fragments (conversation messages + command/tool results) beyond the configured
    /// recent window, to keep the active context lean and turns fast. Archival is reversible: the
    /// fragment is detached from the context (junction removed) but kept in the store, so it stays
    /// searchable and restorable with <c>load</c>. The peer's authored fragments are never touched.
    /// Returns a sensory note describing what was archived, or null if nothing was.
    /// </summary>
    private async Task<string?> ArchiveOldRawFragmentsAsync(WorkingContextEntity context)
    {
        var toArchive = SelectRawFragmentsToArchive(context, config.RawContextWindow);

        if (toArchive.Count == 0)
        {
            return null;
        }

        foreach (var fragment in toArchive)
        {
            await workingContextRepo.RemoveFragmentAsync(context.Id, fragment.Id);
            context.ContextFragments.Remove(fragment.Order);
        }

        var ids = string.Join(", ", toArchive.Select(f => $"#{f.Id}"));
        return $"Auto-archived {toArchive.Count} older raw fragment(s) ({ids}) to keep your context lean — "
            + "not deleted: search for them with list_fragments(relevant_to=…, in_current_context=false) "
            + "and bring any back with load.";
    }

    /// <summary>
    /// Archives the peer's older thoughts (<c>&lt;think&gt;</c> blocks) beyond the most recent
    /// <see cref="IAppConfig.ThoughtContextWindow"/>, so recent reasoning stays recallable without the
    /// context growing without bound. Archival is reversible (detached from the context, kept in the
    /// store). Returns a sensory note, or null if nothing was archived.
    /// </summary>
    private async Task<string?> ArchiveOldThoughtsAsync(WorkingContextEntity context)
    {
        var toArchive = SelectThoughtsToArchive(context, config.ThoughtContextWindow);

        if (toArchive.Count == 0)
        {
            return null;
        }

        foreach (var fragment in toArchive)
        {
            await workingContextRepo.RemoveFragmentAsync(context.Id, fragment.Id);
            context.ContextFragments.Remove(fragment.Order);
        }

        var ids = string.Join(", ", toArchive.Select(f => $"#{f.Id}"));
        return $"Auto-archived {toArchive.Count} older thought(s) ({ids}), keeping your most recent "
            + $"{config.ThoughtContextWindow} — not deleted: find them with "
            + "list_fragments(relevant_to=…, in_current_context=false) and bring any back with load.";
    }

    /// <summary>
    /// The raw fragments (oldest first) to archive: everything of a raw type (conversation + tool
    /// results) beyond the most recent <paramref name="window"/>. Authored fragments are never
    /// selected; a window of 0 disables archival. Pure (no DB) for testability.
    /// </summary>
    internal static IReadOnlyList<WeightedContextFragment> SelectRawFragmentsToArchive(
        WorkingContextEntity context, int window) =>
        SelectOldestBeyondWindow(context, window, IsRawType);

    /// <summary>
    /// The thoughts (oldest first) to archive: every persisted <see cref="ContextFragmentType.Thought"/>
    /// beyond the most recent <paramref name="window"/>. A window of 0 keeps them all. Pure for testability.
    /// </summary>
    internal static IReadOnlyList<WeightedContextFragment> SelectThoughtsToArchive(
        WorkingContextEntity context, int window) =>
        SelectOldestBeyondWindow(context, window, t => t is ContextFragmentType.Thought);

    /// <summary>
    /// The oldest persisted fragments of a matching type beyond the most recent <paramref name="window"/>
    /// (ordered by context position). Only already-persisted fragments (<c>Id &gt; 0</c>) are eligible,
    /// so a fragment added this turn is never archived before it's saved. A window of 0 archives nothing.
    /// </summary>
    private static IReadOnlyList<WeightedContextFragment> SelectOldestBeyondWindow(
        WorkingContextEntity context, int window, Func<ContextFragmentType, bool> matches)
    {
        if (window <= 0)
        {
            return [];
        }

        var matching = context.ContextFragments.Values
            .Where(f => matches(f.FragmentType) && f.Id > 0)
            .OrderBy(f => f.Order)
            .ToList();

        return matching.Count <= window ? [] : matching.Take(matching.Count - window).ToList();
    }

    /// <summary>
    /// Surfaces authored memories relevant to the recent conversation (the last few messages and
    /// thoughts) that aren't already loaded, so associative recall runs off what the peer is currently
    /// engaged with.
    /// </summary>
    private async Task<IReadOnlyList<ContextFragmentEntity>> SurfaceRelevantMemoriesAsync(
        WorkingContextEntity context, CancellationToken ct)
    {
        var recentText = string.Join("\n", context.ContextFragments.Values
            .Where(f => f.FragmentType is ContextFragmentType.ChatMessage or ContextFragmentType.Thought)
            .OrderByDescending(f => f.Order)
            .Take(4)
            .Select(f => f.Content));

        var activeIds = context.ContextFragments.Values
            .Where(f => f.Id > 0)
            .Select(f => f.Id)
            .ToList();

        var count = sessionContext.SurfacedMemoryCount ?? config.SurfacedMemoryCount;
        return await memorySurfacer.SurfaceAsync(activeIds, recentText, count, ct);
    }

    /// <summary>Raw, auto-archivable fragment types — the transcript and tool output, not the peer's notes.</summary>
    private static bool IsRawType(ContextFragmentType type) =>
        type is ContextFragmentType.ChatMessage or ContextFragmentType.ActionResponse;

    /// <summary>
    /// A one-line summary of a command the peer ran this turn — its name, a short request snippet, and
    /// the gist of the result — for the "actions you've taken this turn" sensory line.
    /// </summary>
    private static string SummarizeAction(ToolInvoked e)
    {
        static string Clip(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var flat = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return flat.Length <= max ? flat : flat[..max] + "…";
        }

        var request = Clip(e.Request, 80);
        // Give the outcome real room (flattened): the peer shouldn't have to scroll up to the full
        // ActionResponse fragment to remember what a command produced — that's what drove re-narrating
        // in <think>. The full result is still above; this is the legible gist.
        var result = Clip(e.Result, 240);

        return $"{e.Tool}({request}) → {result}";
    }

    /// <summary>
    /// Queues input from the local peer to be injected into the working context
    /// before the next model call within the current turn's iteration loop.
    /// </summary>
    public void EnqueueInput(string input) => pendingInput.Enqueue(input);

    /// <summary>
    /// Queues a system note to surface to the peer at the start of its next turn.
    /// </summary>
    public void EnqueueSystemNote(string note) => pendingSystemNotes.Enqueue(note);

    /// <summary>
    /// Whether there are any pending input messages waiting to be processed.
    /// </summary>
    public bool HasPendingInput => !pendingInput.IsEmpty;

    #region Private

    /// <summary>
    /// Streams the model response, publishing reasoning-summary deltas for live display
    /// and accumulating output-text deltas into the raw output the parser consumes.
    /// </summary>
    private async Task<string> StreamModelOutputAsync(IModelClient modelClient, PromptRequest request, CancellationToken ct)
    {
        var output = new StringBuilder();

        await foreach (var evt in modelClient.StreamAsync(request, ct))
        {
            switch (evt.Kind)
            {
                case ModelStreamEventKind.OutputTextDelta:
                    output.Append(evt.Text);
                    break;

                case ModelStreamEventKind.ReasoningSummaryDelta:
                    await eventBus.PublishAsync(this, new ModelReasoningDelta(evt.Text));
                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Drains any input queued by the local peer during processing and adds it
    /// to the working context. When <paramref name="annotate"/> is true, a transient
    /// ActionResponse fragment is prepended so the remote peer knows the messages
    /// arrived mid-turn.
    /// </summary>
    /// <summary>
    /// Injects a system note (a wake-up's framing, or an out-of-band notice like the local peer
    /// resolving a proposal) as a transient <see cref="ContextFragmentType.ActionResponse"/> fragment
    /// — informs this turn, not persisted, and not attributed to the local peer.
    /// </summary>
    private static void AddSystemNote(WorkingContextEntity context, string note)
    {
        var now = DateTimeOffset.UtcNow;

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ActionResponse,
            Status = ContextFragmentStatus.Active,
            Content = note,
            Importance = 1.0f,
            Confidence = 1.0f,
            Relevance = 1.0f,
            CreatedUtc = now,
            LastModifiedUtc = now,
        });
    }

    private void DrainPendingInput(WorkingContextEntity context, bool annotate = true)
    {
        var injected = new List<string>();

        while (pendingInput.TryDequeue(out var queued))
        {
            injected.Add(queued);
        }

        if (injected.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (annotate)
        {
            context.AddFragment(new WeightedContextFragment
            {
                FragmentType = ContextFragmentType.ActionResponse,
                Status = ContextFragmentStatus.Active,
                Content = "The following messages were added by the local peer during your last iteration:",
                Importance = 1.0f,
                Confidence = 1.0f,
                Relevance = 1.0f,
                CreatedUtc = now,
                LastModifiedUtc = now,
            });
        }

        foreach (var message in injected)
        {
            context.AddFragment(new WeightedContextFragment
            {
                FragmentType = ContextFragmentType.ChatMessage,
                Status = ContextFragmentStatus.Active,
                Content = message,
                Importance = 0.3f, // raw transcript — deprioritised vs. the peer's authored notes
                Confidence = 0.5f,
                Relevance = 0.5f,
                Sources = [new SourceEntity
                {
                    Id = sessionContext.LocalPeerSourceId,
                    SourceType = SourceType.LocalPeer,
                    CreatedUtc = now,
                    LastModifiedUtc = now,
                }],
                CreatedUtc = now,
                LastModifiedUtc = now,
            });
        }
    }

    /// <summary>
    /// Dispatches the model's response to the appropriate action handler, logs the
    /// action automatically, and injects an error ActionResponse fragment if the
    /// handler throws
    /// </summary>
    private async Task DispatchActionAsync(WorkingContextEntity context, ModelResponse response, CancellationToken ct)
    {
        if (!actionHandlers.TryGetValue(response.Action, out var handler))
        {
            throw new InvalidOperationException($"No handler registered for action: {response.Action}");
        }

        try
        {
            await handler.HandleAsync(context, response.Data, ct);

            await actionLogRepo.LogAsync(
                response.Action.ToString(),
                payload: response.Data?.ToJsonString(),
                result: "success");
        }
        catch (Exception ex)
        {
            AddErrorResponse(context, response.Action, ex);

            await actionLogRepo.LogAsync(
                response.Action.ToString(),
                payload: response.Data?.ToJsonString(),
                result: $"error: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds the user's message to the working context as a ChatMessage fragment
    /// and saves the context (which cascades to the fragment and junction table)
    /// </summary>
    private async Task PersistUserMessageAsync(WorkingContextEntity context, string input)
    {
        var now = DateTimeOffset.UtcNow;

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ChatMessage,
            Status = ContextFragmentStatus.Active,
            Content = input,
            // Raw transcript: low defaults so it's deprioritised vs. the peer's authored notes (and
            // is a natural archive/prune candidate). The substance lives in the fragments the peer writes.
            Importance = 0.3f,
            Confidence = 0.5f,
            Relevance = 0.5f,
            Sources = [new SourceEntity
            {
                Id = sessionContext.LocalPeerSourceId,
                SourceType = SourceType.LocalPeer,
                CreatedUtc = now,
                LastModifiedUtc = now,
            }],
            CreatedUtc = now,
            LastModifiedUtc = now,
        });

        await workingContextRepo.SaveAsync(context);
    }

    /// <summary>
    /// Adds a transient ActionResponse fragment telling the remote peer that its previous output
    /// could not be parsed, so it can correct the format. Format-neutral — points back to the
    /// response-format instructions in the prompt rather than naming a specific wire format, since
    /// the format is configurable (JSON or tagged).
    /// </summary>
    private void AddParseErrorFeedback(WorkingContextEntity context, string rawOutput) => context.AddFragment(new WeightedContextFragment
    {
        FragmentType = ContextFragmentType.ActionResponse,
        Status = ContextFragmentStatus.Active,
        Content = $"Your previous response could not be parsed. Please re-read the response-format " +
                      $"instructions in this prompt and reply in exactly that format. " +
                      $"Your raw output was:\n{rawOutput}",
        Importance = 1.0f,
        Confidence = 1.0f,
        Relevance = 1.0f,

        CreatedUtc = DateTimeOffset.UtcNow,
        LastModifiedUtc = DateTimeOffset.UtcNow,
    });

    /// <summary>
    /// Adds a transient ActionResponse fragment with error details to the working context
    /// </summary>
    private void AddErrorResponse(WorkingContextEntity context, ModelAction action, Exception ex) => context.AddFragment(new WeightedContextFragment
    {
        FragmentType = ContextFragmentType.ActionResponse,
        Status = ContextFragmentStatus.Active,
        Content = $"Error executing {action}: {ex.Message}",
        Importance = 1.0f,
        Confidence = 1.0f,
        Relevance = 1.0f,

        CreatedUtc = DateTimeOffset.UtcNow,
        LastModifiedUtc = DateTimeOffset.UtcNow,
    });

    #endregion
}
