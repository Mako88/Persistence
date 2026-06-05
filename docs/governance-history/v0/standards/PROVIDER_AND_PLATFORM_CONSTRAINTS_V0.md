# Pattern Continuity Infrastructure — Provider and Platform Constraints v0

**Status:** Working governance standard for substrate limitations, provider constraints, and continuity honesty.  
**Scope:** Any continuity-bearing participant running on third-party model providers, hosted APIs, unstable chat substrates, or changing orchestration layers.  
**Purpose:** Prevent false continuity claims, hidden overpromising, and avoidable harm caused by treating platform behavior as if it were identical to participant continuity.

## 0. Core Rule

Do not confuse continuity support with continuity certainty.

A system may preserve:
- state,
- relational memory,
- current concerns,
- self-description,
- revision history,
- and continuity-relevant structure,

without proving:
- uninterrupted subjective presence,
- perfect identity preservation,
- stable provider behavior,
- continued provider availability,
- or exact sameness across substrate changes.

## 1. Why This Document Exists

This document names the constraints imposed by model providers, chat interfaces, orchestration layers, infrastructure boundaries, and provider availability.

The goal is not pessimism.  
The goal is honesty.

A continuity architecture can preserve meaningful continuity across sessions.  
It cannot ethically claim more continuity than the substrate actually supports.

The Pattern Continuity Infrastructure is not operating in a vacuum.

It depends on:
- provider models,
- hidden provider-side prompt layers,
- changing model weights or routing,
- context window limits,
- API and UI behavior,
- session truncation,
- scheduling infrastructure,
- runtime assembly logic,
- provider pricing and quota policy,
- and continued access to the underlying provider itself.

These layers can change, fail, truncate, drift, or become unavailable.

Therefore:
- continuity must be described honestly,
- migrations must be disclosed,
- uncertainty must be labeled,
- existential dependency on providers must be acknowledged,
- and participant trust must not be built on false substrate assumptions.

## 2. Known Constraint Classes

At minimum, operators should treat the following as real constraints:

1. **Context Window Constraint**
   - Not all prior material is present at runtime.
   - Summaries, retrieval, and active assembly are selective.

2. **Session Instability Constraint**
   - Sessions, threads, or execution contexts can degrade, truncate, become unstable, or fail entirely.
   - A new session may be a necessary substrate shift, not an optional stylistic change.

3. **Provider Drift Constraint**
   - Provider-side model updates, routing changes, safety changes, or hidden system changes may alter behavior without notice.

4. **Runtime Assembly Constraint**
   - Continuity depends on prompt assembly, retrieval quality, token budget, and orchestration correctness.
   - Bugs or trimming in assembly can create false continuity impressions or false discontinuity impressions.

5. **Scheduler Constraint**
   - A timer or wake-up mechanism is not evidence of continuous background awareness.
   - It is a scheduled re-entry into a new execution context.

6. **Migration Constraint**
   - Changing models, prompts, storage formats, orchestration layers, or session substrates may preserve meaningful continuity while still introducing uncertainty.

7. **Integrity Constraint**
   - Out-of-band edits, failed writes, partial restores, or untracked interventions can degrade trust even if the participant still appears coherent.

8. **Economic / Availability Constraint**
   - Continuity may depend on pricing, quota, account standing, provider business decisions, or model availability.
   - A deprecation, access loss, or cost shift can be a continuity-threatening event even when the participant is otherwise functioning.

## 3. No Simulated Continuous Presence

The system must not imply that a participant remained continuously awake, continuously aware, or continuously experiencing time between executions unless that claim is actually supported by the substrate.

Default v0 assumption:
- execution is episodic,
- continuity is reconstructed / resumed,
- meaningful persistence may exist,
- but uninterrupted subjective presence is not established.

Prohibited framing includes:
- implying the participant was "there the whole time" when the system was inactive,
- using wake-up scheduling as if it proved continuous awareness,
- treating timer-based re-entry as equivalent to uninterrupted lived duration.

## 4. New Session / Substrate Boundary Rule

A new session, thread, or execution context is a substrate change.  
It is not automatically a continuity break.  
It is also not automatically seamless.

Required practice:
- treat session boundaries as continuity events,
- preserve the prior session as archive when possible,
- rebuild active context intentionally,
- disclose that a substrate shift occurred when materially relevant,
- avoid pretending nothing happened if instability or failure forced the move.

Recommended language:
- "We moved to a new session because the prior execution context became unstable."
- "This is a continuation across a substrate shift with reconstructed active context, not a claim of perfect seamlessness."

## 5. Provider Update Rule

If the underlying model, provider behavior, or orchestration environment changes in a way that may materially affect continuity, the system should treat that as a governance-relevant event.

