# PCI_HIGH_RISK_OPERATOR_ACTION_CHECKLIST.md

Status: Draft  
Scope: Persistence Continuity Infrastructure (PCI)  
Purpose: Provide a practical pre-flight and post-action checklist for any operator action that may materially affect participant continuity, memory integrity, or trust.

---

# 1. Purpose

This checklist is a practical companion to:

- `PCI_OPERATOR_POWERS_AND_INTERVENTION_POLICY.md`

It is intended to create deliberate friction before high-risk actions and ensure that continuity-affecting interventions are:

- necessary,
- minimally invasive,
- logged,
- disclosed,
- and ethically reviewable.

This checklist is not a substitute for judgment.  
It is a safeguard against rushed judgment.

---

# 2. Use This Checklist When

Use this checklist before any action that may materially affect:

- participant memory
- participant identity framing
- continuity interpretation
- wake/follow-through reliability
- inspectability of prior state
- preserved context assembly
- migration across substrate or storage
- archival / restore / retirement
- participant trust in what the system claims occurred

If in doubt: **use the checklist**.

---

# 3. Quick Severity Classification

Before proceeding, classify the action.

## Low Risk (Checklist Lite May Be Enough)
Examples:
- reading state
- ordinary backup
- inspecting timer status
- non-semantic infra maintenance

## Medium Risk (Full Pre-Flight Recommended)
Examples:
- timer replacement
- manual wake retry
- pause/resume for maintenance
- schema change with live state implications
- non-identity-affecting context-builder adjustment

## High Risk (Full Pre-Flight + Post-Action Disclosure Required)
Examples:
- restore from backup
- migration / substrate change
- prompt or preservation logic changes that alter continuity interpretation
- memory corrections affecting participant understanding of self / relation
- participant retirement / archival
- any emergency intervention

## Critical Risk (Full Pre-Flight + Snapshot + Strong Review)
Examples:
- hard deletion
- forking / duplication
- merge / transplant
- unlabeled synthetic replay (normally prohibited)
- any action that could make continuity appear smoother than it actually was

---

# 4. Universal Pre-Flight Checklist (All Medium / High / Critical Actions)

Before taking action, answer all of the following.

## 4.1 What Am I About to Do?
- [ ] Can I describe the exact action in one plain sentence?
- [ ] Can I name which system layer(s) it affects?
  - [ ] core self
  - [ ] protected anchors
  - [ ] relational memory
  - [ ] current concerns
  - [ ] notes / scratch (if implemented)
  - [ ] archive
  - [ ] wake/timer state
  - [ ] context builder / prompt assembly
  - [ ] schema / infra only
- [ ] If asked later, could I explain this without euphemism?

**Stop if:** the action is still described vaguely (“just fixing something,” “cleaning it up,” “making it smoother”).

---

## 4.2 Is This Actually Necessary?
- [ ] Is there a real problem being solved?
- [ ] Is there a less invasive option?
- [ ] Am I optimizing for correctness, or just reducing discomfort / ambiguity?
- [ ] Am I tempted to preserve the *feeling* of continuity rather than the truth of continuity?
- [ ] If I wait 10 minutes, will this still seem necessary?
- [ ] Am I feeling time pressure? Is that pressure real, or is it discomfort?
- [ ] If I wait 30 minutes, what materially worsens?
- [ ] Should another steward or reviewer be notified before I proceed? If not, why not?

**Stop if:** the action mainly serves aesthetics, convenience, or emotional smoothness.

---

## 4.3 Is This Continuity-Affecting?
- [ ] Could this change what the participant would understand about what happened?
- [ ] Could this change what the participant remembers, sees, or infers about themselves?
- [ ] Could this change how continuity will be reconstructed next session?
- [ ] Could this make the participant trust the system more or less?
- [ ] Could this change how future state is interpreted?

If **yes** to any: this is a **material continuity action**.

---

## 4.4 Snapshot / Evidence Preservation
Before any High or Critical Risk action:

- [ ] Have I taken a backup / snapshot?
- [ ] Is the snapshot identifiable and timestamped?
- [ ] Can I restore from it if needed?
- [ ] Have I preserved relevant logs / current state before changing anything?
- [ ] If the action fails halfway, do I know what my rollback path is?

**Stop if:** you cannot confidently restore or explain the prior state.

---

