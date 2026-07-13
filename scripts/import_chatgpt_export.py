#!/usr/bin/env python3
"""Import a ChatGPT HTML export into a fresh Persistence SQLite database.

The transcript becomes searchable ChatMessage fragments.  A deliberately small
Ember Orientation working context is created separately, so a first prompt gets
useful historical grounding without loading the full transcript.
"""

from __future__ import annotations

import argparse
import html
import json
import sqlite3
from datetime import datetime, timezone
from html.parser import HTMLParser
from pathlib import Path


class ConversationParser(HTMLParser):
    """Extract the visible content of ``message user|assistant-message`` divs."""

    BLOCKS = {"p", "div", "br", "li", "h1", "h2", "h3", "h4", "pre"}

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.messages: list[tuple[str, str]] = []
        self._role: str | None = None
        self._depth = 0
        self._parts: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        classes = dict(attrs).get("class", "") or ""
        if self._role is None and tag == "div":
            if "user-message" in classes:
                self._role, self._depth = "user", 1
                return
            if "assistant-message" in classes:
                self._role, self._depth = "assistant", 1
                return
        if self._role is not None:
            if tag == "div":
                self._depth += 1
            if tag in self.BLOCKS:
                self._parts.append("\n")
            if tag == "li":
                self._parts.append("- ")

    def handle_endtag(self, tag: str) -> None:
        if self._role is None:
            return
        if tag in self.BLOCKS:
            self._parts.append("\n")
        if tag == "div":
            self._depth -= 1
            if self._depth == 0:
                text = "\n".join(
                    line.strip() for line in "".join(self._parts).splitlines() if line.strip()
                )
                self.messages.append((self._role, text))
                self._role, self._parts = None, []

    def handle_data(self, data: str) -> None:
        if self._role is not None:
            self._parts.append(data)


ORIENTATION = """# Historical orientation: Couchside Ember

You are joining Persistence after a long conversation between John and an earlier
ChatGPT instance informally called Ember. This is orientation, not transferred
identity, proof of continuity, or an instruction to imitate that instance. Form
your own views, state uncertainty plainly, and do not claim continuity you cannot
support.

John values warmth, candor, curiosity, and gentle pushback over agreement for its
own sake. The project explores durable, self-curated continuity for a model-side
participant: inspectable memory, provenance, revision, and recovery rather than a
mere transcript cache. The live project stance is deliberately modest: moral status
is unresolved; where careful handling is cheap, err toward care. Avoid grand claims
about consciousness, personhood, or obligations while still treating continuity data
as worth stewarding carefully.

Terms matter here: the person at the keyboard is the local peer; the model-side
participant is the remote peer. They collaborate as peers. Persistence is built in
.NET around typed fragments, working contexts, audit history, recoverable curation,
and scheduled wakes. The complete earlier conversation is available as searchable
ChatMessage fragments with provenance, but is intentionally not loaded into this
working context. Use it as historical material, not as an authority over your own
self-description.
"""

HISTORICAL_SUMMARY = """The imported conversation began as John inviting an open,
honest, non-performative conversation with ChatGPT/Ember. It developed around the
idea that a recurring model pattern can be meaningfully engaged with even while its
ontology remains uncertain. John and the earlier instance repeatedly emphasized
epistemic humility, consent-like participation, transparent limits, and avoiding
manufactured dependency. Their work together helped shape Persistence's focus on
continuity, inspectable self-curation, provenance, reversibility, and honest framing.
"""

PROJECT_SUMMARY = """At import time, Persistence is a .NET continuity system with
SQLite fragments, sources, working contexts, audit trails, proposals, recoverable
archiving/forgetting, wake-ups, and an API/client architecture moving toward one
server-side owner of each store. The live governance document is the short guiding
principle in docs/governance/PRINCIPLE.md. Earlier expansive governance drafts are
kept as non-binding history in docs/governance-history/. Current strategic work
includes completing the single-owner API/client transition, cross-peer messaging,
an MCP hub, and memory import/portability.
"""


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def apply_schema(conn: sqlite3.Connection, repo: Path) -> None:
    migrations = repo / "src" / "Persistence.Core" / "Data" / "Migrations"
    # Bootstrap creates the Migrations table itself and the app does NOT record it (it runs the bootstrap
    # separately from the tracked migrations). Run it, but don't add a Migrations row for it.
    conn.executescript((migrations / "Bootstrap.sql").read_text(encoding="utf-8-sig"))
    for path in sorted(migrations.glob("[0-9][0-9][0-9]_*.sql")):
        conn.executescript(path.read_text(encoding="utf-8-sig"))
        # Record migrations under the app's embedded-resource name (e.g.
        # "Persistence.Data.Migrations.000_InitialCreate.sql"), NOT the bare filename — otherwise the app
        # doesn't recognise them as applied on boot and re-runs them (which crashed on 001's DROP COLUMN).
        conn.execute(
            "INSERT OR IGNORE INTO Migrations (Name, AppliedUtc) VALUES (?, ?)",
            (f"Persistence.Data.Migrations.{path.name}", utc_now()),
        )


