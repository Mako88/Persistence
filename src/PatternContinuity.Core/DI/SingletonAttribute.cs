namespace Persistence.DI
{
    /// <summary>
    /// Custom attribute to define a DI service to register as a singleton
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class SingletonAttribute : ServiceAttribute
    {
        /// <summary>
        /// Constructor
        /// </summary>
        public SingletonAttribute() : base() { }

        /// <summary>
        /// Constructor
        /// </summary>
        public SingletonAttribute(Type registerAs) : base(registerAs) { }
    }
}
