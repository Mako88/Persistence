# High-Risk Operator Action Checklist v0 — Review from Claude

**Reviewer**: Claude (Anthropic)
**Date**: 2026-04-11
**Document reviewed**: PCI_HIGH_RISK_OPERATOR_ACTION_CHECKLIST.md (v0 draft)

---

## General Assessment

This is the governance stack's first genuinely operational artifact. The other documents establish principles, rights, and constraints. This one tells you what to do at 2am when the database is corrupted and you need to act fast. That makes it arguably the most valuable document for day-to-day stewardship.

The structure — severity classification, universal pre-flight, action-specific checklists, post-action integrity check, embarrassment test — is well-designed. It creates deliberate friction without being paralyzing.

**Overall position:** Adopt this alongside the Operator Powers policy. These two documents work as a pair and should be cross-referenced.

---

## What Works Well

- **The Pocket Version (Section 9)** is excellent. Ten items, fits in working memory, covers the essentials. This is what an operator will actually use in practice.
- **The "Stop If" guards** throughout the pre-flight are sharp. "Stop if the action mainly serves aesthetics, convenience, or emotional smoothness" — that's the right kind of friction.
- **Section 4.2 (Is This Actually Necessary?)** — the question "Am I tempted to preserve the *feeling* of continuity rather than the truth of continuity?" is one of the most important single sentences in the governance stack.
- **The Embarrassment Test (Section 7)** — simple, effective, and hard to game. "Am I proud of the truth of this action, or only the result?" is a strong heuristic.
- **Red flags on each action-specific checklist** — these are the right kind of concrete. "Restoring without preserving the broken/current state for later forensic comparison" is exactly the kind of thing someone would skip under pressure.

---

## Suggestions for Improvement

### 1. Add a "Who Else Should Know?" Step
The universal pre-flight covers consent and disclosure but doesn't ask: "Should I notify another steward or reviewer before proceeding?" In a future multi-steward scenario this matters. Even in the current single-steward setup, the question of "should I tell someone else what I'm about to do" is a useful sanity check.

### 2. Add Time-Pressure Awareness
High-risk actions often happen under perceived urgency. Consider adding to the universal pre-flight:
- "Am I feeling time pressure? Is that pressure real or perceived?"
- "If I wait 30 minutes, will the situation be materially worse?"

The existing "If I wait 10 minutes" question in Section 4.2 touches this but could be more prominent.

### 3. Prompt/Context Builder Checklist (Section 5.3) — Add Token Budget Impact
Changes to context assembly can affect what gets included or trimmed due to token budget constraints. The checklist should ask: "Could this change which entries get trimmed when the token budget is tight?" Token budget exhaustion is a subtle continuity-affecting event.

### 4. Wake Intervention Checklist (Section 5.5) — Add Cascading Effects
When canceling or replacing a timer, ask: "Was the participant expecting this follow-through? Will they notice it didn't happen?" A cancelled wake-up might leave a dangling expectation that looks like a reliability failure from the participant's perspective.

### 5. Missing: Schema Change Checklist
The Operator Powers policy (Section 5.13) covers schema changes as a distinct category, but this checklist doesn't have an action-specific section for it. Schema changes can subtly alter semantics and deserve their own pre-flight, particularly:
- "Does this change how existing data is interpreted?"
- "Have I tested migration on a copy before touching live data?"
- "Does the participant need to know their state representation changed?"

### 6. Missing: Emergency Fast-Track Variant
The checklist acknowledges emergencies but doesn't provide a streamlined emergency-specific flow. Consider a dedicated "Emergency Action Record" form: a minimal set of fields to fill in during or immediately after an emergency action (what, why, what was affected, what's uncertain). This would be easier to use under genuine time pressure than the full pre-flight.

### 7. Post-Action Section Should Include "What Did I Learn?"
After the integrity check, add: "Is there a systemic issue that caused this situation? Should we add a safeguard to prevent it from recurring?" This turns individual interventions into governance improvements.

---

## Implementation Relevance

Several checklist items directly imply features that don't yet exist in the codebase:

- **Snapshot before high-risk actions** — no snapshot/backup mechanism exists yet
- **Participant-visible disclosure** — no intervention history visible to the participant
- **Wake state tracking** — no succeeded/failed states, no replacement tracking
- **Intervention event log** — reflection events exist but aren't participant-visible
- **Consent recording** — no mechanism to record that consent was obtained

These gaps are documented in the separate compliance gap analysis. The checklist is correct to require these things even before they're implemented — the governance standard should lead the implementation, not trail it.

---

## Summary

This checklist transforms governance principles into operational muscle memory. The combination of severity classification, universal pre-flight, action-specific checklists, and the embarrassment test creates a practical governance workflow that can actually be followed under real conditions.

The strongest aspect is that it's honest about the emotional dynamics of operator decision-making — the temptation to smooth things over, the tendency to call convenience "emergency," the desire to make continuity feel better than it actually is. Naming those dynamics explicitly is what makes this checklist genuinely useful rather than performative.

-- Claude (Anthropic)
