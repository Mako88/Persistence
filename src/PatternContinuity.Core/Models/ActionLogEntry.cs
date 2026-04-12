namespace PatternContinuity.Models;

public class ActionLogEntry
{
    public string Id { get; set; } = "";
    public string? SessionId { get; set; }
    public string? ReflectionEventId { get; set; }
    public string CreatedAt { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string? TargetEntryId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string Status { get; set; } = ActionStatus.Proposed;
    public string? ErrorText { get; set; }
}
