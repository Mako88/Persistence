using Persistence.DI;
using System.Collections.Concurrent;

namespace Persistence.Services;

/// <summary>
/// In-memory <see cref="IRemotePeerBroker"/>. Holds the single in-flight completion (turns are
/// serialized upstream) and hands its response back to the awaiting model client via a
/// <see cref="TaskCompletionSource{TResult}"/>.
/// </summary>
[Singleton]
public class RemotePeerBroker : IRemotePeerBroker
{
    private readonly object sync = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pending = new();

    private PendingCompletion? current;
    private int counter;

    #region Public API

    public Task<string> RequestCompletionAsync(string prompt, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        string id;
        lock (sync)
        {
            id = $"c{++counter}";
            current = new PendingCompletion(id, prompt);
            pending[id] = tcs;
        }

        // Drop the pending request if the turn is cancelled, so a stale response can't resolve it.
        ct.Register(() =>
        {
            if (pending.TryRemove(id, out var cancelled))
            {
                lock (sync)
                {
                    if (current?.Id == id)
                    {
                        current = null;
                    }
                }

                cancelled.TrySetCanceled(ct);
            }
        });

        return tcs.Task;
    }

    public PendingCompletion? TryGetPending()
    {
        lock (sync)
        {
            return current;
        }
    }

    public bool SubmitResponse(string id, string response)
    {
        if (!pending.TryRemove(id, out var tcs))
        {
            return false;
        }

        lock (sync)
        {
            if (current?.Id == id)
            {
                current = null;
            }
        }

        return tcs.TrySetResult(response);
    }

    #endregion
}
