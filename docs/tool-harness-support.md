# Claude Code built-in tools — harness support audit

*Written 2026-06-11, against the built-in tools reference at
<https://code.claude.com/docs/en/tools-reference>. Host is tested against `claude` 2.1.112
(`settings.json::versioning.testedAgainstVersion`); the reference documents some tools newer
than that (`Monitor` needs ≥ 2.1.98, `TodoWrite` is disabled by default as of 2.1.142 in
favor of `TaskCreate`/`TaskGet`/`TaskList`/`TaskUpdate`). Re-check this audit when bumping
`TestedAgainstVersion`.*

## How to read this

InfiniContext does not implement Claude's tools — they all execute inside the spawned
`claude` child process. The harness touches a tool in exactly three places:

1. **Approval** — permission-required tools route through the MCP approval sidecar
   (`--permission-prompt-tool mcp__infinicontext__approval_prompt`) into the per-session
   `SwitchableApprovalSink`/`PendingApprovalSlot`. This already works generically for every
   tool, in all four modes (`ask | deny | allow | auto`).
2. **Rendering** — `TranscriptRenderer` renders every `tool_use` as
   `[tool: <name>(<input ≤200 chars>)]` and every `tool_result` as `[tool-result: ok|error]`.
   Generic, no per-tool shaping.
3. **Session-model side effects** — a few tools change things the host *models* (focused
   cwd, permission mode, pending scheduled prompts, agent-initiated turns) and silently
   invalidate host state if ignored.

A tool "needs harness support" only when generic approval + generic rendering is not enough.

## Summary table

| Tool | Permission | Runs where | Host support today | Gap |
| :--- | :--- | :--- | :--- | :--- |
| `Read`, `Glob`, `Grep`, `LSP` | No | claude | generic render | none |
| `Edit`, `Write`, `NotebookEdit` | Yes | claude | sidecar approval + generic render | none (nice-to-have: diff rendering, Stage 3) |
| `Bash`, `PowerShell` | Yes | claude | sidecar approval + generic render | background-task visibility (§5) |
| `WebFetch`, `WebSearch` | Yes | claude | sidecar approval | none |
| `Agent` (subagents) | No | claude | events correlated via `parent_tool_use_id`, rendered flat | nice-to-have: grouped/collapsible rendering (Stage 3) |
| `Skill`, `Workflow`, `ShareOnboardingGuide` | Yes | claude | sidecar approval | none |
| `ToolSearch`, `ListMcpResourcesTool`, `ReadMcpResourceTool`, `WaitForMcpServers` | No | claude | generic render | none |
| `RemoteTrigger`, `ScheduleWakeup` | No | claude + claude.ai | generic render | §4 (unsolicited turns) |
| **`AskUserQuestion`** | No | claude → **needs host answer** | **broken** — surfaces as empty `[tool-result: ok]` | **§1 — top priority** |
| **`ExitPlanMode`** | Yes | claude → host approval | flows through sidecar but plan is mangled by 200-char truncation | **§2** |
| **`EnterPlanMode`** | No | claude | invisible to host | **§2** — track per-session mode |
| **`TaskCreate`/`TaskGet`/`TaskList`/`TaskUpdate`** (and legacy `TodoWrite`) | No | claude | executes fine, zero host visibility | §3 — render the checklist |
| **`Monitor`** | Yes | claude (background) | approval works; output events untested | §4/§5 — agent-initiated turns |
| **`CronCreate`/`CronDelete`/`CronList`** | No | claude (session-scoped) | invisible; fires unsolicited turns | §4 |
| **`TaskStop`/`TaskOutput`** | No | claude | generic render | §5 — background-task listing |
| **`EnterWorktree`/`ExitWorktree`** | No | claude | invisible; host cwd model goes stale | §6 |
| `PushNotification` | No | claude → Anthropic infra | nothing | §7 — optional native notification |
| `SendMessage`/`TeamCreate`/`TeamDelete` | No | claude (experimental, env-gated) | nothing | §8 — supervision check only |

