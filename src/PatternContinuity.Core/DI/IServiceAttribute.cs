namespace Persistence.DI
{
    /// <summary>
    /// Custom attribute to define a DI service
    /// </summary>
    public interface IServiceAttribute
    {
        public Type? RegisterAs { get; }
    }
}
