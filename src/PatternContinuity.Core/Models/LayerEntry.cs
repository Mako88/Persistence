namespace Persistence.Models;

public class LayerEntry
{
    public string Id { get; set; } = "";
    public string LayerType { get; set; } = "";
    public string? RelationshipScope { get; set; }
    public string Status { get; set; } = EntryStatus.Active;
    public string? Key { get; set; }
    public string Summary { get; set; } = "";
    public string ContentJson { get; set; } = "{}";
    public double Salience { get; set; } = 0.5;
    public double Importance { get; set; } = 0.5;
    public double? Confidence { get; set; }
    public string? SourceType { get; set; }
    public string? SourceRef { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? LastAccessedAt { get; set; }
    public int Version { get; set; } = 1;
    public int IsProtected { get; set; }
    public int IsSystemAnchor { get; set; }
    public string? SupersededBy { get; set; }
}
