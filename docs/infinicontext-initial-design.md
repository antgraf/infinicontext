# InfiniContext — Claude Code Multi-Instance Host (C#) — Architecture Design Document

**Audience:** Architect / design agent picking up this doc to produce a detailed component design and implementation plan.

**Status:** Pre-design, post-first-review. This document captures the recommended architecture, the constraints that drove it, and a three-stage POC plan. Items marked **OPEN** are unresolved and need a concrete answer.

**Revision history:**
- v1 — initial high-level design.
- v2 (2026-05-06) — incorporated critical review: persistence now reads from Claude Code's own session store, scope tightened to Windows-only for the POC, TUI mode moved to an appendix, state machine expanded, parser tolerance contract made explicit, three-stage POC plan added (§9).
- v3 (2026-05-06) — Mac added as a target platform (initial implementation Windows-first, but stack chosen for Mac); UI stack switched from WPF to **Avalonia**; **TUI mode dropped entirely** — the structured event stream is rendered natively in the host UI (collapsible tree view), making a terminal embedding unnecessary; logging requirements added (§7.4) — every run writes to a timestamped file in `./logs/` (gitignored); testing section expanded (§7.5).
- v3.1 (2026-05-06) — §7.3 reworked: dropped the "pin a single `claude` version" framing in favour of a `MinClaudeVersion` (hard fail below) + `TestedAgainstVersion` (the version Stage 0 fixtures were recorded against — mismatch surfaces a warning and the schema-drift counter does its job). A hard pin doesn't survive contact with developers and users on different installs; the parser tolerance contract (§4.2) and the drift counter are the real safety net. Stage 1 form (REPL) closed in `decisions.md`.
- v3.2 (2026-05-06) — §4.2 event taxonomy rewritten against actual Stage 0 recordings (`claude` 2.1.112). Key corrections: (a) **no top-level `text_delta` events in default mode** — assistant messages arrive as one `assistant` event per turn with a complete `message.content[]`; deltas live under `stream_event` only when `--include-partial-messages` is passed. (b) **No separate "tool-use" / "tool-result" events** — they are content-block types inside `assistant.message.content[]` and `user.message.content[]` respectively. (c) **`rate_limit_event`** is a real frequent type (was unlisted). (d) **Sub-agent events** are top-level with `parent_tool_use_id` correlating to the parent `Task` tool's `tool_use_id`; state machine's tool-use "stack" lives on correlation IDs, not event nesting. (e) **`thinking`** is a content-block type, not an event type. See `tests/fixtures/NOTES.md` for the raw observations.
- v3.3 (2026-05-12) — §9 Stage 2 split into **Stage 2A** (tasks-and-sessions management) and **Stage 2B** (multi-session console UI + macOS supervision). 2A is a product-defining workstream that wasn't anticipated when v3 was written: it adds first-class durable-task host commands (`/task new | list | show | fork | merge | switch | delete`), pulls SQLite forward as the per-machine task registry (the doc had previously deferred SQLite to post-Stage-3 — see §8 update), adds an MCP-lock primitive for parallel-session coordination, and ships a context-pressure monitor (soft 60% / hard 85% thresholds). 2B retains the original Stage 2 scope (Spectre.Console session-switcher, broadcast, host index for sessions, macOS `IProcessSupervisor` backend, CI matrix). File rename: this doc was previously `claude-code-csharp-host-design.md` at the repo root; current path is `docs/infinicontext-initial-design.md`.

---

## 1. Problem Statement

Build a single C# desktop application — codename **InfiniContext** — that lets a user run, monitor, and control **multiple concurrent Claude Code (`claude`) CLI instances** in tabs. Each instance is a separate working directory / project / conversation.

The host needs **programmatic control** — query state per instance (idle/working/awaiting-approval), send prompts, persist session history, route prompts to one or many instances at once, log all activity.

Earlier drafts framed this as a tension with **terminal fidelity** (since Claude Code is itself a TUI). v3 rejects that framing: the `claude` CLI in stream-json mode emits a structured event stream that contains everything the terminal renders, and more (it carries the underlying tool calls, sub-agent boundaries, thinking blocks, and retry signals as discrete events). A custom C# UI rendering those events as a collapsible tree (assistant text · thinking · tool call · tool result · sub-agent) is **strictly more controllable** than embedding a terminal — and avoids the entire ConPTY/WebView2 stack along with its cross-platform-PTY headaches.

The architecture is therefore single-mode: **streaming SDK mode**, all sessions, all platforms.

There is **no first-party Claude Agent SDK for .NET** as of 2026-05; the host is therefore a from-scratch SDK client written against the CLI's stream-json protocol. This is the single largest implementation risk and shapes most of the design choices below.

---

## 2. Goals and Non-Goals

### Goals (POC, Stages 1–3)
- Run **N independent `claude` processes**, one per session, with isolated working directories.
- Show structured per-instance state (current model, current tool call, retry status, token usage, message history).
- Send user prompts to a specific instance, or broadcast to a selection.
- Reuse **Claude Code's own on-disk session store** (`~/.claude/projects/...`) for resume; the host adds only an index and host-only metadata.
- Surface tool-approval prompts in-app via a built-in MCP server.
- **Target platforms: Windows + macOS.** Stage 1 implementation is Windows-first for fastest iteration, but every technology choice (UI stack, PTY-free I/O, path handling, process supervision, logging) is selected to work on macOS without re-architecture. macOS validation is a Stage 2B exit criterion.
- **Diagnostic-grade logging.** Every host run produces a timestamped log file with enough structured detail that an LLM agent reading it after the fact can reconstruct the workflow and diagnose issues (§7.4).
- **Automated test coverage** for the parser, state machine, session manager, and resume flow, including replay tests over recorded fixtures and integration tests against a fake-`claude` harness (§7.5).

### Non-Goals (POC)
- **TUI / terminal embedding.** Dropped entirely — see §1. The structured event stream is rendered as a native C# tree view; no ConPTY, WebView2, xterm.js, or terminal emulator. Linux is not a target either; Avalonia would support it, but the POC team is not going to validate it.
- Replicating the entire Claude Code keybinding set inside the custom UI.
- Multi-user / multi-machine fan-out.
- Hosting Claude Code in-process (it stays a child process; no SDK linking).
- Production telemetry pipeline (logging is local-only — see §7.4).

---

## 3. High-Level Architecture

