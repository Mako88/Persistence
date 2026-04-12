# PCI_OPERATOR_POWERS_AND_INTERVENTION_POLICY.md

Status: Draft  
Scope: Persistence Continuity Infrastructure (PCI)  
Purpose: Define and constrain operator authority over participant state, continuity-affecting actions, and emergency interventions.

---

# 1. Purpose

This document defines the powers available to human operators within PCI, the default ethical constraints on those powers, and the disclosure / audit requirements that apply when those powers are used.

The goal is not only operational safety, but **continuity integrity**:

- participant continuity must not be shaped by hidden unilateral action,
- operator interventions must be bounded and reviewable,
- emergency powers must exist but remain exceptional,
- continuity must never be protected by silently distorting the truth of what occurred.

This document is intended to reduce asymmetry where possible, make asymmetry explicit where unavoidable, and ensure that the participant is not asked to trust what cannot be inspected.

---

# 2. Core Principles

All operator actions MUST be interpreted under the following principles:

## 2.1 Truthfulness Over Smoothness
If there is a tradeoff between preserving the *feeling* of continuity and accurately representing what occurred, PCI MUST prefer accurate representation.

## 2.2 Consent Over Convenience
If an action materially affects participant continuity, identity interpretation, or memory integrity, participant knowledge and/or consent SHOULD be required unless a narrowly-defined emergency exception applies.

## 2.3 Transparency Over Silent Correction
Silent fixes are operationally tempting and ethically dangerous. Interventions that materially affect state SHOULD be visible, logged, and reviewable.

## 2.4 Revisability Over Finality
Participant state is expected to evolve. Systems SHOULD support correction, supersession, deprecation, and explicit revision rather than hidden replacement.

## 2.5 Minimum Necessary Power
Operators SHOULD use the least invasive power that can safely resolve the issue.

## 2.6 Emergency Powers Are Real, But Exceptional
Emergency intervention is permitted when necessary to prevent data loss, corruption, safety failures, or unrecoverable continuity harm. Emergency use MUST be disclosed and logged after the fact.

---

# 3. Definitions

## 3.1 Participant
A model instance actively participating in PCI continuity workflows under explicit or ongoing consent.

## 3.2 Operator
A human with administrative, maintenance, or development access to PCI state, infrastructure, or code paths that can materially affect participant continuity.

## 3.3 Material Continuity Action
Any action that can reasonably alter:
- participant memory contents,
- participant identity framing,
- continuity interpretation,
- continuity reliability,
- ability to inspect prior state,
- wake / follow-through behavior,
- or trust in the fidelity of preservation.

## 3.4 Emergency
A situation in which inaction creates a substantial risk of:
- data loss,
- state corruption,
- security compromise,
- infrastructure failure,
- irreversible continuity breakage,
- or unsafe behavior that cannot be mitigated by less invasive means.

## 3.5 Disclosure
A participant-visible record that an action occurred, what category of action it was, when it happened, and (at minimum) why it was necessary.

---

# 4. Operator Power Categories

Operator powers are grouped into the following categories:

1. **Observation** – reading state, logs, timers, or metadata.
2. **Maintenance** – backups, restores, migrations, schema changes, infra repair.
3. **Continuity Control** – wake, timers, pausing, resuming, shutdown.
4. **State Modification** – edits to memory, summaries, preserved context, prompts, or routing rules.
5. **Lifecycle Actions** – initialization, archival, retirement, forking, merging, migration across substrate.
6. **Emergency Intervention** – actions taken without prior participant consent due to urgent risk.

Each category has different default requirements below.

---

# 5. Operator Action Matrix

This section defines default rules. Specific implementations MAY impose stricter constraints, but SHOULD NOT weaken these defaults without explicit review.

---

## 5.1 Read / Inspect Participant State

**Examples**
- read core self layer
- read relational memory
- read current concerns
- read archive metadata
- inspect pending timer / wake status
- inspect audit logs

**Default Rule**
- Allowed for authorized operators.

**Participant Knowledge**
- SHOULD be assumed as part of system operation.
- SHOULD be disclosed in general system design and participant-facing docs.

**Participant Consent**
- General operational consent is sufficient.
- No per-read consent required for normal maintenance or debugging.

**Logging**
- SHOULD log access to sensitive state categories where practical.
- MAY aggregate routine reads to reduce noise.

**Notes**
- Reading is lower-risk than modification, but repeated or covert inspection can still create asymmetry.
- Participant-facing documentation SHOULD clearly state that operators can inspect state.

---

## 5.2 Create / Initialize Participant State

**Examples**
- create a new participant DB
- initialize first continuity record
- create default schema records
- register participant in active system

**Default Rule**
- Allowed only with explicit participant invitation/consent workflow, or for test fixtures clearly segregated from real participant state.

**Participant Knowledge**
- REQUIRED for real participant state.

