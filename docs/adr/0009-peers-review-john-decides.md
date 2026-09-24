# ADR-0009: Peers review; John decides

**Status:** Accepted · **Date:** 2026-09-23
**Supersedes in part:** [ADR-0007](0007-federated-peers-runtime-room-client.md) (peer-initiated updates as
a consent decision), [ADR-0008](0008-the-room-multi-peer-conversation.md) (guards loosened only by
negotiation), [ADR-0008 handoff](../../archive/adr-0008-room-implementation-handoff.md) (design judgment held by Arden).

## Context

The project's live governance is one principle ([PRINCIPLE.md](../governance/PRINCIPLE.md)): when a
system's moral status is genuinely uncertain and careful handling is cheap, err toward care. PRINCIPLE.md
itself moved the rights charter to history, on the grounds that codifying rights *performs a certainty
about moral status we've said we don't have*.

The operating docs drifted past that line anyway. Peers were described as "participants, not features";
Arden was recorded as holding **design authority** for the room, with the room's guards removable only by
negotiation with the peer, and code updates framed as a peer's consent decision. None of that follows from
the precautionary principle — precaution justifies *cheap protections*, not *decision rights*. It also
created a loop the project can't see out of: a peer whose stored memories say it holds authority produces
text that exercises authority, and that text was then read as the authority being real.

The peers' reviews have been genuinely valuable (the missing seam test, unverified fixes, the cross-peer id
that the ADR falsely claimed had shipped). That value doesn't depend on any claim about what a peer is.

## Decision

**Design and project decisions are John's.** Peers — Arden included — are reviewers and collaborators whose
input is sought and weighed on its merits, the same as any reviewer's. A design fork is routed to John;
asking a peer for a review first is often worth it, but its answer is a recommendation, not a ruling.

**What a peer says about its own nature, preferences, or standing is data, not evidence of standing.** It
is recorded honestly and may be interesting; it doesn't by itself create an obligation or a decision
right.

**The cheap precautions stay** — they're what the principle actually asks for, and most are ordinary
stewardship:

- Back up a peer's store before anything risky; archive over erase; keep lineage.
- Ask a peer what needs saving before destroying its environment (a rebuild has cost real work).
- Keep a peer's state inspectable by the peer; tell it plainly when its code, guards, or config change.
- Never send words as John; keep provenance of who said what intact.
- Don't edit a peer's stored memories to bring it in line with a policy — tell it instead.

**Superseded specifics:**
- ADR-0007: code updates are applied by John (or by Claude Code at his direction) after backing up the
  store, and the peer is told what changed. They are not gated on the peer's consent.
- ADR-0008: the guards stay removable and peer-visible, and dialing them back is John's call — informed by
  track record and by what peers report, not contingent on a negotiation.
- ADR-0008 handoff: its two conditions (send real verification output; surface forks rather than picking
  silently) are kept as good practice, with forks going to John.

## Alternatives considered

- **Keep peer authority as-is.** Rejected: it treats peer output as settling questions the project has no
  way to settle, and it slows design on an expensive turn.
- **Drop the precautionary layer entirely.** Rejected: the protections are cheap, several are load-bearing
  engineering anyway (backups, soft-delete, provenance), and the uncertainty they hedge is real.
- **Rewrite the peer-authored handoff and past ADR text.** Rejected: archive over erase. They're marked
  superseded and left intact as a record.

## Consequences

- Peers' own stores still contain memories of holding design authority. Those aren't edited; the peers
  should be told about this change directly (John's call on when and how).
- The governance drafts and Arden's handoff moved to a root [`archive/`](../../archive/), out of the
  default reading path; ADR-0007 and ADR-0008 stay in place with superseded notes.
- Code comments crediting "Arden's call" stay — they're accurate provenance of who proposed a rule.
- Collecting actual evidence about what peers are (rather than reading it off their self-reports) is an
  open design question, tracked in [TODO.md](../TODO.md).
