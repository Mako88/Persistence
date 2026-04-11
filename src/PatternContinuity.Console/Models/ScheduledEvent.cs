namespace PatternContinuity.Models;

public class ScheduledEvent
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string ScheduledFor { get; set; } = "";
    public string Status { get; set; } = ScheduledEventStatus.Pending;
    public string Reason { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string? FiredAt { get; set; }
    public int AutonomousDepth { get; set; }
}

public static class ScheduledEventStatus
{
    public const string Pending = "pending";
    public const string Fired = "fired";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

public static class ScheduledEventType
{
    public const string WakeUp = "wake_up";
}
