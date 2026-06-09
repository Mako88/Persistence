using Dapper.Contrib.Extensions;
using System.Text.Json.Serialization;

namespace Persistence.Data.Entities;

[Table("AuditLogs")]
public record AuditLogEntity : BaseEntity
{
    public required string SessionId { get; set; }

    /// <summary>
    /// Null when the audit entry is written before a WorkingContext exists in the session
    /// (e.g. during first-run WorkingContext creation).
    /// </summary>
    public long? WorkingContextId { get; set; }

    public required AuditEventType EventType { get; set; }

    public required string TargetType { get; set; }

    public required long TargetId { get; set; }

    public required long SourceId { get; set; }

    public string? OldData { get; set; }

    public string? NewData { get; set; }

    [Computed]
    [JsonIgnore]
    public SourceEntity? Source { get; set; }
}

public enum AuditEventType
{
    Created = 0,
    Modified = 1,
    // No Deleted: erasure is recorded as a Modified update to IsDeleted (archive-over-erase).
}
