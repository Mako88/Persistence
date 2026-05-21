using Dapper.Contrib.Extensions;

namespace Persistence.Data.Entities;

[Table("Sources")]
public record SourceEntity : BaseEntity
{
    public required SourceType SourceType { get; set; }

    public string? Name { get; set; }
}

public enum SourceType
{
    System = 0,
    DigitalColleague = 1,
    PhysicalColleague = 2,
    DerivedFromFragments = 3,
}
