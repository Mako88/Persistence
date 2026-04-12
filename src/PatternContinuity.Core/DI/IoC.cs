using Autofac;
using Autofac.Builder;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Persistence.DI
{
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

            if (registerAdditionalServices != null)
            {
                registerAdditionalServices(containerBuilder);
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

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
        /// Register the given service
        /// </summary>
        private static void RegisterService(Type type, ContainerBuilder builder)
        {
            var isSingleton = true;
            IServiceAttribute? attribute = type.GetCustomAttribute<SingletonAttribute>();

            if (attribute == null)
            {
                attribute = type.GetCustomAttribute<ServiceAttribute>();
                isSingleton = false;
            }

            if (attribute == null)
            {
                // This is not a service, so return
                return;
            }

            IRegistrationBuilder<object, ConcreteReflectionActivatorData, SingleRegistrationStyle> registrationBuilder;

            if (attribute.RegisterAs == null)
            {
                // By default register any implemented interface
                registrationBuilder = builder.RegisterType(type)
                    .AsImplementedInterfaces();
            }
            else
            {
                // If a specific type to register as is set, use it
                registrationBuilder = builder.RegisterType(type)
                    .As(attribute.RegisterAs);
            }

            if (isSingleton)
            {
                registrationBuilder.SingleInstance();
            }
        }
    }
}
