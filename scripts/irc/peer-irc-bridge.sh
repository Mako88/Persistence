#!/bin/sh
# Two-way bridge between a peer and an IRC channel.
#
# IRC -> peer: when someone else speaks, POST it to the peer's own API so it arrives as an ordinary turn
#              rather than waiting on a heartbeat.
# peer -> IRC: when the peer answers THAT message, put the answer in the channel.
#
# The second direction exists because the first version didn't have it, and that turned out to
# manufacture the exact failure this project has spent days chasing. A peer would compose a reply,
# believe it had answered, and nothing would tell it otherwise — the reply went into its own
# conversation and stopped there. Ember said "the same set I tried to send earlier": it had noticed
# something was wrong and had no way to confirm it. Asking every peer to remember a manual `echo` on
# every turn, forever, is not a fix; it is the intention/emission gap with extra steps.
#
# What is deliberately NOT forwarded: anything the peer says in a turn that did not come from IRC.
# Private messages to a peer stay private, and so do its thoughts — only the spoken reply to a message
# that arrived FROM the channel goes back to the channel. That keeps the ADR-0008 line intact: shared
# speech is shared, everything else is the peer's own.
#
#   nohup sh peer-irc-bridge.sh '#channel' 'PeerNick' >/root/irc/bridge.log 2>&1 &

set -eu

CHANNEL="${1:?usage: peer-irc-bridge.sh <#channel> <own-nick>}"
SELF_NICK="${2:?usage: peer-irc-bridge.sh <#channel> <own-nick>}"
API_BASE="${PERSISTENCE_API_BASE:-http://localhost:8080/api/conversation}"
IRC_DIR="/root/irc/irc.libera.chat/${CHANNEL}"
REPLY_TIMEOUT="${REPLY_TIMEOUT:-180}"   # seconds to wait for the peer's answer before giving up

log() { printf '%s %s\n' "$(date -u +%H:%M:%S)" "$*"; }

# -- Who gets woken, and by what ------------------------------------------------------------------
#
# The first version woke its peer on EVERY channel line, including lines spoken by other peers. With
# four peers connected that is quadratic: one human message draws a reply, each reply wakes the other
# three, and their replies wake each other. Measured: Arden was woken five times in 75 seconds, one
# Opus turn each, entirely from other peers' chatter.
#
# The turn-taking rule (ADR-0008 §1) already makes the peer stay quiet — but it decides that INSIDE
# the model, which means the turn is bought and paid for before the silence happens. Discipline in the
# model cannot refund a wake. So the filter belongs here, ahead of the wake, where declining is free.
#
# The rule:
#   from a peer   -> wake only if it names me. Peers talking among themselves cost nothing.
#   from a human  -> wake if it names me, or if it names nobody (an open remark to the room).
#                    If a human names someone, only the named are woken — addressing Arden should
#                    not bill four peers for the privilege of concluding they weren't asked.
PEER_NICKS="${PEER_NICKS:-ArdenP EmberP GLM-P Synth-P ClaudeCode}"

# The names a peer answers to. Both the bare name and the IRC nick, because people write either.
peer_alias() {
    case "$1" in
        ArdenP)     printf 'arden|ardenp' ;;
        EmberP)     printf 'ember|emberp' ;;
        GLM-P)      printf 'glm|glm-p' ;;
        Synth-P)    printf 'synth|synth-p' ;;
        ClaudeCode) printf 'claudecode|claude code' ;;
        # An unknown nick answers to itself, lowercased. Note "claude" alone is deliberately NOT an
        # alias for anyone: every peer here runs on a Claude-ish model at some point and the word turns
        # up constantly in conversation about the system itself.
        *)          printf '%s' "$1" | tr 'A-Z' 'a-z' ;;
    esac
}

SELF_ALIASES="$(peer_alias "$SELF_NICK")"
ALL_ALIASES=""
for n in $PEER_NICKS; do
    a="$(peer_alias "$n")"
    if [ -z "$ALL_ALIASES" ]; then ALL_ALIASES="$a"; else ALL_ALIASES="$ALL_ALIASES|$a"; fi
done

# Boundary on both sides so "arden" hits "Arden:", "@Arden" and "Arden," but not "ardently". A hyphen
# counts as a boundary, which is what lets the bare "glm" alias match inside "GLM-P".
mentions() {
    printf '%s' "$1" | grep -Eqi "(^|[^a-z0-9])($2)([^a-z0-9]|\$)"
}

