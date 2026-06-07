using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Extensions;
using Persistence.Notifications;
using Persistence.Utilities;
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
        IAppConfig config)
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

        await InitializeAsync();
        initialized.TrySetResult();

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

        if (input.StartsWith('/'))
        {
            HandleCommand(input);
            return;
        }

        if (!turnLock.Wait(0))
        {
            turnHandler.EnqueueInput(input);
            display.ShowMessageQueued(input);
            return;
        }

        try
        {
            display.ShowThinking();
            await turnHandler.ExecuteTurnAsync(input);

            while (turnHandler.HasPendingInput)
            {
                display.ShowThinking("processing queued messages");
                await turnHandler.ExecuteTurnAsync();
            }
        }
        finally
        {
            turnLock.Release();
        }
    }

    // -- Initialization --

    /// <summary>
    /// Sets up the database, creates or loads the working context, and seeds system
    /// fragments on first run
    /// </summary>
    private async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            display.ShowError("Warning: ApiKey is not set. Model calls may fail.");
        }

        await db.InitializeAsync();

        sessionContext.SessionId = Guid.NewGuid().ToString("N");

        var context = await workingContextRepo.GetMostRecentAsync();

        if (context == null)
        {
            context = await CreateSeededContextAsync();
        }

        sessionContext.WorkingContextId = context.Id;

        var recentMessages = context.ContextFragments.Values
            .Where(f => f.FragmentType == ContextFragmentType.ChatMessage)
            .OrderBy(f => f.Order)
            .TakeLast(10)
            .Select(f => (
                // ChatMessage fragments carry their author as a Source (RemotePeer = the
                // model/assistant), not a Notes role. Notes is always null here.
                Role: f.Sources.Any(s => s.SourceType == Persistence.Data.Entities.SourceType.RemotePeer) ? "assistant" : "user",
                Content: f.Content,
                Timestamp: f.CreatedUtc))
            .ToList();

        display.ShowChatHistory(recentMessages);
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

        await AddFirstWakeGuideAsync(context);

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
    /// Dispatches a slash command to its handler
    /// </summary>
    private void HandleCommand(string command)
    {
        switch (command.ToLower())
        {
            case "/exit":
            case "/quit":
                display.Stop();
                break;

            case "/debug":
                config.DebugMode = !config.DebugMode;
                display.ShowDebugInfo($"DEBUG MODE {(config.DebugMode ? "ENABLED" : "DISABLED")}");
                break;

            default:
                display.ShowUnknownCommand(command);
                break;
        }
    }

    private sealed class FragmentSeed
    {
        public required string Content { get; set; }
    }
}
