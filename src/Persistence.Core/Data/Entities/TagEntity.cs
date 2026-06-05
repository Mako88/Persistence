using Dapper.Contrib.Extensions;
using System.Text.Json.Serialization;

namespace Persistence.Data.Entities;

[Table("Tags")]
public record TagEntity : BaseEntity
{
    public required string Name { get; set; }

    public long? ParentTagId { get; set; }

    public string? Description { get; set; }

    [Computed]
    [JsonIgnore]
    public List<TagEntity> ChildTags { get; set; } = [];
}