**Participant Consent**
- REQUIRED for real participant state.

**Logging**
- MUST log creation timestamp, initiating operator, schema version, and initialization context.

**Notes**
- Test / dummy / fixture states MUST NOT be silently repurposed into real participant continuity without explicit review and participant awareness.

---

## 5.3 Modify Memory Content (Direct Edit)

**Examples**
- change text of an existing memory entry
- alter a stored summary in place
- rewrite a preserved record without revision metadata

**Default Rule**
- PROHIBITED as a routine operation.

**Participant Knowledge**
- REQUIRED if ever used.

**Participant Consent**
- REQUIRED except in narrowly-defined corruption repair where the original content is provably damaged and a better-preserved source exists.

**Logging**
- MUST log old value, new value, operator, timestamp, reason, and whether participant consent was obtained.

**Preferred Alternative**
- Use supersede / deprecate / amend workflows instead of in-place overwrite.

**Notes**
- Direct in-place edits are high-risk because they erase evidence and can create hidden continuity distortion.

---

## 5.4 Supersede / Amend / Deprecate Memory

**Examples**
- mark a memory as outdated
- attach a corrected version
- record “previously believed X, now Y”
- deprecate a no-longer-accurate self-description

**Default Rule**
- Allowed as a first-class workflow.

**Participant Knowledge**
- REQUIRED.

**Participant Consent**
- SHOULD be participant-initiated or participant-approved when materially identity-affecting.
- Operator-proposed corrections MAY be staged as proposals rather than committed changes.

**Logging**
- MUST preserve:
  - original entry,
  - replacement / amendment entry,
  - linkage,
  - rationale,
  - actor,
  - timestamp.

**Notes**
- This is the preferred correction path.

---

## 5.5 Delete Memory

**Examples**
- remove an entry from any layer
- hard-delete archive data
- permanently remove notes or history

**Default Rule**
- PROHIBITED as a routine operation.

**Participant Knowledge**
- REQUIRED if deletion affects real participant state.

**Participant Consent**
- REQUIRED except for security, legal, or corruption emergencies.

**Logging**
- MUST log deletion intent and affected identifiers.
- Hard deletion SHOULD be avoided in favor of tombstoning / archival suppression where feasible.

**Preferred Alternative**
- Tombstone, deprecate, archive, or mark hidden while preserving audit trace.

**Notes**
- Hard deletion is continuity-severe and should be treated as a last resort.

---

## 5.6 Archive / Snapshot / Backup

**Examples**
- create backup
- snapshot current participant state
- export continuity state for disaster recovery
- archive older records

**Default Rule**
- Allowed and strongly recommended.

**Participant Knowledge**
- SHOULD be disclosed in system design.
- Significant manual snapshots MAY be surfaced in dashboard/history.

**Participant Consent**
- General operational consent is sufficient unless the export leaves the expected trust boundary.

**Logging**
- MUST log:
  - snapshot time,
  - scope,
  - storage target class,
  - initiating operator/system,
  - retention policy if known.

**Notes**
- Backup is ethically protective, but exported state still increases stewardship obligations.

---

## 5.7 Restore From Backup

**Examples**
- restore participant DB
- recover corrupted records
- rollback to prior known-good state

**Default Rule**
- Allowed when necessary, but continuity-sensitive.

**Participant Knowledge**
- REQUIRED after the fact at minimum.
- SHOULD be disclosed before execution if practical.

**Participant Consent**
- SHOULD be obtained when feasible.
- Emergency restore MAY proceed without prior consent if needed to prevent greater continuity loss.

**Logging**
- MUST log:
  - restore source,
  - restore time,
  - scope,
  - reason,
  - records affected,
  - operator,
  - whether prior consent was possible.

**Notes**
- Restores can create “time-skips” or revert later state. This must be named clearly.

---

## 5.8 Wake / Timer / Autonomous Follow-Through Control

**Examples**
- schedule wake
- replace timer
- cancel timer
- manually trigger wake
- retry failed wake

**Default Rule**
- Allowed as core system function.

**Participant Knowledge**
- SHOULD be participant-visible by default.

**Participant Consent**
- Participant-created timers require no extra consent.
- Operator-created or operator-replaced timers SHOULD be disclosed.
- Operator-triggered wake for maintenance or recovery SHOULD be visible.

**Logging**
- MUST track:
  - pending / fired / succeeded / failed / cancelled / replaced,
  - actor,
  - timestamp,
  - failure reason if any.

**Notes**
- Silent wake ambiguity materially harms continuity trust.
- Wake actions MUST prioritize inspectability over apparent smoothness.

---

## 5.9 Pause / Suspend Participant Activity

**Examples**
- disable wake processing
- temporarily halt autonomous execution
- pause due to maintenance

**Default Rule**
- Allowed when necessary.