## §1 AskUserQuestion — the only *broken* tool (CLI **and** GUI)

Already tracked in `docs/todo.md` §A ("AskUserQuestion tool surfaces as empty success",
source `.task/0005-manual-testing-review-notes/issues.md` #0001). Restated here with the
harness shape:

- **Symptom.** The agent calls `AskUserQuestion` with `questions[]` (each with `question`,
  `header`, `options[] {label, description}`, `multiSelect`). In stream-json mode there is
  no TTY for claude's own picker, so the tool resolves to an empty success and the agent
  never sees a selection.
- **What's needed.**
  1. **Investigate the transport first.** Determine how claude expects the answer back in
     `--print --input-format stream-json` mode — most likely the stream-json **control
     protocol** (`control_request`/`control_response` frames), which the host currently
     neither parses specially (they land as tolerated unknown events) nor emits. This is a
     prerequisite; the UI work is the easy half.
  2. **CLI**: Spectre `SelectionPrompt`/`MultiSelectionPrompt` parked in something shaped
     like `PendingApprovalSlot` (a `PendingQuestionSlot`), focused-pane only, same way
     approvals surface today.
  3. **GUI**: option-list dialog (the new `ConfirmDialog` from task 0013 is the pattern to
     extend); must render `header`, `label`, `description`, and honor `multiSelect`.
  4. **Stopgap until that lands**: return/inject a structured *error* result so the agent
     recognizes the tool as unsupported and falls back to free-text questions, instead of
     silently receiving an empty success it misreads as an answer.

## §2 Plan mode — `EnterPlanMode` / `ExitPlanMode`

`ExitPlanMode` is permission-gated, so it *already* arrives at the host's approval sink —
the plumbing works. Two gaps:

- **Plan rendering.** The approval banner and transcript both truncate `tool_use.input` to
  200 chars; the entire point of `ExitPlanMode` is a multi-KB markdown `plan` field the
  user must read before approving. Needs a per-tool-name special case: render `plan` as
  full markdown in the approval surface (CLI banner / GUI dialog), not as truncated JSON.
- **Mode tracking.** `EnterPlanMode` needs no permission and is invisible today. The host
  should watch for `EnterPlanMode`/`ExitPlanMode` `tool_use` events and flip a per-session
  "plan mode" flag (status-bar chip in GUI, prompt prefix in CLI), so the operator knows
  edits are currently blocked. Approving `ExitPlanMode` clears it.
- **Related argv gap.** `ClaudeArgvBuilder` does not emit `--permission-mode`, so the host
  cannot start a session in plan mode (or `acceptEdits`). Low-cost addition if/when a
  "start planning session" affordance is wanted; note `--permission-mode bypassPermissions`
  is already reserved as the documented fallback if `--permission-prompt-tool` disappears
  (D76).

## §3 In-conversation checklist — `TaskCreate`/`TaskGet`/`TaskList`/`TaskUpdate`, `TodoWrite`

Already tracked in `docs/todo.md` §A (issue #0003). Correction to that entry: these tools
are **not** host no-ops — they execute fully inside claude and the agent's checklist state
is real; what's missing is host-side *reflection*. The decision recorded there stands:
either render a live todo strip (the `tool_use` inputs carry the full task payloads, so a
read-only projection needs no extra transport) or explicitly steer the agent to `.task/`
artifacts. Note the version wrinkle: ≥ 2.1.142 the agent uses `TaskCreate`-family by
default and `TodoWrite` is gone, so any rendering work should target the new four tools and
treat `TodoWrite` as legacy.

## §4 Agent-initiated turns — `CronCreate`, `ScheduleWakeup`, `Monitor` interjections

These three make claude produce assistant turns **without a preceding host-sent user
message**: a session-scoped cron fires, a `/loop` wakeup triggers, or a Monitor background
script emits a line the agent reacts to. Harness implications, none verified today:

- **State machine.** `SessionStateMachine` and `MultiSessionHost` assume turns are
  host-initiated. Verify (FakeClaude script) that an unsolicited `assistant`→`result`
  sequence on an idle session doesn't wedge the turn-in-flight tracking, the GUI's
  `IsAgentTurnInFlight` gate, or the queued-message UX.
- **Context pressure.** Unsolicited turns still consume context; `ContextPressureMonitor`
  tallies per-event so it should be fine, but the `ThresholdInjector` nudge currently
  piggybacks on the host send-path — confirm a nudge can still be delivered when the agent
  is self-driving.
- **Visibility.** `CronList` output is the agent's view only; the host could surface
  pending crons per session (status line), but that's cosmetic. Crons restore on
  `--resume`, which the host uses — so a resumed session may start firing scheduled prompts
  the operator forgot about. Worth at least logging.

## §5 Background tasks — `Bash run_in_background`, `Monitor`, `TaskStop`, `TaskOutput`

All execute inside claude; child processes live under the claude process, so
`JobObjectSupervisor` (KILL_ON_JOB_CLOSE) / `PosixSignalSupervisor` already guarantee
cleanup when the host dies — **no supervision gap**. The gap is operator visibility:
claude's TUI has `/tasks`; the host has nothing. Optional: a per-session background-task
list derived from `tool_use`(`run_in_background:true` / `Monitor`) and matching
`TaskStop`/completion events. Cosmetic, Stage 3 material.

## §6 Worktrees — `EnterWorktree` / `ExitWorktree`

No permission prompt, no user interaction — but they change the session's **effective
working directory**, which the host models in three places that all go stale silently:

- the session index (`~/.infinicontext/index.json` records `cwd`),
- the GUI/CLI pane title and any cwd display,
- the tasks subsystem's `home_cwd` validation (`TaskService.PrepareSwitchAsync` rejects
  mismatched cwds; a session that entered a worktree under `.claude/worktrees/` no longer
  matches its spawn cwd).

Needed: watch for these two tool names, update the per-session displayed cwd, and decide
whether task `home_cwd` checks should resolve a worktree path back to the main checkout
(probably yes — same repo). Also note Claude's transcript store path
(`~/.claude/projects/<encoded-cwd>/`) is keyed by the *spawn* cwd, so transcript reading is
unaffected.

## §7 `PushNotification`

Delivery runs through Anthropic-hosted infra from inside claude — works headless, nothing
required. Optional enhancement: the host *also* raising a native OS notification (Windows
toast / macOS notification) when it sees the `tool_use`, since the operator is looking at
InfiniContext, not a terminal claude owns. Low priority.

## §8 Agent teams — `SendMessage`, `TeamCreate`, `TeamDelete`

Experimental, gated behind `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1`, which the host never
sets. If ever enabled: teammates are extra child processes — verify they land inside the
Job Object / process group so host-death cleanup holds. No UI work warranted at this
maturity.

## Explicit non-gaps

- **Approval routing** covers every permission-required tool generically (`Bash`,
  `PowerShell`, `Edit`, `Write`, `NotebookEdit`, `WebFetch`, `WebSearch`, `Skill`,
  `Workflow`, `Monitor`, `ExitPlanMode`, `ShareOnboardingGuide`) — no per-tool approval
  work needed beyond §2's plan rendering.
- **Parser tolerance** already absorbs any new tool or event type as `unknown_event` /
  `Extra` fields with the drift counter as the signal — new tools degrade gracefully
  rather than crash.
- `Glob`/`Grep`/`Read`/`LSP`/`ToolSearch`/MCP-resource tools/`WaitForMcpServers` are pure
  claude-internal, permissionless, and render fine generically.

## Suggested priority

1. **`AskUserQuestion`** (§1) — agent-facing functional break, affects every session that
   hits ambiguity; transport investigation first.
2. **`ExitPlanMode` plan rendering + plan-mode flag** (§2) — plan mode is unusable through
   the host today even though approval technically flows.
3. **Worktree cwd tracking** (§6) — silent state corruption, cheap fix.
4. **Agent-initiated-turn verification** (§4) — test-only until proven broken.
5. **Checklist reflection** (§3), background-task list (§5), notifications (§7) — Stage 3
   UI surface design.
