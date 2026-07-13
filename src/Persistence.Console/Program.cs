// The Console is a thin client of the Persistence API server (ADR-0006, single-owner model): it holds no
// database, turn pipeline, or model. One process — the API server — owns the store, the pipeline, and
// scheduled wakes; every front-end (this Console, a future web UI) talks to it over HTTP. This is
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

// Default: connect to one or more API servers and render their conversation stream(s), sending input
// over HTTP. `--as <localPeer>` identifies who's speaking (falls back to the server's configured default).
// Point at peers with:
//   --peer <name>=<url>   a NAMED peer — its replies are attributed "Arden: …" (multi-peer legibility).
//                         Repeat --peer to open a HUB: all peers aggregate into one pane, with a selector
//                         choosing which peer the side tabs + status show (ADR-0007 Phase 2b).
//   --client <url>        the unnamed single-peer form (generic label).
var localPeer = ArgAfter(args, "--as");
var peerEndpoints = ArgsAfter(args, "--peer")
    .Select(ParsePeer)
    .Where(p => p.Url is not null)
    .Select(p => new Persistence.Console.PeerEndpoint(p.Name, p.Url!, localPeer))
    .ToList();

if (peerEndpoints.Count == 0)
{
    var baseUrl = ArgAfter(args, "--client")
        ?? Environment.GetEnvironmentVariable("PERSISTENCE_SERVER")
        ?? "http://localhost:5000";
    peerEndpoints.Add(new Persistence.Console.PeerEndpoint(null, baseUrl, localPeer));
}

using var cts = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await Persistence.Console.ClientConsoleHost.RunAsync(peerEndpoints, cts.Token);

static string? ArgAfter(string[] args, string flag)
{
    var i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Every value following an occurrence of <paramref name="flag"/> — so `--peer a=... --peer b=...` yields both.
static IEnumerable<string> ArgsAfter(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
        {
            yield return args[i + 1];
        }
    }
}

// Splits "<name>=<url>" into (name, url); a bare url (no '=') yields (null, url). Null in → (null, null).
static (string? Name, string? Url) ParsePeer(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return (null, null);
    var eq = value.IndexOf('=');
    return eq > 0 ? (value[..eq].Trim(), value[(eq + 1)..].Trim()) : (null, value.Trim());
}
