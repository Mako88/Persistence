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

    /// <summary>
    /// Registers a prompt as the pending completion and returns a task that resolves when a remote
    /// peer submits its response, or is cancelled if the turn is cancelled
    /// </summary>
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

    /// <summary>
    /// Returns the completion currently awaiting a response, or null if none is pending
    /// </summary>
    public PendingCompletion? TryGetPending()
    {
        lock (sync)
        {
            return current;
        }
    }

    /// <summary>
    /// Resolves the pending completion with the given id using the supplied response; returns false
    /// if no matching request is awaiting
    /// </summary>
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
