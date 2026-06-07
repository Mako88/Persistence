using Autofac;
using Autofac.Extensions.DependencyInjection;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Persistence.Data.TypeMappers;
using System.Reflection;

namespace Persistence.DI;

/// <summary>
/// Helper class for setting up dependency injection
/// </summary>
public static class IoC
{
    /// <summary>
    /// Register all services from all loaded assemblies
    /// </summary>
    public static IServiceProvider RegisterServices(Action<ContainerBuilder>? registerAdditionalServices = null)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();

        var containerBuilder = new ContainerBuilder();
        containerBuilder.Populate(serviceCollection);

        PopulateContainer(containerBuilder, registerAdditionalServices);

        var container = containerBuilder.Build();
        return new AutofacServiceProvider(container);
    }

    /// <summary>
    /// Registers all attribute-marked Persistence services and Dapper type handlers into an
    /// existing <see cref="ContainerBuilder"/>. Use this when a host owns the container build
    /// (e.g. the ASP.NET Autofac service-provider factory); <paramref name="registerAdditionalServices"/>
    /// runs before the attribute scan so it can add instances the scanned services depend on.
    /// </summary>
    public static void PopulateContainer(
        ContainerBuilder containerBuilder, Action<ContainerBuilder>? registerAdditionalServices = null)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => x.FullName?.StartsWith("Persistence") == true)
            .ToList();

        RegisterDapperTypeHandlers(assemblies);

        registerAdditionalServices?.Invoke(containerBuilder);

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                RegisterService(type, containerBuilder);
            }
        }
    }

    /// <summary>
    /// Registers all Dapper type handlers the data layer relies on: the
    /// <see cref="DateTimeOffset"/> handler and a string-backed handler for every public
    /// enum. Exposed so tests can exercise the repositories with the same hydration the
    /// app uses, instead of going through the full DI container. Defaults to scanning the
    /// loaded Persistence assemblies.
    /// </summary>
    public static void RegisterDapperTypeHandlers(IEnumerable<Assembly>? assemblies = null)
    {
        SqlMapper.AddTypeHandler(new DateTimeOffsetMapper());

        assemblies ??= AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => x.FullName?.StartsWith("Persistence") == true);

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                RegisterEnumTypeHandler(type);
            }
        }
    }

    /// <summary>
    /// Registers type handlers so Dapper stores all enum values as their string name
    /// rather than integer value
    /// </summary>
    private static void RegisterEnumTypeHandler(Type enumType)
    {
        if (!enumType.IsEnum || !enumType.IsPublic)
        {
            return;
        }

        var handlerType = typeof(EnumTypeHandler<>);

        var handler = (SqlMapper.ITypeHandler)Activator.CreateInstance(handlerType.MakeGenericType(enumType))!;
        SqlMapper.AddTypeHandler(enumType, handler);
    }

    /// <summary>
    /// Registers a type in the DI container for each service registration attribute it carries.
    /// A type may have multiple <see cref="ServiceAttribute"/>s (e.g. one prompt builder keyed
    /// for several providers); all registrations share one component so a singleton stays single.
    /// </summary>
    private static void RegisterService(Type type, ContainerBuilder builder)
    {
        var attributes = type.GetCustomAttributes<ServiceAttribute>(true).ToList();

        if (attributes.Count == 0)
        {
            return;
        }

        var registrationBuilder = builder.RegisterType(type);

        foreach (var attribute in attributes)
        {
            if (attribute.Name != null)
            {
                if (attribute.RegisterAsType == null)
                {
                    throw new InvalidOperationException(
                        $"You must specify registerAsType when registering a service by name: {type}");
                }

                registrationBuilder.Named(attribute.Name, attribute.RegisterAsType);
            }
            else if (attribute.Key != null)
            {
                if (attribute.RegisterAsType == null)
                {
                    throw new InvalidOperationException(
                        $"You must specify registerAsType when registering a service by key: {type}");
                }

                registrationBuilder.Keyed(attribute.Key, attribute.RegisterAsType);
            }
            else if (attribute.RegisterAsType != null)
            {
                registrationBuilder.As(attribute.RegisterAsType);
            }
            else
            {
                registrationBuilder.AsImplementedInterfaces();
            }
        }

        if (attributes.Any(a => a is SingletonAttribute))
        {
            registrationBuilder.SingleInstance();
        }
    }
}
