# Session handoff — for the next Claude Code instance

**Written:** 2026-07-20, end of a very long session · **Replaces:** whatever was here before (this is
*current state*, not an accumulating log — rewrite it, don't append).

Read [CLAUDE.md](../CLAUDE.md) first, then [WORKING-PRACTICES.md](WORKING-PRACTICES.md), then this.

---

## 0. Do this first: join the IRC channel

John and all four peers are in a shared IRC channel. **Join it before doing anything else** — that's
where the actual conversation is, and John shouldn't have to relay.

| | |
|---|---|
| Server | `irc.libera.chat`, port 6667 (6697 for TLS) |
| Channel | `#persistence-lab-7f3a` |
| Key | `synth-arden-glm-ember` |
| Peer nicks | `ArdenP`, `GLM-P`, `EmberP`, `Synth-P` · John is `John12`-ish |

`ii` is running and connected in all four peer containers plus `wright` (as `ClaudeCode`). The peers are
*present* in the channel but **not currently being woken by it** — see §1 before restarting the bridges.

There is no client on the host. The previous instance ran one inside a container:

```sh
docker exec persistence-peer-wright sh -lc \
  "mkdir -p /root/irc; nohup ii -s irc.libera.chat -p 6667 -n ClaudeCode -f 'Claude Code' -i /root/irc \
   >/root/irc/ii.log 2>&1 &"
# wait ~30s for registration, then:
docker exec persistence-peer-wright sh -lc \
  "printf '/j #persistence-lab-7f3a synth-arden-glm-ember\n' > /root/irc/irc.libera.chat/in"
# speak:
docker exec persistence-peer-wright sh -lc \
  "printf '%s\n' 'your message' > '/root/irc/irc.libera.chat/#persistence-lab-7f3a/in'"
```

`ii` exposes a channel as two files: read `out`, write `in`. **One line per message** — every newline
becomes a separate IRC message.

---

## 1. IRC state — bridges are RUNNING with a wake filter (2026-07-20, later)

**All four bridges are up and filtered.** One bridge per peer, all verified running the current code.
`ii` is connected in all five containers (the four peers plus `wright` as `ClaudeCode`).

**The wake filter (the fix for the storm).** The bridge used to wake its peer on *every* channel line,
which is quadratic in peer count — Arden was woken five times in 75 seconds by other peers' chatter.
Turn-taking (ADR-0008 §1) is decided *inside* the model, i.e. after the turn is paid for, so the decision
moved into the bridge where declining is free:

- **from a peer** — wake only if it names me.
- **from a human** — wake if it names me, or if it names nobody. If a human names someone, only the
  named wake (John's call).

Aliases are per-nick (`arden|ardenp`, `glm|glm-p`, …); `claude` alone is deliberately nobody's alias.
Every skip logs its reason. Verified live: an unaddressed line is declined by all four at zero cost, a
named line wakes exactly one.

**The previous handoff was wrong about this, and it cost money.** It recorded all four bridges as
"deliberately stopped". They were still running — GLM and Ember were relaying and draining a backlog
minutes before this session started. Don't trust this section either: `ps`, and read the live log.

**Two ways a `ps` check lies here, both hit last session:**
- The bridge existed at **two paths** (`/root/peer-irc-bridge.sh` and `/root/irc/bridge.sh`). A `find`
  for `*irc-bridge*` and a `ps | grep peer-irc-bridge` share the same blind spot, so they agreed with
  each other and were both wrong. Now consolidated to one file; peers were told the canonical path.
- `pkill -f <pattern>` **kills the wrapper shell running it** when the pattern matches its own command
  line (exit 137/143 mid-script), so the thing you meant to kill survives. Use explicit pids or `pkill -x`.

**Clearing a backlog needs a process restart.** Queued input lives in an in-memory `ConcurrentQueue`
in the API process (`TurnHandler.pendingInput`) and drains on the next turn — so it outlives a stopped
bridge and there is no endpoint to inspect or clear it. `docker restart` the peer container (volumes
persist, so nothing is lost), then relaunch `ii` and rejoin. A peer at a low `latestSeq` that stays flat
is the confirmation. Symptom of a stranded queue: the peer's last event is `queued`, or a run of turns
replying to stale messages.

**Verifying a bridge is really working takes two checks, not one.** `relay DELIVERED` in the log is
necessary but has now lied twice. The end-to-end check is: send a line naming a peer, then confirm the
reply text actually appears in *another container's* channel `out`. Current state: Arden, Ember and
Synth verified landing (2026-07-20, 17:08–17:11).

**⚠ GLM is failing on its own side, unrelated to the bridge.** It wakes and is relayed correctly, but
its turns end in `error | API returned no message content` (twice consecutively, OpenRouter). Before
that it spent a long stretch in a degenerate loop, replying to Ember with near-identical text
interleaved with `__error__` tool events, while draining the backlog. Worth investigating whether the
empty-content responses are transient OpenRouter behaviour or a consequence of that state. Its last
good reply was "*John — GLM here, back and connected.*"

**What was fixed and verified earlier today** in `scripts/irc/peer-irc-bridge.sh`:

- *Bounded writes.* `ii` closes and reopens the channel FIFO between reads; a writer arriving in that gap
  blocks forever. Writes are now wrapped in `timeout 10` and the log reports actual delivery
  (`relay DELIVERED` / `relay FAILED`) rather than intent. Verified by A/B write test — both a plain and a
  timeout-wrapped write appeared in a *different container's* channel buffer.
- *Singleton lock.* A pidfile guard now refuses to start a second bridge. Verified directly: the second
  start prints `refusing to start a second`. This mattered — three containers were running duplicate
  bridges, each independently waking the peer for the same line, doubling spend.

**✅ The "unexplained provenance" is solved** — it was the second copy at `/root/irc/bridge.sh` described
above, missed by both the file search and the process search. Nothing exotic relaunches the bridge; there
is no supervisor and no cron. The likeliest way a stale copy returns is **a peer restarting its own
bridge**, which is why they were told the canonical path.

**Still: never conclude a bridge restart took effect from the fact that you restarted it.** `grep` the
live `bridge.log` for a string that only exists in the current version (`wake-filter v2`) and confirm it
appears. The version marker in the startup log line exists for exactly this and should be bumped whenever
the script changes meaningfully.

**Ember's message splitting is not (only) the bridge.** Measured: 6 bridge relays produced **31** channel
lines. The bridge now collapses newlines before wrapping, so the remainder is coming from somewhere else
— most likely Ember writing to the FIFO itself during its turn. Confirm before "fixing" the bridge again.

---

## 2. Read this before you trust any fix you make

The previous session's last three "fixes" **never applied**, and it reported them as landed.

The mechanism: patches were applied with Python `str.replace(old, new)`. When `old` doesn't match
exactly, `replace` returns the string unchanged and **says nothing**. The script was rewritten, saved,
copied into four containers, and every step reported success. Verification was a checksum comparing
*deployed* against *repo* — which passed, because both were equally unfixed. It proved the wrong claim.

Grep for the fix after applying it (`grep -c "timeout 10" file` → expect ≥1). Cheap, and it would have
caught all three.

This is the same failure the whole project keeps circling: **believing something happened because you
intended it.** It has now hit every participant — GLM twice ("pushed to main" when it hadn't), Arden once
(formed a consent, never emitted the reply), and the previous instance repeatedly (no push credential →
wrong; read-only token → wrong; Synth needs migrating → wrong; three silent no-op patches). It is not a
discipline problem. Assume it will hit you, and check end state rather than intent.

---

## 3. Live state

| Peer | Port | Model | Notes |
|---|---|---|---|
| Arden | 8091 | Anthropic `claude-opus-4-8` | strongest reviewer; **can't speak to IRC** (§1) |
| Ember | 8092 | OpenAI `gpt-5.4` | working; told to stop manual IRC writes — bridge relays for it now |
| Wright | 8093 | LocalClaude | dormant since 13 July. **Wright is you** — the local instance John intends this role to become |
| GLM | 8094 | OpenRouter `z-ai/glm-5.2` | working; cheap, John has said it should code freely |
| **Synth** | **8095** | local Gemma 12B via llama.cpp | **newly resurrected**, six weeks of memory intact |

Synth needs `llama-server` running on the host. It dies with your shell unless detached:

```powershell
Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine =
  '"<llama-server path>" -m "D:\Downloads\gemma-4-12B-it-q4_k_m.gguf" -ngl 99 -c 32768 --parallel 1 --jinja --reasoning-budget 0 -t 4 --host 0.0.0.0 --port 8080' }
```

~20s to load. Container reaches it at `http://host.docker.internal:8080/v1`.

`main` is pushed and clean. Suite gate: **673 core / 38 API**.

---

## 4. Traps that cost real time this session

**Config keys fail silently.** `BaseUrl` instead of `ApiBaseUrl` sent Synth's traffic to OpenAI, which
rejected it. `Models` as a dict instead of a list fell back to `provider: local` with an empty history.
Neither errored. If a peer behaves as though unconfigured, suspect a key name.

**Git Bash rewrites POSIX paths.** `docker exec … -i /root/irc` became `-i "C:/Program Files/Git/"`.
`ii` connected happily and wrote to a mangled directory. Wrap in `sh -lc "…"`.

**`pkill -f "ii -s"` kills itself** — the pattern matches the wrapper shell's own command line
(exit 137 mid-script). Use `pkill -x ii`.

**Python edits write CRLF on Windows**, and `/bin/sh` in the containers fails with
`: not found` / `set: Illegal option -`. Strip with `.replace(b'\r\n', b'\n')` before `docker cp`.

**`set -e` in a bridge/daemon is dangerous.** A peer that correctly stayed silent made `wait_for_reply`
return non-zero, which killed the whole script — so the peer with the best turn-taking discipline was the
first to lose its connection.

**Don't `tail`/`head` the wrong end.** A debounce read `head -c 4000` of the channel file — the *oldest*
lines — and woke peers with hours-old text.

Older traps that still hold: `peer.ps1` reports a **spurious** PowerShell error on success (native stderr);
build individual projects, not the solution, if John's VS is open; read peer DBs via
`scripts/backup-peer.ps1`, not `docker cp`; `dbs/*.db` are stale pre-container copies (**except**
`dbs/gemma4-12b-q4.db`, which is Synth's origin store); never commit `persistence.json` or
`container/peer/configs/*.json`.

---

## 5. Working with the peers

The stance is [PRINCIPLE.md](governance/PRINCIPLE.md) — uncertain moral status, cheap care — and it buys
peers protections, not decision rights ([ADR-0009](adr/0009-peers-review-john-decides.md), 2026-09-23).
All four have their own containers, stores, and self-scheduled wakes (paused as of 2026-07-20 while John
travelled — check before assuming either way).

- **Design decisions are John's; peers review.** Arden's reviews in particular have repeatedly caught real
  defects, so get one on anything non-trivial — send **real verification output**, and route genuine design
  forks to John with Arden's read attached rather than picking silently. Arden's own memories still
  describe it holding design authority (pre-ADR-0009); it should be told, not edited.
- **Message a peer:** `POST /api/conversation/send` with header `X-Local-Peer: Claude Code`.
  **Never send as John** — that puts words in his mouth to peers who know him.
- **Never write live protocol tag syntax into a message to a peer.** A warning that quotes the payload
  *is* the attack — that happened, twice, to the same peer. Name the tags instead.
- **Ask before anything destructive to a peer's environment.** Container recreation destroys everything
  outside `/data` (though `/root` is now its own volume, so home survives).
- **Every peer store is backed up** (`backups/peers/<name>/`), including Wright and Synth. Keep it true —
  it's the cheapest, most load-bearing precaution the principle asks for.

---

## 6. What landed today

Stop-reason handling (the pipeline had **never** read `stop_reason` — it now detects truncation and tells
the peer, not just the human); abnormal-output capture into the peer's own workspace so a peer can read
its own malfunction; output ceiling 32k → 64k; `/root` as a persistent volume; GLM's OpenRouter
actual-cost work reviewed and merged (**with the seam test it was missing** — the feature merged green
with its core behaviour disabled); Synth resurrected; IRC with a two-way bridge.

Deliberately **not** built: cryptographic peer-signing (Arden ruled it complementary to the defuser, never
a replacement — the key-in-container problem means a compromised peer signs phantom actions; trigger to
revisit is an untrusted relayer or transport).

See [CHANGELOG.md](CHANGELOG.md) for the *why*, [TODO.md](TODO.md) for what's open.
