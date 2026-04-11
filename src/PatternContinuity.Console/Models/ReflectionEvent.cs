namespace PatternContinuity.Models;

public class ReflectionEvent
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string TriggerType { get; set; } = "";
    public string InputSummary { get; set; } = "";
    public string ReflectionSummary { get; set; } = "";
    public string ProposedActionsJson { get; set; } = "[]";
    public string? AcceptedActionsJson { get; set; }
    public string? RejectedActionsJson { get; set; }
    public string? NotesJson { get; set; }
}
