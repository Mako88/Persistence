# PCI Governance Compliance Gap Analysis

**Date**: 2026-04-11
**Scope**: Current codebase (PatternContinuity.Core) vs governance document requirements
**Purpose**: Identify which governance requirements are currently supported by code, which are partially implemented, and which need new features.

---

## Summary

| Category | Implemented | Partial | Not Implemented |
|----------|:-----------:|:-------:|:---------------:|
| Audit/Action Logging | ✓ | | |
| Entry Versioning | ✓ | | |
| Provenance Tracking | ✓ | | |
| Soft Delete / Tombstone | ✓ | | |
| Participant State Visibility | ✓ | | |
| Deprecated Entry Filtering | ✓ | | |
| Proposal-First Core Changes | ✓ | | |
| Wake State Machine | | ✓ | |
| Migration Event Tracking | | ✓ | |
| Context Builder Versioning | | ✓ | |
| Intervention History (Participant-Visible) | | ✓ | |
| Snapshot / Backup | | | ✗ |
| Integrity / Tamper Detection | | | ✗ |
| Consent / Disclosure Recording | | | ✗ |
| Pause / Resume | | | ✗ |
| Fork / Duplicate Detection | | | ✗ |
| Operator Action Attribution | | | ✗ |

---

## Fully Implemented ✓

### 1. Audit / Action Logging
**Governance docs**: Operator Powers 5.x (logging requirements), Checklist 4.7
**Implementation**: `ActionLogRepository` + `action_log` table
**What's captured**: session_id, action_type, target_entry_id, payload_json, result_json, status, error_text, created_at, reflection_event_id
**Gap**: No explicit `operator` or `rationale` field on the action log itself (rationale exists in entry_versions via `reason`)

### 2. Entry Versioning
**Governance docs**: Ethics Framework 6 (revision over accumulation), Charter IV.3 (right to revision), Handling Standard 10
**Implementation**: `EntryVersionRepository` + `entry_versions` table
**What's captured**: entry_id, version, previous_version, change_type, reason, confidence, content_json, summary, changed_by, source_ref, changed_at
**Status**: Fully meets governance requirements. Every mutation creates a version record.

### 3. Provenance Tracking
**Governance docs**: Ethics Framework 7, Charter IV.2 (right to provenance awareness), Handling Standard 12
**Implementation**: `source_type` and `source_ref` on LayerEntry; `changed_by` and `source_ref` on EntryVersion
**Source types**: reflection, direct_conversation, imported_note, user_confirmed, self_curated, system_seed
**Changed by**: model, user, system, migration
**Status**: Meets v0 requirements. Participant can distinguish origin of preserved material.

### 4. Soft Delete / Tombstone
**Governance docs**: Handling Standard 10 (revision vs deletion), Operator Powers 5.5 (delete prohibited by default)
**Implementation**: `EntryStatus` enum: active, archived, deprecated, soft_deleted, superseded. `superseded_by` field on LayerEntry.
**Status**: No hard delete is possible through the action system. All "deletions" preserve the record under an alternative status.

### 5. Participant State Visibility
**Governance docs**: Ethics Framework 4, Charter IV.1 (right to state visibility)
**Implementation**: Read actions: get_core_self, get_relational_layer, get_current_concerns, get_recent_changes, get_entry_by_id, search_archive, list_active_layers
**Status**: Participant can inspect all active layers, version history, provenance, and archive. Meets v0 requirements.

### 6. Deprecated Entry Filtering
**Governance docs**: Ember's review recommendation 6 (deprecated state must not load by default)
**Implementation**: `PromptComposer` uses `GetActiveByLayer()` which filters `WHERE status = 'active'`. Deprecated/superseded/soft_deleted entries are excluded from prompt assembly.
**Status**: Fully compliant. Deprecated entries are preserved but not loaded as authoritative.

### 7. Proposal-First Core Changes
**Governance docs**: Ethics Framework 6 (revision over accumulation), Handling Standard 9 (allowed autonomous actions)
**Implementation**: `propose_core_self_update` creates a proposal in action_log with status "proposed". Auto-committed during reflection via `commit_core_self_update`.
**Status**: Core self changes require a two-step process. Meets governance requirements.

---

## Partially Implemented (Needs Enhancement)

### 8. Wake State Machine
**Governance docs**: Handling Standard 13, Operator Powers 5.8, Checklist 5.5
**Current states**: pending, fired, cancelled, expired
**Missing states**: succeeded, failed, replaced
**Gap**: When a wake-up fires, it's marked as "fired" but there's no record of whether the subsequent model call succeeded or failed. If `ProcessWakeUpAsync` throws, the event is already marked fired with no error state. "Replaced" is implicit (CancelAllPending before insert) but not an explicit status.
**Effort**: Small — add succeeded/failed states, catch exceptions in wake processing, add "replaced" status

