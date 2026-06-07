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

        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(x => x.FullName?.StartsWith("Persistence") == true).ToList();

        RegisterDapperTypeHandlers(assemblies);

        if (registerAdditionalServices != null)
        {
            registerAdditionalServices(containerBuilder);
        }

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                RegisterService(type, containerBuilder);
            }
        }

        var container = containerBuilder.Build();
        return new AutofacServiceProvider(container);
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
    /// Registers a type in the DI container if it has a service registration attribute
    /// </summary>
    private static void RegisterService(Type type, ContainerBuilder builder)
    {
        IServiceAttribute? attribute = type.GetCustomAttribute<ServiceAttribute>(true);

        if (attribute == null)
        {
            return;
        }

        var registrationBuilder = builder.RegisterType(type);

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

        if (attribute is SingletonAttribute)
        {
            registrationBuilder.SingleInstance();
        }
    }
}
