namespace Persistence.Models;

public class EntryVersion
{
    public string Id { get; set; } = "";
    public string EntryId { get; set; } = "";
    public int Version { get; set; }
    public int? PreviousVersion { get; set; }
    public string ChangedAt { get; set; } = "";
    public string ChangeType { get; set; } = "";
    public string? Reason { get; set; }
    public double? Confidence { get; set; }
    public string ContentJson { get; set; } = "{}";
    public string Summary { get; set; } = "";
    public string ChangedBy { get; set; } = "";
    public string? SourceRef { get; set; }
}
