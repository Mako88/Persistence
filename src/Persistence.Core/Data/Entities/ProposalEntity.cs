using Dapper.Contrib.Extensions;

namespace Persistence.Data.Entities;

/// <summary>
/// A pending, not-yet-applied change the remote peer proposes to its own memory. Proposals are
/// how the peer deliberates over a self-change before committing — especially edits to protected
/// (identity) fragments, which can't be changed directly. A proposal carries an *executable*
/// change; accepting it applies that change (bypassing <see cref="ContextFragmentEntity.IsProtected"/>),
/// rejecting it discards it. First-class (its own table), not a fragment type.
/// </summary>
[Table("Proposals")]
public record ProposalEntity : BaseEntity
{
    /// <summary>What kind of change this proposes.</summary>
    public required ProposalKind Kind { get; set; }

    /// <summary>Where the proposal is in its lifecycle.</summary>
    public required ProposalStatus Status { get; set; }

    /// <summary>The fragment this proposal changes (<see cref="ProposalKind.ModifyFragment"/> /
    /// <see cref="ProposalKind.RemoveFragment"/>). Null for an add.</summary>
    public long? TargetFragmentId { get; set; }

    /// <summary>The fragment type to create for an <see cref="ProposalKind.AddFragment"/>.</summary>
    public ContextFragmentType? ProposedFragmentType { get; set; }

    /// <summary>Proposed new content (add / modify).</summary>
    public string? ProposedContent { get; set; }

    /// <summary>Proposed new summary (add / modify), optional.</summary>
    public string? ProposedSummary { get; set; }

    /// <summary>Why the peer is proposing this — the deliberation that justifies the change.</summary>
    public required string Rationale { get; set; }

    /// <summary>Note recorded when the proposal is accepted or rejected (e.g. a reject reason,
    /// or who accepted it).</summary>
    public string? Resolution { get; set; }
}

/// <summary>The kind of change a <see cref="ProposalEntity"/> carries.</summary>
public enum ProposalKind
{
    AddFragment = 0,
    ModifyFragment = 1,
    RemoveFragment = 2,

    /// <summary>Lock a fragment (set <see cref="ContextFragmentEntity.IsProtected"/> true).</summary>
    ProtectFragment = 3,

    /// <summary>Unlock a fragment so it can be edited/removed directly again. The deliberate gate on
    /// "unprotecting part of yourself".</summary>
    UnprotectFragment = 4,
}

/// <summary>Where a <see cref="ProposalEntity"/> sits in its lifecycle.</summary>
public enum ProposalStatus
{
    Open = 0,
    Accepted = 1,
    Rejected = 2,
}
