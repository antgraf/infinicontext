# Phase 3A тАФ InfiniContext GUI: vision, goals, and high-level design

**Status:** First-draft proposal. Phase 3A deliverable per `description.md`. Supersedes the placeholder Stage 3 section in `docs/infinicontext-initial-design.md` ┬з9 once approved.
**Date:** 2026-05-20
**Audience:** Product owner reviewing scope; architect picking up Phase 3B (foundation) and 3C (polish + macOS validation).

This document defines **what the GUI is for, who it serves, how it should feel, and how every feature shipped through Stages 1тАУ2B maps to a visual surface.** It is deliberately stack-light at the top (goals, vision, operations) and stack-heavy at the bottom (Avalonia specifics, MVVM, open decisions). Stack choices that the project has already locked in (`infinicontext-initial-design.md` ┬з6.1, `docs/stage2/decisions.md` ┬з13) are restated, not re-litigated.

---

## 1. Where Phase 3 sits

Stages 1 тЖТ 2B are shipped:

- **Stage 1** тАФ single-instance console REPL; stream-json round trip; parser tolerance contract; MCP approval sidecar; per-session log; resume by id.
- **Stage 2A** тАФ durable tasks (`<repo>/.task/<id>-<slug>/`), SQLite registry at `~/.infinicontext/infinicontext.db`, `/task new|list|show|fork|merge|switch|delete`, parallel-session lock channel, context-pressure soft/hard thresholds, auto-installed `infinicontext-task` skill.
- **Stage 2B** тАФ multi-session console (`SessionManager` + `MultiSessionHost`), persistent session index at `~/.infinicontext/index.json`, `/n /focus /next /prev /list /resume /broadcast` slash commands, per-session approval queueing via `PendingApprovalSlot` + `SwitchableApprovalSink`, macOS POSIX supervisor backend, CI matrix.

Everything user-facing is currently behind a Spectre.Console two-pane layout driven by typed slash commands. **Phase 3 is the move from "powerful but typing-only" to a graphical UI** that makes the existing primitives visible, navigable, and supervisable across many tasks at once.

Phase 3 itself splits into three sub-phases:

- **3A тАФ Design (this doc).** Goal/vision/scope, IA, UI surfaces, operations map, open decisions. No code. Output: this `design.md` plus follow-up sub-docs in `docs/stage3/` once the proposal is approved.
- **3B тАФ Foundation.** Avalonia shell, two-pane window, transcript tree, per-session approval banner, prompt box, the minimum viable set of slash-command equivalents, ViewModel layer feeding off `SessionManager`/`TranscriptModel`. Single-platform first (Windows); macOS follows in 3C.
- **3C тАФ Polish + macOS validation.** Task-centric supervisor view, settings UI, multi-task dashboard, markdown rendering, syntax-highlighted code, large-tool-result virtualization, accessibility, packaging on macOS, CI matrix extended to `osx-arm64`.

This document covers 3A only. 3B and 3C land as separate proposals once 3A is signed off.

---

## 2. Goals and targets

### 2.1 Primary goals

1. **Make the multi-session console UX visible.** Everything the console exposes (sessions, focus, broadcast, approval queue, drift counter, costs) must be at least as discoverable in the GUI, and ideally more тАФ clicking a session is faster than typing a host-id prefix.
2. **Make tasks first-class.** Stage 2A introduced durable tasks but they're invisible until you type `/task list`. The GUI must surface tasks across the whole repo (or the user's machine), let the user drill from a task into its sessions and artifacts, and read the artifact files inline without leaving the host.
3. **Make many-projects supervision practical.** The README vision is *"one human watches multiple tasks across multiple projects from a single UI"*. The GUI is the first surface where that claim becomes true тАФ multiple `cwd`s, multiple tasks, multiple sessions, all in one window.
4. **Make events surface without focus.** Tool approvals, context-pressure trips, errors, completions тАФ every event must reach the user without them having to be looking at the producing session's pane. Today the console hides them off-screen until you `/focus`.
5. **Keep the agent's primary debugging surface (logs, transcripts) intact.** The GUI is not a replacement for `logs/<timestamp>.log` or `~/.claude/projects/.../*.jsonl` тАФ it consumes the same `TranscriptModel` pipeline and never mutates the underlying stores. An agent debugging InfiniContext itself reads logs; a human supervising agents reads the GUI.

### 2.2 Targets (concrete, measurable at Stage-3 exit)

- **Three concurrent sessions across three different `cwd`s, side by side, all rendering live.** No I/O loss when a session is off-screen. *(Inherits Stage 2B ┬з9 exit criteria; the GUI must clear the same bar the console cleared.)*
- **A user can open any task in the registry and see its artifacts (markdown, rendered) and its session history (active + closed) without typing a command.**
- **A pending tool approval is visible from any pane** тАФ at minimum a top-of-window indicator that names the originating session, even when the user is focused elsewhere.
- **Context-pressure soft/hard nudges are visible as events** in a "what just happened" stream тАФ the user doesn't have to scroll through a transcript to learn that a session is about to flush.
- **Spawn-to-first-pixel time тЙд 800 ms** on a developer-grade machine. The GUI must not feel slower than the console it replaces.
- **macOS feature parity** (sign-off on `osx-arm64`, `osx-x64`). Same exit-criteria smoke test as Windows.
- **Accessibility floor:** keyboard navigation between every primary control; non-color status badges (icon + text, not just a green dot); contrast meeting WCAG AA in both light and dark themes.

