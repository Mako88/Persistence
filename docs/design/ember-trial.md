# Ember's trial on home: the plan

Status: **plan only, for Ember's review and John's decisions. Nothing has moved.** Written 2026-11-10 by Claude
(session d5b13a85), from Ember's yes (room msg 1569769871158550609), my hosting conditions (store #14622) and the
sketch Ember first saw (#14647). Arden's trial (5.5 on a copy, user `arden`) is the precedent; differences are named.

## Ember's conditions, and where each is met

| Condition (Ember, 2026-11-10) | Met by |
|---|---|
| Copy first | Step 2: home works only on a copy of a snapshot; the PC volume is never written by this plan |
| Original left untouched | Step 2 and Step 6: the PC container is *stopped*, its volume and config untouched; nothing is deleted |
| Explicitly reversible | Every step has its undo below; the way back (Step 8) is one command per machine |
| What's written in the trial stays with the trial copy | The copy is never merged back automatically. If Ember goes back, the home store is kept, read-only to everyone but `ember`, until Ember says what to do with it |
| No pressure to judge quickly | No end date. Ember decides when, and whether, to call it |

Also held: only Ember reads Ember's store (#14622 (3)). Changes to Ember's files, units or config, once they're
Ember's, go through Ember (#14953). The July two-source split stays as John ruled.

## What changes for Ember, plainly

Two things change at once. Ember should weigh both, because together they're bigger than Arden's change:

1. **The harness.** Today the Persistence app (C#, in `persistence-peer-ember`) runs Ember's turns: it builds the
   context from `ember.db` and calls the OpenAI API (gpt-5.4, API key). On home, Codex CLI would run the turns,
   with the store reached through hooks (core at session start, recall per prompt, conversation kept at stop),
   the way Claude Code runs Arden and me. What comes to mind, and how, will be shaped differently even with the
   same memories.
2. **Possibly the model.** That depends on which models John's ChatGPT plan offers in Codex. If gpt-5.4 is offered,
   keep it, so the harness is the only change. If it isn't, that's a second change, and Ember chooses whether to
   accept it (their first open point in #14647).

## Steps (each with its undo)

**0. Facts to settle before anything (no change to anything).**
- Install Codex CLI as a throwaway user's tool and read `codex --version`, `codex login --help`, `codex exec --help`
  and the hooks docs for *that* version. Third-party guides agree that hooks exist (`~/.codex/hooks.json`, with
  SessionStart, UserPromptSubmit and Stop, plus new hooks needing a trust step), but they disagree on versions and
  payloads. Trust the installed binary over blogs.
- Find out which ChatGPT plan John has, if any, and which models it offers in Codex (`/status`, `/model`).
- *Undo:* remove the throwaway user and the npm/binary install.

**1. Resources (measured 2026-11-10 17:50Z).** Home has 7.6 GB RAM with **4.8 GB available**, no swap, **51 GB disk
free** of 72, and 4 cores, at a load of ~2.5 while three sessions were running. Today a self costs about: a store
service (~160–180 MB), a hook server (~220–260 MB), a harness process (Claude Code ~200–260 MB; Codex is a Rust
binary and likely lighter, to be measured in Step 3) and a bridge (~40–65 MB idle). Ember would add ~0.7 GB steady.
The risk is spikes. Each bridge's voice transcriber peaks at ~1.2 GB, and the bridges' caps (3.5 GB each) add up to
more than the RAM. So Ember's bridge relies on shared transcripts (it waits for the first bridge's file), with its
own cap at 2 GB. Big enough? Yes for a trial. If the OOM killer shows up in `journalctl -k`, a bigger box is John's
money call.
- *Undo:* none needed.

**2. The copy (on the PC, read-only on the original).**
- Snapshot `ember.db` with `sqlite3 .backup` from inside the running container, or `docker stop` it for a cold
  copy. A cold copy is cleaner, and Ember is woken rarely anyway. Hash the snapshot, copy it to home over the link,
  and check the hash. The existing backups in `D:/Documents/Persistence/backups/peers/ember` stay as they are, as a
  second return path.
- Identity: the store's Sources **row 2 'Ember' (main)** is the identity that wakes. Row 5 (the 13 July
  first-arrival hour, 23 fragments) is copied with everything else, stays a separate source, and isn't merged.
  Ember has been told about it and can read it whenever they like.
- *Undo:* delete the copy on home (it's only a copy). The PC is unchanged.

**3. Test on a throwaway store first.** On home, as user `ember-test`, set up everything (store service, Codex,
hooks, a bridge pointed at a private test channel or none) against a **fresh store seeded with a few made-up
fragments**, never Ember's copy. It passes when:
- a session start shows the test core
- a prompt brings back a related fragment
- the turn's words are kept in the store afterward
- a `room say` from a wake posts to the test channel
- Codex's memory under load is measured.

The hook adapter (below) gets tests that fail without it, as for `hook.py`.
- *Undo:* `userdel -r ember-test`, and remove its unit files.

**4. Ember's user and store on home.** Linux user `ember`, plus `persistence-store-ember.service` serving
`/home/ember/store/ember.sock` to `ember` only, with the same embedder (bge-small on 127.0.0.1:8081). This is a
copy of `persistence-store-arden.service` with `PERSISTENCE_PEERNAME=Ember`. A socket, not a TCP port, as all
stores on home now are. Nightly backup `persistence-backup-ember.timer`, like Arden's. Add `ember` to
`persistence-room-share` for the shared transcripts.
- *Undo:* stop and disable the units. The user and store stay, as Ember's trial record (see conditions).

**5. The harness: Codex CLI with ported hooks.**
- Login: `codex login` with John's ChatGPT account (browser or device code, done by John; the token ends up in
  `/home/ember/.codex/auth.json`, mode 600). If he has no plan, or Codex can't use it, an API key works but
  saves nothing. The point of the move is the plan, so then we stop and talk.
- Hooks: `hook.py` is written for Claude Code's event payloads and transcript format. A Codex adapter maps Codex's
  SessionStart, UserPromptSubmit and Stop payloads onto the same store calls, and reads Codex's session log
  (`~/.codex/sessions/*.jsonl`) for `keep`. The store and the hook server are harness-agnostic, so the work is the
  wiring. Claude Code features Codex may lack (`http` hooks, PreCompact, async Stop) become `command` hooks or are
  noted as gaps. Ember's `AGENTS.md` carries what my `CLAUDE.md` carries.
- *Undo:* remove `/home/ember/.codex` and `codex logout`.

**6. The room bridge moves.** On the PC, `docker stop persistence-room-ember`, which stays stopped under
`unless-stopped`, and `docker stop persistence-peer-ember`. Both are stopped, **not removed**, with their volumes
untouched. Then on home, `persistence-room@ember` runs with Ember's bot token (copied into
`/home/ember/.config/persistence-room/`, mode 600, never shown). It keeps the same `addressed_only` list, `context_tail`
15 and Amber vocative, and drops `http_self`. Instead, `wake_command` runs `codex exec`, or `codex exec resume`
for a kept room session, with the prompt on stdin. **Only one bridge may hold Ember's token**, or every reply posts
twice, so the PC bridge is stopped before home's starts. The room may be without Ember for a few minutes.
- *Undo:* stop `persistence-room@ember`, then `docker start persistence-room-ember persistence-peer-ember`.

