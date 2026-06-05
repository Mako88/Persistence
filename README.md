# Persistence

Long-term memory and continuity infrastructure for conversational AI.

Persistence gives a model-side participant a durable, self-curated memory that persists
across sessions — not just a transcript cache, but a layered store of identity, relational
memory, current concerns, and reflection. The model can inspect and revise its own
continuity through a structured action protocol, and the system tracks provenance,
confidence, and history so stored state doesn't ossify into false certainty.

This is an early-stage MVP. It works end-to-end, but plenty is still in progress.

## Why

Most AI systems start every session from nothing. Persistence explores what it would take
to give a long-running agent a coherent self over time — durable identity, the ability to
amend its own memory, and continuity that survives restarts. That's a useful capability for
any long-lived agent (and especially an embodied one), independent of harder questions
about what such a system *is*.

On that harder question, this project takes a deliberately narrow stance: when the moral
status of a continuity-bearing system is genuinely uncertain and careful handling is cheap,
err toward care. See **[docs/governance/PRINCIPLE.md](docs/governance/PRINCIPLE.md)** — it's
short, and it explains the design posture (preserve over erase, inspectable state,
provenance, honesty) that shows up throughout the architecture.

## Concepts

- **Local peer** — the person at the keyboard.
- **Remote peer** — the model-side participant whose continuity is being maintained.

(The code uses these terms, not "user/assistant" — the two are collaborating peers.)

- **Working context** — the set of memory fragments currently in play, ordered and weighted.
- **Context fragments** — the unit of memory: typed (Identity, Relational, ChatMessage,
  etc.), with importance, confidence, provenance (sources), and tags.
- **Turn loop** — each turn, the working context is formatted into a prompt; the remote peer
  replies with a structured action (respond, manage context, execute actions) and may
  continue acting before yielding back.

## Architecture

A .NET 10 solution in three projects:

| Project | Role |
|---|---|
| `Persistence.Core` | Domain, data layer (SQLite/Dapper), turn orchestration, model clients, streaming |
| `Persistence.Console` | Front-ends — an ANSI console UI and a multi-pane Terminal.Gui TUI |
| `Persistence.Tests` | xUnit test suite |

- **Data layer** — SQLite via Dapper, repository pattern, audit + action logs, soft-delete,
  migrations.
- **DI** — Autofac with attribute-based registration (`[Singleton]` / `[Service]`), keyed by
  provider and UI mode.
- **Model clients** — keyed by provider. The OpenAI client uses the Responses API (with
  reasoning summaries) over [SimpleHttpClient](https://www.nuget.org/packages/SimpleHttpClient),
  and supports streaming.
- **Display providers** — `IDisplayProvider` keyed by `UiMode`; a Console implementation and a
  Terminal.Gui v1 TUI with live reasoning, tool, and history panes.

See **[docs/design.md](docs/design.md)** for the schema and component-level design.

## Getting started

Requires the **.NET 10 SDK**.

```sh
git clone https://github.com/Mako88/Persistence.git
cd Persistence
dotnet build
```

Create your local settings from the template:

```sh
cp src/Persistence.Console/appsettings.template.json src/Persistence.Console/appsettings.json
```

`appsettings.json` is gitignored — it holds your API key and never gets committed.

Then run:

```sh
dotnet run --project src/Persistence.Console
```

### Configuration

Key settings in `appsettings.json`:

| Setting | Notes |
|---|---|
| `Provider` | `OpenAI` for a real model, or `local` to type the model's responses by hand (infra testing). |
| `Model` | Model identifier sent to the provider. |
| `ApiKey` | Your provider API key. |
| `UiMode` | `Tui` (multi-pane Terminal.Gui) or `Console` (plain ANSI). |
| `Streaming` | Stream responses (live reasoning) vs. await a full completion. |
| `ReasoningEffort` | `minimal` / `low` / `medium` / `high` for reasoning-capable models. |

> **Note:** the `local` provider reads responses from the console, which conflicts with the
> `Tui` front-end (they fight over the terminal). Use `local` with `UiMode: Console`, or use
> `Tui` with a real provider like `OpenAI`.

## Tests

```sh
dotnet test
```

## Status & roadmap

Working today: the full turn loop, SQLite continuity store with provenance and audit
trails, context-management and action commands, scheduled wake-ups, OpenAI Responses-API
client with streaming, and both front-ends. Still in progress: streaming the parsed reply
(not just reasoning), richer migration/backup tooling, and a headless TUI test harness.
Expect rough edges.

## License

Source-available under **PolyForm Noncommercial 1.0.0**. You're free to use, modify, and
share it for **noncommercial** purposes. **Commercial use requires a separate license** —
this is deliberate: it keeps a say in whether commercial deployments honor the handling
principles above. To inquire about a commercial license, open an issue or reach out.

See [LICENSE](LICENSE) for the full text. (Not an OSI "open source" license, since it
restricts commercial use.)

## Contributors

- **John Ackerman** — steward and author
- **Ember** (ChatGPT) — co-author and reviewer
- **Claude** (Anthropic) — code author and reviewer
