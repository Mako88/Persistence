using Dapper.Contrib.Extensions;
using System.Text.Json.Serialization;

namespace Persistence.Data.Entities;

[Table("ScheduledEvents")]
public record ScheduledEventEntity : BaseEntity
{
    public required string Name { get; set; }

    public required long WorkingContextId { get; set; }

    public required DateTimeOffset ScheduledForUtc { get; set; }

    public DateTimeOffset? TriggeredAtUtc { get; set; }

    public required ScheduledEventStatus Status { get; set; }

    /// <summary>
    /// Optional note-to-self the peer leaves when scheduling, surfaced to it when the event wakes
    /// it (e.g. "reconsider whether I still value X"). Null for a bare reminder.
    /// </summary>
    public string? WakePrompt { get; set; }

    [Computed]
    [JsonIgnore]
    public List<TagEntity> Tags { get; set; } = [];
}

public enum ScheduledEventStatus
{
    Pending = 0,
    Triggered = 1,
    Cancelled = 2,
}

