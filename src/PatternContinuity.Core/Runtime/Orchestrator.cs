using Persistence.Actions;
using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Models;
using Persistence.Prompt;
using Persistence.Services;

namespace Persistence.Runtime
{
    /// <summary>
    /// UI-independent orchestrator that coordinates the main session loop
    /// </summary>
    [Singleton]
    public class Orchestrator : IOrchestrator
    {
        private readonly IDisplayProvider _display;
        private readonly IAppConfig _config;
        private readonly IDatabaseBootstrap _bootstrap;
        private readonly ISessionRepository _sessions;
        private readonly IScheduledEventRepository _scheduledEvents;
        private readonly IMessageRepository _messageRepo;
        private readonly IModelClient _modelClient;
        private readonly IPromptComposer _composer;
        private readonly ILayerEntryRepository _entries;
        private readonly IEntryVersionRepository _versions;
        private readonly IActionLogRepository _actionLog;
        private readonly IReflectionRepository _reflections;

        private TurnEngine _engine = null!;
        private bool _wakeUpFiredThisCycle;
        private DateTimeOffset? _lastWakeDebugLog;

        /// <summary>
        /// Constructor
        /// </summary>
        public Orchestrator(
            IDisplayProvider display,
            IAppConfig config,
            IDatabaseBootstrap bootstrap,
            ISessionRepository sessions,
            IScheduledEventRepository scheduledEvents,
            IMessageRepository messageRepo,
            IModelClient modelClient,
            IPromptComposer composer,
            ILayerEntryRepository entries,
            IEntryVersionRepository versions,
            IActionLogRepository actionLog,
            IReflectionRepository reflections)
        {
            _display = display;
            _config = config;
            _bootstrap = bootstrap;
            _sessions = sessions;
            _scheduledEvents = scheduledEvents;
            _messageRepo = messageRepo;
            _modelClient = modelClient;
            _composer = composer;
            _entries = entries;
            _versions = versions;
            _actionLog = actionLog;
            _reflections = reflections;
        }

        /// <summary>
        /// Run the main session loop
        /// </summary>
        public async Task RunAsync(CancellationToken ct = default)
        {
            await InitializeAsync();

            while (!ct.IsCancellationRequested)
            {
                var input = await PollForInputAsync(ct);

                if (input == null)
                {
                    await EndSessionAsync();
                    break;
                }

                _wakeUpFiredThisCycle = false;

                if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)
                    || input.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    await EndSessionAsync();
                    break;
                }

                input = input.Trim();
                if (string.IsNullOrEmpty(input)) continue;

                if (input.StartsWith('/'))
                {
                    await HandleCommandAsync(input);
                    continue;
                }

