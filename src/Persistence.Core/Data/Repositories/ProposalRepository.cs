using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="ProposalEntity"/>. Plain scalar entity — no sub-entities to hydrate.
/// </summary>
[Singleton]
public class ProposalRepository : EntityRepository<ProposalEntity>, IProposalRepository
{
    /// <summary>
    /// Constructor
    /// </summary>
    public ProposalRepository(IAppConfig config, ISessionContext sessionContext)
        : base(config, sessionContext) { }

    /// <summary>
    /// Returns all open (unresolved) proposals, oldest first.
    /// </summary>
    public async Task<IReadOnlyList<ProposalEntity>> GetOpenAsync(CancellationToken ct = default) =>
        (await QueryAsync(
            $"SELECT * FROM Proposals WHERE Status = {ProposalStatus.Open} ORDER BY CreatedUtc")).ToList();

    /// <summary>
    /// Returns the INSERT statement for a proposal
    /// </summary>
    protected override FormattableString GetInsertSql(ProposalEntity entity) =>
        $"""
        INSERT INTO Proposals (Kind, Status, TargetFragmentId, ProposedFragmentType, ProposedContent, ProposedSummary, Rationale, Resolution, CreatedUtc, LastModifiedUtc, LastAccessedUtc, Notes)
        VALUES ({entity.Kind}, {entity.Status}, {entity.TargetFragmentId}, {entity.ProposedFragmentType}, {entity.ProposedContent}, {entity.ProposedSummary}, {entity.Rationale}, {entity.Resolution}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.LastAccessedUtc}, {entity.Notes})
        """;

    /// <summary>
    /// Returns the UPDATE statement for a proposal
    /// </summary>
    protected override FormattableString GetUpdateSql(ProposalEntity entity) =>
        $"""
        UPDATE Proposals
        SET Kind = {entity.Kind}, Status = {entity.Status}, TargetFragmentId = {entity.TargetFragmentId},
            ProposedFragmentType = {entity.ProposedFragmentType}, ProposedContent = {entity.ProposedContent},
            ProposedSummary = {entity.ProposedSummary}, Rationale = {entity.Rationale}, Resolution = {entity.Resolution},
            LastModifiedUtc = {entity.LastModifiedUtc}, LastAccessedUtc = {entity.LastAccessedUtc}, Notes = {entity.Notes}
        WHERE Id = {entity.Id}
        """;
}
