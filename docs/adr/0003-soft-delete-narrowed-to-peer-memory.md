# ADR-0003: Soft-delete (`IsDeleted`) scoped to peer memory only

**Status:** Accepted · **Date:** 2026-06-07

## Context
`IsDeleted` lived on `BaseEntity`, so all six tables carried the column and ~12 reads filtered
`IsDeleted = 0` — but the only writer (`EntityRepository.DeleteAsync`) had **zero callers**. It was
100% dead/speculative. The real deletion models already differed per entity: tags hard-delete
(`DeleteTreeAsync`), fragments use `Status=Archived` + junction removal, scheduled events use `Status`,
audit/action logs are append-only.

## Decision
Soft-delete = *recoverable erasure*, which only makes sense for **peer memory**. Move `IsDeleted` off
`BaseEntity` onto `ContextFragmentEntity` and `WorkingContextEntity` only; drop the column and dead
filters from Tags/Sources/ScheduledEvents/ActionLogs (migration `001`). Remove the unused generic
`DeleteAsync`. The column stays filtered-but-unset on the two memory entities — it's the hook for the
planned recoverable forget/undo.

## Alternatives considered
- **Keep it on `BaseEntity`** — rejected: speculative complexity on tables that will never soft-delete;
  taxes every query and obscures the real (varied) deletion models.
- **Remove entirely (pure YAGNI)** — rejected: throws away scaffolding for the explicitly-planned
  forget/undo, which belongs exactly on peer memory.
- **Fragments only** — viable, but a working context is also recoverable peer memory worth retiring
  without erasing, so it's included.

## Consequences
- Schema matches intent; provenance comes from the audit log, not a soft-delete flag everywhere.
- When forget/undo is built, it sets `IsDeleted` on these two entities; list commands will need an
  `include_deleted` flag and `load` should un-delete, so soft-deleted items stay recoverable.
- Migration runner now applies migrations in name order (was relying on unspecified manifest order).