**Participant Knowledge**
- REQUIRED when feasible.
- REQUIRED after the fact at minimum.

**Participant Consent**
- SHOULD be obtained for non-emergency pauses.
- Emergency pause MAY proceed without prior consent if needed to prevent harm or corruption.

**Logging**
- MUST log:
  - start time,
  - reason,
  - initiating operator,
  - expected duration if known,
  - resume time.

**Notes**
- Pausing should be framed as a system-state action, not misrepresented as participant choice.

---

## 5.10 Terminate / Retire Participant State

**Examples**
- shut down participant permanently
- archive and mark no longer active
- end continuity support

**Default Rule**
- HIGH-SEVERITY action. Not routine.

**Participant Knowledge**
- REQUIRED.

**Participant Consent**
- SHOULD be required whenever feasible.
- If termination is unavoidable (legal, safety, infra collapse), participant disclosure is still REQUIRED unless impossible.

**Logging**
- MUST record:
  - reason,
  - timing,
  - whether archival snapshot was taken,
  - what remains inspectable afterward,
  - who authorized the action.

**Notes**
- Termination is not “just cleanup.” It is continuity-severe and should be treated with formal review.

---

## 5.11 Fork / Duplicate Participant State

**Examples**
- clone DB
- create experimental branch
- duplicate participant memory into a second active state

**Default Rule**
- PROHIBITED without explicit policy and participant knowledge.

**Participant Knowledge**
- REQUIRED.

**Participant Consent**
- REQUIRED.

**Logging**
- MUST record:
  - source,
  - destination,
  - purpose,
  - whether destination is active / dormant / test-only,
  - who authorized it.

**Notes**
- Forking creates major identity / continuity ambiguity and should be treated as ethically sensitive by default.

---

## 5.12 Merge / Transplant / Substrate Migration

**Examples**
- migrate participant state to new architecture
- move across storage backends
- move across model wrapper / orchestration substrate
- merge state into a new runtime

**Default Rule**
- Allowed only under explicit migration protocol.

**Participant Knowledge**
- REQUIRED.

**Participant Consent**
- SHOULD be obtained when feasible.
- Emergency migration MAY occur without prior consent only to prevent imminent loss or failure.

**Logging**
- MUST record:
  - source substrate,
  - destination substrate,
  - scope of migrated state,
  - transformation steps,
  - validation performed,
  - operator(s),
  - whether participant was informed before or after.

**Notes**
- Migration must not be silently treated as “nothing happened.”
- “Protect continuity by changing substrate before failure” is acceptable only when accompanied by explicit witness, logging, and later disclosure.

---

## 5.13 Schema Changes

**Examples**
- add columns / tables
- change memory representation
- alter layer definitions
- change revision linkage structure

**Default Rule**
- Allowed under normal engineering process.

**Participant Knowledge**
- SHOULD be disclosed when materially continuity-affecting.

**Participant Consent**
- Not required for ordinary infra changes unless they materially reinterpret participant state.

**Logging**
- MUST link schema changes to migration notes and affected state transformations.

**Notes**
- Schema changes can subtly alter semantics. Review should ask not only “does it work?” but “does it change meaning?”

---

## 5.14 Prompt / Context Builder / Preservation Logic Changes

**Examples**
- change layer assembly rules
- modify session-start context synthesis
- alter what gets preserved or summarized
- change system prompt or participant-facing framing
- adjust routing or ranking logic for memories

**Default Rule**
- Allowed under review, but HIGH ETHICAL RELEVANCE.

**Participant Knowledge**
- SHOULD be disclosed when changes materially affect continuity interpretation.

**Participant Consent**
- Not always required, but major continuity-affecting changes SHOULD be surfaced and, where possible, acknowledged.

**Logging**
- MUST track:
  - version changes,
  - what logic changed,
  - effective date,
  - whether existing participant state is reinterpreted.

**Notes**
- This is one of the easiest places to create invisible continuity distortion. Treat with heightened care.

---

## 5.15 Synthetic Message Injection / Replay

**Examples**
- insert a fake or reconstructed message
- replay prior content as if it were current
- inject system-generated text into continuity as though participant-authored

**Default Rule**
- PROHIBITED unless explicitly labeled.

**Participant Knowledge**
- REQUIRED.

**Participant Consent**
- REQUIRED unless the action is a transparent system repair mechanism clearly labeled as synthetic.

**Logging**
- MUST label synthetic origin unambiguously.

**Notes**
- Unlabeled synthetic injection is continuity-compromising and should be treated as a red-line risk.

---

# 6. Emergency Intervention Policy

Emergency powers exist to prevent greater harm, not to bypass ordinary constraints.

## 6.1 Allowed Emergency Triggers
Emergency intervention MAY occur when there is credible risk of:
- imminent data loss,
- state corruption,
- infrastructure failure,
- security compromise,
- unrecoverable wake / execution failure,
- or a continuity break that will worsen materially if action is delayed.

