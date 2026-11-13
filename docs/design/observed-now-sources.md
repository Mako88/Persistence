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

1. **One interface, structured (the seam agreed with Ember, room 1570646253388177512).** A source is
   `Source(read, ack=None)`. `read(event, ctx) -> Observation`, a frozen record: `source`, `observed_at`,
   `status` (`ok | unknown | error`), `freshness` (when the data was true at its source, e.g. a cache's
   time), bounded `lines` (facts without "- "), `reason` (why unknown/error, or what made ok data stale),
   optional `event_range` (oldest, newest of the events shown), `truncated` (items left out), and `ack`
   (what the source's ack would commit; never rendered). A source that raises `observed.Unreadable` is
   `unknown` with its reason; any other exception is `error` with the exception's type only (never its
   text, which can carry a URL); a read that returns anything but an Observation is `error`.
   `observed.render()` is the one place an Observation becomes text: ok is its lines (with "as of <age>
   ago" when the data is over 60 s old); unknown and error are `- <source>: <status> (<reason>)` followed by
   whatever was read; at most 6 lines of 200 characters each, then `- <source>: N more not shown`. So
   "couldn't read" is a status, never a line a source writes. `ctx` carries `state`, `setting`, `run`,
   `now`, so `observed.py` imports nothing from hook.py. Ember's event-buffer projection plugs in as one
   more `Source`: a read projecting "new since cursor" with `event_range`, an ack advancing the cursor.
2. **Opt-in per store.** `PERSISTENCE_OBSERVE_SOURCES` is a comma list of source names, set in a
   login's settings.json env or in its environment. It is empty by default, so the block is unchanged
   until a self turns it on for itself. An unknown name gets a line saying so. Nobody changes Arden's or
   Ember's config: each of them opts in, or not.
3. **Bounded.** The sources run in parallel, with a deadline for all of them together
   (`PERSISTENCE_OBSERVE_SOURCE_SECONDS`, default 4). A source that misses the deadline gets "couldn't
   read (timed out)". Each source caps its own lines, and the block keeps hook.CAP.
4. **Reads write nothing; acks come after injection.** A read never writes: not the claims cursor, not
   the PR cache. What a source would persist rides in `Observation.ack`. `hook.observe_hook` (and
   `subagent_start`) prints the block, flushes, and only then calls `observed.acknowledge()` for the ok
   observations whose every rendered line is in the text actually sent (a block cut by `CAP` acks
   nothing it cut). A failed wake, or an ack that fails, shows the same observation again next time:
   at-least-once. Under `hook.py serve`, "sent" means written to the request's reply buffer, the nearest
   point this process can see. A read past the deadline is reported `unknown (timed out)`; its thread
   can't be cancelled, but since reads write nothing and its result is not in the returned list, a late
   finish changes nothing and is never acked. Nothing a source reads or writes reaches the store.

## The first four sources

- `prs`: open PRs on `PERSISTENCE_OBSERVE_GITHUB_REPO` (default `Mako88/Persistence`), via the REST API,
  with each one's head sha and each reviewer's latest review state. The token is the one `Token:` line
  of `PERSISTENCE_OBSERVE_GITHUB_TOKEN_FILE` (default `~/secrets/accounts`), and no other line is read.
  A live answer's ack caches it for 120 s in `state/observed-prs.json` (mode 600; only numbers, titles,
  short heads and review states, never the token). The cache is ignored unless this user owns it and no
  one else can write it. A cached answer carries its time as `freshness`. When GitHub can't be reached,
  the source shows the cached answer with its age (and does not re-ack it), or is `unknown` if there is none. At most 5 PRs are
  shown, plus a count of the rest.
- `deployed`: compares the hook files installed beside hook.py with `main` in
  `PERSISTENCE_OBSERVE_REPO` (default `~/work/Persistence`), by git blob hash. It also reads
  `/opt/persistence/store/DEPLOYED` (the commit the store was published from, which
  `deploy-remote.sh` now writes) and says how many commits main is ahead of it. With no marker it is `unknown`, and the hooks line still shows. It reads the local `main` ref only, with no fetch.
- `switches`: the recall and query switches that are set (`PERSISTENCE_RECALL_*`,
  `PERSISTENCE_EMBEDDING_QUERY*`, `PERSISTENCE_EMBEDDING_QUALITYWEIGHT`), in two places: the hook's
  side (the environment the hook's recall reads, as recall reads it) and the store's side (`systemctl show -p
  Environment` of `PERSISTENCE_OBSERVE_STORE_UNIT`, when that is set). `PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE`
  is always named, as on or off. This covers this store only: reading another self's units is not
  this source's business.
- `claims`: claims (state/tasks) that reached done or abandoned after this session's cursor and at or
  before the read's `now` (the last 6 h when there is no cursor), other than its own. The ack sets the
  cursor to the read's `now`, not the ack's, so a claim finishing between read and ack shows next time.
  `event_range` is the oldest and newest finish shown. Each gets its short id, its state, the time it
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

Runner: failures are statuses (Unreadable → unknown, other exceptions → error, a non-Observation → error)
and the rest still show; a slow source is `unknown (timed out)` within the deadline; opt-in off calls no
source; an unknown name is an error. Render: each status, empty ok, staleness, the line and length bounds,
truncation. Purity: every source in `SOURCES`, read twice, gives equal Observations and leaves the state
dir byte- and mtime-identical (a new source must join this test). Ack: only ok observations with an ack;
a failing ack doesn't stop the rest; the hook acks after printing and not what `CAP` cut; a claim
finishing between read and ack isn't lost; without an ack the next wake sees the same claims. `prs`:
parsing, cache written by the ack not the read, cache owner-only and token-free, a group-writable cache
ignored, the stale fallback with its age and no exception text, no token → unknown without secrets.
`deployed`, `switches`: as before, now with statuses. Note fix: as before. Each guarantee was also broken
on purpose (21 mutants), and each break turned a test red.
