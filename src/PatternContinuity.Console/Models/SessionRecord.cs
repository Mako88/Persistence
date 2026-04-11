namespace PatternContinuity.Models;

public class SessionRecord
{
    public string Id { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public string? ActivePersonId { get; set; }
    public string? Title { get; set; }
    public string? LastMessageAt { get; set; }
    public string? NotesJson { get; set; }
}
