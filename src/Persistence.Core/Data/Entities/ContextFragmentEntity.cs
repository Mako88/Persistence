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

    /// <summary>
    /// For a <see cref="ContextFragmentType.ChatMessage"/> in a multi-peer room: who the message is
    /// directed at. <c>null</c> means a broadcast to the room (or a plain human↔peer message);
    /// a participant name (matching a source <see cref="SourceEntity.Name"/>) means it is addressed
    /// to that participant. Lets a peer orient — "addressed to me" vs "overheard" — without inferring
    /// it from prose. Unused (left null) by non-ChatMessage fragment types. See ADR-0008.
    /// </summary>
    public string? AddressedTo { get; set; }

    /// <summary>
    /// For a <see cref="ContextFragmentType.ChatMessage"/> in a multi-peer room: the identity of the
    /// <em>utterance</em>, as opposed to <see cref="BaseEntity.Id"/>, which identifies this store's row.
    /// Minted once as a GUID by the peer that said it and carried unchanged through every relay, so the
    /// same thing said once has the same id in every peer's store — the referent a reply, a dedupe, or a
    /// stored delivery can point at. Originator-minted rather than relayer-minted: a relayer would give
    /// one utterance a different id per hop, which is exactly what makes it unusable as identity.
    /// <c>null</c> for anything that isn't a room message. See ADR-0008.
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// How far <em>this copy</em> has travelled: 0 as said, +1 for each peer-to-peer relay hop without a
    /// human speaking. Distinct in kind from <see cref="MessageId"/> — the id is constant per utterance,
    /// while the depth is per delivery path (A→B is 1 and A→B→C is 2 for the one utterance). Persisted
    /// on the message rather than carried on the request so a message at rest knows its own path, which
    /// is what ADR-0008 Phase 4's stored, asynchronous delivery needs. <c>null</c> off the room path.
    /// </summary>
    public int? RelayDepth { get; set; }

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
    // 4 was Proposal — now a first-class entity (the Proposals table), not a fragment type.
    Summary = 5,
    ScratchPad = 6, // Fragments of type ScratchPad are never saved to the DB - they exist only in the current session and current working context
    Personal = 7, // Anything the remote peer wants to save that doesn't fit in other categories
    ActionResponse = 8, // Command/tool results. Persisted (so research/tool output survives across turns); kept lean by the raw-context decay, which archives old ones.
    AuditLog = 9,
    ActionLog = 10,
    Thought = 11, // The peer's open reasoning (a <think> block). Persisted so recent thinking survives across turns; kept to a rolling window by the thought decay, which archives older ones (detached, still searchable/restorable). System-managed, not peer-authorable.
    WorkingNote = 12, // A single pinned "where I am / what's next" note, upserted via the note() command (one per context). Persisted; never auto-archived. System-managed (not set via add).
}

public enum ContextFragmentStatus
{
    Active = 0,
    Archived = 1,
}