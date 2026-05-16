# InfiniContext

**A specialized, opinionated operations tool for long-running autonomous AI agents — agents that work on problems bigger than one context window, supervised by a human watching many projects at once.**

## The vision

Today's coding agents have a hard ceiling: the context window. When it fills, the agent forgets what it was doing — there's no clean "save and continue tomorrow." Around that limit grows a second problem: a human babysitting one agent in one terminal doesn't scale, but stepping away entirely means tool approvals stall and runaway sessions burn budget. InfiniContext is the missing layer.

- **Beyond the context window.** A *Task* is the unit of work; it outlives any single session. Agents read and edit durable markdown artifacts on disk. When context pressure approaches the limit the agent hands off cleanly, and a fresh session picks up where the last one left off — the file system is the working memory, not the LLM's KV cache.
- **Supervise many projects, react to events.** One human watches multiple tasks across multiple projects from a single UI. Agent stops — tool-approval requests, context-pressure flushes, errors, completions — surface as discrete events the supervisor reacts to, without sitting in any one session's terminal.

The bet: most real engineering work is bigger than one context window and benefits from multiple agents working concurrently. The current standard — open a terminal, paste a prompt, watch what happens — does not scale past a hobbyist workflow. InfiniContext is what comes after.

**Today, this means Claude Code.** InfiniContext is agent-host-agnostic in spirit but concrete in practice: the only wire protocol we currently target is `claude --output-format stream-json`. As other agents mature similar interfaces (or as MCP standardizes more of the loop), the host layer can be extended; the durable-task and supervisor layers are already model-agnostic.

## Where we are

- **Stage 1 (done):** single-instance console REPL. Spawns `claude` in stream-json mode, parses the NDJSON event stream, drives a session state machine, renders to its own UI, mediates tool approvals via an MCP sidecar.
- **Stage 2A (done):** task management. Markdown artifacts at `<cwd>/.task/<id>-<slug>/`, identity in SQLite, `/task new` / `list` / `show` / `fork` / `merge` / `switch` / `delete`, parallel sessions coordinated by an MCP lock, context-pressure thresholds that nudge agents toward clean hand-offs before overflow.
- **Stage 2B (in progress):** multi-session console UI, macOS supervision, CI matrix. Phases 1–7 shipped on `main`; Phase 8 (CI matrix + exit-criteria smoke) is in design.
- **Stage 3 (planned):** Avalonia GUI with a per-task tree view spanning multiple projects — the first real surface for the "supervise many projects at once" point.

## Long-range, not in current scope

Fleet-scale operation — a distributed task store, multi-tenant identity, an agent worker pool, the supervisor as a console over many machines — is a horizon we keep the architecture honest against (durable artifacts on disk, per-session MCP sidecars, a shared lock primitive, clean session lifecycle) but **we are not actively building toward it**. Before we run, we learn to walk: one developer's box first, then a small team's, before anything multi-tenant or multi-host. The cluster direction is a north star, not a roadmap item.

## What's a Task?

A **Task** is the unit of work that outlives any single session. Claude Code has *Sessions* (one process, one growing context, ends when the window fills); InfiniContext adds *Tasks* on top — many sessions can serve one task, picking up where the last one left off via durable markdown artifacts the agent reads and edits in place. A task lives at `<repo>/.task/<id>-<slug>/` with three mandatory files (`index.md`, `description.md`, `progress.md`) and any number of optional ones the agent creates as the work demands.

InfiniContext exposes durable-task lifecycle through first-class `/task ...` commands and an auto-installed user-level skill that teaches every session how to work the artifact contract.

## Build & test

The solution is `InfiniContext.slnx`. Target framework is `net10.0`.

```
dotnet build InfiniContext.slnx
dotnet test  InfiniContext.slnx
dotnet run --project src/InfiniContext.Cli                # REPL in the current directory
dotnet test --filter FullyQualifiedName~ReplSmokeTests # one test class
```

REPL commands once the host is running:

Type `/help` inside the REPL for the full list, and `/help <topic>` (e.g. `/help task`,
`/help approval`) for per-command details. Unknown `/cmd` lines are forwarded verbatim to
claude — its own slash-commands like `/clear` or `/compact` reach claude as expected.

```
/task new "<description>"             create a task; light model picks the title
/task list [--status <s>] [--limit N] list tasks (most-recent first)
/task show <id>                       show one task and its artifacts
/task fork <id> ["<title>"]           fork; lineage recorded
/task merge <id1> <id2> [...]         deterministic concat (MVP)
/task switch <id>                     start a fresh session bound to <id>
```

## Where to read next

- **Architecture:** [`docs/infinicontext-initial-design.md`](docs/infinicontext-initial-design.md) — wire protocol, parser tolerance contract, state machine, MCP approval flow, process supervision, version-drift strategy, stage 1/2A/2B/3 plan. The "why" behind the code.
- **Agent contract:** [`CLAUDE.md`](CLAUDE.md) — what Claude Code (this repo's primary author) is told about the code, conventions, and constraints. Read it before non-trivial changes.
