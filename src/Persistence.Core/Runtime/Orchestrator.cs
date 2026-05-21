using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;

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
    private readonly IContextFragmentRepository fragmentRepo;
    private readonly ISessionContext sessionContext;
    private readonly IDisplayProvider display;
    private readonly IEventBus eventBus;
    private readonly ITurnHandler turnHandler;
    private readonly IWakeUpMonitor wakeUpMonitor;
    private readonly IAppConfig config;

    private readonly SemaphoreSlim turnLock = new(1, 1);
    private readonly TaskCompletionSource shutdown = new();

    /// <summary>
    /// Constructor
    /// </summary>
    public Orchestrator(
        IDatabaseManager db,
        IWorkingContextRepository workingContextRepo,
        IContextFragmentRepository fragmentRepo,
        ISessionContext sessionContext,
        IDisplayProvider display,
        IEventBus eventBus,
        ITurnHandler turnHandler,
        IWakeUpMonitor wakeUpMonitor,
        IAppConfig config)
    {
        this.db = db;
        this.workingContextRepo = workingContextRepo;
        this.fragmentRepo = fragmentRepo;
        this.sessionContext = sessionContext;
        this.display = display;
        this.eventBus = eventBus;
        this.turnHandler = turnHandler;
        this.wakeUpMonitor = wakeUpMonitor;
        this.config = config;
    }

    /// <summary>
    /// Initialises the session, subscribes to display input, starts the display provider,
    /// and awaits shutdown (triggered by cancellation or /exit and /quit commands)
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        await InitializeAsync();

        _ = eventBus.Subscribe<DisplayInputReceived>(OnDisplayInputReceived);
        wakeUpMonitor.Start(ct);
        display.Start(ct);

        using var reg = ct.Register(() => shutdown.TrySetCanceled());

        try
        {
            await shutdown.Task;
        }
        catch
        {
            // Do nothing
        }

        display.Stop();
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

        if (input.StartsWith('/'))
        {
            HandleCommand(input);
            return;
        }

        await turnLock.WaitAsync();
        try
        {
            display.ShowThinking();
            await turnHandler.ExecuteTurnAsync(input);
        }
        finally
        {
            _ = turnLock.Release();
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

        var context = await workingContextRepo.GetMostRecentAsync()
            ?? await workingContextRepo.CreateAsync("Default");

        sessionContext.WorkingContextId = context.Id;

        // On first run the context has no fragments — seed it with the system fragments.
        if (context.ContextFragments.Count == 0)
        {
            var systemFragments = await fragmentRepo.GetSystemFragmentsAsync();

            foreach (var fragment in systemFragments)
            {
                context.AddFragment(fragment);
            }

            await workingContextRepo.SaveAsync(context);
        }
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
                _ = shutdown.TrySetResult();
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
}
