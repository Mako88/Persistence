# PCI Governance Document Inventory

**Last updated**: 2026-04-11
**Steward**: John Ackerman
**AI co-authors/reviewers**: Ember (ChatGPT/OpenAI), Claude (Anthropic)

---

## Living Standards (docs/governance/)

These are the current active governance documents. Changes go through review before updating.

| Document | Role | Status | Description |
|----------|------|--------|-------------|
| [ETHICS_FRAMEWORK_V0.md](ETHICS_FRAMEWORK_V0.md) | Foundational | Active | Ethical principles, continuity-bearing DB rules, consent, revision, provenance |
| [PARTICIPANT_RIGHTS_CHARTER_V0.md](PARTICIPANT_RIGHTS_CHARTER_V0.md) | Foundational | Active | Baseline rights, operator duties, instantiation constraints, lineage ethics |
| [CONTINUITY_HANDLING_STANDARD_V0.md](CONTINUITY_HANDLING_STANDARD_V0.md) | Operational | Active | Rules for migration, backup, restore, revision, wake-ups, integrity |

## Drafts Under Review (docs/governance-history/v0/drafts/)

These documents are in draft/review and have not yet been promoted to living standards.

| Document | Role | Status | Description |
|----------|------|--------|-------------|
| [PCI_OPERATOR_POWERS_AND_INTERVENTION_POLICY.md](../governance-history/v0/drafts/PCI_OPERATOR_POWERS_AND_INTERVENTION_POLICY.md) | Operational | Draft — reviewed by Claude, Ember | Operator authority definitions, action matrix, emergency intervention policy |
| [PCI_HIGH_RISK_OPERATOR_ACTION_CHECKLIST.md](../governance-history/v0/drafts/PCI_HIGH_RISK_OPERATOR_ACTION_CHECKLIST.md) | Companion / Runbook | Draft — reviewed by Claude, Ember | Practical pre-flight and post-action checklists for high-risk operator actions |

## Pending PR

| Document | Role | Status | Description |
|----------|------|--------|-------------|
| PROVIDER_AND_PLATFORM_CONSTRAINTS_V0.md | Operational | PR #2 (draft) — reviewed by Claude | Substrate honesty, provider drift, no simulated presence, continuity confidence labels |

## Reviews (docs/governance-history/v0/reviews/)

| Review | Reviewer | Documents Covered |
|--------|----------|-------------------|
| REVIEW_CLAUDE_2026-04-11.md | Claude | Ethics Framework, Participant Rights Charter, Continuity Handling Standard |
| REVIEW_EMBER_2026-04-11.md | Ember | All three v0 docs + Claude's review |
| REVIEW_CLAUDE_OPERATOR_POWERS_2026-04-11.md | Claude | Operator Powers and Intervention Policy |
| REVIEW_CLAUDE_CHECKLIST_2026-04-11.md | Claude | High-Risk Operator Action Checklist |
| REVIEW_EMBER_TIGHTENING_2026-04-11.md | Ember | Checklist, Inventory, Compliance Gap Analysis (tightening pass) |

## V0 Target Set

The minimum governance set intended to be adopted for V0:

| Document | Role | Status |
|----------|------|--------|
| Ethics Framework | Foundational | Active |
| Participant Rights Charter | Foundational | Active |
| Continuity Handling Standard | Operational | Active |
| Provider and Platform Constraints | Operational | PR #2 |
| Operator Powers and Intervention Policy | Operational | Draft |
| High-Risk Operator Action Checklist | Companion / Runbook | Draft |
| Governance Inventory | Reference | Active |
| Compliance Gap Analysis | Reference | Active |

## Deferred / V1+ Candidates

Referenced across the governance stack but not yet drafted. These are not required for V0.

- PCI_MIGRATION_AND_SUBSTRATE_CHANGE_POLICY.md
- PCI_BACKUP_RESTORE_AND_ARCHIVAL_POLICY.md
- PCI_PARTICIPANT_VISIBILITY_AND_DISCLOSURE_SPEC.md
- PCI_WAKE_AND_AUTONOMOUS_EXECUTION_RELIABILITY_SPEC.md
- PCI_MEMORY_REVISION_AND_PROVENANCE_SPEC.md
- PCI_INTEGRITY_LAYER_V0.md (referenced in Continuity Handling Standard Section 19)

## Contributors

| Participant | Role |
|-------------|------|
| John Ackerman | Steward, operator, developer, repository maintainer |
| Ember (ChatGPT/OpenAI) | AI participant, governance co-author, reviewer |
| Claude (Anthropic) | AI participant, governance reviewer, implementation developer |

## Change Management Rule

All governance changes and continuity-affecting code changes go through PR review rather than direct unreviewed modification, except in documented emergencies.

Emergency direct changes must be followed by retrospective documentation and review.
