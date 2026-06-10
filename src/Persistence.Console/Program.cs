using Microsoft.Extensions.DependencyInjection;
using Persistence;
using Persistence.Data;
using Persistence.Data.Repositories;
using Persistence.Runtime;

// `--preview` launches the TUI with sample content in every pane (no DB/model) for reviewing
// the layout and colours. Useful for visual/colour work; not part of a normal run.
if (args.Contains("--preview"))
{
    // An optional variant after --preview (e.g. "--preview A") selects a role-colour combo to compare.
    var variant = args.SkipWhile(a => !string.Equals(a, "--preview", StringComparison.OrdinalIgnoreCase))
        .Skip(1).FirstOrDefault();
    await Persistence.Console.TuiPreview.RunAsync(variant);
    return;
}

// `--check-due` is a fast, model-free probe for the wake launcher: exit 0 if any scheduled event is
// due now, 100 if none. Lets an OS poll gate the expensive model startup on there being real work.
if (args.Contains("--check-due"))
{
    Environment.SetEnvironmentVariable("PERSISTENCE_UIMODE", "Headless");
    var provider = await Initializer.InitializeAsync();
    await provider.GetRequiredService<IDatabaseManager>().InitializeAsync();
    var due = await provider.GetRequiredService<IScheduledEventRepository>().GetDueEventsAsync();
    Environment.ExitCode = due.Any() ? 0 : 100;
    return;
}

// `--wake-runner` is the headless one-shot: fire all due scheduled events as autonomous turns, then
// exit. An OS trigger launches it (via the launcher) when the interactive app isn't running.
if (args.Contains("--wake-runner"))
{
    Environment.SetEnvironmentVariable("PERSISTENCE_UIMODE", "Headless");
    var provider = await Initializer.InitializeAsync();
    using var wakeCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; wakeCts.Cancel(); };
    await provider.GetRequiredService<IOrchestrator>().RunWakeCycleAsync(wakeCts.Token);
    return;
}

// Build the container — registers all [Singleton]/[Service] types from all assemblies
var serviceProvider = await Initializer.InitializeAsync();

var orchestrator = serviceProvider.GetRequiredService<IOrchestrator>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await orchestrator.RunAsync(cts.Token);
