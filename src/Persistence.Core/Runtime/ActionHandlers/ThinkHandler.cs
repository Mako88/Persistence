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
/// <see cref="ContextFragmentType.Thought"/> fragment in the working context.
///
/// This is "reasoning in the open": unlike a model's built-in/private reasoning, the thought
/// becomes visible context the remote peer can build on — within the turn and, because Thought
/// fragments are persisted, across turns too. The system keeps only a rolling window of the most
/// recent thoughts in the active context (see <c>TurnHandler</c>'s thought decay); older ones are
/// archived (detached but searchable/restorable), so recent reasoning is recalled without the
/// context ballooning. To keep a thought permanently, the peer can still promote it via
/// <c>manage_context</c> (e.g. add a Personal fragment).
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
    /// Adds the thought to the working context as a persisted Thought fragment (kept to a rolling
    /// window by the thought decay) and publishes it for display.
    /// </summary>
    public async Task HandleAsync(WorkingContextEntity context, JsonNode? data, CancellationToken ct = default)
    {
        var thought = TextPayload.Extract(data)
            ?? throw new InvalidOperationException("Think action requires a text payload");

        var now = DateTimeOffset.UtcNow;

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.Thought,
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