                await ProcessTurnAsync(input, ct);
            }
        }

        /// <summary>
        /// Initialize the database, create a session, and build the turn engine
        /// </summary>
        private async Task InitializeAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                _display.ShowError("Error: ApiKey is not set in appsettings.json. Please add your API key and try again.");
            }

            // Configure parse mode
            ActionParser.StrictMode = _config.StrictParseMode;

            // Initialize database schema and seed data
            await _bootstrap.InitializeAsync();

            // Create session
            var session = await _sessions.CreateAsync(_config.ActivePersonId);

            // Create conversation window and warm from history
            var window = new ConversationWindow(_messageRepo, session.Id, _config.MaxRecentMessages);
            var warmed = await window.WarmFromHistoryAsync(maxAgeHours: 24);

            // Create session-scoped services
            var executor = new ActionExecutor(_entries, _versions, _actionLog, _scheduledEvents, session.Id);
            var reflection = new ReflectionService(_modelClient, _composer, executor, _reflections, _actionLog);

            _engine = new TurnEngine(
                _modelClient, _composer, executor, reflection, _sessions, _scheduledEvents, _config, session.Id, window);

            // Get restored messages for display
            var history = _engine.GetRecentMessages()
                .Select(m => new Message { Role = m.Role, Content = m.Content })
                .ToList();

            _display.Initialize(session.Id, _config, history);
        }

        /// <summary>
        /// Poll for user input while checking for due wake-up events
        /// </summary>
        private async Task<string?> PollForInputAsync(CancellationToken ct)
        {
            using var inputCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var inputBuffer = new System.Text.StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                // Start collecting input (will return on Enter or cancellation)
                var inputTask = _display.RequestInputAsync(inputCts.Token);

                // Poll for wake-ups while waiting for input
                while (!inputTask.IsCompleted && !ct.IsCancellationRequested)
                {
                    if (!_wakeUpFiredThisCycle)
                    {
                        await CheckAndHandleWakeUpAsync(ct);
                    }

                    await Task.Delay(400, ct).ConfigureAwait(false);
                }

                if (inputTask.IsCompleted)
                {
                    try
                    {
                        return await inputTask;
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Check for due wake-up events and handle them
        /// </summary>
        private async Task CheckAndHandleWakeUpAsync(CancellationToken ct)
        {
            var diag = await _engine.GetWakeUpDiagnosticsAsync();
            if (diag == null) return;

            // Throttled debug logging
            var nowUtc = DateTimeOffset.UtcNow;
            if (_lastWakeDebugLog == null || (nowUtc - _lastWakeDebugLog.Value).TotalSeconds >= 30)
            {
                _lastWakeDebugLog = nowUtc;
                _display.ShowWakeDebug(diag, "");
            }

            if (diag.IsDue)
            {
                var evt = await _engine.CheckForDueWakeUpAsync();
                if (evt != null)
                {
                    _display.ClearPartialInput("");
                    await ProcessWakeUpAsync(evt, ct);
                    _wakeUpFiredThisCycle = true;
                    _display.RestoreInputPrompt("");
                }
            }
        }

        /// <summary>
        /// Process a user turn and display the results
        /// </summary>
        private async Task ProcessTurnAsync(string userMessage, CancellationToken ct)
        {
            _display.ShowThinking();

            var result = await _engine.ProcessTurnAsync(userMessage, ct);

            if (result.IsApiError)
            {
                _display.ShowError($"API Error: {result.ApiErrorMessage}");
                return;
            }

            _display.ShowReply(result.AssistantReply);
            _display.ShowActionResults(result.ActionResults);

            if (result.RecoveryResult != null)
            {
                _display.ShowRecovery(result.RecoveryResult);
            }

            if (result.ReadFollowUpResult != null)
            {
                _display.ShowReadFollowUp(result.ReadFollowUpResult);
            }

            if (result.ReflectionResult != null)
            {
                _display.ShowReflection(result.ReflectionResult);
            }
        }

        /// <summary>
        /// Process a wake-up event and display the results
        /// </summary>
        private async Task ProcessWakeUpAsync(ScheduledEvent evt, CancellationToken ct)
        {
            _display.ShowWakeUpEvent(evt.Reason);
            _display.ShowThinking("thinking (wake)");

            var result = await _engine.ProcessWakeUpAsync(evt, ct);
            _display.ShowWakeUpResult(result);
        }

        /// <summary>
        /// Handle a slash command
        /// </summary>
        private async Task HandleCommandAsync(string command)
        {
            switch (command.ToLower())
            {
                case "/debug":
                    var debugResults = await _engine.GetDebugInfoAsync();
                    _display.ShowDebugInfo(debugResults);
                    break;

                case "/tokens":
                    var usage = await _engine.GetTokenUsageAsync();
                    _display.ShowTokenUsage(usage);
                    break;

                case "/wake":
                    var pendingWake = await _engine.GetPendingWakeUpAsync();
                    _display.ShowWakeStatus(pendingWake);
                    break;

                default:
                    _display.ShowUnknownCommand(command);
                    break;
            }
        }

        /// <summary>
        /// End the current session
        /// </summary>
        private async Task EndSessionAsync()
        {
            await _engine.EndSessionAsync();
            _display.ShowSessionEnded();
        }
    }
}
