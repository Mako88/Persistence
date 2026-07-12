using System.Text.Json.Nodes;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Runtime.ActionHandlers;

/// <summary>
/// Handles <see cref="ModelAction.RespondToUser"/> by adding the reply as a ChatMessage
/// fragment to the working context, persisting it, and then notifying subscribers to
/// display it. The persist-before-publish order matters: the display log (and a client's
/// connect-time snapshot) captures the reply as a sequence-numbered event, while the chat
/// history a client backfills is read from the store. Publishing before the fragment is
/// durable opens a window where a snapshot sees the event's sequence but not the stored
/// message, dropping the reply. Saving first makes the store the source of truth the event
/// only ever trails — turning that race into a harmless duplicate rather than a lost reply.
/// </summary>
[Service(registerAsType: typeof(IActionHandler), key: ModelAction.RespondToUser)]
public class RespondToUserHandler : IActionHandler
{
    private readonly ISessionContext sessionContext;
    private readonly IEventBus eventBus;
    private readonly IWorkingContextRepository workingContextRepo;

    /// <summary>
    /// Constructor
    /// </summary>
    public RespondToUserHandler(ISessionContext sessionContext, IEventBus eventBus, IWorkingContextRepository workingContextRepo)
    {
        this.sessionContext = sessionContext;
        this.eventBus = eventBus;
        this.workingContextRepo = workingContextRepo;
    }

    /// <summary>
    /// Adds the reply to the working context, persists it (so it's in the store before the
    /// display event is emitted), and notifies subscribers to display it
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
                SourceType = SourceType.DigitalPeer,
                CreatedUtc = now,
                LastModifiedUtc = now,
            }],
            CreatedUtc = now,
            LastModifiedUtc = now,
        });

        // Persist before publishing so the reply is in the store the moment the display event
        // exists (see the type remarks). The end-of-turn save re-saves the same context; this
        // one just moves the reply's durability ahead of its announcement. Like the user-message
        // persist, it saves the whole context — the reply is the only non-transient addition here.
        await workingContextRepo.SaveAsync(context, ct: ct);

        await eventBus.PublishAsync(this, new RemotePeerReplied(reply));
    }
}