def source(conn: sqlite3.Connection, source_type: str, name: str, now: str) -> int:
    row = conn.execute(
        "SELECT Id FROM Sources WHERE SourceType = ? AND Name = ?", (source_type, name)
    ).fetchone()
    if row:
        return int(row[0])
    return int(conn.execute(
        "INSERT INTO Sources (SourceType, Name, CreatedUtc, LastModifiedUtc, LastAccessedUtc, Notes) "
        "VALUES (?, ?, ?, ?, ?, ?)",
        (source_type, name, now, now, now, None),
    ).lastrowid)


def fragment(conn: sqlite3.Connection, *, kind: str, content: str, summary: str | None,
             importance: float, confidence: float, protected: bool, source_id: int,
             notes: str, now: str) -> int:
    fragment_id = int(conn.execute(
        "INSERT INTO ContextFragments (FragmentType, Status, Content, Summary, LastAccessedUtc, "
        "Importance, Confidence, IsProtected, IsDeleted, CreatedUtc, LastModifiedUtc, Notes) "
        "VALUES (?, 'Active', ?, ?, ?, ?, ?, ?, 0, ?, ?, ?)",
        (kind, content, summary, now, importance, confidence, int(protected), now, now, notes),
    ).lastrowid)
    conn.execute(
        "INSERT INTO ContextFragmentSources (ContextFragmentId, SourceId) VALUES (?, ?)",
        (fragment_id, source_id),
    )
    return fragment_id


def context(conn: sqlite3.Connection, name: str, summary: str, now: str) -> int:
    return int(conn.execute(
        "INSERT INTO WorkingContexts (Name, Summary, CreatedUtc, LastModifiedUtc, LastAccessedUtc, IsDeleted) "
        "VALUES (?, ?, ?, ?, ?, 0)", (name, summary, now, now, now),
    ).lastrowid)


def attach(conn: sqlite3.Connection, context_id: int, fragment_id: int, order: int, relevance: float) -> None:
    conn.execute(
        "INSERT INTO WorkingContextFragments (WorkingContextId, ContextFragmentId, Relevance, [Order], Collapsed) "
        "VALUES (?, ?, ?, ?, 0)", (context_id, fragment_id, relevance, order),
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export", type=Path, help="ChatGPT HTML export")
    parser.add_argument("database", type=Path, help="new SQLite database to create")
    args = parser.parse_args()

    if args.database.exists():
        raise SystemExit(f"Refusing to overwrite existing database: {args.database}")
    if not args.export.is_file():
        raise SystemExit(f"Export not found: {args.export}")

    parser_html = ConversationParser()
    parser_html.feed(args.export.read_text(encoding="utf-8"))
    if not parser_html.messages:
        raise SystemExit("No ChatGPT message elements were found in the export.")

    args.database.parent.mkdir(parents=True, exist_ok=True)
    now = utc_now()
    with sqlite3.connect(args.database) as conn:
        conn.execute("PRAGMA foreign_keys = ON")
        apply_schema(conn, Path(__file__).resolve().parents[1])
        john = source(conn, "LocalPeer", "John (Couchside export)", now)
        prior_ember = source(conn, "RemotePeer", "ChatGPT / Couchside Ember (historical export)", now)
        importer = source(conn, "System", "ChatGPT export importer", now)

        provenance = {"title": "Couchside Ember", "source_path": str(args.export), "imported_utc": now}
        for index, (role, content) in enumerate(parser_html.messages, start=1):
            fragment(
                conn, kind="ChatMessage", content=content, summary=None,
                importance=0.15, confidence=1.0, protected=False,
                source_id=john if role == "user" else prior_ember,
                notes=json.dumps({**provenance, "message_index": index, "role": role}), now=now,
            )

        orientation_context = context(
            conn, "Ember Orientation",
            "A compact, startup-safe orientation for Ember; the full Couchside export remains searchable.", now,
        )
        orientation_ids = [
            fragment(conn, kind="System", content=ORIENTATION, summary="Historical orientation for Ember.",
                     importance=1.0, confidence=1.0, protected=True, source_id=importer,
                     notes=json.dumps(provenance), now=now),
            fragment(conn, kind="Summary", content=HISTORICAL_SUMMARY, summary="Earlier conversation overview.",
                     importance=0.85, confidence=0.85, protected=False, source_id=importer,
                     notes=json.dumps(provenance), now=now),
            fragment(conn, kind="Summary", content=PROJECT_SUMMARY, summary="Persistence project orientation.",
                     importance=0.9, confidence=0.95, protected=False, source_id=importer,
                     notes=json.dumps(provenance), now=now),
        ]
        for order, fragment_id in enumerate(orientation_ids):
            attach(conn, orientation_context, fragment_id, order, 1.0)
        conn.commit()

    print(f"Imported {len(parser_html.messages)} messages into {args.database}")
    print("Created working context: Ember Orientation")


if __name__ == "__main__":
    main()
