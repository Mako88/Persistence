using Autofac;
using Persistence.Config;
using Persistence.DI;
using Persistence.Extensions;
using Persistence.Services;

namespace Persistence;

/// <summary>
/// Bootstraps the DI container. The caller can extend registrations via
/// <paramref name="registerAdditionalServices"/> for project-specific services
/// (e.g. display providers).
/// </summary>
public static class Initializer
{
    /// <summary>Builds and returns the configured service provider.</summary>
    public static async Task<IServiceProvider> InitializeAsync(
        Action<ContainerBuilder>? registerAdditionalServices = null)
    {
        var config = await AppConfig.LoadAsync();

        return IoC.RegisterServices(builder =>
        {
            builder.RegisterInstance(config).As<IAppConfig>().SingleInstance();

            if (config.ModelName == null || !Enum.TryParseDescription<ParticipantModels>(config.ModelName, out var modelEnum))
                throw new ArgumentException($"Unrecognized or missing ModelName value: '{config.ModelName}'");

            builder.Register(c => c.ResolveKeyed<IModelClient>(modelEnum));

            registerAdditionalServices?.Invoke(builder);
        });
    }
}
