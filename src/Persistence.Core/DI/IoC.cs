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
        _ = serviceCollection.AddLogging();

        var containerBuilder = new ContainerBuilder();
        containerBuilder.Populate(serviceCollection);

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        SqlMapper.AddTypeHandler(new DateTimeOffsetMapper());
        RegisterEnumTypeHandlers(assemblies);

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
    /// Registers type handlers so Dapper stores all enum values as their string name
    /// rather than integer value
    /// </summary>
    private static void RegisterEnumTypeHandlers(Assembly[] assemblies)
    {
        var handlerType = typeof(EnumTypeHandler<>);

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsEnum || !type.IsPublic)
                {
                    continue;
                }

                var handler = Activator.CreateInstance(handlerType.MakeGenericType(type))!;
                SqlMapper.AddTypeHandler(type, (SqlMapper.ITypeHandler)handler);
            }
        }
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