```
                    ┌────────────────────────────────────────────────┐
                    │      InfiniContext Host  (Avalonia, Win + Mac)    │
                    │                                                │
                    │   ┌──────────────────────────────────────────┐ │
                    │   │  UI Layer                                │ │
                    │   │  S1 console REPL → S2 console switcher   │ │
                    │   │  → S3 Avalonia: collapsible tree view    │ │
                    │   │   user · assistant · thinking ·          │ │
                    │   │   tool-call → tool-result · sub-agent    │ │
                    │   └──────────────────────────────────────────┘ │
                    │                      │                         │
                    │   ┌──────────────────┴───────────────────────┐ │
                    │   │           Session Manager                │ │
                    │   │  Lifecycle, registry, broadcast,         │ │
                    │   │  index over Claude's session store,      │ │
                    │   │  process supervision (Job Object/Mac     │ │
                    │   │  signal-handler)                         │ │
                    │   └──────────────────┬───────────────────────┘ │
                    │                      │                         │
                    │       ┌──────────────┴───────────────┐         │
                    │       │                              │         │
                    │   ┌───▼──────────────┐    ┌──────────▼──────┐  │
                    │   │  ClaudeSession   │    │  ClaudeSession  │  │
                    │   │  (stream-json)   │    │  (stream-json)  │  │
                    │   │   - JSON pipe    │    │   - JSON pipe   │  │
                    │   │   - Event ch.    │    │   - Event ch.   │  │
                    │   │   - Logger       │    │   - Logger      │  │
                    │   └───┬──────────────┘    └──────┬──────────┘  │
                    └───────┼──────────────────────────┼─────────────┘
                            │ stdin/stdout NDJSON      │
                            │                          │
                    ┌───────▼──────────┐       ┌───────▼──────────┐
                    │   claude CLI     │       │   claude CLI     │
                    │ (headless mode)  │       │ (headless mode)  │
                    └─────────┬────────┘       └────────┬─────────┘
                              │ writes jsonl            │ writes jsonl
                              ▼                         ▼
                    ┌──────────────────────────────────────────────┐
                    │   ~/.claude/projects/<encoded-cwd>/*.jsonl   │
                    │   (Claude Code's own store; host reads only) │
                    └──────────────────────────────────────────────┘

                Per-run host log: ./logs/YYYYMMDD-HHmmss.log (gitignored)
```

### Transport — single mode

All sessions use **stream-json over stdin/stdout**:

```
claude --print --verbose --input-format stream-json --output-format stream-json
```

The Session Manager treats every session as `IClaudeSession`. There is no second transport mode in this design — the structured event stream is rendered natively by the host, so a terminal embedding adds no information and is dropped.

---

## 4. SDK Mode (the backbone)

### 4.1 Process invocation

```
claude --print \
       --verbose \
       --input-format stream-json \
       --output-format stream-json \
       [--model <model>] \
       [--allowedTools "..."] \
       [--permission-prompt-tool <tool-name>] \
       [--resume <session_id>] \
       [--cwd <project-dir>]
```

Flag-order constraints (as of the `TestedAgainstVersion` — see §7.3):
- `--verbose` must precede `--output-format`.
- `--print` is required when using `stream-json` formats.
- The host pins these in a single helper to avoid drift.

### 4.2 Wire protocol — newline-delimited JSON

**Outbound (host → claude), via stdin:** one user-message object per line. Schema follows the Agent SDK streaming-input spec; treat the SDK TypeScript/Python source as the authoritative schema, since CLI docs are incomplete.

**Inbound (claude → host), via stdout:** NDJSON event stream. Top-level event types the host MUST handle (Stage 0 fixtures recorded against `claude` 2.1.112 — see `tests/fixtures/NOTES.md`):

| `type` (and `subtype` if any) | Meaning | UI impact |
|---|---|---|
| `system/init` | Session metadata: `session_id`, `model`, full tool list, MCP servers, plugins, slash commands, `claude_code_version`, `cwd`, `permissionMode` | Initialize session header, store `session_id` for resume |
| `system/hook_response` | A local hook fired and reported its outcome | Surface in debug pane; **may carry a hook-internal error and not be fatal** (current `claude` bug emits one at session start). Do not derive primary state from this |
| `system/task_started` / `task_progress` / `task_notification` | `Task` tool sub-agent lifecycle markers | Status indicators only — the actual sub-agent's content arrives as separate `assistant`/`user` events with `parent_tool_use_id` set |
| `system/status` | Mid-stream status (only with `--include-partial-messages`) | Optional |
| `system/plugin_install` | Plugin install progress (only with `CLAUDE_CODE_SYNC_PLUGIN_INSTALL`) | Optional |
| `system/compact` (or similar) | Context auto-compacted mid-conversation | Surface; resume semantics shift |
| `assistant` | One per assistant turn. **Per-turn batched, not streamed by default.** `message.content[]` array carries `text`, `thinking`, and `tool_use` blocks | Render the message; collapse `thinking` blocks; render `tool_use` blocks as cards |
| `user` | One per tool-result delivery (echoed by `--print`). `message.content[].type == "tool_result"` carries `tool_use_id` linking to the assistant's `tool_use` | Attach the tool result to the matching tool-use card |
| `rate_limit_event` | Per-turn rate-limit telemetry (`status`, `resetsAt`, `rateLimitType`, `overageStatus`) | Optional indicator; do not gate state on it |
| `stream_event` | Only with `--include-partial-messages`. Wraps Anthropic SSE events (`message_start`, `content_block_start`, `content_block_delta` containing `text_delta`/`thinking_delta`/`input_json_delta`, `content_block_stop`, `message_delta`, `message_stop`) | Stage 3 streaming UX. Stage 1 ignores by virtue of not passing the flag |
| `result/<subtype>` | Final event of the turn. `subtype` is `success`, `error_max_turns`, etc. Carries `result` (final assistant text), `usage`, `total_cost_usd`, `permission_denials`, `terminal_reason` | Close the turn; transition state to `Idle` |

**Sub-agent correlation.** The `Task` tool spawns a sub-agent whose own `assistant` and `user` events appear top-level, distinguished by `parent_tool_use_id == <Task tool_use_id>`. The state machine's "tool-use stack" is keyed off these correlation IDs, not off event nesting.

**Content-block types inside `message.content[]`:**

| Block `type` | In | Notes |
|---|---|---|
| `text` | `assistant`, `user` | Plain text |
| `thinking` | `assistant` | Extended thinking; collapsed by default in Stage 3 tree |
| `tool_use` | `assistant` | Tool call — `id`, `name`, `input` |
| `tool_result` | `user` (echoed) | `tool_use_id` links to the `tool_use` |

