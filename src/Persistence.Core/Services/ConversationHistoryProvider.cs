using Persistence.Contracts;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Runtime;

namespace Persistence.Services;

/// <summary>
/// Reads the active conversation's recent messages straight from the store, on demand — the fresh
/// backfill a client fetches on connect (via the API snapshot) before it starts streaming live events.
/// Pull-on-connect, not push-and-maintain: the server never has to keep a client's chat view in sync.
/// </summary>
public interface IConversationHistoryProvider
{
    Task<IReadOnlyList<ChatHistoryItem>> GetRecentAsync(int limit = 10, CancellationToken ct = default);
}

/// <inheritdoc />
[Service(typeof(IConversationHistoryProvider))]
public class ConversationHistoryProvider(IWorkingContextRepository contexts, ISessionContext session)
    : IConversationHistoryProvider
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatHistoryItem>> GetRecentAsync(int limit = 10, CancellationToken ct = default)
    {
        var context = await contexts.GetByIdAsync(session.WorkingContextId, ct);

        if (context is null)
        {
            return [];
        }

        return context.ContextFragments.Values
            .Where(f => f.FragmentType == ContextFragmentType.ChatMessage)
            .OrderBy(f => f.Order)
            .TakeLast(limit)
            .Select(f => new ChatHistoryItem(
                f.Id,
                // ChatMessage fragments carry their author as a Source (DigitalPeer = the model/assistant).
                f.Sources.Any(s => s.SourceType == SourceType.DigitalPeer) ? "assistant" : "user",
                ResolveAuthor(f),
                f.Content,
                f.CreatedUtc))
            .ToList();
    }

    /// <summary>
    /// The display name of who authored a message: the source's name (the peer's name, or "Remote Peer"
    /// for the digital peer), falling back to a coarse label if a legacy fragment has an unnamed source.
    /// </summary>
    private static string ResolveAuthor(WeightedContextFragment fragment)
    {
        var source = fragment.Sources.Count > 0 ? fragment.Sources[0] : null;

        if (source is { Name: { Length: > 0 } name })
        {
            return name;
        }

        return source?.SourceType == SourceType.DigitalPeer ? "Remote Peer" : "Local Peer";
    }
}