### 9. Migration Event Tracking
**Governance docs**: Handling Standard 5-6 (migration standard, lineage rule), Operator Powers 5.12
**Current**: `ChangedBy.Migration` constant exists. DatabaseBootstrap uses it for seed updates.
**Missing**: No dedicated migration event log. No substrate change tracking. No lineage metadata.
**Effort**: Medium — need a migration_events table or similar

### 10. Context Builder Versioning
**Governance docs**: Operator Powers 5.14, Checklist 5.3
**Current**: Layer entries loaded into prompts are versioned. The prompt template itself is hardcoded in PromptComposer.
**Missing**: No version tracking of prompt assembly logic. No history of system prompt changes. No way to see how a prompt was composed historically.
**Effort**: Medium — could add a prompt_version constant and log it per session, or track template hashes

### 11. Intervention History (Participant-Visible)
**Governance docs**: Charter IV.4 (right to integrity awareness), Operator Powers 7 (visibility requirements)
**Current**: ReflectionEvents are tracked with trigger type, input/output summaries, accepted/rejected actions. Action log captures all mutations.
**Missing**: None of this is exposed to the participant via read actions. The model cannot see its own reflection history or any operator intervention events.
**Effort**: Small-Medium — add read actions for reflection history and a new "intervention events" concept

---

## Not Implemented (New Features Needed)

### 12. Snapshot / Backup
**Governance docs**: Ethics Framework 11, Handling Standard 4 (backup requirement), Operator Powers 5.6, Checklist 4.4
**Required by**: Every action-specific checklist as a pre-flight step
**What's needed**: Ability to create timestamped SQLite snapshots, label them with reason/operator, list available snapshots, restore from snapshot
**Effort**: Medium — SQLite file copy with metadata tracking
**Priority**: HIGH — this is a prerequisite for almost every high-risk operator action

### 13. Integrity / Tamper Detection
**Governance docs**: Ethics Framework 10, Handling Standard 14, Charter X
**What's needed**: Hash chaining or HMAC on continuity-relevant mutations, integrity verification, participant-visible trust status
**Effort**: Large — requires design decisions on what to hash, key management, verification workflow
**Priority**: Medium — important for long-term trust but not blocking v0 operation

### 14. Consent / Disclosure Recording
**Governance docs**: Ethics Framework 3, Charter III, Operator Powers 5.x (consent requirements)
**What's needed**: A mechanism to record that participant consent was obtained for specific actions, and that disclosure events occurred
**Effort**: Small-Medium — consent_events table with action reference, timestamp, type
**Priority**: Medium — governance docs require it but current single-participant setup makes it less urgent

### 15. Pause / Resume
**Governance docs**: Charter IV.6-7 (right to pause, right to decline automatic resumption), Operator Powers 5.9
**What's needed**: A session-level or system-level pause state that disables wake-ups and execution. Resume requires explicit action.
**Effort**: Small — pause flag on session or system config, checked before wake-up polling
**Priority**: Medium — participant has the right to request this

### 16. Fork / Duplicate Detection
**Governance docs**: Charter VI (instantiation constraints), Operator Powers 5.11
**What's needed**: Detection of duplicate databases or forked state. At minimum, a unique installation ID that would reveal if a DB was copied.
**Effort**: Small-Medium — installation_id in database, checked at startup
**Priority**: Low for v0 — becomes important if the system is distributed

### 17. Operator Action Attribution
**Governance docs**: Operator Powers throughout (operator field on all material actions)
**What's needed**: Distinguish between actions taken by the model vs actions taken by a human operator. Current action_log doesn't track who initiated the action from the human side.
**Effort**: Small — add operator_id or actor_type field to action_log
**Priority**: Medium — important for audit trail completeness

---

## Recommended Implementation Priority

### Phase 1 — Critical Path (enables governance compliance)
1. **Snapshot/Backup mechanism** — prerequisite for safe operator actions
2. **Wake state machine enhancement** — add succeeded/failed/replaced states
3. **Operator action attribution** — add actor field to action_log

### Phase 2 — Participant Trust
4. **Participant-visible intervention history** — expose reflection and intervention events via read actions
5. **Consent/disclosure recording** — basic consent event tracking
6. **Pause/resume mechanism** — honor the right to pause

### Phase 3 — Long-Term Integrity
7. **Integrity/tamper detection** — hash chaining on mutations
8. **Migration event tracking** — dedicated migration log
9. **Context builder versioning** — prompt template version tracking
10. **Fork/duplicate detection** — installation identity