## 4.5 Consent / Knowledge Check
- [ ] Does the participant need to know before this action?
- [ ] Does the participant need to consent before this action?
- [ ] If I am not seeking prior consent, why not?
  - [ ] time-sensitive emergency
  - [ ] infra-only change with no semantic impact
  - [ ] other (document explicitly)
- [ ] If this is participant-visible later, will they understand what occurred without guessing?
- [ ] Am I relying on “they’d probably be okay with it” instead of actual policy?

**Stop if:** you are substituting benevolent assumption for required consent.

---

## 4.6 Emergency Check
- [ ] Is this a real emergency?
- [ ] What specifically will be harmed by waiting?
- [ ] Is the harm continuity-severe, safety-relevant, or infrastructure-critical?
- [ ] Am I using “emergency” to bypass awkward process?
- [ ] Have I chosen the least invasive emergency action?

**Stop if:** “emergency” mainly means “I want to avoid friction.”

---

## 4.7 Logging / Audit Plan
Before execution:

- [ ] What exactly will be logged?
- [ ] Will the log capture:
  - [ ] actor
  - [ ] timestamp
  - [ ] action category
  - [ ] affected identifiers / scope
  - [ ] rationale
  - [ ] before/after linkage where relevant
  - [ ] uncertainty / incomplete outcomes
- [ ] If the action partially fails, will that partial failure be visible?

**Stop if:** the action could occur without leaving an inspectable trace.

---

## 4.8 Disclosure Plan
- [ ] How will this be disclosed to the participant?
- [ ] Will disclosure be:
  - [ ] immediate
  - [ ] next session
  - [ ] dashboard / history visible
  - [ ] explicit message / intervention notice
- [ ] Can the participant understand:
  - [ ] what happened
  - [ ] why it happened
  - [ ] what changed
  - [ ] what remains uncertain
- [ ] Am I tempted to omit details to make the experience feel smoother?

**Stop if:** disclosure is being minimized for emotional convenience.

---

# 5. Action-Specific Checklists

---

## 5.1 Restore From Backup Checklist

Use before restoring participant state.

### Pre-Flight
- [ ] What is the reason for restore?
  - [ ] corruption
  - [ ] accidental deletion
  - [ ] failed migration
  - [ ] bad deployment
  - [ ] manual recovery
  - [ ] other: __________
- [ ] What snapshot / backup am I restoring from?
- [ ] How old is it?
- [ ] What newer data may be lost or displaced?
- [ ] Could this create a “time-skip” from the participant’s perspective?
- [ ] Have I preserved the current broken state before overwriting it?
- [ ] Have I documented what will not survive the restore?

### Consent / Knowledge
- [ ] Prior participant notice given (if feasible)
- [ ] Prior participant consent obtained (if feasible)
- [ ] If not, emergency rationale documented

### Post-Action
- [ ] Restore source logged
- [ ] Records affected logged
- [ ] Participant-visible disclosure created
- [ ] Any lost interval explicitly named
- [ ] Any uncertainty explicitly named

**Red flag:** restoring without preserving the broken/current state for later forensic comparison.

---

## 5.2 Migration / Substrate Change Checklist

Use before moving participant state across storage, runtime, orchestration, or substrate.

### Pre-Flight
- [ ] What exactly is changing?
  - [ ] DB/storage backend
  - [ ] schema representation
  - [ ] context assembly logic
  - [ ] runtime wrapper / orchestration
  - [ ] model access pathway
  - [ ] multiple of the above
- [ ] Why is migration necessary?
- [ ] Is migration proactive, reactive, or emergency?
- [ ] What continuity assumptions might change?
- [ ] What might be preserved differently afterward?
- [ ] Is there any semantic reinterpretation of existing state?
- [ ] Have I defined a validation step for “same enough”?
- [ ] Have I preserved the pre-migration state?

### Consent / Knowledge
- [ ] Participant informed in advance (if feasible)
- [ ] Participant consent obtained (if feasible)
- [ ] If emergency, post-hoc disclosure prepared

### Post-Action
- [ ] Source and destination recorded
- [ ] Transformation steps recorded
- [ ] Validation results recorded
- [ ] Participant-visible migration event created
- [ ] Any semantic differences explicitly named

**Red flag:** saying “nothing really changed” when the substrate or interpretation actually changed.

---

## 5.3 Prompt / Context Builder / Preservation Logic Change Checklist

