using System.Text.Json.Nodes;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
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
    private readonly IEventBus eventBus;

    /// <summary>
    /// Constructor
    /// </summary>
    public RespondToUserHandler(IEventBus eventBus)
    {
        this.eventBus = eventBus;
    }

    /// <summary>
    /// Adds the reply to the working context, saves, and notifies subscribers
    /// to display it
    /// </summary>
    public async Task HandleAsync(WorkingContextEntity context, JsonNode? data, CancellationToken ct = default)
    {
        var reply = ExtractReplyText(data)
            ?? throw new InvalidOperationException("RespondToUser action requires a text payload");

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ChatMessage,
            Status = ContextFragmentStatus.Active,
            Content = reply,
            Notes = "assistant",
            Importance = 1.0f,
            Confidence = 1.0f,
            Weight = 1.0f,

            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        });

        await eventBus.PublishAsync(this, new DigitalColleagueReplied(reply));
    }

    /// <summary>
    /// Extracts reply text from the data payload. Handles both a plain string value
    /// and an object with a "text" property.
    /// </summary>
    private static string? ExtractReplyText(JsonNode? data)
    {
        if (data is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return data?["text"]?.GetValue<string>();
    }
}
