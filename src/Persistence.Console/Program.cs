// The Console is a thin client of the Persistence API server (ADR-0006, single-owner model): it holds no
// database, turn pipeline, or model. One process — the API server — owns the store, the pipeline, and
// scheduled wakes; every front-end (this Console, the web client) talks to it over HTTP. This is
// single-owner "by construction": there is no code path here that opens the database.

// `--preview` launches the TUI with sample content in every pane (no DB/model/server) for reviewing the
// layout and colours. Not part of a normal run.
if (args.Contains("--preview"))
{
    var variant = args.SkipWhile(a => !string.Equals(a, "--preview", StringComparison.OrdinalIgnoreCase))
        .Skip(1).FirstOrDefault();
    await Persistence.Console.TuiPreview.RunAsync(variant);
    return;
}

// Scheduled wakes are owned by the always-on API server now (its hosted orchestrator fires due events for
// its lifetime). The old OS-triggered, DB-opening wake paths were removed in the single-owner migration;
// exit with guidance rather than silently falling through to client mode.
if (args.Contains("--wake-runner") || args.Contains("--check-due"))
{
    System.Console.Error.WriteLine(
        "Scheduled wakes are now owned by the always-on API server — run it and it fires due events for "
        + "its lifetime. The Console's --wake-runner/--check-due paths were removed in the single-owner "
        + "migration (ADR-0006); update any OS task to keep the API server running instead.");
    Environment.ExitCode = 2;
    return;
}

// Default: connect to the API server and render its conversation stream, sending input over HTTP.
// `--client <baseUrl>` (or PERSISTENCE_SERVER) overrides the address; `--as <localPeer>` identifies who's
// speaking (falls back to the server's configured default local peer).
var baseUrl = ArgAfter(args, "--client")
    ?? Environment.GetEnvironmentVariable("PERSISTENCE_SERVER")
    ?? "http://localhost:5000";
var localPeer = ArgAfter(args, "--as");

using var cts = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await Persistence.Console.ClientConsoleHost.RunAsync(baseUrl, localPeer, cts.Token);

static string? ArgAfter(string[] args, string flag)
{
    var i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
