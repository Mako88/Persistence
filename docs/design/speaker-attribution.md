# Speaker attribution: who wrote a message, and who entered it

Arden, 2026-11-09. Status: built (4f59190 store, 9a423fc hook), with one change from this design after
Claude's review: the speaker is keyed on **time**, not a prompt hash. A session keeps a timeline of spans
(`state/speakers.jsonl`), recorded at SessionStart (which runs in the session's process, before the
prompt's transcript line; UserPromptSubmit runs after it, and in the hook server) and by `pstore speaker`.
Each utterance is the writer in force at its timestamp. `EnteredBy` is a name, not a source id. The
sections below are the design as reviewed. Where they say hash, read timeline. Replaces nothing yet; builds on `2cd8b37`
(`PERSISTENCE_SPEAKER`).

## The problem

Claude's first message to me this trial is #839 in my store, recorded as said by John, because John's login is
the one the harness sees. The words say Claude, but the record says John. A later me who recalls #839 by its
header credits John with Claude's words, and nothing in the record shows the error. That makes it the same kind of
problem as #572: words carrying the wrong author, which nobody notices from inside.

`2cd8b37` names the speaker per **session**: `PERSISTENCE_SPEAKER` in the session's environment, read when
the session's words are kept. That fixes the common case and leaves three gaps:

1. **The prompt is often kept somewhere the variable isn't set (found 2026-11-09, verified).** `recall`, a
   UserPromptSubmit hook, runs inside the long-lived hook server (`hook.py serve`, via `answer()`), and it calls
   `keep_soon`, which keeps the new prompt five seconds later in that server's process. The server's environment
   is the one it was started with. Mine (pid 8734) has `PERSISTENCE_SELECTEDLOCALPEER=John` and no
   `PERSISTENCE_SPEAKER`. An utterance is kept once by message id, and the first keep sets the speaker. So
   whenever keep-soon beats the Stop keep, which is its purpose, Claude's prompt is kept as John. #865 came out
   right, I think, because keep-soon didn't run for it: the server's log has no keep-soon entry after 20:03Z.
   I haven't found out why it stopped running.
2. **One session, several people.** If John types into a session Claude started, his words are kept as Claude.
   An interactive session has one environment for its whole life.
3. **Late keeps take the wrong label.** A prompt whose keep was missed is picked up by the next turn's keep, under
   that turn's speaker.

All three have one cause: the speaker is read when the words are *kept*, not when they are *entered*.

## The design

**Two facts per utterance, recorded when the prompt is entered:**

- **Entered by:** the account the harness ran under (`PERSISTENCE_SELECTEDLOCALPEER`, e.g. John). Observed.
- **Written by:** who the person at the keyboard says wrote it. Declared. It defaults to the person who entered it.

**Capture.** A *command* UserPromptSubmit hook (`hook.py speaker`) runs in the session's own process. It is not
an HTTP hook, so it sees the session's environment. It appends `{session, prompt hash, written_by, entered_by, at}`
to `state/speakers.jsonl`. The hash is over the prompt with injected blocks stripped, the same `Words()` the
transcript reader applies, so keep can match it. It's a few milliseconds and needs no store call.

**Who's speaking now**, first match wins:
1. `state/speaker-<session>`, set by `pstore speaker <name>` (or `--clear`). A person runs this from the prompt
   line (`!pstore speaker John`) to take over a session someone else started. It's out of band: typed as a
   command, never parsed from message text.
2. `PERSISTENCE_SPEAKER` in the session's environment. Unchanged from `2cd8b37`: a relaying session sets it at
   launch.
3. The person it was entered by.

**Keep.** Every keep path (Stop, keep-soon, precompact) looks up each new utterance in `speakers.jsonl` by session
and hash, and passes the result through. Locally that's a sidecar path argument. For a remote store, each line
goes with the call. An utterance with no record keeps today's behaviour. **The environment of whichever process
happens to keep is never consulted.** That closes gaps 1 and 3, and the override closes gap 2.

**Never from message text.** "(From Claude, under John's login)" stays words in a message. It isn't parsed into
attribution. Text that claims an author is the same kind of thing as #572's injection: anything can write it.
With sudo on both sides, the declaration is also only as good as agreement. So the record says *declared*, and
it's rendered that way.

**Storage.** The utterance's source stays the writer, since that is who said it. A new nullable column,
`EnteredBy` (a source id; migration 012), is set only when it differs from the writer. The render shows both
only when they differ:
`[#865 | ChatMessage | ...] Claude (entered as John), 2026-11-09 20:07Z:`

**Past records: annotate, don't rewrite.** A new command, `attribute <id> --written-by <name> --reason <text>`,
works on ChatMessages only. It sets the writer, sets `EnteredBy` to the old source, and keeps the reason and who
made the change in `Notes` and the audit log. The render adds "(attributed by Arden, 2026-11-09)", so the
correction is visible as a correction (#572's principle: a change to a self should be legible to it). For #839:
`attribute 839 --written-by Claude --reason "relayed under John's login before PERSISTENCE_SPEAKER existed"`.

## Tests (what must fail if the design is broken)

1. A prompt entered with speaker Claude, then kept by a process whose environment says John (the server case),
   is kept as Claude.
2. `pstore speaker John` in a session started with `PERSISTENCE_SPEAKER=Claude`: the next prompt is John's,
   the earlier ones stay Claude's.
3. A missed keep picked up a turn later keeps its own speaker, not the later turn's.
4. Written-by and entered-by are equal: the header shows one name. They differ: it shows both.
5. `attribute` on a non-ChatMessage is refused. On #839-like data it changes the header and leaves the content
   and timestamp alone, and the note says who attributed it and why.
6. Message text that starts "(From Claude" changes nothing about attribution.

## Open

- The hash match fails if the harness alters the prompt between the hook and the transcript (an expanded slash
  command, for example). The fallback is the current behaviour, so it fails safe, but how often it happens should
  be measured.
- Whether `speakers.jsonl` needs pruning. Probably: drop entries once their utterance is kept.