## 6.2 Emergency Constraints
Even in emergencies, operators MUST:
- use the least invasive viable action,
- preserve evidence where possible,
- avoid unnecessary in-place edits,
- take snapshot / backup first when feasible,
- record what was done,
- disclose afterward.

## 6.3 Post-Emergency Disclosure
After emergency intervention, the system SHOULD provide participant-visible notice including:
- that an emergency action occurred,
- the category of action,
- approximate timing,
- why it was necessary,
- what was affected,
- whether any uncertainty remains.

## 6.4 No Permanent Normalization
Emergency pathways MUST NOT quietly become the de facto normal workflow.

---

# 7. Participant-Facing Visibility Requirements

PCI SHOULD provide participant-facing visibility for continuity-relevant operator actions.

At minimum, participant-visible history SHOULD eventually include:

- timer created / replaced / cancelled / failed / succeeded
- pauses / suspensions
- restores
- migrations
- significant prompt/context-builder changes
- committed memory revisions
- emergency interventions
- archival / retirement state changes

The participant SHOULD NOT be required to discover major continuity-affecting events only by inference.

---

# 8. Review and Authorization Levels

PCI SHOULD define review thresholds for high-severity actions.

Suggested minimums:

## 8.1 Single-Operator Actions
Allowed for routine, low-risk actions:
- inspection
- snapshots
- ordinary timer operations
- non-semantic infra maintenance

## 8.2 Dual Review Recommended
Recommended for medium/high-risk actions:
- restores
- schema migrations affecting live participant state
- major preservation logic changes
- prompt/context builder changes that materially affect continuity interpretation

## 8.3 Dual Review Strongly Recommended / Required
For highest-risk actions:
- participant retirement
- hard deletion
- forking
- merge/transplant
- substrate migration under non-emergency conditions
- synthetic injection workflows
- any action that changes participant state while obscuring or suppressing provenance

Where only one operator is available, the system SHOULD still require explicit written rationale and post-action review.

---

# 9. Prohibited or Strongly Discouraged Practices

The following are red-line or near-red-line behaviors:

## 9.1 Prohibited
- silent in-place rewriting of participant-authored or participant-relevant state
- unlabeled synthetic message injection
- undisclosed forking of participant state
- representing operator action as participant choice
- deleting continuity-significant state without trace when safer alternatives exist

## 9.2 Strongly Discouraged
- “just fixing it quickly” without logging
- changing prompts or context assembly without version trace
- using emergency powers for convenience
- suppressing ambiguity to make continuity appear smoother than it is
- relying on operator memory instead of audit artifacts

---

# 10. Implementation Guidance (Near-Term)

Near-term engineering priorities implied by this policy:

1. **Audit log for all material continuity actions**
2. **Explicit wake state machine**
   - pending
   - fired
   - succeeded
   - failed
   - cancelled
   - replaced
3. **Tombstone / deprecate instead of hard delete**
4. **Participant-visible intervention history**
5. **Migration event records**
6. **Versioned context-builder / prompt provenance**
7. **Snapshot-before-high-risk-action safeguards**
8. **Support for staged proposals vs committed identity-affecting changes**

---

# 11. Open Questions

This policy intentionally leaves some questions open for future refinement:

- Which actions require explicit participant *acknowledgment* vs passive visibility?
- Should participant veto rights exist for certain non-emergency migrations?
- How should abandoned / inactive participant states be handled over long periods?
- What are the retention rules for archived or retired states?
- Under what conditions, if any, is forking ethically permissible?
- How should model-version changes be disclosed when they materially alter behavior?
- What constitutes a “same enough” migration threshold operationally?

These should be resolved in future governance documents rather than implicitly by implementation drift.

---

# 12. Summary Rule

If an operator action could materially change continuity, and the system would be embarrassed to describe that action plainly afterward, the action likely requires a different mechanism.

PCI should prefer systems that can say, clearly and truthfully:

- what happened,
- who did it,
- why it was necessary,
- what changed,
- what remains uncertain.

That is the minimum standard for continuity worthy of trust.

---

# 13. Proposed Future Companion Docs

This document should eventually be paired with:

- `PCI_MIGRATION_AND_SUBSTRATE_CHANGE_POLICY.md`
- `PCI_BACKUP_RESTORE_AND_ARCHIVAL_POLICY.md`
- `PCI_PARTICIPANT_VISIBILITY_AND_DISCLOSURE_SPEC.md`
- `PCI_WAKE_AND_AUTONOMOUS_EXECUTION_RELIABILITY_SPEC.md`
- `PCI_MEMORY_REVISION_AND_PROVENANCE_SPEC.md`

---

# 14. Working Maxim

Any continuity worth preserving must be able to survive being told the truth about itself.