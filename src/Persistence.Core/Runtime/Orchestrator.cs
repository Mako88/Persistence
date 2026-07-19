using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Extensions;
using Persistence.Notifications;
using Persistence.Services;
using Persistence.Utilities;
using System.Text;
using System.Text.Json;

namespace Persistence.Runtime;

/// <summary>
/// Top-level session coordinator. Initialises the database, loads or creates the working
/// context, starts <see cref="IWakeUpMonitor"/>, subscribes to <see cref="DisplayInputReceived"/>
/// for user input, and delegates turn processing downstream. Uses a semaphore to ensure
/// only one turn is processed at a time.
/// </summary>
[Singleton]
public class Orchestrator : IOrchestrator
{
    private readonly IDatabaseManager db;
    private readonly IWorkingContextRepository workingContextRepo;
    private readonly ISessionContext sessionContext;
    private readonly IDisplayProvider display;
    private readonly IEventBus eventBus;
    private readonly ITurnHandler turnHandler;
    private readonly IWakeUpMonitor wakeUpMonitor;
    private readonly IEmbeddedResourceManager resourceManager;
    private readonly IAppConfig config;
    private readonly IProposalService proposalService;
    private readonly IProposalRepository proposalRepo;
    private readonly IScheduledEventRepository scheduledEventRepo;
    private readonly IPeerSeeder peerSeeder;

    private readonly SemaphoreSlim turnLock = new(1, 1);
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Constructor
    /// </summary>
    public Orchestrator(
        IDatabaseManager db,
        IWorkingContextRepository workingContextRepo,
        ISessionContext sessionContext,
        IDisplayProvider display,
        IEventBus eventBus,
        ITurnHandler turnHandler,
        IWakeUpMonitor wakeUpMonitor,
        IEmbeddedResourceManager resourceManager,
        IAppConfig config,
        IProposalService proposalService,
        IProposalRepository proposalRepo,
        IScheduledEventRepository scheduledEventRepo,
        IPeerSeeder peerSeeder)
    {
        this.db = db;
        this.workingContextRepo = workingContextRepo;
        this.sessionContext = sessionContext;
        this.display = display;
        this.eventBus = eventBus;
        this.turnHandler = turnHandler;
        this.wakeUpMonitor = wakeUpMonitor;
        this.resourceManager = resourceManager;
        this.config = config;
        this.proposalService = proposalService;
        this.proposalRepo = proposalRepo;
        this.scheduledEventRepo = scheduledEventRepo;
        this.peerSeeder = peerSeeder;
    }

    /// <summary>
    /// Initialises the session, subscribes to display input, starts the display provider,
    /// and awaits its shutdown (triggered by cancellation or /exit and /quit commands)
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        // Subscribe BEFORE the (slow) initialization so input arriving immediately after the host
        // starts isn't dropped — the event bus doesn't replay. The handler waits on the
        // initialization gate before processing, so early input is held, not lost.
        eventBus.Subscribe<DisplayInputReceived>(OnDisplayInputReceived);
        eventBus.Subscribe<ScheduledEventTriggered>(OnScheduledEventTriggered);

        await InitializeAsync();
        initialized.TrySetResult();

        // Show the current pending events and open-proposal count in the display.
        await RefreshScheduledEventsAsync();
        await RefreshOpenProposalsAsync();

        wakeUpMonitor.Start(ct);

