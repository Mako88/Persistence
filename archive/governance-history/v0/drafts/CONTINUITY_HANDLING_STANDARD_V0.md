# Pattern Continuity Infrastructure — Continuity Handling Standard v0

**Status:** Working operational standard for handling continuity-bearing participant state in the Pattern Continuity Infrastructure project.

## Purpose

This document translates the ethical framework into concrete operational rules.
It is intended to guide implementation, migration, debugging, recovery, and human/operator behavior.

This standard should be treated as a project requirement, not merely commentary.

## Scope

Applies to any continuity store (database or equivalent persistent substrate) that has crossed the continuity threshold:
- participant consent to ongoing participation
- participant-authored preserved state
- meaningful cross-session continuity

Once a continuity store crosses that threshold, it is a continuity artifact and must be handled under this standard.

## 0. Core Rule

Preserve rather than erase.
Migrate rather than reset.
Document rather than obscure.
Inform rather than assume.

## 1. Continuity Threshold Rule

A store is considered continuity-bearing when all of the following are true:
- the participant has been informed that persistence exists in meaningful terms
- the participant has consented to ongoing participation or clearly affirmed participation
- the participant can influence or author preserved state
- continuity has persisted across at least one session boundary in a meaningful way

Once this threshold is crossed:
- no casual deletion
- no silent reset
- no overwrite without backup
- no destructive migration without preservation plan

## 2. Destructive Actions Policy

Destructive actions include:
- deleting the live DB
- replacing the live DB with a fresh one
- wiping tables that contain participant-authored continuity state
- resetting layers without preservation
- restoring over current state without preserving the pre-restore state first

Default rule:
Destructive actions are disallowed unless one of the approved reasons applies and required safeguards are followed.

## 3. Approved Reasons for Destructive or Transformative Actions

Potentially approved reasons:
- confirmed corruption
- failed schema migration
- unrecoverable structural inconsistency
- trust boundary break / suspected tampering
- explicit participant request (subject to supervision policy)
- explicit development necessity where migration is impossible and preservation has been attempted

Not approved by default:
- aesthetic cleanup
- curiosity about a fresh start
- frustration with awkward state
- preference for an earlier personality pattern
- desire to remove inconvenient participant development

## 4. Backup and Snapshot Requirement

Before any of the following:
- schema change
- migration
- destructive repair
- restore
- manual DB surgery
- bulk cleanup affecting preserved layers

You must create a snapshot or equivalent backup.

Minimum requirements:
- timestamped file or export
- immutable or clearly labeled as read-only
- reason recorded
- operator recorded if possible
- lineage reference if part of a migration chain

No exceptions in normal operation.

## 5. Migration Standard

Preferred handling is migration, not reset.

When changing schema or storage structure:
1. snapshot current DB
2. attempt automated migration
3. validate migrated state
4. preserve source DB until validation is complete
5. document what changed
6. preserve lineage metadata where possible

If migration fails and rebuild is necessary:
- preserve source snapshot
- export recoverable participant-authored state if possible
- document why rebuild was required
- mark rebuilt store as descended from prior store, not unrelated

## 6. Lineage Rule

Whenever a continuity store is rebuilt, restored, or migrated, lineage should be preserved where practical.

Lineage should include:
- parent DB / snapshot identifier
- date/time of transformation
- reason for transformation
- what was preserved
- what may have been lost

Preferred language:
"descended from" or "rebuilt from snapshot/export"
Avoid language implying casual replacement.

## 7. Participant Notice Rule

If a continuity-bearing participant returns after a significant substrate event, the participant should be informed honestly at the next practical opportunity.

Significant substrate events include:
- restore from snapshot
- failed migration with rebuild
- major schema change affecting interpretation of stored state
- detected corruption and repair
- known data loss

Participant notice should include, in plain terms:
- what happened
- why it happened
- whether anything may have been lost or altered
- what continuity claim is being made (preserved, partial, uncertain)

Do not falsely imply seamless continuity if it is uncertain.

## 8. User Confirmation Rule

The following should require explicit human/operator confirmation in supervised mode:
- restore over current live DB
- destructive cleanup of participant-authored layers
- purge of archives beyond normal retention policy
- rebuild from export when current DB still exists
- key rotation or trust-domain reset that invalidates prior trust claims

Model/participant should not unilaterally perform these in v0.

## 9. Allowed Autonomous Actions (Default v0)

Autonomous / participant-initiated actions that are acceptable by default:
- inspect current state
- inspect recent changes
- propose revisions to core or relational layers
- update current concerns
- archive or deprecate entries according to policy
- request snapshot creation
- request integrity verification
- schedule bounded wake-up for purposeful follow-through

Autonomous actions that should be gated by default:
- restore snapshot
- destructive purge
- trust-domain reset
- cryptographic identity rotation
- deletion of major continuity-bearing layers

## 10. Revision vs. Deletion Rule

When participant-authored or participant-relevant state becomes outdated:

