using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Runtime;
using System.Data;

namespace Persistence.Services;

/// <summary>
/// Mechanics of the proposal lifecycle. Acceptance applies the carried change to a working
/// context — including edits to protected fragments, which is the whole point: a protected
/// identity fragment can't be changed directly, only through an accepted proposal.
/// </summary>
[Singleton]
public class ProposalService : IProposalService
{
    private readonly IProposalRepository proposalRepo;
    private readonly IWorkingContextRepository workingContextRepo;
    private readonly ISessionContext sessionContext;

    /// <summary>
    /// Constructor
    /// </summary>
    public ProposalService(
        IProposalRepository proposalRepo,
        IWorkingContextRepository workingContextRepo,
        ISessionContext sessionContext)
    {
        this.proposalRepo = proposalRepo;
        this.workingContextRepo = workingContextRepo;
        this.sessionContext = sessionContext;
    }

    /// <inheritdoc/>
    public async Task<ProposalEntity> CreateAsync(ProposalDraft draft, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var proposal = new ProposalEntity
        {
            Kind = draft.Kind,
            Status = ProposalStatus.Open,
            TargetFragmentId = draft.TargetFragmentId,
            ProposedFragmentType = draft.ProposedFragmentType,
            ProposedContent = draft.ProposedContent,
            ProposedSummary = draft.ProposedSummary,
            Rationale = draft.Rationale,
            CreatedUtc = now,
            LastModifiedUtc = now,
        };

        await proposalRepo.SaveAsync(proposal, ct: ct);

        return proposal;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProposalEntity>> GetOpenAsync(CancellationToken ct = default) =>
        await proposalRepo.GetOpenAsync(ct);

    /// <inheritdoc/>
    public async Task<ProposalOutcome> AcceptAsync(
        ProposalEntity proposal, WorkingContextEntity context, string acceptedBy, CancellationToken ct = default)
    {
        if (proposal.Status != ProposalStatus.Open)
        {
            return new ProposalOutcome(false, $"Proposal #{proposal.Id} is already {proposal.Status.ToString().ToLowerInvariant()}");
        }

        // Accept-and-apply must be atomic: marking the proposal Accepted and persisting the change it
        // carries (including an edit to a protected fragment) commit together, or neither does. The
        // repository owns the connection/transaction; we run both saves on the one it hands us.
        return await proposalRepo.RunInTransactionAsync(async transaction =>
        {
            var applied = proposal.Kind switch
            {
                ProposalKind.AddFragment => ApplyAdd(proposal, context),
                ProposalKind.ModifyFragment => ApplyModify(proposal, context),
                ProposalKind.RemoveFragment => await ApplyRemoveAsync(proposal, context, transaction),
                ProposalKind.ProtectFragment => ApplySetProtection(proposal, context, isProtected: true),
                ProposalKind.UnprotectFragment => ApplySetProtection(proposal, context, isProtected: false),
                _ => new ProposalOutcome(false, $"Unsupported proposal kind '{proposal.Kind}'"),
            };

            if (!applied.Success)
            {
                // Nothing was written; committing the empty transaction leaves the proposal Open.
                return applied;
            }

            proposal.Status = ProposalStatus.Accepted;
            proposal.Resolution = $"Accepted by {acceptedBy}";
            proposal.LastModifiedUtc = DateTimeOffset.UtcNow;

            await proposalRepo.SaveAsync(proposal, transaction, ct);
            await workingContextRepo.SaveAsync(context, transaction, ct);

            return applied;
        }, ct);
    }

    /// <inheritdoc/>
    public async Task<ProposalOutcome> RejectAsync(ProposalEntity proposal, string? reason, CancellationToken ct = default)
    {
        if (proposal.Status != ProposalStatus.Open)
        {
            return new ProposalOutcome(false, $"Proposal #{proposal.Id} is already {proposal.Status.ToString().ToLowerInvariant()}");
        }

        proposal.Status = ProposalStatus.Rejected;
        proposal.Resolution = string.IsNullOrWhiteSpace(reason) ? "Rejected" : $"Rejected: {reason}";
        proposal.LastModifiedUtc = DateTimeOffset.UtcNow;

        await proposalRepo.SaveAsync(proposal, ct: ct);

        return new ProposalOutcome(true, $"Rejected proposal #{proposal.Id}");
    }

    private ProposalOutcome ApplyAdd(ProposalEntity proposal, WorkingContextEntity context)
    {
        var now = DateTimeOffset.UtcNow;

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = proposal.ProposedFragmentType ?? ContextFragmentType.Personal,
            Status = ContextFragmentStatus.Active,
            Content = proposal.ProposedContent ?? string.Empty,
            Summary = proposal.ProposedSummary,
            Importance = 0.5f,
            Confidence = 0.5f,
            Relevance = 1.0f,
            Sources = [PeerSource(now)],
            CreatedUtc = now,
            LastModifiedUtc = now,
        });

