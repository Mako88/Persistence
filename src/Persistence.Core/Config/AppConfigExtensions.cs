namespace Persistence.Config;

/// <summary>
/// Helpers for resolving string-typed config values to their enums in one place, so callers don't
/// each re-implement the parse + default.
/// </summary>
public static class AppConfigExtensions
{
    /// <summary>
    /// Resolves <see cref="IAppConfig.ProposalApproval"/> to a <see cref="ProposalApproval"/>,
    /// defaulting to <see cref="ProposalApproval.Self"/> for an unset or unrecognised value.
    /// </summary>
    public static ProposalApproval ResolvedProposalApproval(this IAppConfig config) =>
        Enum.TryParse<ProposalApproval>(config.ProposalApproval, ignoreCase: true, out var result)
            ? result
            : ProposalApproval.Self;
}
