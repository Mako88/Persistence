
namespace Persistence.Runtime
{
    public interface IOrchestrator
    {
        Task RunAsync(CancellationToken ct = default);
    }
}