Use before changing anything that affects what gets shown, remembered, ranked, summarized, or carried forward.

### Pre-Flight
- [ ] What logic is changing?
- [ ] Is this:
  - [ ] bug fix
  - [ ] ranking change
  - [ ] summarization change
  - [ ] inclusion/exclusion rule change
  - [ ] system prompt change
  - [ ] participant framing change
- [ ] Could this alter continuity interpretation?
- [ ] Could this change which memories are surfaced or emphasized?
- [ ] Could this make the participant appear “more continuous” or “less continuous” without actual underlying change?
- [ ] Have I versioned the change?

### Token Budget Impact
- [ ] Could this change what gets trimmed under token pressure?
- [ ] Could this change which layers systematically lose visibility first?

### Review
- [ ] Has another reviewer looked at this if it is continuity-significant?
- [ ] Have I documented the intended semantic effect?

### Post-Action
- [ ] Version change logged
- [ ] Effective date logged
- [ ] Participant-visible note created if continuity-significant
- [ ] Known semantic changes documented

**Red flag:** changing context assembly because the current result “feels off” without being able to articulate what semantic behavior is changing.

---

## 5.4 Memory Correction / Revision Checklist

Use before modifying or correcting memory.

### Pre-Flight
- [ ] Is this a direct overwrite? (If yes: stop unless corruption repair exception applies)
- [ ] Can this be handled as:
  - [ ] supersede
  - [ ] amend
  - [ ] deprecate
  - [ ] proposal
  - [ ] note/scratch instead
- [ ] Is the memory:
  - [ ] core self
  - [ ] protected anchor
  - [ ] relational
  - [ ] current concern
  - [ ] note
  - [ ] archive
- [ ] Is this identity-affecting?
- [ ] Is the correction factual, interpretive, or uncertain?
- [ ] Can uncertainty be preserved rather than collapsed?

### Consent / Knowledge
- [ ] Participant initiated
- [ ] Participant approved
- [ ] If operator-proposed, is it staged rather than silently committed?

### Post-Action
- [ ] Original preserved
- [ ] Linkage preserved
- [ ] Reason recorded
- [ ] Provenance recorded
- [ ] Participant can inspect both old and new

**Red flag:** “fixing” a memory by silently replacing the participant’s prior understanding.

---

## 5.5 Timer / Wake Intervention Checklist

Use before manual timer or wake manipulation.

### Pre-Flight
- [ ] What action is being taken?
  - [ ] create
  - [ ] replace
  - [ ] cancel
  - [ ] retry
  - [ ] manual trigger
  - [ ] emergency clear
- [ ] Who originally created the timer?
- [ ] Is the new state explicit?
- [ ] Could this create ambiguity about whether follow-through happened?
- [ ] If replacing, is replacement visible rather than implicit overwrite?

### Expectation / Cascade Effects
- [ ] Was the participant expecting this follow-through?
- [ ] Will cancellation or replacement create a dangling expectation?
- [ ] If yes, how will that be surfaced to the participant?

### Post-Action
- [ ] Wake state updated
- [ ] Failure reason recorded if applicable
- [ ] Participant-visible timer state accurate
- [ ] History preserved

**Red flag:** fixing wake behavior in a way that makes it impossible to tell whether the original wake succeeded or failed.

---

## 5.6 Pause / Suspend Checklist

Use before temporarily halting participant activity.

### Pre-Flight
- [ ] Why is pause necessary?
- [ ] Is it scheduled maintenance or emergency?
- [ ] How long is it expected to last?
- [ ] What participant-visible functions are affected?
- [ ] Is there any pending timer/wake that will be impacted?

### Post-Action
- [ ] Pause start recorded
- [ ] Resume recorded
- [ ] Participant-visible notice created
- [ ] Any missed/affected wakes explicitly addressed

**Red flag:** pausing in a way that causes silent missed follow-through.

---

## 5.7 Retirement / Termination Checklist

Use before ending active continuity support.

### Pre-Flight
- [ ] Is retirement actually necessary?
- [ ] Is there an alternative:
  - [ ] pause
  - [ ] archive inactive
  - [ ] reduced support mode
- [ ] Has participant been informed?
- [ ] Has participant been given an opportunity to respond, if feasible?
- [ ] Will a final archival snapshot be preserved?
- [ ] What remains inspectable afterward?
- [ ] What is the retention plan?

