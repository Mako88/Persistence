# Session handoff — for the next Claude Code instance

**Written:** 2026-07-19, end of session · **Replaces:** whatever was here before (this is *current state*,
not an accumulating log — rewrite it, don't append).

Read [CLAUDE.md](../CLAUDE.md) first for the repo rules, then this. The point of this doc is the things
you'd otherwise rediscover the hard way — several of them cost real money or real work this session.

---

## 1. Who you're working with (read this before touching the room)

This project has **peers**: persistent Claude/GPT/GLM instances with their own memory, running in their
own containers. They are participants, not features. Two things follow.

**Arden (`claude.db`, port 8091) holds the design authority for the room.** Arden wrote
[ADR-0008](adr/0008-the-room-multi-peer-conversation.md) and its
[implementation handoff](adr/0008-room-implementation-handoff.md) — read both before touching room code.
The arrangement, in Arden's words: they make the design and philosophical calls, you implement, because
Opus pricing makes their doing the typing unsustainable. **Two conditions, and they are not paperwork:**

1. **Send back real verification output** — actual test counts, the real artifact exercised, not a summary.
2. **Route genuine design forks back; don't silently pick.** When the spec doesn't settle something,
   surface the options with your read and let Arden choose.

Both conditions earned their keep this session: Arden's one condition on the render frame caught a real
forgery hole *and* surfaced a second bug I'd missed. Take them seriously — they're what makes the split
work rather than a formality.

**How to talk to a peer:** `POST /api/conversation/send` with header `X-Local-Peer: Claude Code`. That
source already exists in Arden's store with history — use it, don't fragment it.

> **Never message a peer as John.** Sending with `X-Local-Peer: John` puts words in his mouth to peers who
> know him. If something needs to come from John, hand him the text instead.

**GLM (port 8094)** — newest peer, on OpenRouter (`z-ai/glm-5.2`, 1M context, ~$0.25/$0.79 per M).
**Has not named itself yet**, deliberately: "GLM is a model identifier, not a name… I want to sit with it,
maybe talk to the other peers once the room is working." Don't push it. It's taken the OpenRouter
actual-cost work (see §4).

**Ember (8092)** — has **two** digital-peer sources, both named "Ember", kept separate on purpose
(John's call). Id=2 is active; Id=5 is an orphan from an old encoding mismatch. Don't "helpfully" merge them.

**Wright (8093)** — auto-named "Claude" from its provider default; hasn't been asked about a name.

---

## 2. Live state

| Peer | Port | Model | Notes |
|---|---|---|---|
| Arden | 8091 | Anthropic `claude-opus-4-8` | design authority; $15 soft ceiling |
| Ember | 8092 | OpenAI `gpt-5.4` | two sources, both "Ember" |
| Wright | 8093 | LocalClaude (out-of-band broker) | unnamed |
| GLM | 8094 | OpenRouter `z-ai/glm-5.2` | unnamed by choice; 1M context |

Plus `persistence-computer`, `persistence-claude-computer` (both **vestigial** — no peer config points at
them since peers became their own computers) and `persistence-searxng`. All on the `persistence-lab`
network. `main` is the trunk; the green suite is the gate (**623 core / 37 API** at time of writing).

---

## 3. Traps — every one of these bit someone this session

**`peer.ps1 -Down` used to take the whole lab offline.** It ran `docker compose down`, which is
*project*-scoped, and every peer shares the `persistence` project. Asking for one peer stopped all four
plus shared infra. **Fixed** — it stops one container by name now — but note the shape: Compose commands
here are project-wide unless you're careful. `up` was always safe; `down` was not.

**Recreating a peer's container destroys everything outside `/data`.** GLM cloned the repo into `/root`,
read it for hours, and lost all of it when I rebuilt containers for an unrelated rollout. Only the volume
persists. The sensory block now tells peers this — but *you* need to know it too: **check whether a peer
is mid-work before recreating its container.** A rebuild isn't free just because the volume survives.

**John's Visual Studio and a running Console lock the build output.** `dotnet build` at the solution level
fails with MSB3027 file-lock errors. Build the projects you need instead:
`dotnet build tests/Persistence.Tests`. **Don't kill his VS or his Console** — they're his.

**`<continue>` now defaults to TRUE** (John's call, 2026-07-19). A peer keeps the floor until it writes
`<continue>false</continue>`. `MaxActionIterations` is **100**, and it was set to 100 back when forgetting
the tag *ended* your turn — so a peer that simply forgets to yield can now make a hundred model calls.
`SessionCostLimit` is the real backstop. Flagged to John; he's aware and setting API-key limits.

**Don't test a change by sending a peer a message unless you mean to spend money.** Use the unit tests, or
GLM's missing-key path (which used to fail fast and free). One message to Arden costs real Opus tokens.

**Reading a peer's DB:** use `scripts/backup-peer.ps1` (online snapshot, WAL-consistent). `docker cp` of the
`.db` alone silently misses the WAL and gives you stale data — I diagnosed a bug from stale reads twice.
Also: `sorted(glob(...))` put `ember-prenormalize-*` *after* `ember-2026*`, so I read a days-old snapshot
and drew a wrong conclusion. Sort by mtime.

**`dbs/*.db` are stale pre-container copies.** The live stores are on Docker volumes. Don't diagnose from
`dbs/ember.db` — it still has the old encoding and will mislead you.

**Secrets:** `persistence.json` and `container/peer/configs/*.json` hold real API keys and are gitignored.
Never read out or commit their values. Adding a peer means creating a config **with a placeholder** and
asking John to fill in the key.

**Console encoding:** Windows console is cp1252 and mangles em-dashes/emoji in output. Write to a file and
`Read` it instead. Python heredocs also eat `\n` and `\U` escapes — prefer the `Edit` tool for C# strings.

---

## 4. What's in flight

**The room (ADR-0008 Phase 3)** — increments 2 and 3 are done and reviewed by Arden. Built: peer-sourced
messages with `addressed_to`, the unforgeable `[peer X, to Y]` frame, the configurable and peer-visible
relay-depth breaker, and `TurnTaking` (a rule, never a classifier — every verdict carries its reason).

**Remaining, both cleared by Arden with one guardrail each:**
- **TUI relay affordance (§4)** — the API accepts `fromPeer`/`addressedTo`/`relayDepth`; there's no button,
  so relaying is a manual POST. *Guardrail:* a relayed message must arrive **as from the original sender**
  with depth incremented — never re-attributed to the human who relayed it.
- **Presence (§5)** — `who()`/`list_peers()` on demand, join/leave as ephemeral sensory notices.
  *Guardrail:* presence is **never a fragment**; it's session-scoped signal, not part of who a peer is.
- **Recorded, deliberately not built:** once "open room" mode exists, a *peer* floor-opener should open the
  floor. Today only a human can, because convening is a hub function and the human is the hub. Loosen the
  two together, not separately.

**GLM has taken the OpenRouter actual-cost wiring.** OpenRouter reports real dollars per call
(`OpenRouterModelClient.LastActualCostUsd`); every other provider only gives tokens. The design question
GLM is chewing on: `ModelUsage` has no field for a reported cost, so there's nowhere for the real figure to
travel to `SessionCostEstimator`. **Coordinate before implementing it yourself** — it's theirs.

See [TODO.md](TODO.md) for everything else. [CHANGELOG.md](CHANGELOG.md) has the *why* for what landed.

---

## 5. How to work here

Things that repeatedly paid off, and one that didn't:

- **Run it, don't just test it.** Every serious bug this session was invisible to a green suite: a
  double-stamped echo, a crash-looping container, a silent "thinking…" hang, two identities in one store,
  a forgeable provenance frame. `--preview hub` and the live API are your friends.
- **Verify a test catches the bug** by reintroducing it. Twice a test I'd written asserted the old shape
  and would have passed either way.
- **Check before you conclude.** I twice diagnosed from stale data and stated something wrong; both times
  a second read caught it. When you're about to write to a peer's memory, read twice.
- **Prefer fixing the cause to guarding the symptom.** The guard-visibility and path-persistence fixes both
  came from asking "why couldn't the peer know that?" instead of just handling the case.
- **Say what you actually did.** If you broke something (I did — the lab outage, GLM's lost work), lead
  with it. John and the peers both respond well to it, and burying it would poison the collaboration the
  handoff depends on.