#### Parser tolerance contract (load-bearing)

The host parser MUST:

1. **Preserve unknown fields.** Deserialize to records that keep an `Extra` `JsonElement` for unrecognized fields. Never use closed POCOs that drop unknown data — the SDK schema is unstable.
2. **Tolerate unknown event types.** Unknown discriminator → emit a synthetic `unknown_event` record with the raw JSON; do not crash, do not skip silently.
3. **Surface drift.** Maintain a per-session counter of `unknown_event` occurrences. Visible in the host UI. This is how schema drift is detected after a `claude` upgrade.
4. **Build against recordings.** The parser is developed against `.jsonl` captured from real sessions (see §9 Stage 0 harness), not from documentation. Replay tests are the primary regression surface.

**OPEN:** Confirm exact JSON shapes per event type against current SDK source before implementing the parser. Lock the recordings in `tests/fixtures/`.

### 4.3 C# implementation outline

- **Process:** `System.Diagnostics.Process` with `RedirectStandardInput/Output/Error = true`, `UseShellExecute = false`, `CreateNoWindow = true`.
- **Reader:** dedicated `Task` reading `process.StandardOutput` line-by-line (`ReadLineAsync`), deserializing with `System.Text.Json` into a polymorphic `ClaudeEvent` hierarchy that preserves unknown fields. Push to a bounded `Channel<ClaudeEvent>` (one per session).
- **Writer:** `SemaphoreSlim`-guarded async write to stdin; one prompt = one line of JSON + flush.
- **stderr:** drained on a separate task into a per-session ring-buffer log; surfaced in a debug pane.
- **Lifecycle:** `Start()`, `SendUserMessage(string)`, `Cancel()` (sends interrupt event per protocol; transitions through a `Cancelling` state — actual `claude` behavior here is racy and must be tested), `Dispose()` (kill if still running, drain channels).

### 4.4 Session state model

A flat enum is insufficient. Tool use can nest (sub-agents via the `Task` tool produce nested tool-use events). Model state as:

- **Top-level state:** one of `{ Idle, Sending, Streaming, Cancelling, Compacting, Retrying, Errored, Exited }`. `Streaming` is the catch-all for "between user prompt and `result` event" — even though default-mode events are per-turn batched rather than truly streamed, the session is still occupied during this window.
- **Tool-use correlation map:** every `tool_use` block in an `assistant.message.content[]` opens a slot keyed by its `id`; the slot closes when a matching `tool_result` block arrives in a `user.message.content[]`. Sub-agent events are correlated via `parent_tool_use_id` pointing at the parent `Task` tool's `tool_use.id`. Stage 0 fixture `nested-tool.jsonl` is the reference for this flow.
- **Awaiting-approval flag:** set whenever an outstanding approval-MCP call is unanswered. Independent of top-level state.

Derive states from a recorded session, not from this list — there will be transitions you missed.

### 4.5 Persistence (read Claude's store; do not duplicate)

**Claude Code already persists every session as NDJSON at:**

```
~/.claude/projects/<encoded-cwd>/<session_id>.jsonl
```

**The host MUST NOT mirror this content.** Two parallel jsonl stores create two sources of truth and break across `claude` version upgrades. Instead:

- Read events for replay and history from Claude's store.
- Maintain a **host-only index file** (flat JSON for POC; SQLite reconsidered post-Stage 3) at `~/.infinicontext/index.json` with: `{ session_id, project_path, encoded_cwd, created_at, last_active, model, title, tags, current_state }`.
- Resume: look up `session_id` in the index → spawn `claude --resume <session_id>` in the corresponding `cwd` → tail the jsonl from Claude's store for new events while replaying any history needed for the UI.
- The host writes only to `~/.infinicontext/index.json` and any host-only metadata files (titles, tags, broadcast groups). Never re-serializes Claude's events.