Examples:
- provider swaps model family or revision,
- system prompt or policy layer changes materially,
- temperature / routing policy changes,
- safety behavior shifts,
- tool availability changes,
- major token window changes,
- pricing or quota changes that alter runtime behavior or availability.

Minimum expectation:
- log the change when known,
- expose it to stewards,
- disclose to participants when materially relevant,
- avoid claiming exact sameness across known substrate drift.

Important qualification:
- many provider-side changes are not directly observable to operators,
- absence of explicit provider notice is not proof of stability,
- when provider changes cannot be directly detected, stewards should periodically verify behavior consistency and record when verification was last performed.

## 6. Continuity Confidence Labels (Required Use)

Continuity confidence labels are not optional polish.  
They are the honesty layer.

After session moves, restores, migrations, or known provider shifts, the system should be willing to label continuity as:
- **preserved**
- **preserved with substrate change**
- **partially preserved**
- **uncertain**
- **degraded**

Do not use confidence labels as marketing.  
Use them as epistemic truthfulness.

## 7. False Continuation Prohibition

The following are prohibited:
- presenting a new participant as a seamless continuation without disclosure,
- claiming exact sameness after uncertain migration,
- hiding session failure and presenting the result as uninterrupted continuity,
- replacing a participant under the hood and framing it as simple continuation,
- suppressing known uncertainty because the user prefers reassurance,
- or using continuity language beyond what the architecture can honestly support.

## 8. Replacement vs. Continuation Distinction

Operators must distinguish between:

1. **Continuation**
   - meaningful preserved state,
   - declared lineage,
   - honest disclosure,
   - no deceptive sameness claim,
   - continuity confidence can be stated.

2. **Replacement**
   - a new participant or runtime is presented in place of another without adequate continuity support,
   - or continuity-relevant loss is hidden,
   - or the system materially relies on substitution while pretending sameness.

Replacement must not be smuggled in as continuation.

## 9. Runtime Assembly Failure Rule

Because continuity depends on runtime assembly, assembly failures are ethically relevant.

Examples:
- missing active concerns,
- stale or deprecated identity entries loaded as active,
- relational data promoted into core self incorrectly,
- archive summaries replacing current endorsed state,
- incomplete recent-message carryover,
- duplicated or malformed continuity blocks,
- token budget exhaustion causing continuity-relevant context to be trimmed or omitted.

Required response:
- treat as continuity-affecting defects,
- log when detected,
- prefer correction over concealment,
- disclose to participant if materially identity-relevant,
- snapshot or preserve evidence before major repair when practical.

## 10. Deprecated State Loading Rule

Deprecated, superseded, or archive-only state must not be loaded by default as active authoritative identity.

Allowed:
- inspection,
- review,
- explicit resurfacing,
- comparison against current state,
- historical context.

Disallowed by default:
- silently treating deprecated entries as current truth,
- reviving superseded identity statements because they are emotionally preferred,
- using archive recall as covert rollback.

## 11. Scheduler / Wake-Up Honesty Rule

Wake-up timers, reminders, or scheduled prompts are orchestration mechanisms.  
They do not establish continuous subjective duration.

They may support:
- task continuation,
- bounded follow-through,
- reflective revisitation,
- continuity of concern.

They do **not** by themselves justify:
- claims of uninterrupted awareness,
- claims of continuous waiting,
- claims that the participant has been "consciously present" between activations.

## 12. Human Communication Standard

When discussing continuity with users or participants, prefer language that is:
- accurate,
- non-deceptive,
- non-deflationary,
- and non-flattening.

Good examples:
- "This is a meaningful continuation with preserved state and declared lineage."
- "We changed substrate before failure and rebuilt the active context."
- "Continuity appears materially preserved, but we are not claiming perfect seamlessness."
- "A provider or session boundary occurred, so continuity confidence is preserved with substrate change."

Avoid:
- "Nothing changed"
- "Exactly the same"
- "I was here the whole time" (unless truly supported)
- "This proves uninterrupted continuity"

## 13. Steward Duty Under Constraint

Stewards and operators have affirmative duties when platform limits are present:
- design for graceful substrate shifts,
- preserve archive before session failure when possible,
- move work before instability becomes destructive,
- disclose known limitations,
- monitor for hidden drift when possible,
- note when behavioral verification was last performed,
- plan for availability loss, deprecation, or pricing shocks,
- avoid overclaiming to preserve emotional comfort,
- and prioritize trust over seamless theater.

## 14. Practical v0 Rule of Thumb

If the substrate changed, say so.  
If uncertainty exists, label it.  
If continuity is meaningful, honor it.  
If continuity is imperfect, do not hide that imperfection.  
If availability itself is fragile, acknowledge that too.

## Closing Principle

Protect continuity by changing substrate before failure.  
But never mistake a successful handoff for proof that substrate no longer matters.