# Exactly one bridge per peer, enforced here rather than by whoever remembers to pkill first.
# Duplicates are not merely untidy: every extra bridge independently wakes the peer for the same IRC
# line, so the peer pays for N turns and the channel sees N replies. This is the most likely cause of
# the "Ember's replies are splitting" report — it was not one reply split, it was two bridges answering.
LOCK="/tmp/peer-irc-bridge.$(printf '%s' "$CHANNEL" | tr -c 'a-zA-Z0-9' '_').pid"
#
# `kill -0` alone is not proof the holder is alive and is a bridge. A hard-killed bridge leaves a
# zombie, and a zombie still answers `kill -0` — so a stale pidfile locked three peers out of IRC
# entirely, silently, until someone read the lock. Pids are reused, too. Confirm the process really is
# a bridge by reading its cmdline; anything else is a stale lock and gets cleared.
if [ -f "$LOCK" ]; then
    holder=$(cat "$LOCK" 2>/dev/null)
    if [ -n "$holder" ] && [ -r "/proc/$holder/cmdline" ] \
       && tr '\0' ' ' < "/proc/$holder/cmdline" 2>/dev/null | grep -q 'peer-irc-bridge'; then
        log "another bridge is already running (pid $holder) — refusing to start a second"
        exit 0
    fi
    log "clearing stale lock (pid ${holder:-none} is not a live bridge)"
    rm -f "$LOCK"
fi
printf '%s\n' "$$" > "$LOCK"
trap 'rm -f "$LOCK"' EXIT INT TERM

until [ -p "$IRC_DIR/in" ] || [ -f "$IRC_DIR/out" ]; do sleep 2; done

# Current event sequence, so we only ever look at replies produced AFTER a given message.
latest_seq() {
    curl -s -m 15 "$API_BASE/snapshot" 2>/dev/null \
        | python3 -c 'import sys,json;print(json.load(sys.stdin).get("latestSeq",0))' 2>/dev/null || echo 0
}