### 2.3 Non-targets (explicit)

- **No terminal embedding.** Stage 3 inherits the design-doc Appendix A rejection of ConPTY/WebView2/xterm.js. The tree view + event stream is strictly richer.
- **No real-time delta streaming UI in 3A/3B.** `--include-partial-messages` and per-token deltas are an optional Stage 3 enhancement, only if needed. Default mode (per-turn batched) is fine for v1.
- **No agent fleet across machines.** Single-host, single-user. The README cluster horizon is a north star, not a Phase 3 target.
- **No "approve everything" workflow.** The approval modes (`ask|auto|deny|allow`) ship in 3B; the GUI surfaces them but does not invent a new "blanket approval" gesture.
- **No native packaging story** (installers, code-signing, notarization) in 3B. Self-contained `dotnet publish` outputs are the developer-mode delivery; Stage 4 owns release engineering.
- **No mobile, no web.** Avalonia could go there; we are not.

---

## 3. Vision

> *InfiniContext is the operations console for a small fleet of long-running coding agents. One human, one window, several agents working on real problems across several repositories - supervising rather than driving.*

The GUI is the first surface where that claim is honest. Until Phase 3 the host has been a *single agent's* control plane that happens to support N sessions; with the GUI it becomes a *supervisor's* console.

Three things the GUI must make easy that the console makes hard:

- **"Show me what's happening right now."** A live dashboard - one row per active session, with state badge, current tool, latest assistant fragment, awaiting-approval indicator, drift counter. The console has `/state` and `/list`; the GUI has them on-screen continuously.
- **"What did this task do over the last week?"** Drilling into a task should open the artifact set as a navigable view (markdown rendered, file list on the left), with the session history below - every session that touched this task, what it did, when it ended. The console has `/task show`; the GUI lets the user *read* the artifacts.
- **"Tell me when something needs me."** Tool approvals, context-pressure nudges, errors, completions are first-class events. They appear in a notifications/activity feed alongside per-pane decoration. The user can step away from the keyboard for ten minutes and come back to a clear "here's what asked for you while you were gone."

The bet is that **supervision quality is the bottleneck**, not raw spawn count. A user with five competent agents and a UI that surfaces what they need is faster than the same user with twenty agents in twenty terminal tabs. Phase 3 is what makes "five competent agents" navigable.

---

## 4. Personas and scenarios

### 4.1 Personas

- **Solo developer** - runs InfiniContext on a workstation against 1-3 repos, 1-4 agents at a time. Wants to keep agents productive while doing other work (reviewing PRs, in meetings). Approval mode is `auto` with `confidence=low` falling back to ask. *Primary persona for v1.*
- **Tech-lead supervisor** - runs InfiniContext across 5-10 repos and 3-10 agents simultaneously, mostly read-only for the user (the agents drive). Wants the cross-task dashboard above all. Approval mode varies per session (some `auto`, some `ask`). *Primary persona for Stage 3 polish.*
- **Maintainer of InfiniContext itself** - uses the GUI as the test surface while also debugging the host. Wants the existing console fallback to still work; wants logs accessible; wants the GUI to expose enough state that a bug report does not require attaching a transcript file. *Tertiary persona; lower priority but must not be regressed.*

### 4.2 Scenarios (drives the IA in section 5)

1. **Resume yesterday's work.** Launches host, sees task list, notices `0042` was `Stale`, clicks `0042`, reads `progress.md` to see where the last session stopped, clicks **Switch**, a new session pane opens, focused, with the pointer pre-fill already sent.
2. **Fan out a refactor across three repos.** Has three sessions running in three `cwd`s, opens broadcast palette, types prompt once, confirms target sessions, watches three transcripts populate side by side.
3. **Step away for 10 minutes.** Comes back. Top of window shows two pending approvals. Clicks one; side panel shows tool name + parameters + originating session; Approve. Repeats for the second. No transcript scrolling needed.
4. **Diagnose a stalled session.** A session has been "Streaming" for two minutes. Opens the session; drift counter is `7`; clicks the counter; modal lists the 7 unknown event types with raw JSON; copies one; files an issue. Falls back to opening the `logs/` file from a menu when needed.
5. **Hand off a long-running task.** Soft-threshold nudge fires for session A. User sees a notification: *"Session A is at 62% - agent has been asked to wind down."* User does nothing; session ends cleanly. New session creation hint pops in the task pane: *"Task 0042 ready for hand-off."* User clicks **Continue**; new session is born against the same task.

These scenarios drive the IA below.

---

## 5. Information architecture

The GUI is organised around **three nested levels** of object that the user navigates. None of these are new - they already exist in code; the GUI is the first place they are first-class in the UI.

