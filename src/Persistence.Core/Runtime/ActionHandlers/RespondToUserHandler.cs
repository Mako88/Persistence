using System.Text.Json.Nodes;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Runtime.ActionHandlers;

/// <summary>
/// Handles <see cref="ModelAction.RespondToUser"/> by adding the reply as a
/// ChatMessage fragment to the working context, saving (which cascades to the
/// fragment and junction table), and notifying subscribers to display it
/// </summary>
[Service(registerAsType: typeof(IActionHandler), key: ModelAction.RespondToUser)]
public class RespondToUserHandler : IActionHandler
{
    private readonly ISessionContext sessionContext;
    private readonly IEventBus eventBus;

    /// <summary>
    /// Constructor
    /// </summary>
    public RespondToUserHandler(ISessionContext sessionContext, IEventBus eventBus)
    {
        this.sessionContext = sessionContext;
        this.eventBus = eventBus;
    }

    /// <summary>
    /// Adds the reply to the working context, saves, and notifies subscribers
    /// to display it
    /// </summary>
    public async Task HandleAsync(WorkingContextEntity context, JsonNode? data, CancellationToken ct = default)
    {
        var reply = TextPayload.Extract(data)
            ?? throw new InvalidOperationException("RespondToUser action requires a text payload");

        var now = DateTimeOffset.UtcNow;

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ChatMessage,
            Status = ContextFragmentStatus.Active,
            Content = reply,
            // Raw transcript — low defaults so it's deprioritised vs. the peer's authored notes.
            Importance = 0.3f,
            Confidence = 0.5f,
            Relevance = 0.5f,
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

        await eventBus.PublishAsync(this, new RemotePeerReplied(reply));
    }
}