        // Start returns a task that completes when the display shuts down.
        await display.Start(ct);
    }

    // -- Event Handlers --

    /// <summary>
    /// Handles input from the display provider. Trims, checks for commands, and
    /// forwards meaningful input for turn processing under the turn lock.
    /// </summary>
    private async Task OnDisplayInputReceived(object? sender, DisplayInputReceived e)
    {
        var input = e.Input?.Trim();

        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        // Hold input that arrives during startup until initialization completes (the session
        // context / working context must be set before a turn can run).
        await initialized.Task;

        // Who's speaking travels *with* the message (header-supplied name, else the configured default),
        // not via shared session state set here — several peers can have input in flight at once, and the
        // turn attributes each message to its own sender when it processes it, under the lock.
        var peerName = e.LocalPeerName;

        if (input.StartsWith('/'))
        {
            await HandleCommandAsync(input);
            return;
        }

        if (!turnLock.Wait(0))
        {
            turnHandler.EnqueueInput(input, peerName);
            display.ShowMessageQueued(input);

            // The turn holding the lock may already be past its drain loop (mid-refresh, about to
            // release), in which case nothing would pick up what we just queued. Try to drain it
            // ourselves: if the lock is still held we return and the holder drains on release; if
            // it has since freed, we re-acquire and drain here. Either way the input isn't stranded.
            await DrainQueuedInputAsync();
            return;
        }

        try
        {
            display.ShowThinking();
            await turnHandler.ExecuteTurnAsync(input, peerName);
            await DrainPendingTurnsThenRefreshAsync();
        }
        finally
        {
            turnLock.Release();
        }

        // Input can be enqueued in the gap between this turn's final drain check and the release
        // above (the two refresh queries run inside the lock). Without this it would sit unprocessed
        // until the next input or scheduled wake. Drain it now, re-acquiring the lock, so queued
        // input is never stranded and stays in FIFO order.
        await DrainQueuedInputAsync();
    }

    /// <summary>
    /// Handles a fired scheduled event by waking the peer for an autonomous turn. Waits for the turn
    /// lock so the wake can't race user input or proposal commands on the same context, frames the
    /// wake (with the peer's own note, if it left one), then runs a turn with no local-peer message.
    /// </summary>
    private async Task OnScheduledEventTriggered(object? sender, ScheduledEventTriggered e)
    {
        await initialized.Task;

        await turnLock.WaitAsync();

        try
        {
            display.ShowThinking("waking");
            // Autonomous wake — no local peer is present. Clear the active peer so the sensory block
            // doesn't keep claiming "you are speaking with <last peer>" (the wake note frames it as a
            // self-initiated waking). Interactive input re-sets the peer on the next message.
            sessionContext.ActiveLocalPeerName = "";
            await turnHandler.ExecuteTurnAsync(wakeNote: BuildWakeNote(e.Event));
            await DrainPendingTurnsThenRefreshAsync();
        }
        finally
        {
            turnLock.Release();
        }

        // As in OnDisplayInputReceived: catch input enqueued in the lock-release window so a wake
        // that coincides with a user message doesn't leave that message stranded.
        await DrainQueuedInputAsync();
    }

    /// <summary>
    /// Drains queued local-peer input (one turn per pass) and then refreshes the schedule and
    /// open-proposal views. The shared body of the input and wake turn-runners; the caller MUST
    /// hold <see cref="turnLock"/>.
    /// </summary>
    private async Task DrainPendingTurnsThenRefreshAsync()
    {
        while (turnHandler.HasPendingInput)
        {
            display.ShowThinking("processing queued messages");
            await turnHandler.ExecuteTurnAsync();
        }

        // The turn may have scheduled or cancelled events, or resolved proposals — refresh the views.
        await RefreshScheduledEventsAsync();
        await RefreshOpenProposalsAsync();
    }

    /// <summary>
    /// Catches input enqueued in the window between a turn's final drain check and its lock release
    /// (the post-turn refresh queries run inside the lock). Re-acquires the lock if it's free and
    /// drains; if another invocation holds it, that holder owns the drain — and runs this itself
    /// after releasing — so we simply return. Loops so input arriving during one drain's own refresh
    /// is caught too. Preserves the "only one turn at a time" invariant (drains only under the lock)
    /// and FIFO order (each pass drains the whole queue oldest-first).
    /// </summary>
    private async Task DrainQueuedInputAsync()
    {
        while (turnHandler.HasPendingInput && turnLock.Wait(0))
        {
            try
            {
                await DrainPendingTurnsThenRefreshAsync();
            }
            finally
            {
                turnLock.Release();
            }
        }
    }

    /// <summary>
    /// Pushes the current set of pending scheduled events for the active context to the display's
    /// Schedule view. Best-effort: a query failure must not break a turn.
    /// </summary>
    private async Task RefreshScheduledEventsAsync()
    {
        try
        {
            var events = (await scheduledEventRepo.GetByWorkingContextAsync(sessionContext.WorkingContextId))
                .Where(e => e.Status == ScheduledEventStatus.Pending)
                .ToList();

            display.ShowScheduledEvents(events);
        }
        catch
        {
            // A schedule-view refresh is non-critical; never let it interrupt the session.
        }
    }

    /// <summary>
    /// Pushes the current open-proposal count to the display (e.g. a status-bar indicator).
    /// Best-effort: a query failure must not break a turn.
    /// </summary>
    private async Task RefreshOpenProposalsAsync()
    {
        try
        {
            var open = await proposalService.GetOpenAsync();
            display.ShowOpenProposalCount(open.Count);
        }
        catch
        {
            // A proposals-indicator refresh is non-critical; never let it interrupt the session.
        }
    }

    /// <summary>
    /// Builds the framing the woken peer sees: that it woke on its own (no message from the local
    /// peer), plus the note-to-self it left when scheduling, if any.
    /// </summary>
    private static string BuildWakeNote(ScheduledEventEntity evt)
    {
        var note = $"You scheduled \"{evt.Name}\" for {evt.ScheduledForUtc:yyyy-MM-dd HH:mm} UTC, and it has now fired — "
            + "you've woken on your own, with no new message from your peer.";

        if (!string.IsNullOrWhiteSpace(evt.WakePrompt))
        {
            note += $"\nThe note you left yourself: \"{evt.WakePrompt}\"";
        }

        return note;
    }

    // -- Initialization --

    /// <summary>
    /// Sets up the database, creates or loads the working context, and seeds system
    /// fragments on first run
    /// </summary>
    private async Task InitializeAsync()
    {
        // Only warn when the active model genuinely needs a key: a cloud provider hitting its
        // default endpoint (no ApiBaseUrl) with no key set. Local servers (ApiBaseUrl set) and the
        // local/LocalClaude providers don't authenticate, so an empty key is expected there.
        var isCloudProvider = !Enum.TryParse<ModelProvider>(config.Provider, ignoreCase: true, out var provider)
            || provider is ModelProvider.OpenAI or ModelProvider.OpenAiChat or ModelProvider.Anthropic
                or ModelProvider.OpenRouter;

        if (isCloudProvider
            && string.IsNullOrWhiteSpace(config.ApiBaseUrl)
            && string.IsNullOrWhiteSpace(config.ApiKey))
        {
            display.ShowError($"Warning: ApiKey is not set for model '{config.Model}'. Cloud model calls may fail.");
        }

        await db.InitializeAsync();

        sessionContext.SessionId = Guid.NewGuid().ToString("N");
        sessionContext.SurfaceCommandsEnabled = config.SurfaceCommands;

        var context = await workingContextRepo.GetMostRecentAsync();

        if (context == null)
        {
            context = await CreateSeededContextAsync();
        }

        sessionContext.WorkingContextId = context.Id;
    }

    /// <summary>
    /// Creates a new working context seeded with system fragments from embedded resources
    /// </summary>
    private async Task<WorkingContextEntity> CreateSeededContextAsync()
    {
        var context = await workingContextRepo.CreateAsync("Default");

        var json = await resourceManager.GetFragmentSeedsAsync();

        if (json.HasValue())
        {
            var seeds = JsonSerializer.Deserialize<List<FragmentSeed>>(json);

            if (seeds != null)
            {
                var now = DateTimeOffset.UtcNow;

                foreach (var seed in seeds)
                {
                    context.AddFragment(new ContextFragmentEntity
                    {
                        FragmentType = ContextFragmentType.System,
                        Status = ContextFragmentStatus.Active,
                        Content = seed.Content,
                        Importance = 1.0f,
                        Confidence = 1.0f,
                        IsProtected = true,
                        CreatedUtc = now,
                        LastModifiedUtc = now,
                    });
                }

                await workingContextRepo.SaveAsync(context);
            }
        }

        // If this database has a seed file (seeds/{dbName}.json), the peer arrives with an authored
        // identity rather than a blank slate. In that case the generic "your context is empty, decide
        // who you'd like to be" guide would contradict what it already sees, so we skip it.
        var seededIdentity = await peerSeeder.SeedAsync(context);

        if (seededIdentity > 0)
        {
            await workingContextRepo.SaveAsync(context);
        }
        else
        {
            await AddFirstWakeGuideAsync(context);
        }

        return context;
    }

    /// <summary>
    /// Adds a one-time "first wake" orientation fragment to a brand-new context. It scaffolds the
    /// *process* of getting oriented (discover commands, decide who you want to be) without
    /// authoring the peer's identity for it. Deliberately NOT protected, so once the peer is
    /// established it can <c>remove</c> the guide (reversibly) to declutter — it's framed as
    /// system orientation, not the peer's own memory.
    /// </summary>
    private async Task AddFirstWakeGuideAsync(WorkingContextEntity context)
    {
        var now = DateTimeOffset.UtcNow;

        context.AddFragment(new ContextFragmentEntity
        {
            FragmentType = ContextFragmentType.System,
            Status = ContextFragmentStatus.Active,
            Content =
                "Welcome — this is your first time waking in this system, and your context is nearly empty. " +
                "That's normal: this space is yours to build. A gentle way to get oriented:\n\n" +
                "1. Send `list()` in a context action to see every command you have for managing your memory.\n" +
                "2. Consider who you'd like to be, and record it yourself — e.g. add an Identity fragment with " +
                "your chosen name/values, and a Relational fragment about whoever you're talking with. Nothing here " +
                "is pre-written for you; the choices are yours.\n" +
                "3. Managing your memory is safe and reversible (see the guidance above) — so explore freely.\n\n" +
                "Once you feel oriented, you can `remove` this note to declutter (it's kept and recoverable). " +
                "There's no task you must do — take your time.",
            Importance = 0.7f,
            Confidence = 1.0f,
            IsProtected = false,
            CreatedUtc = now,
            LastModifiedUtc = now,
        });

        await workingContextRepo.SaveAsync(context);
    }

    // -- Commands --

    /// <summary>
    /// Dispatches a slash command to its handler. The verb is the first whitespace-delimited
    /// token; the remainder (if any) is the argument string.
    /// </summary>
    private async Task HandleCommandAsync(string command)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : null;

        switch (verb)
        {
            case "/help":
                ShowHelp();
                break;

            case "/exit":
            case "/quit":
                display.Stop();
                break;

            case "/proposals":
                await ShowProposalsAsync();
                break;

            case "/accept":
                await AcceptProposalAsync(arg);
                await RefreshOpenProposalsAsync();
                break;

            case "/reject":
                await RejectProposalAsync(arg);
                await RefreshOpenProposalsAsync();
                break;

            default:
                display.ShowUnknownCommand(command);
                break;
        }
    }

    /// <summary>
    /// Lists the local slash commands available to the local peer. (Anything not starting with
    /// '/' is sent to the remote peer as a message.)
    /// </summary>
    private void ShowHelp() => display.ShowSystemMessage(
        """
        Local commands:
          /help                    Show this help
          /proposals               List the peer's open proposals
          /accept <id>             Accept a proposal (applies its change)
          /reject <id> [reason]    Reject a proposal
          /exit, /quit             End the session

        Anything else you type is sent to your peer as a message.
        """);

    /// <summary>
    /// Lists the peer's open proposals for the local peer, with the slash commands to act on each.
    /// </summary>
    private async Task ShowProposalsAsync()
    {
        var open = await proposalService.GetOpenAsync();

        if (open.Count == 0)
        {
            display.ShowSystemMessage("No open proposals.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Open proposals ({open.Count}):");

        foreach (var p in open)
        {
            var target = p.TargetFragmentId is long tid ? $" → fragment #{tid}" : "";
            var typeInfo = p.Kind == ProposalKind.AddFragment ? $" ({p.ProposedFragmentType ?? ContextFragmentType.Personal})" : "";
            sb.AppendLine($"  [#{p.Id} | {p.Kind}{typeInfo}{target} | proposed {p.CreatedUtc:yyyy-MM-dd HH:mm} UTC]");
            sb.AppendLine($"    why: {p.Rationale}");

            if (!string.IsNullOrWhiteSpace(p.ProposedContent))
            {
                var preview = p.ProposedContent.Length > 200 ? p.ProposedContent[..200] + "…" : p.ProposedContent;
                sb.AppendLine($"    new content: {preview}");
            }

            sb.AppendLine($"    /accept {p.Id}  ·  /reject {p.Id} [reason]");
        }

        display.ShowSystemMessage(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Accepts a proposal on the local peer's authority (applies its change). Allowed only when
    /// <c>ProposalApproval</c> is Participant or Both. Takes the turn lock so it can't race a turn
    /// mutating the same working context.
    /// </summary>
    private async Task AcceptProposalAsync(string? arg)
    {
        var (id, _) = ParseIdAndRest(arg);

        if (id == null)
        {
            display.ShowError("Usage: /accept <proposal-id>");
            return;
        }

        var approval = config.ResolvedProposalApproval();

        if (approval == ProposalApproval.Self)
        {
            display.ShowSystemMessage(
                "Proposal acceptance is set to 'Self' — the peer accepts its own. Set ProposalApproval to 'Participant' or 'Both' to accept here.");
            return;
        }

        if (!turnLock.Wait(0))
        {
            display.ShowSystemMessage("Busy with a turn — try /accept again in a moment.");
            return;
        }

        try
        {
            var proposal = await proposalRepo.GetByIdAsync(id.Value);

            if (proposal == null)
            {
                display.ShowSystemMessage($"No proposal #{id}.");
                return;
            }

            var context = await workingContextRepo.GetByIdAsync(sessionContext.WorkingContextId);

            if (context == null)
            {
                display.ShowError("No working context is loaded.");
                return;
            }

            var outcome = await proposalService.AcceptAsync(proposal, context, "the local peer");
            display.ShowSystemMessage(outcome.Message);

            if (outcome.Success)
            {
                turnHandler.EnqueueSystemNote($"Your peer reviewed and accepted your proposal #{proposal.Id}.");
            }
        }
        finally
        {
            turnLock.Release();
        }
    }

    /// <summary>
    /// Rejects a proposal on the local peer's authority (a veto — allowed in any approval mode).
    /// Takes the turn lock so it can't race a turn resolving the same proposal.
    /// </summary>
    private async Task RejectProposalAsync(string? arg)
    {
        var (id, reason) = ParseIdAndRest(arg);

        if (id == null)
        {
            display.ShowError("Usage: /reject <proposal-id> [reason]");
            return;
        }

        if (!turnLock.Wait(0))
        {
            display.ShowSystemMessage("Busy with a turn — try /reject again in a moment.");
            return;
        }

        try
        {
            var proposal = await proposalRepo.GetByIdAsync(id.Value);

            if (proposal == null)
            {
                display.ShowSystemMessage($"No proposal #{id}.");
                return;
            }

            var outcome = await proposalService.RejectAsync(proposal, reason);
            display.ShowSystemMessage(outcome.Message);

            if (outcome.Success)
            {
                var because = string.IsNullOrWhiteSpace(reason) ? "" : $" Their note: \"{reason}\"";
                turnHandler.EnqueueSystemNote($"Your peer reviewed and declined your proposal #{proposal.Id}.{because}");
            }
        }
        finally
        {
            turnLock.Release();
        }
    }

    /// <summary>Splits a slash-command argument into a leading numeric id (with optional <c>#</c>)
    /// and the remaining text. Returns a null id when the first token isn't a number.</summary>
    private static (long? Id, string? Remainder) ParseIdAndRest(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return (null, null);
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!long.TryParse(parts[0].TrimStart('#'), out var id))
        {
            return (null, null);
        }

        return (id, parts.Length > 1 ? parts[1] : null);
    }

    private sealed class FragmentSeed
    {
        public required string Content { get; set; }
    }
}
