using Microsoft.Extensions.DependencyInjection;
using Persistence;
using Persistence.Runtime;

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
