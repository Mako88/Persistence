using System.Text.Json.Nodes;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Runtime.ActionHandlers;

/// <summary>
/// Handles <see cref="ModelAction.Think"/> by recording the remote peer's thought as a
/// transient <see cref="ContextFragmentType.ScratchPad"/> fragment in the working context.
///
/// This is "reasoning in the open": unlike a model's built-in/private reasoning, the thought
/// becomes visible context the remote peer can build on across iterations (and surface to the
/// display), without being sent to the local peer. ScratchPad fragments are never persisted to
/// the database — a thought lives only for the current session's working context. To keep a
/// thought permanently, the remote peer can promote it via <c>manage_context</c> (e.g. add a
/// Personal fragment).
/// </summary>
[Service(registerAsType: typeof(IActionHandler), key: ModelAction.Think)]
public class ThinkHandler : IActionHandler
{
    private readonly ISessionContext sessionContext;
    private readonly IEventBus eventBus;

    /// <summary>
    /// Constructor
    /// </summary>
    public ThinkHandler(ISessionContext sessionContext, IEventBus eventBus)
    {
        this.sessionContext = sessionContext;
        this.eventBus = eventBus;
    }

    /// <summary>
    /// Adds the thought to the working context as a transient ScratchPad fragment and
    /// publishes it for display.
    /// </summary>
    public async Task HandleAsync(WorkingContextEntity context, JsonNode? data, CancellationToken ct = default)
    {
        var thought = TextPayload.Extract(data)
            ?? throw new InvalidOperationException("Think action requires a text payload");

        var now = DateTimeOffset.UtcNow;

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ScratchPad,
            Status = ContextFragmentStatus.Active,
            Content = thought,
            Importance = 0.5f,
            Confidence = 1.0f,
            Relevance = 1.0f,
            Sources = [new SourceEntity
            {
                Id = sessionContext.RemotePeerSourceId,
                SourceType = SourceType.RemotePeer,
                CreatedUtc = now,
                LastModifiedUtc = now,
            }],
            CreatedUtc = now,
            LastModifiedUtc = now,
        });

        await eventBus.PublishAsync(this, new ModelThought(thought));
    }
}
