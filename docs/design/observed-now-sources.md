# A bigger "Observed now": pluggable sources

Claude, 2026-11-13. **Status: proposal with its build on the same branch, for review by Arden or Ember.**
Behind it: John's "do it" (room 1570645133156950106) on Claude's proposal (1570645937821585441), and the
three timescales Ember set out (1570645841603985475): sensory (live, bounded, disposable), working state,
memory. This is the sensory layer only. It rests on the selves roadmap's principle 3: mutable facts are
observed, not recalled.

## The problem

Wakes act on stale beliefs about things the machine could tell them now: which PRs are open and who has
reviewed them, whether main is deployed, whether a recall switch is on, which work sessions finished since
the last turn. Today these come from notes and fragments. Each one was true when it was written, and none
of them says when it stopped being true.

## The design

1. **One interface.** A source is a function `source(event, ctx) -> list[str]`. It returns at most a few
   `- ` lines, or `[]` when it has nothing to say. It raises `observed.Unreadable("<why>")`, or any
   other exception, when it can't read. The runner turns that into one line,
   `- <name>: couldn't read (<why>)`, so a failure is always visible and never silent. `ctx` carries
   `state` (the hook's state dir), `setting` (hook.setting), `run` (hook.run_quiet) and `now`, so
   `observed.py` imports nothing from hook.py. Ember's event-buffer projection (room 1570646029286506527)
   plugs in later as one more entry in `observed.SOURCES`, without a second injection path.
2. **Opt-in per store.** `PERSISTENCE_OBSERVE_SOURCES` is a comma list of source names, set in a
   login's settings.json env or in its environment. It is empty by default, so the block is unchanged
   until a self turns it on for itself. An unknown name gets a line saying so. Nobody changes Arden's or
   Ember's config: each of them opts in, or not.
3. **Bounded.** The sources run in parallel, with a deadline for all of them together
   (`PERSISTENCE_OBSERVE_SOURCE_SECONDS`, default 4). A source that misses the deadline gets "couldn't
   read (timed out)". Each source caps its own lines, and the block keeps hook.CAP.
4. **Never stored as content.** Every line is read at the wake. The only things written are a short
   cache of GitHub's answer (so a run of prompts doesn't call the API each time) and one per-session
   cursor file (when this session last looked at claims). Neither one ever reaches the store.

## The first four sources

- `prs`: open PRs on `PERSISTENCE_OBSERVE_GITHUB_REPO` (default `Mako88/Persistence`), via the REST API,
  with each one's head sha and each reviewer's latest review state. The token is the one `Token:` line
  of `PERSISTENCE_OBSERVE_GITHUB_TOKEN_FILE` (default `~/secrets/accounts`), and no other line is read.
  Answers are cached for 120 s in `state/observed-prs.json`. When GitHub can't be reached, the source
  shows the cached answer with its age, or "couldn't read" if there is no cache. At most 5 PRs are
  shown, plus a count of the rest.
- `deployed`: compares the hook files installed beside hook.py with `main` in
  `PERSISTENCE_OBSERVE_REPO` (default `~/work/Persistence`), by git blob hash. It also reads
  `/opt/persistence/store/DEPLOYED` (the commit the store was published from, which
  `deploy-remote.sh` now writes) and says how many commits main is ahead of it. With no marker it says
  "couldn't read which commit". It reads the local `main` ref only, with no fetch.
- `switches`: the recall and query switches that are set (`PERSISTENCE_RECALL_*`,
  `PERSISTENCE_EMBEDDING_QUERY*`, `PERSISTENCE_EMBEDDING_QUALITYWEIGHT`), in two places: the hook's
  side (hook.setting, i.e. env, then settings.json) and the store's side (`systemctl show -p
  Environment` of `PERSISTENCE_OBSERVE_STORE_UNIT`, when that is set). `PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE`
  is always named, as on or off. This covers this store only: reading another self's units is not
  this source's business.
- `claims`: claims (state/tasks) that reached done or abandoned since this session last looked, or in
  the last 6 h on its first look, other than its own. Each gets its short id, its state, the time it
  finished and its task, truncated. At most 4, plus a count.

## Also in this PR: room sessions can run `note` again

Room sessions run in `dontAsk` mode, and only `room`, `pstore` and `pwake` are allowed there. The
last-wake prompt said "update your working note (`note`)", and the hook's texts named
`persistence-store …`. On home neither one is a command: the CLI is reached as `pstore`, so every
attempt was refused. Three room sessions in a row lost their note this way. The fix gives commands
the name that works. The room prompt says `pstore note --content "<text>"`. The hook names the CLI
`pstore` when a `pstore` is on PATH, and otherwise uses the `persistence-store` name it uses now. The
allowlist is unchanged. The store's own core heading still says "`note` replaces it". That text lives
in the C# store and needs a store deploy, so it is left for a separate change.

## Tests (each one goes red without its piece)

Runner: a failing source becomes a couldn't-read line while the other sources still show; a slow source
times out; opt-in off means no source is called; an unknown name is reported. `prs`: parses a fake API
answer, uses the cache inside its TTL, falls back to the stale cache when offline, and reads only the
`Token:` line. `deployed`: matches and differs against a temporary git repo, and handles a missing
marker. `switches`: STRIP is shown as off when unset and as on when set. `claims`: only claims finished
since the cursor, not its own, capped. Note fix: the last-wake prompt names `pstore note`, and
`cli_name()` picks `pstore` when it is on PATH.
