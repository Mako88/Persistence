using Microsoft.Extensions.DependencyInjection;
using Persistence;
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
