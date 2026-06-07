using Dapper.Contrib.Extensions;
using System.Text.Json.Serialization;

namespace Persistence.Data.Entities;

[Table("ContextFragments")]
public record ContextFragmentEntity : BaseEntity
{
    public required ContextFragmentType FragmentType { get; set; }

    public required ContextFragmentStatus Status { get; set; }

    public required string Content { get; set; }

    public string? Summary { get; set; }

    public required float Importance { get; set; }

    public required float Confidence { get; set; }

    /// <summary>
    /// Whether or not the remote peer can modify this fragment
    /// </summary>
    public bool IsProtected { get; set; } = false;

    /// <summary>
    /// Soft-delete flag. Fragments are peer memory, so erasure must be recoverable: a deleted
    /// fragment is filtered from reads but kept in the table (the home for the planned forget/undo).
    /// </summary>
    public bool IsDeleted { get; set; } = false;

    [Computed]
    [JsonIgnore]
    public List<SourceEntity> Sources { get; set; } = [];

    [Computed]
    [JsonIgnore]
    public List<TagEntity> Tags { get; set; } = [];
}

public enum ContextFragmentType
{
    System = 0, // May or may not need to be sent as a separate system prompt (depending on provider / model)
    Identity = 1,
    Relational = 2,
    ChatMessage = 3,
    Proposal = 4,
    Summary = 5,
    ScratchPad = 6, // Fragments of type ScratchPad are never saved to the DB - they exist only in the current session and current working context
    Personal = 7, // Anything the remote peer wants to save that doesn't fit in other categories
    ActionResponse = 8, // Fragments of this type are also not saved to the DB, and are only included in the first context sent immediately following action execution
    AuditLog = 9,
    ActionLog = 10,
}

public enum ContextFragmentStatus
{
    Active = 0,
    Archived = 1,
}
