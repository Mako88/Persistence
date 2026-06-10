
namespace Persistence.Runtime
{
    public interface IOrchestrator
    {
        Task RunAsync(CancellationToken ct = default);

        /// <summary>
        /// Headless one-shot: initialize the session, fire all currently-due scheduled events as
        /// autonomous turns (running each to completion), then return. No interactive display loop and
        /// no background poll timer — for the wake-runner that an OS trigger launches when the
        /// interactive app isn't running.
        /// </summary>
        Task RunWakeCycleAsync(CancellationToken ct = default);
    }
}