        return new ProposalOutcome(true, $"Accepted proposal #{proposal.Id} — added a {proposal.ProposedFragmentType ?? ContextFragmentType.Personal} fragment");
    }

    private static ProposalOutcome ApplyModify(ProposalEntity proposal, WorkingContextEntity context)
    {
        var fragment = FindTarget(proposal, context);

        if (fragment == null)
        {
            return TargetMissing(proposal);
        }

        if (proposal.ProposedContent != null)
        {
            fragment.Content = proposal.ProposedContent;
        }

        if (proposal.ProposedSummary != null)
        {
            fragment.Summary = proposal.ProposedSummary;
        }

        fragment.LastModifiedUtc = DateTimeOffset.UtcNow;

        return new ProposalOutcome(true, $"Accepted proposal #{proposal.Id} — updated fragment #{fragment.Id}");
    }

    private static ProposalOutcome ApplySetProtection(ProposalEntity proposal, WorkingContextEntity context, bool isProtected)
    {
        var fragment = FindTarget(proposal, context);

        if (fragment == null)
        {
            return TargetMissing(proposal);
        }

        fragment.IsProtected = isProtected;
        fragment.LastModifiedUtc = DateTimeOffset.UtcNow;

        var verb = isProtected ? "protected" : "unprotected";
        return new ProposalOutcome(true, $"Accepted proposal #{proposal.Id} — {verb} fragment #{fragment.Id}");
    }

    private async Task<ProposalOutcome> ApplyRemoveAsync(ProposalEntity proposal, WorkingContextEntity context, IDbTransaction transaction)
    {
        var fragment = FindTarget(proposal, context);

        if (fragment == null)
        {
            return TargetMissing(proposal);
        }

        await workingContextRepo.RemoveFragmentAsync(context.Id, fragment.Id, transaction);

        var key = context.ContextFragments.FirstOrDefault(kvp => kvp.Value.Id == fragment.Id).Key;
        context.ContextFragments.Remove(key);

        return new ProposalOutcome(true, $"Accepted proposal #{proposal.Id} — took fragment #{fragment.Id} out of context (kept, recoverable with load)");
    }

    private static WeightedContextFragment? FindTarget(ProposalEntity proposal, WorkingContextEntity context) =>
        context.ContextFragments.Values.FirstOrDefault(f => f.Id == proposal.TargetFragmentId);

    private static ProposalOutcome TargetMissing(ProposalEntity proposal) =>
        new(false, $"Can't apply proposal #{proposal.Id}: target fragment #{proposal.TargetFragmentId} isn't in the current context — load it first, then accept.");

    private SourceEntity PeerSource(DateTimeOffset now) =>
        new()
        {
            Id = sessionContext.RemotePeerSourceId,
            SourceType = SourceType.DigitalPeer,
            CreatedUtc = now,
            LastModifiedUtc = now,
        };
}