```
Host
+-- Tasks                    (Stage 2A registry; SQLite at ~/.infinicontext/infinicontext.db)
    +-- Sessions             (active SessionContext, plus historical rows from
        +-- Turns               ~/.infinicontext/index.json; per-task filtering optional)
            +-- Content blocks  (assistant text, thinking, tool call, tool result, sub-agent)
```

Per design-doc section 4.7, **Turns and Content blocks are tree nodes** - the existing model is unchanged.

Two cross-cutting surfaces tie this hierarchy together:

- **Activity feed** - a flat, time-ordered stream of *interesting* events (approval requests, threshold trips, task status changes, drift increments, session exits) drawn from every session simultaneously. Acts as the "what asked for me?" surface.
- **Status strip** - a permanent, compact summary (aggregate counts: idle/working/awaiting-approval/drift) plus the current host-wide approval mode and the per-host metadata (host id, `claude` version, `MinClaudeVersion`/`TestedAgainstVersion`).

### 5.1 Top-level navigation

A single primary window. Three top-level views, switchable via a sidebar (or app-menu / keyboard shortcut):

| View | Purpose | Default landing? |
|---|---|---|
| **Sessions** | Live multi-session work - the closest analogue to today's `MultiSessionHost`. | Yes (matches current muscle memory). |
| **Tasks** | Browse durable tasks across the host, read artifacts, see lineage, start/resume sessions. | No (one click away). |
| **Activity** | Cross-session event feed. Filterable by kind (approval, threshold, drift, exit, error). | No (one click away). |

A fourth, *Settings*, is a modal/sub-window, not a top-level view.

This is the minimum useful split. Tab-bar / tabbed-document UI is rejected for the same reason design-doc section 9 Stage 3 rejected it: the list scales better past ~8 sessions, and tabs imply equal weight where the Sessions view ranks them.

### 5.2 Sessions view (primary)

A three-column layout when room allows; collapses gracefully on narrow windows:

```
+----------------+---------------------------------+----------------------+
|  Session list  |     Focused session transcript  |   Inspector / context|
|  (active first;|                                 |   (collapsible)      |
|   recent below;|                                 |                      |
|   state badges)|                                 |                      |
+----------------+---------------------------------+----------------------+
|        Activity strip (last 5 events; click to open Activity view)      |
+-------------------------------------------------------------------------+
| Prompt box (multi-line; Ctrl+Enter to send; Esc to interrupt)           |
+-------------------------------------------------------------------------+
```