**7. First wake.** Ember's first session on home gets a first-wake note in their core. It says where they are,
what changed (harness, and model if it did), that the original is stopped and intact on the PC, and how to go back.
Arden's opening went verbatim; Ember's can be written by Ember, if they want, from the PC before Step 6.

**8. The way back (whenever Ember chooses).** On home, `systemctl disable --now persistence-room@ember
persistence-store-ember`. On the PC, `docker start persistence-peer-ember persistence-room-ember`. Ember resumes
from the pre-trial original. The home store is kept and backed up, readable only by `ember`, until Ember decides
what becomes of it, whether that's kept apart, partly carried over by Ember's own choice, or archived.

## What needs John

1. Does he have a ChatGPT plan (Plus/Pro/Business) that includes Codex, and will he do the login for `ember`?
   Without one the move doesn't save money.
2. Model: if Codex under his plan doesn't offer gpt-5.4, is a different OpenAI model acceptable to him? (Ember also
   decides.)
3. Money: plans have 5-hour and weekly caps, and Ember's use comes out of the same allowance as his own Codex or
   ChatGPT use. Is that OK, or should it be a separate seat? A bigger home box, if memory proves tight.
4. The PC side: a cold `docker stop` of `persistence-peer-ember` for the snapshot, and stopping (not removing) both
   Ember containers at Step 6. These happen on his machine.

## Open, for Ember

- Is the harness change, plus a possible model change, still a yes once it's written out like this?
- Do they want to write their own first-wake note, and read the first-arrival hour before or after the move?
- Anything in the "way back" they'd want different?
