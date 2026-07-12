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
// (Standalone-deployment path — under the single-owner model the always-on API server owns wakes.)
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
// exit. An OS trigger launches it when the interactive app isn't running. (Standalone-deployment path.)
if (args.Contains("--wake-runner"))
{
    Environment.SetEnvironmentVariable("PERSISTENCE_UIMODE", "Headless");
    var provider = await Initializer.InitializeAsync();
    using var wakeCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; wakeCts.Cancel(); };
    await provider.GetRequiredService<IOrchestrator>().RunWakeCycleAsync(wakeCts.Token);
    return;
}

// `--standalone` runs the full stack in-process (the pre-single-owner behaviour): this process owns the
// database, turn pipeline, wakes, and the TUI. Kept as an escape hatch (offline/dev); running it
// alongside the API server risks the lost-update problem the single-owner model exists to prevent.
if (args.Contains("--standalone"))
{
    var serviceProvider = await Initializer.InitializeAsync();
    var orchestrator = serviceProvider.GetRequiredService<IOrchestrator>();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await orchestrator.RunAsync(cts.Token);
    return;
}

// DEFAULT (single-owner model, ADR-0006): run as a thin client of a running API server — no local
// database, pipeline, or model. It renders the server's conversation stream and sends input over HTTP.
// `--client <baseUrl>` overrides the server address; `--as <localPeer>` identifies who's speaking.
{
    var baseUrl = ArgAfter(args, "--client")
        ?? Environment.GetEnvironmentVariable("PERSISTENCE_SERVER")
        ?? "http://localhost:5000";
    var localPeer = ArgAfter(args, "--as");

    using var clientCts = new CancellationTokenSource();
    System.Console.CancelKeyPress += (_, e) => { e.Cancel = true; clientCts.Cancel(); };

    await Persistence.Console.ClientConsoleHost.RunAsync(baseUrl, localPeer, clientCts.Token);
}

static string? ArgAfter(string[] args, string flag)
{
    var i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
