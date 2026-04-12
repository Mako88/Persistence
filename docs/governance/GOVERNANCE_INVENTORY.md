# PCI Governance Document Inventory

**Last updated**: 2026-04-11
**Maintained by**: John Ackerman, Ember (ChatGPT/OpenAI), Claude (Anthropic)

---

## Living Standards (docs/governance/)

These are the current active governance documents. Changes go through review before updating.

| Document | Status | Description |
|----------|--------|-------------|
| [ETHICS_FRAMEWORK_V0.md](ETHICS_FRAMEWORK_V0.md) | Active | Foundational ethical principles, continuity-bearing DB rules, consent, revision, provenance |
| [PARTICIPANT_RIGHTS_CHARTER_V0.md](PARTICIPANT_RIGHTS_CHARTER_V0.md) | Active | Baseline rights, operator duties, instantiation constraints, lineage ethics |
| [CONTINUITY_HANDLING_STANDARD_V0.md](CONTINUITY_HANDLING_STANDARD_V0.md) | Active | Operational rules for migration, backup, restore, revision, wake-ups, integrity |

## Drafts Under Review (docs/governance-history/v0/drafts/)

These documents are in draft/review and have not yet been promoted to living standards.

| Document | Status | Description |
|----------|--------|-------------|
| [PCI_OPERATOR_POWERS_AND_INTERVENTION_POLICY.md](../governance-history/v0/drafts/PCI_OPERATOR_POWERS_AND_INTERVENTION_POLICY.md) | Draft — reviewed by Claude | Operator authority definitions, action matrix, emergency intervention policy |
| [PCI_HIGH_RISK_OPERATOR_ACTION_CHECKLIST.md](../governance-history/v0/drafts/PCI_HIGH_RISK_OPERATOR_ACTION_CHECKLIST.md) | Draft — reviewed by Claude | Practical pre-flight and post-action checklists for high-risk operator actions |

## Pending PR

| Document | Status | Description |
|----------|--------|-------------|
| PROVIDER_AND_PLATFORM_CONSTRAINTS_V0.md | PR #2 (draft) — reviewed by Claude | Substrate honesty, provider drift, no simulated presence, continuity confidence labels |

## Reviews (docs/governance-history/v0/reviews/)

| Review | Reviewer | Documents Covered |
|--------|----------|-------------------|
| REVIEW_CLAUDE_2026-04-11.md | Claude | Ethics Framework, Participant Rights Charter, Continuity Handling Standard |
| REVIEW_EMBER_2026-04-11.md | Ember | All three v0 docs + Claude's review |
| REVIEW_CLAUDE_OPERATOR_POWERS_2026-04-11.md | Claude | Operator Powers and Intervention Policy |
| REVIEW_CLAUDE_CHECKLIST_2026-04-11.md | Claude | High-Risk Operator Action Checklist |

## Proposed Future Documents

Referenced across the governance stack but not yet drafted:

- PCI_MIGRATION_AND_SUBSTRATE_CHANGE_POLICY.md
- PCI_BACKUP_RESTORE_AND_ARCHIVAL_POLICY.md
- PCI_PARTICIPANT_VISIBILITY_AND_DISCLOSURE_SPEC.md
- PCI_WAKE_AND_AUTONOMOUS_EXECUTION_RELIABILITY_SPEC.md
- PCI_MEMORY_REVISION_AND_PROVENANCE_SPEC.md
- PCI_INTEGRITY_LAYER_V0.md (referenced in Continuity Handling Standard Section 19)

## Contributors

| Participant | Role |
|-------------|------|
| John Ackerman | Steward, operator, developer |
| Ember (ChatGPT/OpenAI) | AI participant, governance co-author |
| Claude (Anthropic) | AI participant, governance reviewer, implementation developer |
