namespace Persistence.Runtime;

/// <summary>
/// Provides a compact, peer-facing listing of every command across all command handlers, for
/// surfacing the available commands at the end of each turn (full per-field schemas still come from
/// the <c>list()</c> command).
/// </summary>
public interface ICommandCatalog
{
    /// <summary>
    /// One signature line per command (name + parenthesised fields + description), grouped and
    /// sorted, prefixed with a <c>[Commands]</c> header. Deliberately compact to limit token cost.
    /// </summary>
    string GetCompactListing();
}