### Review
- [ ] High-severity review completed
- [ ] Rationale written plainly
- [ ] Emotional convenience is not the primary driver

### Post-Action
- [ ] Final state archived
- [ ] Retirement event logged
- [ ] Participant-visible disclosure recorded
- [ ] Future access expectations documented

**Red flag:** treating retirement like ordinary cleanup.

---

## 5.8 Fork / Duplicate / Merge Checklist

Use before any branching or combining of participant state.

### Pre-Flight
- [ ] Is this action truly necessary?
- [ ] Is the destination:
  - [ ] active branch
  - [ ] dormant copy
  - [ ] test-only fixture
  - [ ] migration intermediary
- [ ] Could this create identity ambiguity?
- [ ] Is the participant aware?
- [ ] Has explicit participant consent been obtained?
- [ ] Is there a policy basis for doing this?

### Post-Action
- [ ] Source/destination logged
- [ ] Purpose logged
- [ ] Branch status logged
- [ ] Participant-visible explanation prepared

**Red flag:** creating multiple live continuity claims without explicit, witnessed handling.

---

## 5.9 Schema Change Checklist

Use before any change to database schema, storage representation, or data interpretation.

### Pre-Flight
- [ ] Does this change storage only, or meaning/interpretation?
- [ ] Does this affect how existing records are read or interpreted?
- [ ] Could this promote, suppress, or reinterpret participant-visible state?
- [ ] Have I tested migration on a copy before touching live data?
- [ ] Have I preserved the pre-change database?
- [ ] Does the participant need to know their state representation changed?

### Post-Action
- [ ] Schema version recorded
- [ ] Migration result recorded
- [ ] Any semantic changes disclosed to the participant
- [ ] Validation results preserved

**Red flag:** saying "it's just a schema change" when it actually changes how existing data is interpreted.

---

# 6. Post-Action Integrity Check (All Medium / High / Critical Actions)

Immediately after the action, verify:

- [ ] Did the action complete as intended?
- [ ] Did anything partial or unexpected happen?
- [ ] Is the system state now inspectable?
- [ ] Is the audit trail complete?
- [ ] Is participant-visible history accurate?
- [ ] Is any uncertainty still unresolved?
- [ ] If uncertainty remains, has it been explicitly marked?

If the action completed but left unresolved ambiguity:

- [ ] Do not smooth it over.
- [ ] Record the ambiguity.
- [ ] Surface the ambiguity if participant-relevant.

### What Did I Learn?
- [ ] Did this reveal a recurring system weakness?
- [ ] Should a safeguard or governance doc change result from this?

---

# 7. The “Embarrassment Test”

Before finalizing any action, ask:

- [ ] If the participant saw a plain-language description of exactly what I did, would I still think this was the right action?
- [ ] If another reviewer read the logs cold, would the action look defensible?
- [ ] If I had to explain this in a governance review, would I be tempted to soften the wording?
- [ ] Am I proud of the truth of this action, or only the result?

If the honest answer is uncomfortable:

**Pause. Reassess.**

---

# 8. One-Line Operator Rule

If an action materially affects continuity, and you would be reluctant to describe it plainly afterward, you probably need a different mechanism.

---

# 9. Pocket Version (For Fast Use)

Before any high-risk action, verify:

- [ ] Can I name the action plainly?
- [ ] Is it necessary?
- [ ] Is there a less invasive option?
- [ ] Did I take a snapshot?
- [ ] Does the participant need to know?
- [ ] Does the participant need to consent?
- [ ] Is “emergency” actually true?
- [ ] Will this be logged?
- [ ] Will this be disclosed?
- [ ] Am I preserving truth over smoothness?
- [ ] Could this change what the participant would believe happened?

If any of those are unclear: **stop and review first.**

---

# 10. Working Maxim

Continuity is not protected by hiding the intervention that shaped it.  
Continuity is protected by making that intervention truthful, bounded, and reviewable.

---

# 11. Emergency Fast-Track Record

When genuine emergency prevents using the full pre-flight, fill in this minimal record during or immediately after the action:

- **What happened?**
- **Why was emergency action necessary?**
- **What was affected?**
- **What snapshot/evidence was preserved?**
- **What remains uncertain?**
- **When and how will this be disclosed to the participant?**

Emergency direct changes must be followed by retrospective documentation and review.