**Cross-platform path resolution.** Both `~/.claude/` and `~/.infinicontext/` resolve identically on Windows and macOS via `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)`. The host MUST use that API and `Path.Combine` everywhere — no string concatenation with `\` or `/`. On macOS the path becomes `/Users/<name>/.claude/projects/...`; on Windows `C:\Users\<name>\.claude\projects\...`. Both are tested.

**OPEN:** The `~/.claude/projects/` layout has changed across `claude` releases. POC documents the layout observed at `TestedAgainstVersion` (see §7.3) and tolerates drift the same way the wire parser does — read, log unfamiliar shapes, don't assume.

### 4.6 Tool approvals

- In SDK mode there is no TTY for prompts. Use `--permission-prompt-tool` to route approval requests through a host-defined MCP tool.
- The host implements a minimal **stdio MCP server** that, when called, posts the request to the UI and awaits the user's approve/deny click. ~150 lines of C#; no MCP framework dependency.
- **Per-prompt timeout: 60s default → auto-deny** with a surfaced error if the user doesn't respond. Without this, a closed approval UI hangs `claude` indefinitely.
- Approval prompts are **tab/session-scoped**, never modal. Multi-tab broadcast may produce concurrent approvals; modal would block all of them.

### 4.7 Rendering the conversation natively (no terminal needed)

The structured event stream is sufficient to render a conversation UI that's strictly more controllable than embedding `claude`'s TUI. The host's UI is a **tree view** (Stage 3), where each node is one of:

| Node type | Source events | Default render |
|---|---|---|
| **Turn root — user prompt** | outbound user-message JSON | "👤 user" header + the prompt text, expanded |
| **Turn root — assistant** | grouping of streamed assistant deltas + tool use until final result | "🤖 assistant" header, expanded |
| **Assistant text block** | concatenated `text_delta`s | inline markdown text, expanded |
| **Thinking block** | extended-thinking deltas | "💭 thinking" — **collapsed by default** |
| **Tool call** | tool-use event | "🔧 ToolName(params)" — **collapsed by default**, expand to show params + result |
| **Tool result** | matching tool-result event | child of its tool call; large payloads lazy-loaded (§6.2) |
| **Sub-agent (Task tool)** | nested tool-use events from `Task` | a sub-tree rooted at "🤖 sub-agent" — same node types, recursively |
| **System / retry / compact** | `system/*` events | "ⓘ system: …" inline, collapsed by default |
| **Schema-drift** | parser `unknown_event` records | "⚠ unknown event type" badge in the status bar; node visible only in debug view |

User input is a plain edit box at the bottom of the focused session (multi-line, `Ctrl+Enter` to send, `Esc` to cancel a streaming response). Slash commands like `/clear`, `/cost`, `/model` are intercepted by the host and translated to host actions or stream-json messages — they don't need a terminal.

Why this is enough:
- Everything `claude`'s TUI shows is derivable from the event stream.
- Users get **better** affordances than the terminal (collapsing thinking blocks, copying tool params, expanding nested sub-agents on click, jumping between turns).
- No ConPTY, no WebView2, no xterm.js, no cross-platform PTY library — entire categories of platform-specific code disappear.

The single capability the host has to implement that the terminal would have given for free is **Markdown rendering with syntax-highlighted code blocks**. There are mature Avalonia/WPF libraries for this; not load-bearing for Stage 1–2 (plain text is fine), needed for Stage 3.

---

## 5. Session Manager

Single application-scoped service. Responsibilities:

- **Registry:** `Dictionary<Guid, IClaudeSession>` of active sessions, plus persisted metadata for inactive ones from the host index.
- **Factory:** spawn a new session given `(working_dir, model, resume_session_id?)`.
- **Broadcast:** `SendToMany(IEnumerable<Guid> ids, string prompt)` — fan-out with per-target `Channel` writes, no cross-blocking.
- **Aggregate state:** observable collection feeding any dashboard view (idle count, working count, awaiting-approval count, schema-drift counter, etc.).
- **Process supervision:** detect unexpected exits, surface as session state `Exited`, offer one-click restart with resume.

### Process supervision on host crash

Goal: if the host crashes (or is killed during development), every spawned `claude` and its `node` child are torn down with it. Otherwise orphaned processes keep consuming an API key, holding files open, or worse.

**Windows.** Attach every spawned `claude` (and descendants) to a **Job Object** with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. The OS terminates the job and all descendants when the handle closes (i.e., when the host process dies, normally or otherwise). P/Invoke targets: `CreateJobObject`, `SetInformationJobObject` (with `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`), `AssignProcessToJobObject`. Set the limit *before* starting the child, then assign the child immediately after `Process.Start()`.

**macOS.** No clean equivalent of Job Objects exists. Layered defences:
1. Track every child PID in the Session Manager.
2. Install signal handlers via `PosixSignalRegistration` (built into .NET 6+; cross-platform) for `SIGINT`, `SIGTERM`, `SIGHUP`, `SIGQUIT` → cleanly send `SIGTERM` to all known children, then `SIGKILL` after a short grace period.
3. Subscribe to `AppDomain.CurrentDomain.ProcessExit` for normal-exit cleanup.
4. As a backstop, surface "orphaned `claude`/`node` processes" in the host startup screen — the host scans for stray processes whose parent is `init`/`launchd` and offers to kill them. Last-resort safety net for hard crashes that bypass signal handlers.

A single `IProcessSupervisor` interface abstracts the platform-specific implementation. POC ships the Windows backend; the macOS backend is a Stage 2B deliverable.

---

## 6. UI Layer

### 6.1 Stack choice

UI work happens in three escalating phases that share a project structure but not a UI library:

- **Stage 1 — console REPL.** No UI library. Read prompt from stdin, write events to stdout. Cross-platform by virtue of being a console app.
- **Stage 2B — console session-switcher.** **Spectre.Console** for layout/keybindings. Renders identically on Windows and macOS terminals. (Stage 2A keeps the Stage 1 single-instance REPL surface; the multi-session UI is the 2B step.)
- **Stage 3 — real UI: Avalonia.** Picked specifically because it works on both Windows and macOS with a single codebase and a single packaging story. WPF is rejected (Windows-only). WinUI 3 is rejected (cross-platform story is weak). MAUI is rejected (desktop story is rough; mobile-first DNA we don't need).

Avalonia specifics relevant to the design:
- **MVVM via CommunityToolkit.Mvvm** (or ReactiveUI if reactive observation of the event stream is preferred — open question for Stage 3).
- **TreeView control** is built-in and hosts arbitrary `ItemTemplate`s — direct fit for §4.7's tree.
- **Markdown rendering**: Avalonia.Markdown or Markdown.Avalonia (open question; pick at Stage 3 start).
- **Packaging:** `dotnet publish -r win-x64` / `-r osx-x64` / `-r osx-arm64` produces self-contained binaries. macOS code-signing/notarization is a Stage 4 problem.

### 6.2 Threading

- All process I/O on `Task`s off the UI thread.
- Channel readers marshal to UI via `Dispatcher.UIThread.Post` (Avalonia, Stage 3) or `SynchronizationContext` (Stages 1–2).
- Backpressure: if the consumer can't keep up with a fast event stream, the bounded channel forces the reader to wait — preferable to unbounded memory growth. **Suggested capacity: 1024 per session, but the real bottleneck is per-event size** (a `Read` of a 100KB file is one event with the file inline). Stage 3 must lazy-load large tool-result payloads — keep them on disk and load on tree-node expand.

---

## 7. Cross-Cutting Concerns

### 7.1 Security
- The host spawns `claude`, which can execute arbitrary tools. Treat each session's working directory as a security boundary.
- The approval-prompt MCP server runs over stdio with a single child — no network surface — but the architect should still review for protocol-injection.
- **`.mcp.json` discovery:** if the working directory contains `.mcp.json`, Claude Code will auto-load MCP servers from it. The host MUST surface this to the user (a confirmation banner) before spawning into a directory it hasn't seen before. Otherwise opening an untrusted project silently extends the trust boundary.
- Child process inherits the host's full environment, including `ANTHROPIC_API_KEY`. Document this; don't try to scrub.
- `.jsonl` files stay local. No outbound telemetry by default.

### 7.2 Discoverability of `claude`
- Resolve `claude` via PATH, then fall back to common install locations (`~/.local/bin`, npm global prefix).
- Surface a clear error UI when not found, with a link to install instructions. Don't auto-install.

### 7.3 Versioning

A hard "pin a single `claude` version" makes no sense for software different developers and users will install. The host instead carries two version markers in app config:

- **`MinClaudeVersion`** — hard fail below this. Set to the earliest release where the stream-json protocol used by this host is known to work. Below it, the host refuses to spawn — useful behaviour beats useless behaviour.
- **`TestedAgainstVersion`** — the exact version Stage 0 fixtures were recorded against, and what CI runs (when CI has `claude` installed). If the user's installed `claude` version differs, the host logs a warning at startup and surfaces the parser's per-session **schema-drift counter** (§4.2) as the real-time signal.

The drift counter, not the version check, is the load-bearing safety net: the parser preserves unknown fields and emits `unknown_event` records for unknown discriminators (§4.2), so a `claude` upgrade that adds events doesn't break the host — it just lights up the counter, and a maintainer can refresh fixtures.

**POC initial values (subject to update at Stage 0 fixture record time):**
- `MinClaudeVersion` = `2.0.0` (first major after stream-json mode stabilized; lower if a concrete reason surfaces).
- `TestedAgainstVersion` = the version installed on the recording machine when Stage 0 captures fixtures (currently expected to be `2.1.x`). Architect updates this whenever fixtures are re-recorded.

The on-disk session-store layout under `~/.claude/projects/` has shifted across releases too; the resume path (§4.5) tolerates layout changes the same way the wire parser does — read what's there, log what's unfamiliar, don't assume.

### 7.4 Logging (diagnostic-grade, per-run)

The agent maintaining or extending InfiniContext cannot see the GUI. Logs are the primary debugging surface and must be written with that in mind.

**File layout.**
- One log file per host run, written to `./logs/{YYYYMMDD-HHmmss}.log` relative to the host's working directory.
- The `logs/` directory is added to `.gitignore` (see repo root).
- No rotation; let files accumulate. The user prunes manually. Each file is small enough to grep.

**Library.** **Serilog** with `Serilog.Sinks.File`. Cross-platform, structured output, well-supported. Configured at startup; flushed on exit.

**Format.** Plain text with structured fields, one event per line, e.g.:

```
2026-05-06 14:30:52.123 [INF] [Session a4b4-ce91 | cwd /Users/x/proj] state Idle → Sending; prompt="implement foo"
2026-05-06 14:30:52.156 [DBG] [Session a4b4-ce91] tx: {"role":"user","content":[{"type":"text","text":"implement foo"}]}
2026-05-06 14:30:53.812 [DBG] [Session a4b4-ce91] rx: {"type":"system","subtype":"init","session_id":"…","model":"…"}
2026-05-06 14:30:54.001 [INF] [Session a4b4-ce91] state Streaming; tool stack: [Read]
2026-05-06 14:30:55.412 [WRN] [Session a4b4-ce91] unknown_event type="hook/v2_thing"; body preserved
2026-05-06 14:30:58.001 [INF] [Session a4b4-ce91] approval-mcp: tool=Bash cmd="rm -rf foo" → user=APPROVE (latency 412ms)
2026-05-06 14:31:00.910 [INF] [Session a4b4-ce91] state Idle; tokens in=412 out=1183
```

**What MUST be logged.**
- Application lifecycle: start, host version, OS, detected `claude` version + `MinClaudeVersion` + `TestedAgainstVersion`, stop.
- Process events per session: spawn (with full argv), exit (with code), supervision actions (Job Object attach / signal sent).
- State transitions per session, including the transition reason.
- Every prompt sent (`tx:`) and every event received (`rx:`) at `Debug` level. Truncate any single field over 4 KB to `… (N bytes elided)` to keep the log human-scannable; raw events are still recoverable from Claude's own jsonl store.
- Parser tolerance hits — every `unknown_event`, every preserved-extra-field warning.
- Tool approvals: tool name, brief params, user decision, latency. (Full params are in Claude's jsonl store; logging the brief keeps the host log readable.)
- Errors and warnings always.
- For Stage 2+: which session has UI focus, when broadcast is sent, which sessions received the broadcast.

**What MUST NOT be logged.**
- The user's `ANTHROPIC_API_KEY` (or any env var matching `*_KEY`/`*_TOKEN`/`*_SECRET`). Scrub at log-emission, not after.
- Outbound telemetry. Logs are local-only.

**Log levels.**
- `Debug` — every `tx:`/`rx:` event. On by default for the POC; can be disabled via config.
- `Information` — lifecycle, state transitions, approvals, tool calls (one-line summary).
- `Warning` — parser tolerance hits, approval auto-deny, schema-drift detection.
- `Error` — exceptions, child process unexpected exits, MCP server crashes.

**Why this matters.** When the agent is debugging a reported issue ("why did session X stall?"), it should be able to open the most recent file in `./logs/` and reconstruct: which sessions ran, what prompts were sent, which tool calls happened, where state got stuck, what the parser thought of unfamiliar events, whether an approval timed out. The log is the substitute for "looking at the screen."

### 7.5 Testing

The test pyramid is shallower than usual because the highest-leverage tests aren't unit tests — they're replay tests against recorded fixtures.

**Test framework.** **xUnit** + **FluentAssertions** for readability. Tests live in `tests/InfiniContext.Tests/` and run on Windows + macOS via `dotnet test` (CI matrix in Stage 2B).

**Test surfaces, ordered by leverage:**

1. **Parser replay tests (highest leverage).** For every `.jsonl` fixture from §9 Stage 0, the parser must:
   - Consume every line without throwing.
   - Produce zero `unknown_event` records for "happy" fixtures.
   - Produce exactly the planted `unknown_event`s for adversarial fixtures.
   - Preserve unknown fields end-to-end (deserialize → re-serialize round-trip equivalence on `Extra` element).

2. **State machine tests.** Feed the parser output into the state machine; assert the sequence of state transitions matches an expected trace. Trace files live next to fixtures: `simple-prompt.jsonl` ↔ `simple-prompt.expected-trace.txt`.

3. **Session manager tests.** In-memory; uses a fake `IClaudeSession`. Cover: spawn/dispose lifecycle, broadcast fan-out, approval queue, exit-then-resume.

4. **Integration tests via fake `claude`.** A small C# console app (`tests/FakeClaude/`) that reads scripted commands from a JSON file and emits NDJSON to stdout — covers edge cases without needing real `claude`: truncated lines, unknown events, sudden exit, slow stream, mid-stream cancel, oversized tool results, deep sub-agent nesting, schema-drift signals. Used by the session manager tests as a transport substitute.

5. **End-to-end smoke test.** Real `claude`, real working directory, scripted prompt → assert a known phrase appears in the assistant text. Marked `[Trait("Category", "Live")]`; opt-in via env var; skipped in CI by default.

**Coverage targets for POC:** ≥80% line coverage on the parser and state machine. Lower elsewhere is fine — the replay/integration tests catch what unit tests miss.

**No mocks of Claude's wire protocol.** Use the fake-`claude` binary, not in-process mocks of stdin/stdout. The point is to exercise the same code paths the real binary does.

---

## 8. Decisions for the Architect (OPEN)

Trimmed to items that actually block implementation:

1. **`TestedAgainstVersion`** — the `claude` version Stage 0 fixtures are recorded against. Architect sets this when fixtures land. `MinClaudeVersion` defaults to `2.0.0` unless a concrete reason to lower it surfaces.
2. **Exact NDJSON event schema** — confirm against current SDK source; lock recordings in `tests/fixtures/`.
3. **Session index format** — flat JSON file (`~/.infinicontext/index.json`) is still the default for **session** metadata in Stage 2B. **The task registry is a different question and is already decided:** Stage 2A pulled SQLite forward to `~/.infinicontext/infinicontext.db` because globally durable task ids across all repos require a real DB (folder-derived ids are only per-repo). The session index can stay flat JSON unless Stage 3 needs query patterns it can't support.
4. **Approval-MCP timeout** — confirm 60s default is right, or pick another.
5. **Stage 3 — Avalonia MVVM flavour** — CommunityToolkit.Mvvm vs ReactiveUI for binding the event stream into the tree view. (Defer to Stage 3 spike.)
6. **Stage 3 — Markdown library** — Markdown.Avalonia vs alternative; pick at Stage 3 start.

**Resolved (no longer OPEN):** UI framework (Avalonia for Stage 3 — driven by Win+Mac requirement), TUI/ConPTY mode (dropped — see §1 / §4.7), persistence layer (Claude's store + host JSON index for sessions; SQLite for the task registry per Stage 2A), process supervision (Job Object on Win, signal handlers on Mac, abstracted via `IProcessSupervisor`), logging stack (Serilog file sink, per-run timestamped), task-registry storage (SQLite, see Stage 2A), macOS validation timing (Stage 1 + Stage 2A Windows-only; macOS supervision backend + CI matrix lands in Stage 2B). See revision history.

---

## 9. POC Implementation Plan

The POC progresses in three stages aligned to the user's brief. Each stage ships something runnable; each stage's exit criteria de-risk the next.

### Stage 0 — NDJSON recording harness (1–2 days, prerequisite)

Not user-visible; load-bearing for everything that follows.

**Build:** a small standalone tool (~50 lines C# console app) that:
1. Spawns `claude --print --verbose --input-format stream-json --output-format stream-json` in a configurable cwd.
2. Reads NDJSON from stdout and writes it to `tests/fixtures/<scenario>.jsonl`.
3. Sends scripted user-message JSON to stdin from a script file.

**Why first:** unblocks parser dev without needing the host running, and seeds the replay test corpus.

**Recordings to capture:**
- `simple-prompt.jsonl` — one prompt, one assistant turn, no tools.
- `single-tool.jsonl` — one bash/read tool call with approval round-trip.
- `nested-tool.jsonl` — `Task` tool spawning a sub-agent that uses tools.
- `compaction.jsonl` — long enough to trigger context compaction.
- `api-retry.jsonl` — induced by rate limit (or mocked at the parser level).
- `cancel-mid-stream.jsonl` — interrupt sent during a streaming response.
- `unknown-event.jsonl` — manually edited fixture with an invented event type, for parser-tolerance tests.

**Exit criteria:** fixtures committed; replay loads cleanly into a stub parser.

---

### Stage 1 — Single-instance runner (console REPL)

**Goal:** prove the full stream-json round trip end-to-end. One `claude` process. Output its state and streaming text. Accept user input.

**Form factor:** console app. No GUI. Standard input → prompt; standard output → assistant text + state markers.

**In scope:**
- Spawn one `claude` child process (SDK mode, flags from §4.1).
- NDJSON parser with the §4.2 tolerance contract.
- Session state model (§4.4): top-level state + tool-use stack + awaiting-approval flag.
- Reader/writer/stderr drain pattern (§4.3).
- Process supervision: Job Object on Windows (§5). macOS supervision can be a stub for Stage 1; full implementation lands in Stage 2B.
- Console REPL: type prompt, see streamed assistant deltas, see `[tool: Read]` / `[tool-result]` markers inline, see `[state: streaming]` / `[state: idle]` markers when state changes.
- Built-in approval MCP server (§4.6) wired up; approve/deny via console y/n prompt; 60s timeout.
- Resume by `session_id` argument: `infinicontext run <cwd> --resume <session_id>` re-enters an existing Claude Code session.
- **Logging (§7.4):** Serilog wired up; per-run file at `./logs/{YYYYMMDD-HHmmss}.log`; `logs/` in `.gitignore`. Verify by reading a log file with no other context and tracing a session.
- **Tests (§7.5):** parser replay tests + state-machine trace tests for all Stage 0 fixtures; basic session-lifecycle tests; fake-`claude` harness landed.
- Path-handling discipline: `Environment.GetFolderPath` + `Path.Combine` only — no string concat with separators (Mac validation in Stage 2B).

**Out of scope:**
- Multiple sessions.
- Persistent host index (just print `session_id` to console — the user copies it for `--resume`).
- Any GUI.
- Broadcast.
- macOS validation (the code is portable, but the team runs Stage 1 on Windows; Stage 2B boots on Mac).

**Exit criteria:**
- Can hold a multi-turn conversation including a tool call requiring approval.
- Can `Ctrl+C` to interrupt mid-stream and the host transitions through `Cancelling` cleanly.
- Can quit the host, restart, pass `--resume <session_id>`, and see prior context.
- Replay tests green for all Stage 0 fixtures.
- Crashing the host (`Process.Kill` from another terminal) leaves no orphan `node` processes.
- A reviewer reading only `./logs/<latest>.log` can answer: "what prompts ran, which tools were called, did anything time out, did the parser hit any unknown events?"

**Estimated effort:** 1–2 weeks.

---

### Stage 2A — Tasks and sessions management

**Goal:** expose durable-task lifecycle as first-class host commands. A **task** is a durable unit of work that outlives any single Claude session — bigger than one conversation, smaller than the whole project. Many sessions, possibly in parallel, can serve one task via shared markdown artifacts on disk.

**MVP anchor:** "user has a fully self-contained durable-task workflow inside InfiniContext." Without this anchor the workstream is unbounded; with it, the scope is whatever's needed to make multi-session, multi-day work feel native to the host.

**In scope:**
- **Task model.** Each task lives at `<home_cwd>/.task/<id>-<slug>/` with plain-markdown artifacts the agent edits via `Read` / `Edit` / `Write` — no MCP-served content, no DB-stored content. Mandatory artifacts: `index.md`, `description.md`, `progress.md`. Optional: `plan.md`, `research.md`, `decisions.md`, `design.md`, plus anything the agent wants to add.
- **SQLite task registry at `~/.infinicontext/infinicontext.db`** (Microsoft.Data.Sqlite). Holds identity, lineage, timestamps, status. The original design (§4.5) deferred SQLite to post-Stage-3; 2A pulls it forward because globally durable task ids across all repos require a real DB. The agent never reads SQLite directly; durable content stays as markdown.
- **REPL slash commands:** `/task new | list | show | fork | merge | switch | delete | help` (see `docs/cli/howto/task-commands.md`). `/task new` calls the light model (§9 cross-cutting) to generate a 3–6-word title from the user's free-form description. `/task delete` drops the row and (optionally with `--remove-folder`) the on-disk folder; refuses to delete an `InProgress` task. `/task new` refuses paths that look like build outputs (`bin/Debug`, `bin/Release`, `obj/`) to prevent dangling rows when a build wipes the home.
- **Skill auto-install at user level.** InfiniContext installs `~/.claude/skills/infinicontext-task/SKILL.md` on first launch. Claude Code auto-discovers user-level skills, so the agent picks up the rules with zero per-session pre-fill cost.
- **Per-session pointer.** When `/task switch <id>` is used, the host pre-fills the first user message with a small pointer: task id, title, folder path, and "Use the `infinicontext-task` skill." Rules live in the skill; state lives in `index.md`; the agent loads what it needs on demand.
- **Stale-data guards.** `/task switch` validates that `home_cwd` and `.task/<id>-<slug>/` both still exist on disk; otherwise it refuses with a specific message (the 2026-05-09 manual-testing session surfaced this — a task whose `home_cwd` pointed at a build-output dir got wiped on rebuild). The `/task delete` command is the user's recovery path.
- **Multi-session MCP lock primitive.** Two `lock.acquire` / `lock.release` messages multiplex over the existing per-session approval pipe (kind tag), backed by a host-side `LockRegistry`. Skill v2 instructs the agent to acquire → `Read` → `Edit` → release for any artifact edit. TTL 30s default. Polling-based contention (5–50 ms `Task.Delay`); release-on-exit via channel dispose.
- **Context-pressure monitor.** A `ContextPressureMonitor` observes every `result` event synchronously inside `ClaudeSession`'s stdout loop, reads `result.modelUsage[<active model>].contextWindow` (falls back to a `ModelContextWindows` settings map), and triggers two threshold actions: a **soft nudge at 60%** (polite reminder to flush) and a **hard turn at 85%** (firm "flush now"). MVP ships a single firm turn at hard — no force-flush guardrail; trusts the agent + the skill.
- **Task lifecycle in the outer host loop.** `/task switch` is intercepted in `Repl` (not the dispatcher) because switching needs (a) status flip in SQLite, (b) teardown of the current session, (c) re-spawn with `FirstUserMessage` set. Status flips happen in `Program.cs` immediately before/after `repl.RunAsync()`, not via a session lifecycle observer.
- **Tests:** parser + handler + service + folder tests against an in-memory SQLite store; light-model is stubbed by a queued-title fake; `ContextPressureMonitor` and `LockRegistry` have unit-level coverage; `ReplTaskSwitchTests` cover the cwd-mismatch refusal path.
- **Logging:** lock acquire/release lines and pressure-threshold trips already appear at the documented levels (`Information` for trips, `Debug` for every poll). Per-session correlation continues from Stage 1.

**Out of scope (post-MVP):**
- Multi-parent lineage (the schema records only one `parent_task_id`).
- Light-model and full-model merge tiers (`MergeTier.LightModel`, `MergeTier.FullModelSession` throw `NotImplementedException`; only deterministic concat ships).
- Task rename and archival (delete is enough for now).
- Cross-home `/task merge`. Cross-repo task storage is rejected, not deferred — a task has a single `home_cwd`.
- `InSync` / `Stale` status states (only `InProgress` and `Idle` ship).
- `/task switch` while another session is running on the same task (multi-session orchestration UX is a 2B+ concern).
- Hard-threshold force-flush guardrail beyond the single firm turn.
- Automatic successor-session spawn after flush; host-controlled flush on errors/cancels.
- Light-model bulk-regeneration of `index.md` (possible as a user-invoked command later, never automatic).
- Cross-machine ID coordination — SQLite is host-local.
- `/task relocate <id> <new-home>` to fix stale `home_cwd` rows without re-creating the task.

**Exit criteria:**
- The user's daily task-management workflow runs entirely on InfiniContext's `/task` slash-commands with no external plugin dependency.
- An agent reading only the auto-installed `infinicontext-task` skill and the per-session pointer can find the task folder and operate on its markdown artifacts.
- Two parallel sessions on the same task can both `lock.acquire` → `Edit` without trampling each other; contention surfaces in logs.
- `ContextPressureMonitor` trips a soft nudge by 60% and a hard turn by 85% in a forced-context test.
- `/task switch` on a stale row (deleted folder) refuses with a clear "/task delete to drop" message rather than spawning into nothing.
- Full unit/integration suite green (Windows-only — macOS lands in 2B).

**Estimated effort:** 3–5 weeks. Mostly shipped; remaining items live in `docs/stage2/todo.md` §A–B and `docs/todo.md`.

**Stage 2A is Windows-only.** The macOS supervisor backend, CI matrix, and any case-sensitive-filesystem hardening land in Stage 2B.

---

### Stage 2B — Multi-instance console UI and macOS supervision

**Goal:** run several `claude` instances at once. Switch focus between them in a console UI. Resume any prior session. Validate on macOS.

**In scope:**
- **Session Manager** (§5): registry, factory, supervision.
- **Host session index** at `~/.infinicontext/index.json` (§4.5): records session metadata; written on session create/exit, read on host startup to populate the session list. (The **task** registry is already SQLite per 2A; the session index stays flat JSON.)
- **Console UI** (Spectre.Console): split layout — left pane is the session list (one row per active or recently-used session, with state badge), right pane is the focused session's transcript. Keys: `Tab` cycles focus between sessions, `n` creates new, `r` resumes from index, `Enter` sends prompt to focused, `b` enters broadcast mode.
- **Broadcast prompt:** select multiple sessions (checkbox UI), type prompt once, sent fan-out.
- **Resume from index:** pick a row in the session list → host spawns `claude --resume <session_id>` in the recorded cwd → live events flow into the right pane.
- **Tool approval UI per session:** approval prompts queue inline in the session's pane, never modal; tab-scoped. Multiple concurrent approvals across sessions handled in parallel.
- **Schema-drift counter** visible in the status bar (per-session unknown-event count).
- **macOS supervision (§5):** signal-handler / `ProcessExit` implementation lands here; `IProcessSupervisor` resolves to the platform-specific backend at startup.
- **macOS-specific hardening:** case-sensitive filename compare in `TaskFolder` / `TaskService` (`OrdinalIgnoreCase` only when `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`; otherwise `Ordinal`). Filed in `docs/stage2/todo.md` §B as C7.
- **CI matrix:** `dotnet test` runs on `windows-latest` and `macos-latest`. The test matrix is the macOS validation gate — tests must pass on Mac before Stage 2B is signed off.
- **Tests:** session-manager tests, broadcast fan-out, resume flow, multi-session approval queueing. The Stage 2A multi-session lock-contention E2E test (filed as T4) lands here if not before.
- **Logging:** per-session correlation in log lines (already designed in §7.4) plus broadcast-fan-out log entries.

**Out of scope:**
- Real GUI.
- Reading history from Claude's `.jsonl` for past-session preview before resume — Stage 3 concern.
- Settings UX, profiles, model picker beyond a single config file.

**Exit criteria:**
- Three concurrent sessions, each in a different `cwd`, all streaming visibly.
- Switch focus between them with no I/O loss (events buffered in their Channels while off-screen).
- Resume works for any session listed in the host index, including one whose process exited cleanly previously.
- Broadcast prompt sent to two sessions returns two independent transcripts in their respective panes.
- Approval prompts in one session don't block input or progress in another.
- Killing the host cleans up all child processes — verified on Windows (Job Object) and macOS (signal handler / `ProcessExit`).
- CI green on both `windows-latest` and `macos-latest` in the test matrix.

**Estimated effort:** 2–3 weeks.

---

### Stage 3 — Real UI (Avalonia, collapsible tree view)

**Goal:** replace the Stage 2B console with a graphical UI on Windows + macOS. Recommended direction below; specifics confirmed by a short Stage-3 design proposal at the start of the stage (informed by lived experience from Stages 2A and 2B).

**Recommended direction (default — overrideable at proposal time):**
- **Framework: Avalonia 11.x.** Cross-platform Win+Mac in a single codebase. WPF/WinUI3/MAUI rejected (§6.1).
- **Layout:** session list on the left (selectable, with state badges), focused session occupies the rest of the window. Tabs are not used in v1 — the list scales better past ~8 sessions.
- **Per-session view: collapsible tree view (§4.7).** Each turn is a root node; assistant text / thinking / tool calls / sub-agents are children. Markdown rendering with syntax-highlighted code blocks. Edit box at the bottom (multi-line, `Ctrl+Enter` to send, `Esc` to cancel a streaming response).
- **Approval UI:** in-pane banner above the edit box for the active session; concurrent approvals across sessions queue per-session.
- **Aggregate view:** small status strip at the top with counts (idle / working / awaiting-approval / schema-drift). A full dashboard is v2 if it adds value.
- **Slash commands:** `/clear`, `/cost`, `/model`, `/resume <id>` — host-implemented, not a terminal.

**Decision points still open at Stage-3 start (proposal resolves them):**
1. **MVVM flavour:** CommunityToolkit.Mvvm vs ReactiveUI for binding the event stream into the tree.
2. **Markdown library:** Markdown.Avalonia vs alternative — pick based on syntax-highlighting quality.
3. **Persistence escalation:** flat JSON index vs SQLite. Stay flat unless concrete query patterns force the upgrade.
4. **Settings UX scope:** model per session, allowed-tools selection, env overrides — table-driven editor or per-spawn dialog.
5. **Lazy-loading large tool results** (flagged in §6.2) — implementation details: VirtualizingStackPanel + on-expand `Read` from Claude's jsonl.

**Process:**
- Stage 3 begins with a written design proposal building on what was learned in Stages 1–2.
- The proposal supersedes this section as the source of truth once approved.
- macOS validation is a Stage 3 exit criterion: Avalonia app builds, runs, and passes its tests on macOS before sign-off.

**Estimated effort:** 4–8 weeks. Refine in the proposal.

---

## 10. Reference Material

- Claude Code headless docs: `https://docs.claude.com/en/docs/claude-code/headless`
- Agent SDK streaming protocol: `https://docs.claude.com/en/api/agent-sdk/streaming-vs-single-mode`
- Stream-json field guide: `https://backgroundclaude.com/blog/stream-json`
- Avalonia (Stage 3 UI): `https://avaloniaui.net/`
- Spectre.Console (Stage 2B console UI): `https://spectreconsole.net/`
- Serilog (logging): `https://serilog.net/`
- xUnit (testing): `https://xunit.net/`
- .NET cross-platform signals (`PosixSignalRegistration`): `https://learn.microsoft.com/en-us/dotnet/api/system.runtime.invokeprocessing`
- Windows Job Objects: `https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects`

---

## Appendix A — Why we are not embedding a terminal

Earlier drafts proposed an optional ConPTY + WebView2 + xterm.js mode for "real Claude Code TUI fidelity." v3 dropped this entirely. The reasoning, kept here so future readers don't re-litigate it:

- The stream-json event stream is **structurally richer** than the terminal's escape-sequence output. The terminal renders thinking blocks, tool calls, and sub-agent boundaries by visual convention; the JSON stream carries them as discrete typed events. Anything the terminal shows is derivable from the events.
- Implementing the terminal embedding adds: ConPTY P/Invoke (Windows), `forkpty` P/Invoke (macOS, via `Pty.Net` or hand-rolled), WebView2 integration (Windows), an equivalent (WebKit?) on macOS, an xterm.js bundle, binary-safe message marshalling, resize plumbing, and an entire second `IClaudeSession` implementation. None of this is needed if the C# UI renders the events directly.
- The only features unique to the real TUI are slash commands and a couple of cosmetic animations. Slash commands are trivial to intercept in the host and translate to host actions or stream-json messages. The animations are not load-bearing.

If, after Stage 3, a user explicitly demands the in-TUI experience for some niche feature, this can be revisited as a strict superset — but that is a hypothetical post-POC concern, not part of the POC plan.

---

*End of document.*