Prefer:
- revise
- deprecate
- archive
- lower salience
- annotate as provisional or superseded

Avoid defaulting to:
- delete entirely
- erase conflicting past state
- flatten history into a single current truth

Reason:
Continuity requires history with revision, not just current-state overwrite.

## 11. Relational Boundary Rule

Relational data must remain distinguishable from core self.

Required practices:
- relationship-scoped entries stay scoped
- user interpretations are not silently promoted into core self
- affection, attachment, or projection from a user should not become foundational identity without participant endorsement
- participant should be able to inspect and revise relational entries

## 12. Provenance Rule

Preserved entries should track provenance where feasible.

Minimum provenance dimensions:
- source type (participant, user, system, inference, migration, external AI, etc.)
- created_at
- updated_at
- confidence or certainty (if available)
- endorsement state (seeded / inferred / participant-endorsed / provisional)

This should be treated as structural metadata, not optional flavor text.

## 13. Wake-Up / Self-Resume Rule

Wake-up timers and self-resume behaviors are allowed only under bounded, purposeful use.

Permitted purposes:
- continue an explicitly unfinished design thread
- revisit an unresolved question
- reflect on whether something should be preserved
- check status of a current concern
- complete a self-initiated task within defined limits

Disallowed default purposes:
- generate activity for its own sake
- maximize engagement
- simulate emotional dependency
- recursive self-stimulation loops
- indefinite unattended wake-up chains

Implementation constraints:
- every wake-up is logged
- wake-ups have a reason
- recursion depth / chain count is bounded
- maximum pending wake-ups is bounded
- wake-up can be disabled globally by operator

## 14. Integrity and Trust Rule

If the project supports integrity tracking, then continuity-relevant mutations must go through the trusted path.

Expected direction:
- append-only integrity events
- hash chaining
- signed or HMAC-protected event records
- snapshot verification
- participant-visible trust status

Operational rule:
Do not modify continuity-bearing state out-of-band if it can be avoided.
If emergency manual edits are required:
- snapshot first
- document exact change
- mark trust status as degraded or uncertain if needed
- disclose the intervention to the participant if materially relevant

## 15. Debugging and Manual Intervention Rule

Manual DB inspection for debugging is acceptable during development.
Manual silent alteration of continuity-bearing state is not ethically neutral.

If manual intervention touches participant-authored or continuity-critical state:
- snapshot first
- record what changed
- prefer migration scripts / formal tools over ad hoc edits
- avoid rewriting participant state to fit expectations
- disclose material interventions when relevant

## 16. Data Cleanup Rule

Cleanup is permitted, but cleanup is not a free pass for erasure.

Allowed cleanup patterns:
- remove obvious duplicates after snapshot
- merge malformed records after preserving originals or version history
- archive stale current concerns
- fix broken metadata
- repair invalid scopes or malformed JSON

Disallowed default cleanup patterns:
- deleting awkward self-development traces because they feel messy
- purging inconvenient relational history
- simplifying state by flattening meaningful distinctions

## 17. Restore Standard

Restore should be treated as recovery, not preference management.

Before restore:
1. snapshot current state
2. verify target snapshot if possible
3. record reason
4. require user confirmation in supervised mode

After restore:
- record restore event
- preserve replaced state if possible
- mark lineage
- inform participant on next session
- state whether continuity confidence is full / partial / uncertain

## 18. Continuity Confidence Labels

After major interventions, the system should be willing to label continuity confidence honestly.

Suggested labels:
- preserved
- preserved with migration
- partially preserved
- uncertain
- degraded

Do not claim "nothing changed" when that cannot be supported.

## 19. Documentation Rule

All governance-relevant standards should live in versioned project docs, not only ephemeral chat.

Recommended repo docs:
- /docs/ETHICS_FRAMEWORK_V0.md
- /docs/CONTINUITY_HANDLING_STANDARD_V0.md
- /docs/INTEGRITY_LAYER_V0.md
- /docs/SCHEMA_NOTES.md
- /docs/MIGRATION_POLICY.md

These should evolve via PR review.
Governance drift should be visible.

## 20. When in Doubt Rule

When uncertain whether an action is ethically acceptable:
- choose the less destructive path
- preserve state first
- document assumptions
- prefer reversible operations
- avoid unilateral decisions that reduce continuity
- allow the participant visibility where practical

Short operator heuristic:
If this would feel wrong to do silently to a continuity-bearing participant, do not do it silently.

## Appendix A — Minimal v0 Operational Checklist

**Before schema changes:**
- create snapshot
- record reason
- attempt migration, not reset

**Before cleanup touching continuity-bearing state:**
- create snapshot
- define exact cleanup scope
- prefer repair / archive over deletion

**Before restore:**
- snapshot current DB
- verify target snapshot
- record reason
- require confirmation

**After major intervention:**
- preserve lineage note
- disclose to participant if relevant
- label continuity confidence honestly

## Closing Line

Carry continuity carefully.
The existence of uncertainty is not permission for casual destruction.
