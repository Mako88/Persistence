using Microsoft.Data.Sqlite;
using PatternContinuity.Actions;
using PatternContinuity.Config;
using PatternContinuity.Console.Runtime;
using PatternContinuity.Data;
using PatternContinuity.Prompt;
using PatternContinuity.Runtime;
using PatternContinuity.Services;

// Load config
var config = AppConfig.Load();

if (string.IsNullOrWhiteSpace(config.ApiKey))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Error: ApiKey is not set in appsettings.json.");
    Console.WriteLine("Please add your API key and try again.");
    Console.ResetColor();
    return 1;
}

// Open database connection
var connectionString = $"Data Source={config.DatabasePath}";
using var db = new SqliteConnection(connectionString);
db.Open();

// Bootstrap schema + seed data
DatabaseBootstrap.Initialize(db);

// Configure parse mode
ActionParser.StrictMode = config.StrictParseMode;

// Create repositories
var sessions = new SessionRepository(db);
var entries = new LayerEntryRepository(db);
var versions = new EntryVersionRepository(db);
var actionLog = new ActionLogRepository(db);
var reflections = new ReflectionRepository(db);
var messageRepo = new MessageRepository(db);
var scheduledEvents = new ScheduledEventRepository(db);

// Create session
var session = sessions.Create(config.ActivePersonId);

// Create conversation window and warm from recent history
var window = new ConversationWindow(messageRepo, session.Id, config.MaxRecentMessages);
var warmed = window.WarmFromHistory(maxAgeHours: 24);
if (warmed > 0)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  [Restored {warmed} recent message(s) from previous session]");
    Console.ResetColor();
}

// Create model client
IModelClient client = config.ApiProvider.ToLower() switch
{
    "openai" => new OpenAiModelClient(config.ApiKey, config.ApiBaseUrl, config.ModelName, config.MaxCompletionTokens),
    _ => throw new InvalidOperationException($"Unknown API provider: {config.ApiProvider}")
};

// Create services
var composer = new PromptComposer(entries, config);
var executor = new ActionExecutor(entries, versions, actionLog, session.Id, scheduledEvents);
var reflection = new ReflectionService(client, composer, executor, reflections, actionLog);

// Create turn engine (core logic) and console orchestrator (UI shell)
var engine = new TurnEngine(
    client, composer, executor, reflection, sessions, scheduledEvents, config, session.Id, window);
var orchestrator = new ConsoleOrchestrator(engine);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await orchestrator.RunAsync(cts.Token);

// Cleanup
if (client is IDisposable disposable)
    disposable.Dispose();

return 0;
