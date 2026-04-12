namespace Persistence.DI
{
    /// <summary>
    /// Custom attribute to define a DI service
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class ServiceAttribute : Attribute, IServiceAttribute
    {
        /// <summary>
        /// Constructor
        /// </summary>
        public ServiceAttribute() { }

        /// <summary>
        /// Constructor
        /// </summary>
        public ServiceAttribute(Type registerAs) : this()
        {
            RegisterAs = registerAs;
        }

        /// <summary>
        /// The type this service should be registered as
        /// </summary>
        public Type? RegisterAs { get; private set; }
    }
}
