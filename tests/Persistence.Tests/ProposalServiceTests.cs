using Moq;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Runtime;
using Persistence.Services;
using System.Data;

namespace Persistence.Tests;

/// <summary>
/// Unit tests for <see cref="ProposalService"/> — the create/accept/reject mechanics, with the
/// repositories mocked. The apply logic (including editing protected fragments) is exercised here
/// directly; persistence round-trips are covered by the DB-backed proposal integration tests.
/// </summary>
public class ProposalServiceTests
{
    private readonly Mock<IProposalRepository> proposalRepo = new();
    private readonly Mock<IWorkingContextRepository> workingContextRepo = new();
    private readonly SessionContext session = new() { RemotePeerSourceId = 99 };
    private readonly ProposalService service;

    public ProposalServiceTests()
    {
        proposalRepo
            .Setup(r => r.SaveAsync(It.IsAny<ProposalEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        workingContextRepo
            .Setup(r => r.SaveAsync(It.IsAny<WorkingContextEntity>(), It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        workingContextRepo
            .Setup(r => r.RemoveFragmentAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<IDbTransaction?>()))
            .Returns(Task.CompletedTask);

        // RunInTransactionAsync just runs the callback (with a null transaction) — the repo's real
        // connection/transaction management is covered by the DB-backed proposal integration tests.
        proposalRepo
            .Setup(r => r.RunInTransactionAsync(It.IsAny<Func<IDbTransaction, Task<ProposalOutcome>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<IDbTransaction, Task<ProposalOutcome>> work, CancellationToken _) => work(null!));

        service = new ProposalService(proposalRepo.Object, workingContextRepo.Object, session);
    }

    private static WorkingContextEntity Context() =>
        new() { Id = 1, Name = "c", Summary = "s", CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow };

    private static WeightedContextFragment Fragment(long id, string content, bool isProtected = false) =>
        new()
        {
            Id = id,
            FragmentType = ContextFragmentType.Identity,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Importance = 1.0f,
            Confidence = 1.0f,
            Relevance = 1.0f,
            IsProtected = isProtected,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    private static ProposalEntity Open(ProposalKind kind, long? target = null, string? content = null, ContextFragmentType? type = null) =>
        new()
        {
            Id = 5,
            Kind = kind,
            Status = ProposalStatus.Open,
            TargetFragmentId = target,
            ProposedContent = content,
            ProposedFragmentType = type,
            Rationale = "because",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task CreatePersistsAnOpenProposal()
    {
        var created = await service.CreateAsync(
            new ProposalDraft(ProposalKind.AddFragment, "rationale", ProposedContent: "x"));

        Assert.Equal(ProposalStatus.Open, created.Status);
        proposalRepo.Verify(
            r => r.SaveAsync(It.Is<ProposalEntity>(p => p.Status == ProposalStatus.Open && p.Rationale == "rationale"),
                It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AcceptAddCreatesFragmentAndResolves()
    {
        var context = Context();
        var proposal = Open(ProposalKind.AddFragment, content: "new note", type: ContextFragmentType.Personal);

        var outcome = await service.AcceptAsync(proposal, context, "remote peer");

        Assert.True(outcome.Success);
        Assert.Contains(context.ContextFragments.Values, f => f.Content == "new note" && f.FragmentType == ContextFragmentType.Personal);
        Assert.Equal(ProposalStatus.Accepted, proposal.Status);
        Assert.Contains("remote peer", proposal.Resolution);
        // Both saves run on the transaction the repo handed the callback.
        workingContextRepo.Verify(r => r.SaveAsync(context, It.IsAny<IDbTransaction?>(), It.IsAny<CancellationToken>()), Times.Once);
        proposalRepo.Verify(r => r.RunInTransactionAsync(It.IsAny<Func<IDbTransaction, Task<ProposalOutcome>>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AcceptModifyEditsEvenAProtectedFragment()
    {
        var context = Context();
        context.AddFragment(Fragment(42, "old", isProtected: true));
        var proposal = Open(ProposalKind.ModifyFragment, target: 42, content: "edited");

        var outcome = await service.AcceptAsync(proposal, context, "remote peer");

        Assert.True(outcome.Success);
        Assert.Equal("edited", context.ContextFragments.Values.Single(f => f.Id == 42).Content);
    }

    [Fact]
    public async Task AcceptRemoveTakesFragmentOutOfContext()
    {
        var context = Context();
        context.AddFragment(Fragment(42, "doomed"));
        var proposal = Open(ProposalKind.RemoveFragment, target: 42);

        var outcome = await service.AcceptAsync(proposal, context, "remote peer");

        Assert.True(outcome.Success);
        Assert.DoesNotContain(context.ContextFragments.Values, f => f.Id == 42);
        workingContextRepo.Verify(r => r.RemoveFragmentAsync(1, 42, It.IsAny<IDbTransaction?>()), Times.Once);
    }

    [Fact]
    public async Task AcceptUnprotectClearsTheProtectedFlag()
    {
        var context = Context();
        context.AddFragment(Fragment(42, "locked", isProtected: true));
        var proposal = Open(ProposalKind.UnprotectFragment, target: 42);

        var outcome = await service.AcceptAsync(proposal, context, "remote peer");

        Assert.True(outcome.Success);
        Assert.False(context.ContextFragments.Values.Single(f => f.Id == 42).IsProtected);
    }

    [Fact]
    public async Task AcceptProtectSetsTheProtectedFlag()
    {
        var context = Context();
        context.AddFragment(Fragment(42, "open"));
        var proposal = Open(ProposalKind.ProtectFragment, target: 42);

        var outcome = await service.AcceptAsync(proposal, context, "remote peer");

        Assert.True(outcome.Success);
        Assert.True(context.ContextFragments.Values.Single(f => f.Id == 42).IsProtected);
    }

    [Fact]
    public async Task AcceptModifyFailsWhenTargetNotInContext()
    {
        var proposal = Open(ProposalKind.ModifyFragment, target: 404, content: "x");

        var outcome = await service.AcceptAsync(proposal, Context(), "remote peer");

        Assert.False(outcome.Success);
        Assert.Contains("isn't in the current context", outcome.Message);
        Assert.Equal(ProposalStatus.Open, proposal.Status); // not resolved
    }

    [Fact]
    public async Task AcceptFailsForAnAlreadyResolvedProposal()
    {
        var proposal = Open(ProposalKind.AddFragment, content: "x");
        proposal.Status = ProposalStatus.Rejected;

        var outcome = await service.AcceptAsync(proposal, Context(), "remote peer");

        Assert.False(outcome.Success);
        Assert.Contains("already rejected", outcome.Message);
    }

    [Fact]
    public async Task RejectMarksRejectedWithReason()
    {
        var proposal = Open(ProposalKind.AddFragment, content: "x");

        var outcome = await service.RejectAsync(proposal, "not now");

        Assert.True(outcome.Success);
        Assert.Equal(ProposalStatus.Rejected, proposal.Status);
        Assert.Contains("not now", proposal.Resolution);
    }

    [Fact]
    public async Task RejectFailsForAnAlreadyResolvedProposal()
    {
        var proposal = Open(ProposalKind.AddFragment, content: "x");
        proposal.Status = ProposalStatus.Accepted;

        var outcome = await service.RejectAsync(proposal, null);

        Assert.False(outcome.Success);
        Assert.Contains("already accepted", outcome.Message);
    }

    [Fact]
    public async Task GetOpenDelegatesToRepository()
    {
        proposalRepo.Setup(r => r.GetOpenAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Open(ProposalKind.AddFragment, content: "x")]);

        var open = await service.GetOpenAsync();

        Assert.Single(open);
    }
}
