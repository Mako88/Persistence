using Autofac;
using Microsoft.Extensions.DependencyInjection;
using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;

// Load config and register all services
var serviceProvider = IoC.RegisterServices(builder =>
{
    var config = AppConfig.Load();

    // Register config as singleton
    builder.RegisterInstance(config).As<IAppConfig>().SingleInstance();

});

// Resolve the orchestrator and run the session
var orchestrator = serviceProvider.GetRequiredService<IOrchestrator>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await orchestrator.RunAsync(cts.Token);