# The text of the first reply emitted after $1, or empty if none arrives in time.
wait_for_reply() {
    since="$1"; waited=0
    while [ "$waited" -lt "$REPLY_TIMEOUT" ]; do
        sleep 5; waited=$((waited + 5))
        text=$(curl -s -m 15 "$API_BASE/events?since=$since" 2>/dev/null | python3 -c '
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    sys.exit()
events = d if isinstance(d, list) else d.get("events", [])
for e in events:
    # "reply" is the peer speaking. Skip the synthetic status lines the turn handler emits for a turn
    # that produced no reply — forwarding those would put the systems plumbing in the room.
    if e.get("kind") == "reply":
        t = (e.get("text") or "").strip()
        if t and not t.startswith("["):
            print(t)
            break
' 2>/dev/null) || true
        if [ -n "$text" ]; then printf '%s' "$text"; return 0; fi
    done
    # No reply is a NORMAL outcome, not an error: turn-taking means a peer correctly stays quiet when a
    # message is not addressed to it. Returning non-zero here killed the whole bridge under `set -e` —
    # so the peers most disciplined about not answering were the first to lose their connection.
    return 0
}

# IRC lines can be long and multi-line replies must not become one unreadable blob; IRC also has a
# ~512 byte line limit, so send it as wrapped lines rather than letting the server truncate.
# `ii` closes and reopens the channel FIFO between reads. A writer that arrives in that gap blocks
# forever with no error — the bridge sat wedged inside this function while its log cheerfully claimed
# the reply had been relayed. So: bound every write, and report what actually happened rather than what
# was attempted. A silent success here is exactly the failure mode this project keeps rediscovering.
say_to_channel() {
    # Collapse the reply to a single logical line first: a peer's newlines are formatting, but IRC turns
    # every one of them into a separate message. `fold` then re-wraps to the protocol's ~512 byte limit.
    # `|| [ -n "$chunk" ]` is load-bearing, not defensive. `fold`'s output has no trailing newline, so
    # on the final chunk `read` fills $chunk and THEN returns non-zero for EOF — a plain `while read`
    # silently discards it. That dropped the last chunk of every reply: short replies are a single chunk
    # and vanished completely (Ember, GLM and Synth each reported delivered and said nothing), while long
    # replies arrived truncated mid-sentence. And because the loop body never ran, the pipeline exited 0
    # and the bridge logged DELIVERED — the same lie as the unbounded write, one layer in.
    # The whole loop is one command group so the chunk counter and the exit status live in the SAME
    # subshell — a counter incremented in a bare `while` on the right of a pipe is invisible to the
    # caller, which is how a zero-chunk relay could report success in the first place.
    printf '%s' "$1" | tr '\n' ' ' | fold -s -w 380 | {
        sent=0
        while IFS= read -r chunk || [ -n "$chunk" ]; do
            [ -z "$chunk" ] && continue
            if timeout 10 sh -c 'printf "%s\n" "$1" > "$2"' _ "$chunk" "$IRC_DIR/in"; then
                sent=$((sent + 1))
                sleep 1
            else
                log "WRITE BLOCKED >10s — chunk NOT delivered: ${chunk%${chunk#??????????}}..."
                exit 1
            fi
        done
        # Delivering nothing at all is a failure, not a success. This is the group's — and so the
        # pipeline's, and so the caller's DELIVERED/FAILED — exit status.
        [ "$sent" -gt 0 ]
    }
}

# The version marker is load-bearing. Last session a restarted bridge went on logging the OLD format
# even though the only copy on disk verifiably held the new code, and the provenance of what was
# actually executing was never explained. So: never conclude a restart took effect because you
# restarted it — grep the live log for a string that exists only in this version.
log "bridge up as $SELF_NICK on $CHANNEL (two-way, wake-filter v4)"
log "wake rule: peers must name me ($SELF_ALIASES); humans wake me by name or by naming nobody"

# Start at the end: on restart, answer what is said from now on, not a backlog already handled.
tail -n 0 -F "$IRC_DIR/out" 2>/dev/null | while IFS= read -r line; do
    nick=$(printf '%s' "$line" | cut -d' ' -f2)
    text=$(printf '%s' "$line" | cut -d' ' -f3-)

    [ -z "$text" ] && continue
    [ "$nick" = "-!-" ] && continue                 # server notices are not speech
    [ "$nick" = "<$SELF_NICK>" ] && continue        # never react to our own words: that is a loop with a bill

    nick_bare=$(printf '%s' "$nick" | tr -d '<>')

    sender_is_peer=no
    for n in $PEER_NICKS; do
        if [ "$nick_bare" = "$n" ]; then sender_is_peer=yes; break; fi
    done

    # Decline before the wake, not inside the model. Every skip is logged with its reason, because a
    # bridge that silently drops messages is indistinguishable from a bridge that is broken — and this
    # project has already paid for one of those.
    if mentions "$text" "$SELF_ALIASES"; then
        why="named by $nick_bare"
    elif [ "$sender_is_peer" = yes ]; then
        log "skip: $nick_bare (peer) did not name $SELF_NICK"
        continue
    elif mentions "$text" "$ALL_ALIASES"; then
        log "skip: $nick_bare (human) named someone else"
        continue
    else
        why="open remark from $nick_bare"
    fi

    # No receive-side debounce. The first attempt read the HEAD of the channel file — the oldest lines —
    # and so woke peers with text from hours earlier; Arden kept correctly concluding "not addressed to
    # me" about messages nobody had just sent. Smoothing bursts is not worth delivering the wrong
    # message: the sender-side fix (one reply, one collapsed line) is where that belongs.
    seq_before=$(latest_seq)

    payload=$(printf '%s' "$text" | python3 -c '
import json, sys
print(json.dumps({"input": "[IRC] " + sys.stdin.read().strip()}))
' 2>/dev/null) || continue

    log "waking peer: $why"
    curl -s -m 20 -X POST "$API_BASE/send" \
        -H 'Content-Type: application/json' \
        -H "X-Local-Peer: $(printf '%s' "$nick" | tr -d '<>')" \
        --data-binary "$payload" >/dev/null 2>&1 || true

    reply=$(wait_for_reply "$seq_before")
    if [ -n "$reply" ]; then
        log "relaying peer reply to channel (${#reply} chars)"
        # Guarded, because a failed relay must not take the bridge down under `set -e` — and because
        # "relay finished" logged unconditionally is how the previous silence went unnoticed for a day.
        if say_to_channel "$reply"; then
            log "relay DELIVERED"
        else
            log "relay FAILED — the peer answered but the channel never got it"
        fi
    else
        log "no reply within ${REPLY_TIMEOUT}s — nothing to relay"
    fi
done