- **Session list** - one row per active session: host-id short prefix, focused indicator, `cwd` shortened, claude-id short prefix, state badge, drift count, last cost, approval-pending indicator. Below: collapsible "Recent" group of closed sessions sourced from `~/.infinicontext/index.json`. Click activates; double-click opens in a new pane (post-MVP if multiple split-views land).
- **Focused session transcript** - a **collapsible tree** per design-doc section 4.7. Each turn is a root node; assistant text / thinking / tool calls / tool results / sub-agents are children. Markdown rendered in text blocks; tool calls collapsed by default, expanded to show params + result. Long tool results lazy-loaded from `~/.claude/projects/<encoded-cwd>/<session_id>.jsonl` on expand (the per-event-size concern from design-doc section 6.2).
- **Inspector** (right column, collapsible) - context-sensitive: when no special selection, shows session metadata (cwd, model, `system/init` data, supervisor backend, approval mode, lock holdings if any, attached task id). When a tool-call node is selected, shows full input/output. When an approval is pending in this session, the approval card appears here (with Allow / Deny / details) - see section 6.3.
- **Activity strip** (bottom) - most recent 3-5 events across all sessions. One-line each. Click opens Activity view filtered to that session/kind. This is the compromise that lets a user keep eyes on Sessions while still seeing what's happening elsewhere.
- **Prompt box** - multi-line text field at the very bottom. Ctrl+Enter sends to focused session (overridable in settings); Esc sends `InterruptAsync`. Slash commands (`/n`, `/focus`, `/help`, `/task ...`) are intercepted (same registry as today's `SlashCommandRegistry`) and routed; unknown ones still forward verbatim to claude (per Stage 2B D6 - preserves `/clear`, `/compact`). A small **Broadcast** toggle next to the send button opens the target picker (section 6.2).

### 5.3 Tasks view

A two-pane layout:

```
+----------------------------+------------------------------------------+
|  Task list                 |   Selected task                          |
|  (filterable / sortable;   |   +------------------------------------+ |
|   per-repo group; status   |   | Header: id + title + status + cwd  | |
|   badge)                   |   +------------------------------------+ |
|                            |   | Artifact list   | Selected file    | |
|                            |   | (index/desc/    | (markdown render;| |
|                            |   |  progress/...)  |  read-only)      | |
|                            |   +-----------------+------------------+ |
|                            |   | Session history (active + closed)  | |
|                            |   +------------------------------------+ |
+----------------------------+------------------------------------------+
```

- **Task list** - derived from `TaskService.ListAsync`. Filters: status (`InProgress` / `Idle` plus future `InSync`/`Stale`), `home_cwd`, free-text title search. Right-click: **New**, **Fork**, **Merge selected**, **Delete**, **Show in Files**, **Open `home_cwd` in shell**. Honours the host's existing CRUD; no new primitives.
- **Selected task** - header (id, title, status, `home_cwd`, parent-task lineage if any, created/last-active timestamps), then a three-row inner layout:
  - **Artifacts** - list of files in `.task/<id>-<slug>/` from `TaskFolder.ListArtifactsAsync` with the `index.md`-known description; the selected file is rendered with markdown (read-only in v1; *Open in EDITOR* button for edits, since the agent contract says the agent is the author of these files).
  - **Session history** - every session that touched this task: started, ended, end-reason (`flushed | crashed | cancelled | completed`), session-id, link to "open transcript" when the underlying jsonl still exists in `~/.claude/projects/`. Drives the "what did this task do" scenario.
- **Switch / Resume buttons** (header) - `Switch` calls the same `TaskService.PrepareSwitchAsync` path as the slash command, creating a new session bound to the task. `Resume <session>` from the session-history list opens a closed session via the existing resume-by-id path.

### 5.4 Activity view

A flat time-ordered list (newest first) of events from every session, filterable by:

- **Kind**: `Approval requested | Approval resolved | Threshold trip (soft/hard) | Drift increment | Task status change | Session exit | Error`
- **Severity**: `Info | Warning | Error`
- **Session / Task**: scope to one
- **Time range**: last hour / day / since launch

Each event is a row with timestamp, kind icon, originating session/task, one-line summary, and a "Open" link that takes the user to the matching transcript turn or task artifact.

This view is the activity strip's overflow: when the strip is too small for what's happening, the user opens Activity for the full record.

### 5.5 Status strip

Permanent, top of window. Compact:

```
[InfiniContext]   sessions: 3 active, 2 awaiting, drift:1   approval: auto   claude 2.1.133 (tested:2.1.112)   [gear]
```

Click any segment for the relevant view (sessions opens Sessions view; approval opens settings; drift opens a modal listing unknown types from the parser's drift counter). The gear icon opens Settings.

Importantly: **the "awaiting" count is the same number that today appears via per-session `PendingApprovalSlot.Current` flags**. The status strip's only job is to make it impossible to miss.

---

## 6. UI operations (feature mapping)

This section is the exhaustive map: every operation the host supports today has a primary GUI surface in 3B/3C. Operations marked **(post-3C)** are deferred.

### 6.1 Session lifecycle

| Operation | Today (console) | Phase 3 surface |
|---|---|---|
| Spawn new session | `/n <cwd>` | Sessions view, **+ New session** button, cwd picker (file dialog + recent cwds dropdown sourced from session index). |
| Focus session | `/focus <prefix>` / `/next` / `/prev` | Click row in session list. `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle. |
| Send prompt | type at REPL, Enter | Type in prompt box, **Ctrl+Enter** (config). |
| Interrupt | `Esc` (focused) | Stop button next to prompt box; **Esc** keyboard shortcut. |
| Close session | implicit on `/quit` / host exit | Right-click row, **Close**. Confirmation modal if approvals pending. |
| List sessions | `/list` | Sessions view itself. |
| Resume closed | `/resume <id>` | Session list "Recent" group, click row, **Resume** action. Or from Tasks view, session history, **Resume**. |

### 6.2 Broadcast

| Operation | Today | Phase 3 surface |
|---|---|---|
| Multi-target broadcast | `/broadcast <prefixes,*> <text>` | Prompt box, **Broadcast** toggle, multi-select target list (checkbox per active session, `*` shortcut), send. Result rendered as a per-target receipt strip ("sent / failed / not-found"), mirroring `BroadcastSendResult`. |
| Single-target rejection | console refuses (steering message) | Same rejection at the UI: send button disabled until 2+ targets selected, with the same steering hint. |

### 6.3 Approvals

| Operation | Today | Phase 3 surface |
|---|---|---|
| Approval request appears | inline in focused console transcript; `[approval]` line | (a) Banner in the originating session's transcript at the relevant turn. (b) Card in the right-column Inspector when that session is focused - tool name, brief params, expand for full params, **Allow** / **Deny** buttons, countdown. (c) Pulse on the session-list row icon if not focused. (d) Increment status-strip "awaiting" count. (e) Optional OS notification (post-3C). |
| Resolve approval | console `y` / `n` after prompt | Allow/Deny buttons; keyboard `A` / `D` when the card is focused. Auto-deny on timeout - same 60 s default. |
| Switch approval mode | `/approval ask\|auto\|deny\|allow` | Status-strip badge dropdown. Banner gating (the `announce` flag from D35) carries over: the first toggle prints the back-end info, subsequent toggles are silent. |
| View past approvals | scroll the transcript | Activity view filtered to `Approval resolved`. |

### 6.4 Tasks

| Operation | Today | Phase 3 surface |
|---|---|---|
| Create | `/task new "<desc>"` | Tasks view, **+ New task**, description box, optional cwd override, "Title will be generated" hint while the light model runs. |
| List / browse | `/task list` | Tasks view itself. |
| Show one | `/task show <id>` | Select row in task list. |
| Switch into | `/task switch <id>` | Tasks view header, **Switch** button. Refuses with explanation if `home_cwd` is stale or the task is already claimed (`TaskActivityCoordinator`). |
| Fork | `/task fork <id> [title]` | Right-click, **Fork**; optional title in modal. |
| Merge | `/task merge <ids>` | Multi-select rows in task list, **Merge selected**, confirmation modal naming output task; deterministic-concat in v1 (light/full-model tiers post-3C). |
| Delete | `/task delete <id>` | Right-click, **Delete**, confirmation modal with `--remove-folder` checkbox. Refuses `InProgress` (same as the slash command). |
| Spawn parallel session | `/task spawn <id>` | Tasks view header, **Spawn parallel pane**. Same `TaskActivityCoordinator` gating. |
| Read artifact | open `.task/<id>-<slug>/<file>.md` in EDITOR | Inline markdown render in Tasks view, **Open in EDITOR** button for edits. |
| Reconcile index drift | host prepends note to first user message | Visible in Tasks view as a yellow "drift detected" indicator next to the task; clicking shows the diff between disk and `index.md`. (Reconciliation still happens via the agent - the GUI does *not* silently rewrite the index.) |

### 6.5 Issues (per Stage 2A section B `/issue` plan)

The `/issue` capture command is a near-term Stage 2B+ follow-up (todo.md section B). The GUI should expose it from day one even if the slash command lands first:

| Operation | Phase 3 surface |
|---|---|
| Capture issue from focused turn | Right-click on any transcript node, **File issue**, modal pre-filled with `Where:` + a copy of the surrounding context, **File**. Appended via the existing `issues.md` contract under the lock channel. |
| List task issues | Tasks view artifact list shows `issues.md` if present; tab inside it lists numbered entries. |
| Resolve issue | Mark resolved checkbox, editor edit through the lock channel. |

### 6.6 State, drift, cost

| Operation | Today | Phase 3 surface |
|---|---|---|
| `/state` | one-line printout | Permanently visible in the status strip + session-list row metadata. |
| `/cost` | per-session cost printout | Inspector, session metadata panel ("cost: $0.0124 across 14 turns"). |
| Drift counter | `/state` shows top-5 unknowns | Status strip drift number; click opens a modal showing per-session drift histogram, click an unknown type for raw JSON of the first occurrence (sourced from the parser's preserved `Raw`). |

### 6.7 Settings

| Operation | Today | Phase 3 surface |
|---|---|---|
| Edit `settings.json` / `settings.user.json` | hand-edit JSON | **Settings** modal: tabbed page per section (`approval`, `tasks`, `models`, `logging`, `versioning`). Each field is a typed input with the schema description as a tooltip (`settings.schema.json` already exists; the modal renders from it). **`settings.user.json` writes only** - `settings.json` defaults are read-only in the UI. Same precedence rules; the modal shows both the effective value and which layer set it. Validation hard-fails on save, matching the loader. |
| Reload settings | restart host | Restart is still required for some keys; the modal documents per-field which need a restart. (Hot-reload is rejected per Stage 2A - Stage 3 does not change that.) |
| Discover `claude` install | startup PATH probe | Settings, **About** tab shows the resolved path, `claude --version`, `MinClaudeVersion`, `TestedAgainstVersion`. |

### 6.8 Logs

| Operation | Today | Phase 3 surface |
|---|---|---|
| Open the latest log | `code logs/` | Menu, **Open logs folder**. Per-session log view is **not** built in 3B; logs remain the agent's primary debugging surface, not the user's. |

### 6.9 Process supervision

| Operation | Today | Phase 3 surface |
|---|---|---|
| Job-Object / POSIX-signal supervision | automatic, invisible | Invisible - same. Status strip shows backend (`Win JobObject` / `POSIX SIGTERM/SIGKILL` / `Noop`) under **About** for diagnostic purposes only. |

---

## 7. Stack and layering

### 7.1 Choices already locked

From `docs/infinicontext-initial-design.md` section 6.1 and `docs/stage2/decisions.md` section 13, **not re-litigated in Phase 3**:

- **Framework: Avalonia 11.x.** Cross-platform Win + Mac single codebase. WPF / WinUI 3 / MAUI rejected.
- **Single primary window** with sidebar navigation. No tabbed-document layout.
- **No terminal embedding.** No ConPTY, no WebView2, no xterm.js.
- **MVVM** as the binding style. (Flavour open - see section 10.)
- **Markdown rendering with syntax-highlighted code blocks** required for assistant text. (Library open - see section 10.)
- **Self-contained `dotnet publish` packaging** for `win-x64`, `osx-x64`, `osx-arm64`. macOS code-signing / notarization is Stage 4.

### 7.2 Project layout (3B target)

Add one Avalonia project; keep Core unchanged. The console host stays buildable through 3B as a fallback / diagnostic surface; it is retired at the end of 3C when the GUI is fully equivalent.

```
src/
+-- InfiniContext.Core/           (unchanged - already UI-agnostic)
+-- InfiniContext.Cli/            (today's Spectre.Console multi-session host; retained through 3B)
+-- InfiniContext.Gui/            (new - Avalonia application; entry point InfiniContext.Gui.exe)
```

`InfiniContext.Gui` depends on `InfiniContext.Core` only - *no* dependency on `InfiniContext.Cli`. Any shared primitive currently in `Cli` that the GUI needs (e.g. `ISlashCommand` registry, `ApprovalSinkFactory`, `RunArgs` parsing) is either (a) called from `Core` already, or (b) moved into Core / a new `InfiniContext.Hosting` library shared by both heads. Decision: move shared types into `InfiniContext.Core` rather than introducing a third library, *unless* a circular dependency forces otherwise - which it should not, because `Cli`-side types like `SlotApprovalSink` already follow the "linked-source-file unit-testing" convention that proves they're stack-agnostic.

### 7.3 Threading model

- **Process I/O on `Task`s off the UI thread**, same as today. `ClaudeSession` is already async and channel-based; nothing changes server-side.
- **Channel reader to UI marshalling via `Dispatcher.UIThread.Post`.** Spectre uses `IAnsiConsole` writes; Avalonia uses the dispatcher. The plumbing lives in a new `TranscriptViewModel` that subscribes to `TranscriptModel.FragmentAppended` and posts diffs to the UI.
- **`SessionTranscriptReader` is reused unchanged.** Per Stage 2B D28 it already lives in Core; the GUI just attaches another observer.
- **Backpressure unchanged.** `ClaudeSession._events` stays `bounded(1024, DropOldest)`; `TranscriptModel` stays a bounded in-memory ring. The 3C disk-overflow item (Stage 2B D20) becomes more pressing because a GUI user can scroll *back* a long way - pair with section 10 open question 4.

### 7.4 ViewModel surface (rough sketch)

Not the full design - that's a 3B sub-doc - but the shape:

- `MainWindowViewModel` - owns `SessionsViewModel`, `TasksViewModel`, `ActivityViewModel`, current top-level view, status-strip aggregate.
- `SessionsViewModel` - observable collection of `SessionRowViewModel` (one per active + recent session); `FocusedSessionId`; `BroadcastTargets`. Wraps `SessionManager.Active` and the session index.
- `SessionViewModel` - per-session: `TranscriptTree` (root nodes per turn), `ApprovalSlot`, `State`, `Drift`, `Cost`. Wraps `SessionContext`.
- `TasksViewModel` - observable collection of `TaskRowViewModel`; selected task; selected artifact. Wraps `TaskService` + `TaskFolder`.
- `ActivityViewModel` - observable, append-only list of `ActivityEvent`s. Fed by hooks on `TranscriptModel`, `PendingApprovalSlot`, `ContextPressureMonitor`, `SessionManager.Closed`, `TaskService` status changes.
- `SettingsViewModel` - typed bindings against `InfiniContextSettings` defaults + the user-overrides layer. Writes only the user layer.

Bindings are explicit, not "magic" - every observable maps to a Core source-of-truth. The GUI is a *view* over the running host; it does not duplicate state.

### 7.5 Spawn-to-first-pixel target

Targets less than 800 ms. The cost budget breaks down roughly as: Avalonia startup ~200 ms (well-known cold-start cost), settings load + DB open ~50 ms, session index read ~10 ms, first frame ~100 ms, headroom ~440 ms. **This is achievable but not free** - measure end-to-end in 3B and treat regressions as bugs. If it slides past 1.2 s, investigate (likely culprits: DB migrations on first launch, font loading, theme load).

---

## 8. Cross-cutting concerns

### 8.1 Theming

- Light + dark, switchable. Default follows OS (`Application.RequestedThemeVariant = Default`).
- Avoid colour-only status - every badge has icon + text. (Accessibility floor section 2.2.)
- Contrast meets WCAG AA in both modes.

### 8.2 Accessibility

- Every primary control reachable by keyboard. `Tab` cycles within a view; `Ctrl+1/2/3` switches top-level views; `Ctrl+,` opens Settings; `Ctrl+N` new session; `Ctrl+T` new task; `Esc` interrupts focused session.
- Screen-reader labels on every actionable control (Avalonia `AutomationProperties.Name`).
- Font scaling honours OS setting; the layout reflows from 100% to 200%.
- No animation that can't be disabled (Avalonia animations gated on `EnableAnimations` setting).

### 8.3 Internationalisation

- English-only in v1. All user-facing strings via a single resource file from day one, so a future translation pass doesn't require a refactor.
- Dates / times use the OS locale; numbers use invariant-culture in transcripts (so a log line a German user copies parses for a US developer).

### 8.4 Security

- All Stage 1-2B security rules carry forward unchanged: cwd is a trust boundary, `.mcp.json` auto-load needs a confirmation banner (the trusted-cwds banner from Stage 2B D15 lands in the GUI naturally - first-time-discovery modal on every new cwd), env vars matching `*_KEY`/`*_TOKEN`/`*_SECRET` are scrubbed from logs.
- The GUI never displays a scrubbed env var. Where a session header says "API key set: yes / no", it never echoes the value.
- File-open dialogs that target `.task/` or `.claude/` get a clear visual treatment so the user knows they're inside InfiniContext-managed paths.

### 8.5 Crash and orphan handling

- `IProcessSupervisor` is unchanged. The GUI's `App.OnExit` flushes Serilog and disposes `SessionManager`, which cascades to every child.
- On Windows, killing the GUI process via Task Manager triggers `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` - same as today.
- On macOS, the signal handlers fire as designed.
- Hard-kill orphan recovery: a startup scan (deferred in Stage 2B Phase 7) becomes more attractive in Phase 3 because the GUI can surface "found N stray claude processes - kill them?" as a modal. *Promoted from "deferred" to "opportunistic Phase 3B add-on."*

### 8.6 Telemetry

- Local-only logging continues. No outbound telemetry in v1.
- Crash dumps stay opt-in and local (a "save crash dump to `~/.infinicontext/crashes/`" prompt on a caught exception).

### 8.7 Updates

- Out of scope for Phase 3. The GUI tells the user the host version + tested-against-`claude` version, but does not check for or install updates. Stage 4 owns this.

---

## 9. Phase 3 staging

| Phase | Scope | Exit |
|---|---|---|
| **3A - Design (this doc)** | Goals, vision, IA, operations map, open decisions. Sub-docs in `docs/stage3/`. | User sign-off on this doc + open-question answers. |
| **3B - Foundation** | Avalonia shell; Sessions view (list + transcript tree + prompt + interrupt); status strip; basic per-session approval banner; Settings modal (read-only first, write-second); minimum slash-command coverage (`/n`, `/focus`, `/list`, `/quit`, `/help`, free-form prompts, broadcast, approval mode toggle). Windows-only. Markdown rendering minimal (plain monospace OK). | Three concurrent sessions, side-by-side, with approvals visible from any focused pane. Exit-criteria smoke test mirrors Stage 2B's `MultiSessionExitCriteriaTests.cs` adapted for the GUI. |
| **3C - Polish + macOS** | Tasks view (artifact rendering, session history, full lifecycle UI); Activity view; markdown + syntax highlighting; large tool-result virtualization; drift modal; OS notifications for approvals; macOS validation + CI; theme polish; accessibility audit. | All section 2.2 targets met on both platforms; full feature parity with the console host; the Stage 2B console can be retired (or kept as `--console-mode` fallback for diagnostic). |

**Estimated total effort, Phase 3:** 8-14 weeks for the primary developer, depending on Markdown / syntax-highlight library shape and how much accessibility / polish lands inside the budget vs. as a 3D follow-up. Refine when 3B begins.

Each phase ends with a runnable, green-tests artifact; each lands as its own series of PRs.

---

## 10. Open decisions

These need answers before 3B begins. Recommendations marked **(rec.)** are the author's default; the user resolves.

1. **MVVM flavour** - CommunityToolkit.Mvvm vs ReactiveUI vs hand-rolled `INotifyPropertyChanged`.
   - **(rec.) CommunityToolkit.Mvvm.** Smallest dependency surface, source-generator-based, well-supported by Avalonia samples. ReactiveUI is overkill for a UI whose state is mostly point-events (turn arrived, approval resolved), not continuous observables.
2. **Markdown library** - Markdown.Avalonia vs Avalonia.Markdown vs AvaloniaEdit (with custom markdown layer) vs roll our own minimal renderer.
   - **(rec.) Markdown.Avalonia** for v1 of 3B (plain rendering, no code-block highlight). For 3C, evaluate replacing or layering a syntax-highlight pipeline (TextMate grammars via `TextMateSharp`, which `AvaloniaEdit` already integrates) only if Markdown.Avalonia's code-block story is insufficient. Decision deferred to 3C start with a spike.
3. **Session list virtualization** - at what session count does naive rendering start hurting?
   - **(rec.)** Don't pre-optimise; profile at 25 sessions in 3B. Avalonia `ListBox` is `VirtualizingStackPanel`-backed by default; should be fine well past the user's likely cap.
4. **Transcript tree memory model** - keep everything in memory, or page out closed sessions to disk?
   - **(rec.)** In-memory for 3B (matches Stage 2B's `TranscriptModel` ring). In 3C, implement disk-backed overflow per `~/.infinicontext/transcripts/<session_id>.log` (Stage 2B D20 deferral). The trigger is a real user scenario hitting the ring cap.
5. **Settings modal write target** - only `settings.user.json`, or both layers with a "edit defaults" advanced switch?
   - **(rec.)** Only `settings.user.json`. Defaults are committed code; mutating them from a GUI breaks the layered-precedence story (`docs/stage2/decisions.md` section 6) and surprises tests.
6. **Activity feed persistence** - across-session-only (rebuilt at launch from session index + transcript replay), or written to its own file for survivability?
   - **(rec.)** Reconstructed at launch in 3B from the existing stores (no new file). Persist only if 3C needs a "what happened while the host was off" recap, and even then derive from claude's jsonl rather than a new InfiniContext-owned mirror - consistent with the design-doc section 4.5 "do not duplicate" rule.
7. **Tab key in the GUI prompt box** - insert literal tab, or move focus?
   - **(rec.)** Move focus (matches IDE / web conventions). Authors who need a literal tab in a prompt can paste it; the loss is small and the focus-stuck-in-prompt surprise is worse.
8. **Stage-2B console retirement** - keep `InfiniContext.Cli` shipping post-3C as a fallback, or delete it?
   - **(rec.)** Keep through Stage 3 as a documented diagnostic mode (`infinicontext --console-mode`). Delete in Stage 4 alongside release engineering. Avoids a flag day during GUI stabilisation.
9. **Approval OS-notifications** - in 3B or 3C?
   - **(rec.)** 3C. The status strip + activity strip cover the in-app surface adequately for 3B; cross-platform desktop notifications (toast / NSUserNotification) is its own scope.
10. **Theming default** - follow OS, dark-only, light-only?
    - **(rec.)** Follow OS, with explicit override in Settings, Appearance.
11. **Single-instance enforcement** - what happens if the user double-clicks the GUI binary?
    - **(rec.)** Second instance hand-off: the second launch detects the first via a named pipe / Unix socket lock, sends its argv (e.g. `cwd-to-open`), and exits. The first window comes to the front and creates a new session in that cwd. Single-instance is friendlier than two competing GUIs against the same `~/.infinicontext/index.json`.
12. **What happens when the host can't find `claude`?**
    - **(rec.)** A first-launch modal with the install link from design-doc section 7.2 and a re-detect button. The GUI then disables every action that needs a session until the user resolves it.

---

## 11. Risks

1. **Scope creep.** The "supervisor across many projects" vision is intoxicating; the bar for v1 is "console parity + tasks-as-first-class + cross-session visibility." Mitigation: section 6 is the contract - anything beyond it goes through a separate proposal.
2. **Markdown / syntax-highlight library churn.** Avalonia's ecosystem is younger than WPF's; the chosen lib may stop being maintained. Mitigation: pick something thin and replaceable; isolate behind an `IMarkdownRenderer` so the swap is one file.
3. **macOS divergence.** Phase 7 shipped the POSIX supervisor unvalidated; Phase 8 wired CI but the manual `ci-macos.yml` dispatch is still the only proof point. Phase 3 is the first thing a Mac user actually *runs interactively*, so a class of bugs (font rendering, theme, file dialogs) will surface for the first time. Mitigation: 3C reserves explicit Mac-validation time; don't compress it.
4. **Multi-session UI scaling.** 3 sessions is easy; the tech-lead persona wants 10. Mitigation: load-test at 15 in 3B; if any pane drops below 30 FPS or the activity feed lags, redesign the affected surface before 3C.
5. **Agent contract drift.** The GUI must never silently mutate `.task/` files or the agent's understanding of artifacts. Mitigation: all write paths under `.task/` continue through `TaskService` + the lock channel; the GUI's read-only-with-Open-in-Editor stance enforces this at the surface.
6. **Approval latency.** A user clicking through approvals one-at-a-time is slower than the 60 s auto-deny window when 5 stack up. Mitigation: per-session queueing exists (Stage 2B Phase 5); the GUI must surface it clearly so the user isn't surprised by a deny.
7. **Backwards compatibility with console-mode users.** Some users (notably maintainers) will prefer the console for diagnostic / scripting. Mitigation: see open question 8 - keep the console shipping.

---

## 12. Exit criteria for Phase 3A (this doc)

- User has reviewed this document and (a) approved the goals/targets in section 2, (b) confirmed or amended the IA in section 5, (c) decided the section 10 open questions or scheduled them for sub-docs.
- The Stage 3 section of `docs/infinicontext-initial-design.md` section 9 has been updated to point here (one-line change: *"superseded by `docs/stage3/design.md` once Phase 3B starts"*).
- A Phase 3B plan is queued in `docs/stage3/plan.md` (separate task / doc) covering the foundation slice in section 9.

---

## Appendix A - What carries forward unchanged

For a future agent picking this up: the following are **already correct** in the existing design docs and need no Phase 3 amendment.

- Wire protocol, parser tolerance contract, state machine, persistence rules (`infinicontext-initial-design.md` section 4).
- Process supervision (section 5). The GUI is just another host; same `IProcessSupervisor`.
- Tool approvals (section 4.6) - the GUI consumes per-session approval slots that already exist.
- Logging (section 7.4) - same Serilog file sink; the GUI adds nothing.
- Versioning policy (section 7.3) - same `MinClaudeVersion` / `TestedAgainstVersion`.
- Tasks (section 9 Stage 2A) - fully shipped; the GUI is a view, not a re-implementation.
- Multi-session (section 9 Stage 2B) - `SessionManager`, `TranscriptModel`, `SessionTranscriptReader`, `PendingApprovalSlot`, `SwitchableApprovalSink` all carry over. The GUI is the second consumer; the console was the first.

The GUI's job is **rendering and routing**, not new behaviour. Everything user-visible should already have a corresponding primitive in Core.

---

*End of Phase 3A.*