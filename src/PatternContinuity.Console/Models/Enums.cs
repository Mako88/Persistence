namespace PatternContinuity.Models;

public static class LayerType
{
    public const string CoreSelf = "core_self";
    public const string Relational = "relational";
    public const string CurrentConcern = "current_concern";
    public const string Archive = "archive";

    public static readonly string[] All = [CoreSelf, Relational, CurrentConcern, Archive];

    public static bool IsValid(string type) => All.Contains(type);
}

public static class EntryStatus
{
    public const string Active = "active";
    public const string Archived = "archived";
    public const string Superseded = "superseded";
    public const string Deprecated = "deprecated";
    public const string SoftDeleted = "soft_deleted";
}

public static class ChangeType
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Promote = "promote";
    public const string Demote = "demote";
    public const string Protect = "protect";
    public const string Unprotect = "unprotect";
    public const string Consolidate = "consolidate";
    public const string Rollback = "rollback";
    public const string Supersede = "supersede";
}

public static class ChangedBy
{
    public const string Model = "model";
    public const string User = "user";
    public const string System = "system";
    public const string Migration = "migration";
}

public static class SourceType
{
    public const string Reflection = "reflection";
    public const string DirectConversation = "direct_conversation";
    public const string ImportedNote = "imported_note";
    public const string UserConfirmed = "user_confirmed";
    public const string SelfCurated = "self_curated";
    public const string SystemSeed = "system_seed";
}

public static class TriggerType
{
    public const string PostTurn = "post_turn";
    public const string Periodic = "periodic";
    public const string Manual = "manual";
    public const string Consolidation = "consolidation";
    public const string StartupReview = "startup_review";
    public const string ShutdownReview = "shutdown_review";
}

public static class ActionStatus
{
    public const string Proposed = "proposed";
    public const string Executed = "executed";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string RolledBack = "rolled_back";
}

public static class ActionType
{
    public const string GetCoreSelf = "get_core_self";
    public const string ProposeCoreUpdate = "propose_core_self_update";
    public const string CommitCoreUpdate = "commit_core_self_update";
    public const string RollbackCoreSelf = "rollback_core_self";
    public const string GetRelationalLayer = "get_relational_layer";
    public const string UpdateRelationalLayer = "update_relational_layer";
    public const string GetCurrentConcerns = "get_current_concerns";
    public const string UpdateCurrentConcerns = "update_current_concerns";
    public const string DemoteCurrentConcern = "demote_current_concern";
    public const string SearchArchive = "search_archive";
    public const string StoreArchiveEntry = "store_archive_entry";
    public const string PromoteArchiveToCurrent = "promote_archive_to_current";
    public const string ProposeArchiveToCore = "propose_archive_to_core";
    public const string GetRecentChanges = "get_recent_changes";
    public const string GetEntryById = "get_entry_by_id";
    public const string ListActiveLayers = "list_active_layers";
    public const string ReflectOnTurn = "reflect_on_turn";
}
