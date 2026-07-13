using Autofac;
using Autofac.Extensions.DependencyInjection;
using Persistence.Api;
using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;
using Persistence.Services;

var builder = WebApplication.CreateBuilder(args);

// Run as a Windows Service when hosted by the SCM (always-on, survives logout/reboot), and as a plain
// console app otherwise — UseWindowsService auto-detects and is a no-op outside a service context.
builder.Host.UseWindowsService();

var config = await AppConfig.LoadAsync();

// Autofac owns the container so our attribute-registered services live alongside the
// framework's (controllers, hosting, etc.).
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(container =>
{
    IoC.PopulateContainer(container, b =>
    {
        b.RegisterInstance(config).As<IAppConfig>().SingleInstance();

        if (!Enum.TryParse<ModelProvider>(config.Provider, ignoreCase: true, out var provider))
            throw new ArgumentException($"Unrecognized Provider value: '{config.Provider}'");

        if (!Enum.TryParse<UiMode>(config.UiMode, ignoreCase: true, out var uiMode))
            throw new ArgumentException($"Unrecognized UiMode value: '{config.UiMode}'");

        b.Register(c => c.ResolveKeyed<IModelClient>(provider));
        b.Register(c => c.ResolveKeyed<IPromptBuilder>(provider));
        b.Register(c => c.ResolveKeyed<IDisplayProvider>(uiMode));
    });
});

builder.Services.AddControllers();

// The orchestrator (DB init, input subscription, wake-up monitor, display lifecycle) runs as a
// background service for the host's lifetime.
builder.Services.AddHostedService<OrchestratorHostedService>();

var app = builder.Build();

app.MapControllers();

await app.RunAsync();

/// <summary>
/// Exposed so the integration test project can boot the real app via WebApplicationFactory.
/// </summary>
public partial class Program;
