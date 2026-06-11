using Autofac;
using Autofac.Extensions.DependencyInjection;
using Persistence.Api;
using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;
using Persistence.Services;

var builder = WebApplication.CreateBuilder(args);

// Serve the bundled web client (wwwroot) regardless of environment. Static web assets are auto-wired
// only in Development; without this the published/run-from-bin app (which boots as Production, with the
// content root at the launch directory) can't find wwwroot and returns 404 for the page.
builder.WebHost.UseStaticWebAssets();

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

// Serve the shared web client (wwwroot/index.html) so John/Claude/Ember can watch and talk to the
// peer through one backend — a thin API client, no direct DB access.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

await app.RunAsync();

/// <summary>
/// Exposed so the integration test project can boot the real app via WebApplicationFactory.
/// </summary>
public partial class Program;
