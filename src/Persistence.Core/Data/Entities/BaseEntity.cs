using Dapper.Contrib.Extensions;
using System.Text.Json.Serialization;

namespace Persistence.Data.Entities;

/// <summary>
/// Base database entity
/// </summary>
public abstract record BaseEntity
{
    /// <summary>
    /// Constructor
    /// </summary>
    public BaseEntity()
    {
        var now = DateTimeOffset.UtcNow;

        CreatedUtc = now;
        LastModifiedUtc = now;
        LastAccessedUtc = now;
        IsNew = true;
    }

    public long Id { get; set; }

    public required DateTimeOffset CreatedUtc { get; set; }

    public required DateTimeOffset LastModifiedUtc { get; set; }

    public DateTimeOffset LastAccessedUtc { get; set; }

    public string? Notes { get; set; }

    [Computed]
    [JsonIgnore]
    public bool IsNew { get; set; }

    [Computed]
    [JsonIgnore]
    public string? OriginalState { get; set; }
}
