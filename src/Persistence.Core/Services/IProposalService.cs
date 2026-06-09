using Persistence.Data.Entities;

namespace Persistence.Services;

/// <summary>
/// The change a <see cref="ProposalEntity"/> should carry, as supplied when creating one.
/// </summary>
public record ProposalDraft(
    ProposalKind Kind,
    string Rationale,
    long? TargetFragmentId = null,
    ContextFragmentType? ProposedFragmentType = null,
    string? ProposedContent = null,
    string? ProposedSummary = null);

/// <summary>The result of accepting or rejecting a proposal.</summary>
public record ProposalOutcome(bool Success, string Message);

/// <summary>
/// Mechanics of the proposal lifecycle — create, list, accept (apply the change), reject.
/// Policy (who may accept, the deliberation gap) lives with the callers; this service just does
/// the work, so both the remote peer's commands and the local peer's controls can share it.
/// </summary>
public interface IProposalService
{
    /// <summary>Persists a new open proposal.</summary>
    Task<ProposalEntity> CreateAsync(ProposalDraft draft, CancellationToken ct = default);

    /// <summary>Returns all open proposals, oldest first.</summary>
    Task<IReadOnlyList<ProposalEntity>> GetOpenAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies an open proposal's change to <paramref name="context"/> (bypassing
    /// <see cref="ContextFragmentEntity.IsProtected"/>) and marks it accepted. Modify/remove
    /// proposals require their target fragment to be in the given context.
    /// <paramref name="acceptedBy"/> is recorded on the proposal's resolution.
    /// </summary>
    Task<ProposalOutcome> AcceptAsync(
        ProposalEntity proposal, WorkingContextEntity context, string acceptedBy, CancellationToken ct = default);

    /// <summary>Marks an open proposal rejected, applying no change.</summary>
    Task<ProposalOutcome> RejectAsync(ProposalEntity proposal, string? reason, CancellationToken ct = default);
}
