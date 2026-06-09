using Autofac;
using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence;

/// <summary>
/// Bootstraps the DI container. The caller can extend registrations via
/// <paramref name="registerAdditionalServices"/> for project-specific services
/// (e.g. display providers).
/// </summary>
public static class Initializer
{
    /// <summary>
    /// Builds and returns the configured service provider.
    /// </summary>
    public static async Task<IServiceProvider> InitializeAsync(
        Action<ContainerBuilder>? registerAdditionalServices = null)
    {
        var config = await AppConfig.LoadAsync();

        return IoC.RegisterServices(builder =>
        {
            builder.RegisterInstance(config).As<IAppConfig>().SingleInstance();

            if (!Enum.TryParse<ModelProvider>(config.Provider, ignoreCase: true, out var provider))
                throw new ArgumentException($"Unrecognized Provider value: '{config.Provider}'");

            if (!Enum.TryParse<UiMode>(config.UiMode, ignoreCase: true, out var uiMode))
                throw new ArgumentException($"Unrecognized UiMode value: '{config.UiMode}'");

            builder.Register(c => c.ResolveKeyed<IModelClient>(provider));
            builder.Register(c => c.ResolveKeyed<IPromptBuilder>(provider));
            builder.Register(c => c.ResolveKeyed<IDisplayProvider>(uiMode));

            registerAdditionalServices?.Invoke(builder);
        });
    }
}
