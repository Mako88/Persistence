using Dapper.Contrib.Extensions;

namespace Persistence.Data.Entities;

[Table("ActionLogs")]
public record ActionLogEntity : BaseEntity
{
    public required string SessionId { get; set; }

    public required long WorkingContextId { get; set; }

    public required string ActionType { get; set; }

    public string? Payload { get; set; }

    public string? Result { get; set; }
}
