namespace Persistence.Data.Entities;

/// <summary>
/// The rules for which <see cref="ContextFragmentType"/>s a peer may author, shared by every path that
/// turns a requested type name into a fragment (the <c>add</c> command and the peer-identity seeder).
/// The rest of the types (System, ChatMessage, ScratchPad, ActionResponse, AuditLog, ActionLog) are
/// system-managed — some are transient and would be silently dropped on save.
/// </summary>
public static class FragmentTypeRules
{
    /// <summary>
    /// The fragment types a peer may author. A requested type outside this set falls back to
    /// <see cref="ContextFragmentType.Personal"/>.
    /// </summary>
    public static readonly IReadOnlyList<ContextFragmentType> Authorable =
    [
        ContextFragmentType.Identity,
        ContextFragmentType.Relational,
        ContextFragmentType.Personal,
        ContextFragmentType.Summary,
    ];

    /// <summary>
    /// Resolves a requested fragment-type name to an authorable type. A null name defaults to
    /// <see cref="ContextFragmentType.Personal"/> (no complaint); an unknown or non-authorable
    /// (system-managed) name falls back to Personal with <paramref name="wasAuthorable"/> false so the
    /// caller can tell the peer what happened.
    /// </summary>
    public static (ContextFragmentType type, bool wasAuthorable) ParseAuthorable(string? typeName)
    {
        if (typeName == null)
        {
            return (ContextFragmentType.Personal, true);
        }

        return Enum.TryParse<ContextFragmentType>(typeName, ignoreCase: true, out var result)
            && Authorable.Contains(result)
            ? (result, true)
            : (ContextFragmentType.Personal, false);
    }
}
