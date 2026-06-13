# InfiniContext — Open Engineering Items

Snapshot date: **2026-05-20** (post Stage 2B close: macOS CI dispatched and validated; absorbed the former `docs/stage2/todo.md` queue into this single file).

This file is the single consolidated work queue for InfiniContext. Items 1-8 below are the "actionable today" pile with full files-to-touch / acceptance / cost detail (carried over from the manual-test-session task after auto-approval performance and `/task` home_cwd realignment landed). After item 8, sections A through I capture the broader queue absorbed from the former `docs/stage2/todo.md` on 2026-05-20: open bugs and hardening (§A), Stage 2A leftover items (§B), items deferred from Stage 2B (§C), Stage 3 (§D), Stage 4 (§E), light-model research follow-ups (§F), fixture/testing follow-ups (§G), and `/task review` / `/task spawn` follow-ups (§I). Each item is written to be picked up cold — file paths, decisions to consult, acceptance criteria, and cost estimates inline. Items are independent unless noted.

When you complete an item, **delete its section from this file** (the canonical truth lives in code + decision records; this file is a queue, not a log).

---

## 1. `AutoApprovalSink` → `IAsyncDisposable` (close the long-lived approval leak)

**Status:** actionable. Bounded scope.

**Motivation.** `LongLivedCliLightModelService` holds a `claude` child alive across calls. With default settings (`approval.auto.mode = "SingleShot"`) the auto-mode safety reviewer doesn't hit this — it constructs a `SingleShotCliLightModelService` which has no long-lived state. But if a user sets `approval.auto.mode = "LongLived"` (legal, supported, just not the default), every `/approval auto` toggle in the REPL constructs a fresh `LongLivedCliLightModelService` via `ApprovalSinkFactory.BuildInner` → `_lightModelFactory(effort)`. The old `AutoApprovalSink` (and its inner light model) becomes unreferenced; its `claude` child is never killed until host exit (Job Object reaps it on Windows). Cumulative cost: one orphan `claude` process per `/approval` toggle.

Session 5 hit the equivalent issue for `lightModelForTasks` and fixed it via `await using var _ = … as IAsyncDisposable;` in `Program.cs` (commit `c5e6525`). The `light` inside `ApprovalSinkFactory` has the same shape but is harder to fix because (a) `AutoApprovalSink` doesn't currently implement `IAsyncDisposable`, (b) `SwitchableApprovalSink` swaps its inner sink without disposing the old one, and (c) `ApprovalSinkFactory.Build` returns `IApprovalSink` (an interface that doesn't extend `IAsyncDisposable`).

**Files / lines:**
- `src/InfiniContext.Cli/AutoApprovalSink.cs` — make it `IAsyncDisposable`; if `_lightModel is IAsyncDisposable d` then dispose on `DisposeAsync`.
- `src/InfiniContext.Cli/ConsoleEchoingApprovalSink.cs` — wrapper. Forward `DisposeAsync` to the inner sink if it's `IAsyncDisposable`.
- `src/InfiniContext.Core/Mcp/IApprovalSink.cs` (or wherever the interface lives) — leave alone; do **not** make `IApprovalSink` extend `IAsyncDisposable`. Keep disposal opt-in via cast.
- `src/InfiniContext.Cli/SwitchableApprovalSink.cs` — on `Switch(newSink)`, schedule the OLD inner sink for background disposal (fire-and-forget `Task.Run` like D13's pattern, so /approval toggles don't hang while the previous long-lived child winds down).
- `src/InfiniContext.Cli/ApprovalSinkFactory.cs` — no change needed; the wrapping happens at sink-build time.
- `src/InfiniContext.Cli/Program.cs` — `await using` the initial sink and the SwitchableApprovalSink so end-of-program disposes the current chain.

**Decisions to consult:**
- D13 in `.claude/tasks/20260507-manual-test-session/decisions.md` — the background-disposal pattern. Apply it for `/approval` toggles; the user explicitly liked dynamic switching feel.

**Acceptance:**
- Test: switch approval mode auto → ask → auto repeatedly with `approval.auto.mode = "LongLived"`; assert no orphan `claude` processes accumulate (compare `Process.GetProcessesByName("claude")` count before/after, with grace for startup races).
- Test: at host shutdown, AutoApprovalSink.DisposeAsync runs and the long-lived child terminates within 1 s.
- Manual: full suite (`dotnet test InfiniContext.slnx`) green; no regression.

**Cost:** ~30–60 minutes. Pure code; no API calls.

---

## 2. `MAX_OUTPUT_TOKENS` ceiling for thinking-mode (bench)

**Status:** actionable. Wall-clock + ~$0.22 of API tokens.

**Motivation.** `CliLightModelOptions.ThinkingMaxOutputTokens` is currently `1024` — a placeholder per **D9** sub-question in `.claude/tasks/20260507-manual-test-session/decisions.md`. The reasoning was: non-thinking is comfortable at 128 tokens (matches a 4-field JSON verdict), but thinking-on needs room to think before emitting JSON. 1024 was a safe-but-untested upper bound. Goal: find the smallest cap that doesn't truncate verdicts on the standard 10-payload probe set, commit the data, and bump the default if appropriate.

**Files / artifacts:**
- Bench harness: `.claude/tasks/20260507-manual-test-session/artifacts/bench-singleshot-tight.py` — currently configured for nothink+cap. Fork to a thinking-on variant; pass `effort=high` semantically.
- Standard probe set: same 10 payloads as the published research (see `docs/research/claude-cli-light-model.md` §6 / Appendix A).
- Setting to potentially update: `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` — `CliLightModelOptions.ThinkingMaxOutputTokens` default.
- Settings schema: `settings.schema.json` for the matching default + description update.
- Result CSVs: write to `docs/research/claude-cli-light-model/results-thinking-cap-{512,1024,2048}-n10.csv`.

**Procedure:**
1. Fork `bench-singleshot-tight.py` → `bench-singleshot-thinking.py`. Drop `CLAUDE_CODE_DISABLE_THINKING=1`. Parameterize `CLAUDE_CODE_MAX_OUTPUT_TOKENS`.
2. Run N=10 at each of {512, 1024, 2048}. Record wall_ms, api_ms, in_tok, out_tok, total_cost_usd, and **whether the JSON verdict parsed** (the truncation signal — if the model ran out of tokens mid-JSON, parsing fails).
3. If 512 produces ≥1 truncated verdict on the standard set, recommend 1024. If 1024 produces ≥1 truncated, recommend 2048. The smallest cap with zero truncations across the probe set wins.
4. Update `CliLightModelOptions.ThinkingMaxOutputTokens` default to the chosen value.
5. Append a "Thinking-mode cap bench" subsection to `docs/research/claude-cli-light-model.md` and reference the new CSVs.

**Decisions to consult:**
- D8 — recipe for nothink+cap (the baseline this is symmetric to).
- D9 — four-corner matrix; the open sub-question this bench answers.

**Acceptance:**
- Three CSVs committed under `docs/research/claude-cli-light-model/`.
- Recommended cap committed in `InfiniContextSettings.cs` + schema, with a one-line decision update in the manual-test-session `decisions.md` (extend D9 or add D14).
- Research doc updated.

**Cost:** ~30 calls × ~$0.0074/call ≈ **$0.22 in API tokens** (less if a cap truncates and the call costs less). ~10 minutes wall time + write-up.

---

## 3. Two-tier escalation: `effort=low` auto-retry as `effort=high` on `confidence=low`

**Status:** **deferred — design open question.** Do not implement without explicit user buy-in.

**Motivation.** D10 added `approval.auto.effort: "low" | "high"`. Low is fast/cheap (D8 recipe, ~2.3 s/call). High is slower (~9 s/call) but better on ambiguous cases. The open idea: when `effort=low` returns `"confidence": "low"`, the host could automatically re-call with `effort=high` for a smarter verdict. Better UX in the common case (fast); smart when it matters.

**Why this is deferred:** Doubles worst-case latency. Doubles cost on the ambiguous-but-actually-fine path (today: low-confidence allow → ask the human). The current behavior (`onLowConfidence: "Deny"` default; `"FallbackToAsk"` opt-in) is well-defined. Auto-escalation is "smart but slow" instead of "fast but pessimistic." Not obviously the right trade-off without user feedback that the deny/ask fallback is annoying in practice.

**What an agent picking this up would do:**
1. **First, discuss with the user.** Confirm there's enough pain with the current low-confidence handling to justify the latency hit on ambiguous calls. If not, leave deferred.
2. If proceeding: add `approval.auto.escalateOnLowConfidence: bool` (default `false` to preserve current behavior). When true and `effort=low` returns confidence=low, AutoApprovalSink re-invokes the light model with thinking enabled, accepts the high-effort verdict.
3. Wire into `AutoApprovalSink.RequestAsync` after the `review.Confidence == Low` branch.

**Files (when ready):**
- `src/InfiniContext.Cli/AutoApprovalSink.cs`
- `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` (`AutoApprovalOptions`)
- `settings.schema.json`

**Decisions:** D10 — the framing this would extend.

**Cost:** ~1–2 hours code + tests + a few real-world API calls to validate. Don't start without user say-so.

---

## 4. Per-tool effort overrides

**Status:** **deferred — explicitly post-v1.** Do not implement without explicit user buy-in.

**Motivation.** Idea: `approval.auto.effort.bash: "high"`, `approval.auto.effort.read: "low"`, etc. — different tools could carry different latency/cost trade-offs.

**Why deferred:** Probably overkill. The current single dial is simple and the per-tool risk landscape isn't dramatically different — a destructive Bash and a destructive Edit are both destructive; thinking-mode helps both equally.

**What an agent picking this up would do:** Discuss with the user first. If they confirm, design the override schema (`approval.auto.effortOverrides: { bash: "high", … }`?) and wire through `AutoApprovalSink` to per-tool effort lookup before each call.

**Decisions:** D10. This would amend it.

**Cost:** ~2–3 hours including schema + tests.

---

## 5. Long-lived crash-recovery retry policy

**Status:** **open sub-question.** Code today: `LongLivedCliLightModelService.ExecuteAsync` kills the child on any mid-call exception and rethrows; the next call respawns transparently. No automatic mid-call retry.

**Motivation.** A child crash mid-call surfaces as an exception to the caller (auto-mode → fallback to ask; title generation → user sees an error). One transparent respawn-and-retry would smooth over rare crashes at the cost of doubling the worst-case latency for the affected call.

**Open questions for the user before any code:**
1. Has anyone observed a real `claude` child crash mid-call in the wild? If not, this is a theoretical fix; defer until evidence.
2. Should retry apply only to title generation (where a one-off slowdown is fine) or also auto-mode safety review (where a 2× latency hit might be worse than just falling through to ask)?
3. Single retry, or exponential?

**Files (when ready):**
- `src/InfiniContext.Core/LightModel/LongLivedCliLightModelService.cs` — wrap `child.RunTurnAsync` in a retry loop guarded by `RetryOnCrash` setting.
- Settings: a new `CliLightModelOptions.RetryOnCrash: bool` (default `false`)?

**Cost:** ~1 hour code + tests once the policy is decided.

---

## 6. Re-record fixtures vs `claude` 2.1.133 (and bump `TestedAgainstVersion`)

**Status:** actionable but **non-trivial risk** — `tests/fixtures/README.md` and `CLAUDE.md` both flag "do not regenerate casually."

**Motivation.** Committed NDJSON fixtures under `tests/fixtures/` are from `claude` 2.1.112. Current binary on this machine is 2.1.133. The host warns at startup when the live version differs from `versioning.testedAgainstVersion`. Real protocol drift (new event types, schema changes) becomes visible via the parser drift counter, but regression coverage benefits from fixtures matching the version we ship against.

**Procedure:**
1. Read `tests/fixtures/README.md` and `tests/fixtures/NOTES.md` end-to-end first. They contain the "why this fixture exists" for each one.
2. Use `tools/InfiniContext.Recorder/` (already exists) to re-record each scenario. Recorder spawns real `claude` and writes raw NDJSON.
3. **Eyeball-diff** old vs new for each fixture before replacing. Schema drift between 2.1.112 and 2.1.133 is the whole reason this exists; understanding the diffs is the deliverable.
4. Run `dotnet test InfiniContext.slnx` after replacement. Failing tests are real (parser regression) — fix or document.
5. Bump `versioning.testedAgainstVersion` in `settings.json` to `2.1.133`. Update `versioning.minClaudeVersion` only if a real incompatibility is found (otherwise keep at `2.0.0`).

**Decisions to consult:**
- The "Versioning policy" and "Parser tolerance is load-bearing" sections of `CLAUDE.md`. Both apply.

**Risk:** Fixture replacement could mask regressions if done sloppily. The point of the fixtures is to be a **frozen** reference; updating them resets that reference. Worth doing only when (a) we're going to ship soon and want the regression surface tight, or (b) protocol drift has accumulated enough that the existing fixtures don't exercise current-shape events.

**Cost:** ~2–4 hours. Mostly review work, not code.

## 7. Multi-pane non-focused-session auto-rotation (extend task 0005 issue 0013 to background panes)

**Status:** actionable. Bounded scope but spans Core (lock release) + CLI (rotation hook).

**Motivation.** Task 0005 issue 0013 shipped auto-rotation for the **focused** session: when a task-bound focused session exits and a context-pressure nudge fired during its life, `MultiSessionHost.OnStateTransition` builds a `TaskSwitchPlan` and the outer loop in `Program.cs` respawns the session. That covers the single-pane case the operator reported. The multi-pane case is still broken: if a background (non-focused) pane's task-bound session hits hard nudge and exits, the host does nothing — the pane silently dies, the task row stays `InProgress`, and the operator finds out only when they `/focus` to it. This makes long-running multi-pane workflows lose work the same way the single-pane case used to.

Two things have to happen for this to work:
1. **Auto-release the task lock on session exit** (latent bug, separately tracked at the §I entry "Wire `TaskActivityCoordinator.LeaveAsync` into `SessionManager.CloseAsync`"). Without this, a replacement session can't claim the same task — the dead pane's session id still holds it. The §I entry already names the fix shape (`SessionManager.CloseAsync` hook + `SessionContext.OnDispose`); 0013's focused-pane path side-stepped the issue because `Program.cs`'s outer loop calls `LeaveAsync` after every iteration. Background panes have no such per-iteration cleanup.
2. **Subscribe to every session's state transitions in `MultiSessionHost`** (not just the focused one), and on `Exited` for a task-bound pane with `RotationReason != None`, in-host create a replacement: `SessionManager.CreateAsync` with the same `FirstUserMessage`/`ActiveTaskId`, `TaskActivityCoordinator.EnterAsync` to claim the lock, then `SessionManager.CloseAsync` on the old context. Focus stays where the operator put it; the rotation is invisible if they weren't watching that pane.

The existing `AutoRotationGuard` already keys by task id, so the cap from 0013 applies to background rotations without change.

**Files / lines:**
- **(1) lock release** — `src/InfiniContext.Core/Sessions/SessionManager.cs::CloseAsync`: take an optional `TaskActivityCoordinator` (or a leave delegate) and call `LeaveAsync(ctx.Id, ctx.ActiveTaskId.Value)` before `ctx.DisposeAsync` if `ActiveTaskId` is set. Plumb the coordinator through `SessionManager.OpenAsync` so `Program.cs` constructs it once and passes it in. Mirror the change in `DisposeAsync`'s per-session loop. Delete the per-iteration `LeaveAsync` in `Program.cs` once auto-release covers it.
- **(2) per-session lifecycle subscription** — `src/InfiniContext.Cli/Ui/MultiSessionHost.cs`: expose a `SessionManager.SessionCreated` event (new) and subscribe in the host's constructor; for each new context, hook `ctx.Session.State.Transitioned` and detect Exited+nudge+task-binding the same way the focused path already does. Refactor the focused-only `TryStartAutoRotation` into a per-context method that takes a `SessionContext` parameter; the focused-Exited branch and the new background-Exited branch both call it. Background rotation builds a new pane via `SessionManager.CreateAsync` + coordinator `EnterAsync` (the `/task spawn` flow in `TaskSlashCommand.TrySpawnTaskSessionAsync` is the existing template) instead of going through `RequestSwitch`; old pane is closed via `SessionManager.CloseAsync`.

**Decisions to consult:**
- Task 0005 issue 0013 resolution in `.task/0005-manual-testing-review-notes/issues.md` (the focused-pane design that this extends).
- `docs/stage2/decisions.md` D9 (one-session-per-task). Background rotation must respect it; the auto-release fix from §I makes this clean.
- The existing `TaskSlashCommand.TrySpawnTaskSessionAsync` (`src/InfiniContext.Cli/SlashCommands/TaskSlashCommand.cs`) — the canonical "create new pane bound to existing task" path; background rotation can lift its construction shape verbatim.

**Acceptance:**
- New unit test in `tests/InfiniContext.Tests/Cli/Ui/MultiSessionHostTests.cs`: two task-bound panes, the **non-focused** one hits hard nudge and exits; assert the focused pane is unchanged AND a fresh pane bound to the same task id appears in `SessionManager.Active`.
- New unit test in `tests/InfiniContext.Tests/Sessions/SessionManagerTests.cs`: `CloseAsync` on a task-bound context calls the supplied leave delegate before disposal; `CloseAsync` on a non-task-bound context does not.
- Regression: every existing 0013 test still passes (focused-pane rotation must keep working).
- Manual: spawn two task-bound sessions via `/task spawn`, drive one of them to hard nudge in the background (long prompt or thresholds dialed down), confirm a replacement pane appears without operator action and the original `InProgress` task row never flickers to `Idle` between the two sessions.

**Cost:** ~1-1.5 hours code + tests. Roughly half is the `SessionManager.CloseAsync` plumbing and the matching test, half is the in-host rotation hook and its test. The hardest part is making the new `SessionCreated` event observable from tests without leaking SessionManager internals — likely just a plain `event Action<SessionContext>` on the manager, subscribed by `MultiSessionHost` once at construction.

---

## 8. Rip out the `tasks/lock` MCP channel; replace with a process-local write lock

**Status:** actionable. Direction confirmed with the operator (manual-testing-review issue #0011) on 2026-05-21: D9's `TaskActivityCoordinator` already enforces intra-host one-session-per-task, which makes the cross-process MCP lock channel mostly dead weight.

**Motivation.** Today the `.task/` write serialization story has three layers; only two earn their keep:
- (intra-host) `TaskActivityCoordinator` blocks two panes from running on the same task. D9. Load-bearing.
- (cross-host) `LockRegistry` + the `tasks/lock` channel served via the approval sidecar would serialize writes between two `infinicontext.exe` processes on the same machine. Rare; not a goal of the design.
- (agent-vs-host inside one process) The same lock channel serializes the agent's writes to `progress.md` / `index.md` / `issues.md` against host-side writes from `IssueService` (`/issue add`) and `TaskCommandHandler` (`/task review`). This is the only thing that fires routinely.

The whole "MCP channel over named pipe with skill-side acquire/release" machinery exists to handle the second and third items. The second is rare; the third can be solved with a process-local `SemaphoreSlim` keyed by absolute file path. Killing the lock channel removes a meaningful pile of moving parts: pipe wire contract, MCP tool definitions, `LockRegistry` storage, skill-side acquire/release steps, lock-acquire/release tests.

**Constraint to document:** "one `infinicontext.exe` per home_cwd at a time." Two host processes touching the same `.task/` folder is unsupported and may corrupt artifacts. Add a one-line note to `docs/quickstart.md` and to the `CLAUDE.md` "Persistence" section.

**Files to delete (or strip down):**
- `src/InfiniContext.Core/Mcp/Tasks/LockRegistry.cs` and any sibling lock-message types (`LockAcquireRequest` / `Response`, `LockReleaseRequest` / `Response`) under `src/InfiniContext.Core/Mcp/Tasks/`.
- `src/InfiniContext.Core/Mcp/ApprovalChannel.cs`: strip the `tasks/lock` JSON-RPC dispatch in `RunServerAsync`, plus the `_lockRegistry.ReleaseAllOwnedBy(_pipeName)` call in `DisposeAsync`.
- `src/InfiniContext.Core/Mcp/ApprovalSidecarHost.cs`: strip the `mcp__infinicontext__task_acquire_lock` / `task_release_lock` tool registrations and forwarders.
- `src/InfiniContext.Core/Tasks/Skill/InfiniContextTaskSkill.cs`: strip the "call `mcp__infinicontext__task_acquire_lock` ... then call `mcp__infinicontext__task_release_lock`" steps from the skill body. Bump the skill `Version` constant so `SkillInstaller`'s version-check auto-updates installed copies on next host start.
- `src/InfiniContext.Cli/Program.cs`: delete the `LockRegistry` construction (currently around line 138) and the parameter passing to `SessionManager.OpenAsync` and `IssueService`.
- Tests: any `LockRegistry*Tests.cs`, lock-specific cases in `ApprovalChannel*Tests.cs` and `ApprovalSidecar*Tests.cs`, and the `mcp__infinicontext__task_*_lock` MCP tool tests if they exist. Grep `LockRegistry|task_acquire_lock|task_release_lock` to find them.

**Files to add / change for the replacement:**
- New `src/InfiniContext.Core/Tasks/TaskArtifactWriteLock.cs`: a small process-local lock keyed by absolute file path. `Task<IAsyncDisposable> AcquireAsync(string absolutePath, CancellationToken ct)` returns a disposable; the lock is a `SemaphoreSlim(1, 1)` per path, held in a `ConcurrentDictionary<string, SemaphoreSlim>`. Idle semaphores can be released opportunistically. Pure in-memory, host-scoped.
- `src/InfiniContext.Core/Tasks/IssueService.cs` and `src/InfiniContext.Core/Tasks/TaskCommandHandler.cs` (and `TaskService` if it writes artifacts directly): wrap their `index.md` / `progress.md` / `issues.md` writes with `await using var _ = await writeLock.AcquireAsync(fullPath, ct);`. Drop the `LockRegistry` dependency. Confirm `TaskFolder.WriteArtifactAsync` is the single write funnel; if so, do the wrapping inside that method so callers don't have to think about it.
- The skill body: agent stops calling lock tools. Just `Read` then `Edit` / `Write`. Inside one host the host's write lock serializes against the agent's writes (the agent writes via the same `TaskFolder.WriteArtifactAsync` funnel through MCP); if there are two hosts, the documented constraint applies.

**Decisions to consult:**
- D9 (`docs/stage2/decisions.md`): the underlying "one session per task" rule that makes intra-host concurrency manageable in the first place. Add a closing note in `decisions.md` that the lock channel was removed and why.
- The lock-design notes in `.claude/tasks/20260507-tasks-and-sessions-management/decisions.md`: the original "why MCP lock channel" rationale. Drop a back-reference there too so future archaeologists find the rip-out.

**Acceptance:**
- Full test suite green after deletion + replacement (981 today; expect a small reduction once lock-specific tests are dropped).
- New test: two host-side concurrent `IssueService.AddAsync` calls on the same `issues.md` are serialized by the new write lock (no interleaving, no corrupted markdown).
- `docs/quickstart.md` carries the "one `infinicontext.exe` per home_cwd at a time" note.
- `CLAUDE.md` "Persistence" section carries the same note.
- Manual: run a `/task review` while a session is mid-edit on the same task's `progress.md`; confirm no corruption with the new write lock.
- The `infinicontext-task` skill on a fresh user install reflects the new (lock-free) body after `SkillInstaller` runs.

**Cost:** ~2-3 hours. Roughly one-third deletion, one-third the `TaskArtifactWriteLock` replacement, one-third skill bump + docs + tests. Bigger than item 7 because it touches the wire contract and the skill body, but bounded; every load-bearing piece is named above.

**Source:** `.task/0005-manual-testing-review-notes/issues.md` #0011 + operator confirmation 2026-05-21.

---

## 9. [SHIPPED 2026-05-25] GUI operator message-queue indicator + glue-send semantics (task 0012 finding #7)

**Status:** actionable. Design landed during task 0012 (2026-05-24) but ZERO code on disk — the implementing session was aborted under hard context pressure while approval prompts were stalling. The investigation cost is sunk; pick up cold from the design below.

**Motivation.** Today every `MainWindowViewModel.SendToSessionAsync` call writes immediately to claude's stdin via `ClaudeSession.SendUserMessageAsync` (which flushes under `_writeLock`). Claude buffers them FIFO and processes each as a separate turn. Two operator-visible problems:
1. **No queue indicator beyond the "Agent busy" banner.** Operators report sending the same prompt twice because the second one looks like nothing happened. The visual cue (a `IsQueued=true` turn card) is per-card and easy to miss in a long transcript.
2. **Multiple typed prompts produce multiple claude turns.** Operator wants them glued into one combined turn so claude sees the operator's full intent in one shot (and the agent doesn't half-execute the first message before reading the second).

The "are queued messages actually flushed on Idle?" sub-question (operator's #7.2) was investigated and confirmed: yes, current writes hit claude immediately. The operator's perception ("not sent until any new user message is sent") is wrong under the current code path. So #7.2 is NOT a fix to existing code; auto-flush-on-Idle is a new property of the glue feature in #7.3.

**Design — two parallel queueing mechanisms in `SessionRowViewModel`:**

1. **Legacy FIFO (`_queuedTurns`)** — kept as-is. Used ONLY by `BeginUserTurn` (the seed-pointer path at `MainWindowViewModel.SpawnSessionForTaskAsync:464` and the existing test matrix). Do NOT retire this — task 0011 RC2/RC3 mandated the FIFO contract and the tests assert it.

2. **Glue buffer (NEW)** — used by operator-driven sends. New fields immediately after `_queuedTurns = new();` in `SessionRowViewModel.cs`:

```csharp
private readonly List<string> _pendingPrompts = new();
private TurnNodeViewModel? _pendingBundleTurn;
private const string GlueSeparator = "\n\n---\n\n";
[ObservableProperty] private int _queuedPromptCount;
public bool HasQueuedPrompts => QueuedPromptCount > 0;
```

NEW public method `SessionRowViewModel.SendOrQueueAsync(string text, CancellationToken ct = default)`:
- If `!IsAgentTurnInFlight`: call `BeginUserTurn(text)`, `Context.Transcript.Append("\n> {text}\n")`, `await Context.Session.SendUserMessageAsync(text, ct)`. Return.
- Else (glue path), dispatcher-marshalled:
  - `_pendingPrompts.Add(text)`.
  - If `_pendingBundleTurn is null`: create `new TurnNodeViewModel(text) { IsQueued = true }`, add to `Turns`, store in `_pendingBundleTurn`.
  - Else: `_pendingBundleTurn.UserPrompt = string.Join(GlueSeparator, _pendingPrompts);`
  - `QueuedPromptCount = _pendingPrompts.Count;`
  - `Context.Transcript.Append("\n> {text}  [queued: glued with N other(s) on next Idle]\n")`.
  - Do NOT call `SendUserMessageAsync` — that's what makes this glue.

Flush hook in `CloseInFlightTurnWith` (around line 494, after the existing `_queuedTurns.TryDequeue` block):
- If `_pendingBundleTurn is not null && _inFlightTurn is null` (no FIFO-queued turn took the slot):
  - `combined = string.Join(GlueSeparator, _pendingPrompts);`
  - Clear `_pendingPrompts`; bundle's `IsQueued = false`; `_inFlightTurn = bundle`; null the bundle field; `QueuedPromptCount = 0`.
  - Fire-and-forget the send: `_ = Task.Run(() => Context.Session.SendUserMessageAsync(combined));` (Task.Run avoids deadlock — we are on the UI thread from the dispatched OnEvent).

Process-exit flush in `OnStateTransition`'s `t.To == SessionState.Exited` branch (around line 280-301, after the existing `_queuedTurns` flush):
- If `_pendingBundleTurn is not null`: clear `IsQueued` + `IsInProgress`, add a `TurnContentItemViewModel(TurnContentKind.System, "(not sent - session exited with N prompt(s) buffered)", joined)` child, clear `_pendingPrompts`, null the bundle field, `QueuedPromptCount = 0`.

**Files / lines to touch:**
- `src/InfiniContext.Gui/ViewModels/SessionRowViewModel.cs`: add the 5 new fields, the new `SendOrQueueAsync` method, the flush hook in `CloseInFlightTurnWith`, the Exited-branch bundle flush.
- `src/InfiniContext.Gui/ViewModels/MainWindowViewModel.cs`: `SendToSessionAsync` (line 508) becomes a thin wrapper that calls `target.SendOrQueueAsync(text)` and sets `StatusText` to either "Sent to {ShortId}." or "Queued to {ShortId} (N pending, will flush on next Idle).". `SendCannedAsync` (line 497) switches to `SendOrQueueAsync`. Move the `Transcript.Append` INTO `SendOrQueueAsync` (the row VM now decides queued-vs-sent).
- `src/InfiniContext.Gui/Views/MainWindow.axaml`: row template (TreeDataTemplate at line 130 for `vm:SessionRowViewModel`) gains a badge between StatusGlyph and ShortId:
  ```xml
  <Border IsVisible="{Binding HasQueuedPrompts}" Background="#FF8800" CornerRadius="6" Padding="4,0" Margin="0,0,4,0">
    <TextBlock Text="{Binding QueuedPromptCount, StringFormat='+{0}'}" Foreground="White" FontSize="10" FontWeight="Bold" />
  </Border>
  ```
  Status-strip `IsAgentBusy` border (line 65-68): when the focused session has `HasQueuedPrompts`, change the text from "Agent busy - next message will queue" to "Agent busy - N message(s) queued, will glue on Idle". Easiest path: add a derived `MainWindowViewModel.QueuedHint` string and bind to it; or pull `FocusedSession.QueuedPromptCount` into the text via a Run/StringFormat.

**Test fixture note:** `tests/InfiniContext.Tests/Gui/FakeClaudeSession.cs:35` already records into `SentMessages` — no new fake plumbing needed. Use `SessionContextFactory` + `RaiseEvent` exactly like the existing `SessionRowViewModelTests` to drive `ResultEvent` etc.

**Tests to add in `tests/InfiniContext.Tests/Gui/SessionRowViewModelTests.cs`:**
1. `SendOrQueue_when_idle_sends_immediately_and_creates_inflight_turn` — `SentMessages.Count == 1`, no bundle.
2. `SendOrQueue_while_inflight_buffers_in_bundle_and_does_not_send` — first send creates in-flight turn; second call (during in-flight) appends to bundle; `SentMessages.Count` still 1; `QueuedPromptCount == 1`; bundle card `IsQueued == true`.
3. `SendOrQueue_multiple_buffered_prompts_join_with_glue_separator` — three queued prompts; bundle's `UserPrompt == "a\n\n---\n\nb\n\n---\n\nc"`.
4. `Result_event_flushes_bundle_as_one_combined_send` — drive in-flight + queue two prompts + ResultEvent; assert the 2nd `SentMessages` entry equals the joined string, bundle becomes in-flight, `QueuedPromptCount == 0`.
5. `Process_exit_flushes_bundle_with_not_sent_marker` — queue 2 prompts then `HandleProcessExit(-1)`; bundle card has the "not sent" system child, no extra `SentMessages` write, `QueuedPromptCount == 0`.
6. `SendOrQueue_property_change_fires_for_QueuedPromptCount` — subscribe to `PropertyChanged`, send-while-inflight, assert `QueuedPromptCount` raised.

Existing tests that DO NOT change (the `BeginUserTurn` FIFO contract is preserved):
- `Operator_send_during_inflight_turn_is_queued`
- `Result_event_promotes_next_queued_turn_and_clears_isqueued`
- `Assistant_event_during_queued_state_attaches_to_inflight_not_queued`
- `Process_exit_flushes_queue_and_marks_each_not_sent`

**Acceptance:**
- All existing `SessionRowViewModelTests` still pass (FIFO contract preserved).
- 6 new tests pass.
- Manual: in the GUI, spawn a session, send a prompt, then while busy send two more — see ONE queued badge with `+2`, ONE single new turn card showing the joined messages, and ONE combined claude turn after the in-flight result lands.
- Process-kill while bundle is non-empty: bundle card shows "(not sent...)" instead of vanishing.
- `dotnet test` green; `dotnet format --verify-no-changes` clean.
- Update findings.md and progress.md for task 0012 marking finding #7 SHIPPED.

**Cost:** ~2-3 hours. Most of the design risk is already burned; the remaining work is mechanical edits + the 6 new tests.

**Source:** task 0012 findings.md #7.

---

## 10. Agent surface for inspecting + changing host/harness settings

**Status:** large, design-first. Operator flagged on 2026-05-24 as "needs its own ticket" (task 0012 findings.md #13). Scope creep risk is real; resist shipping a thin slice without a security/audit story.

**Motivation.** Today the agent has zero visibility into and zero control over the host's settings surface: approval mode, model selection, light-model effort, soft/hard context thresholds, etc. Several real workflows want this:
- An agent in an extended autonomous loop knows it would benefit from flipping to `auto` approval to avoid stalling, but has no way to ask for or perform the switch.
- An agent about to start a high-cost operation knows it should drop to a cheaper model first; same gap.
- A task-bound agent hitting context pressure could ask the host to raise the soft threshold for this one task; same gap.

In every case, today the operator has to notice and toggle manually. Fine for an attended workflow; kills autonomous use.

**Design boundaries to settle first (NOT pre-decided -- this is the design work):**

1. **Read vs write.** Read-only ("what's my current approval mode?") is cheap and safe; write ("switch me to auto") is where the security boundary matters. Likely outcome: read always available; write gated.

2. **What's settable, what's not.**
   - Probably-OK to expose: approval mode (subject to gating), session model override for the next turn (subject to budget), per-session soft/hard thresholds (subject to bounds), prompt-text for the next turn.
   - Probably-NOT exposed (host-process scope, not session scope): `versioning.MinClaudeVersion`, `loggingMaxFieldBytes`, anything that affects the sidecar's wire contract, anything that could mask security signals (e.g. disabling the auto-mode safety reviewer).
   - Probably-gated on operator policy: model selection if a budget exists.

3. **The gate.** Three plausible models:
   - **(a)** Every agent-initiated settings change goes through the same `auto`-mode safety reviewer that gates tool calls (light-model classifies as `allow|deny|ask`). Cheap; reuses infra; loses nuance.
   - **(b)** A separate `settings-change` approval channel surfaced as a banner in the focused-pane UI -- operator can approve a one-off change or whitelist it for the session.
   - **(c)** A per-key allowlist in `settings.json` ("agent may change these keys without approval"). Simplest; least flexible; works for the autonomous-loop case if operator pre-approves "agent may flip approval mode to auto."
   - Likely outcome: hybrid. Read freely; some keys allowlisted; others go through (a) or (b).

4. **Surfacing.** Operator-visible audit trail is non-negotiable: every agent-initiated settings read or write logs at INFO with the agent's session id, what changed, what gated it. A small "agent settings activity" pane in the focused-pane status strip would let the operator see "agent just read your approval mode" / "agent switched to auto (you allowlisted this)" without scrubbing logs.

5. **Wire.** Most natural surface: extend the existing MCP sidecar with `mcp__infinicontext__settings_get` / `settings_set` tools. They'd live next to the lock channel (which item #8 above proposes ripping out -- coordinate; if #8 lands first, these tools just live in the sidecar JSON-RPC alongside the approval tools). Per-tool schema lists settable keys + types; out-of-range writes return structured errors.

**Files / lines (preliminary, firm up during design):**
- `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` + `…Loader.cs`: per-key metadata flagging "agent-readable" / "agent-writable" / "requires-approval" / "session-scope vs host-scope".
- `src/InfiniContext.Core/Mcp/ApprovalSidecarHost.cs` + `ApprovalChannel.cs`: register the new tools, dispatch via the same JSON-RPC server.
- `src/InfiniContext.Core/Sessions/SessionManager.cs` + `SessionContext.cs`: a settings-mutation surface that respects session scope (writes affect only this session unless flagged host-scope and approved).
- `src/InfiniContext.Cli/Program.cs`: register the new MCP tools and wire the approval gate.
- Stage 3 GUI: focused-pane "agent settings activity" indicator (Avalonia).
- `settings.schema.json`: matching metadata for the agent-facing surface.

**Acceptance (when this becomes actionable):**
- `mcp__infinicontext__settings_get` returns the current approval mode and context thresholds from inside an agent prompt.
- `mcp__infinicontext__settings_set` switches approval mode to `auto`, gated by the chosen approval flow; succeeds silently when allowlisted and produces an audit log entry.
- Out-of-range writes return structured errors; agent can recover.
- Operator UI shows the activity in real time (CLI status line + GUI status strip).
- Full test suite green; new tests cover allowlist enforcement, scope (session vs host), and the audit-trail emission.

**Cost:** large. Budget ~1-2 days for the design and another 1-2 days for implementation. Could be split into "read-only surface first, write later" if a quick win is wanted.

**Source:** task 0012 findings.md #13, operator flag 2026-05-24.

**Source:** `.task/0012-gui-development-phase-3-design-documentation/findings.md` #7. Design recorded during the 2026-05-24 session that hit the AFK-ask-mode wall — see `feedback_edit_timeout_means_ask_mode_afk.md` in memory for the diagnostic pattern that surfaced after four consecutive Edit timeouts.

---

## Cross-cutting context

- **Memory files** at `~/.claude/projects/C--src-infinicontext-poc/memory/`:
  - `feedback_avoid_kill_process_tree_windows.md` — relevant for any new `Process.Kill` call.
  - `feedback_cli_flags_before_freeform.md` — relevant for any new slash command with a long free-form text arg.
- **Decision records** at `.claude/tasks/20260507-manual-test-session/decisions.md` — D1–D13 cover the light-model architecture. New decisions should append.
- **Research doc** at `docs/research/claude-cli-light-model.md` — canonical, externally-publishable. Any new bench data goes into the sister `docs/research/claude-cli-light-model/` folder.
- **Build/test commands** are in `CLAUDE.md` at the repo root.

---

# Broader queue (absorbed from former `docs/stage2/todo.md`, 2026-05-20)

The items below were the long tail of InfiniContext deferred work. They lived in `docs/stage2/todo.md` while Stage 2B was in flight; once Stage 2B closed (2026-05-20, after `ci-macos.yml` ran green on `macos-latest`) the queue was folded into this file so there is one place to look. Format follows the prior file: brief item summaries with file pointers and source references; full design context lives in the linked decisions / progress documents.

When an item is picked up, **remove its bullet from this file** (or promote it into the numbered items 1-6 pile above first if a real "actionable today" workup is needed, then remove from there when shipped).

## A. Bugs and hardening (open queue, not yet in the numbered list above)

- **C-6 - `.cmd` shim spawn under `UseShellExecute=false`.** Documented-fragile across .NET runtime versions; path with `%` in `Path.GetTempPath()` could be misquoted. Not observed. Files: `ClaudeArgvBuilder.ResolveExecutable`. Source: `20260506-review-stage1-poc`.
- **C-4 / C-5 - narrow races.** Process-handle race in `Attach` failure path; `mcp-config` temp-file leak window. Safe-by-construction with `await using` but not zero-risk. Source: `20260506-review-stage1-poc`.
- **`SessionStateMachine` adversarial test gaps.** `JobObjectSupervisor` double-Dispose; `Attach` of already-exited process; `McpServer` `tools/call` missing `arguments`; notification-style `tools/call` (no `id`). Source: `20260506-review-stage1-poc/context.md`.
- **Flakiness watch.** `Script_slow_stream_remains_in_streaming_through_inter_event_gap` (200 ms sleep / 150 ms assert); `SidecarSmokeTests.Sidecar_handshake_then_tools_list...` (5 s read timeout); `Repl_renders_*` (20 s `WaitForExit`). Source: `20260506-review-stage1-poc/context.md`.
- **Automated Ctrl+C smoke test never landed** (deferred from Stage 1G to 1H, still open). Cross-platform Ctrl+C synthesis is fiddly. Source: `20260506-poc/decisions.md`.
- **`BelowMinimum` does not yet block.** `ClaudeVersionPolicy` logs error but allows spawn; should eventually hard-fail. Source: `20260506-poc/decisions.md`.
- **`/task relocate <id> <new-home>`** - move the folder (if present) and update the DB row to fix stale `home_cwd` entries. The 2026-05-09 manual-testing follow-ups for "validate home_cwd at switch time" and "reject build-output `--home` paths" already landed in `TaskService.PrepareSwitchAsync` and `TaskService.CreateAsync` respectively. Source: `.claude/tasks/20260509-manual-testing-session/research.md`.
- **Smoke tests create real SQLite at `~/.infinicontext/infinicontext.db`.** Easy follow-up: set `INFINICONTEXT__TASKS__DBPATH` to a temp path in the smoke driver. Source: `20260507-tasks-and-sessions-management/progress.md` Phase 5 known caveat.
- **Update `ApprovalSinkFactory.cs:73` warning text** - be specific about OAuth-vs-API-key gap and link `console.anthropic.com`. *May already be done in Session 4; verify.* Source: `20260507-manual-test-session/findings.md`.
- **Out-of-process "kill host -> child cleanup" integration test.** Spawn `InfiniContext.Cli` as a child, `/n` one or two sessions, kill the host, scan for orphan PIDs. Deferred per D37 (per `docs/stage2/decisions.md`): the existing `JobObjectSupervisorTests` (Windows, same kernel path as host crash) and `PosixSignalSupervisorTests` (POSIX SIGTERM path) cover the kill primitives; the integration gap is the disposal-cascade wiring, which manager-level disposal tests cover indirectly. Files: new `tests/InfiniContext.Tests/Cli/KillHostCleanupTests.cs` (~100 LOC). Source: `20260516-phase-8`.
- **`AskUserQuestion` tool surfaces as empty success.** When the agent calls Claude's `AskUserQuestion` tool, the host returns `[tool-result: ok]` with no payload — the agent never sees the user's selection and has to fall back to a free-text confirm-with-recommended-option. Two-part fix: (1) host-side picker UI (Spectre `SelectionPrompt` / `MultiSelectionPrompt` in CLI; native dropdown in Stage 3 GUI) wired to whatever transport the tool uses; (2) until that lands, return a structured error so the agent recognises the tool as unsupported. Source: `.task/0005-manual-testing-review-notes/issues.md` #0001.
- **`TaskCreate` / `TaskUpdate` / `TaskList` (Claude's in-conversation todo tools) currently no-op.** The agent calls these expecting Claude-side todo tracking; the host returns `[tool-result: ok]` but does nothing visible. The agent's todo state stays in its own context with no host-side reflection. Decision: implement a host-side todo tracker exposed via the MCP sidecar (so users see a "live todo strip" in the UI), OR explicitly reject the tools and steer the agent toward `.task/` artifacts. Choice depends on whether per-session in-conversation todos are valuable alongside the structured task artifacts; revisit alongside Stage 3 GUI surface design. Source: `.task/0005-manual-testing-review-notes/issues.md` #0003.
- **Broaden the default auto-approve allowlist.** Operator reports that the auto-mode safety reviewer denies routine read-only tool calls more often than it should. Action: scrape the most recent host logs for top auto-deny tool names + commands, propose a specific expanded set of defaults for `settings.json::approval.auto.allowedTools`, and ship the wider default. No code change beyond settings + a paragraph in `docs/quickstart.md`. Source: `.task/0005-manual-testing-review-notes/issues.md` #0005.
- **Safety-reviewer prompt tune so routine `git push` isn't auto-denied.** Today the auto-mode reviewer denies `git push origin main` as "destructive, wide blast radius" — technically true for force-push, but most `git push` is non-destructive and the operator hits this every working day. Iterate the reviewer prompt template (in `src/InfiniContext.Core/LightModel/` — the safety-review prompt builder) to distinguish "push to a non-protected branch" from "force-push / history rewrite / branch delete." Validate with a small probe set before rolling. Source: `.task/0005-manual-testing-review-notes/issues.md` #0007.
- **Built-in tool harness gaps beyond AskUserQuestion/TaskCreate (audited 2026-06-11).** Full audit in `docs/tool-harness-support.md`. New items it surfaces: (1) `ExitPlanMode` plan markdown is mangled by the 200-char approval/transcript truncation — needs per-tool rendering in the approval surface, plus a per-session plan-mode flag driven by `EnterPlanMode`/`ExitPlanMode` tool_use events; (2) `EnterWorktree`/`ExitWorktree` silently stale the host's cwd model (session index, pane title, `TaskService` `home_cwd` checks); (3) agent-initiated turns (session crons, `/loop` wakeups, `Monitor` interjections) are unverified against the turn-in-flight state machine and `ThresholdInjector` send-path — needs a FakeClaude script test. Source: `docs/tool-harness-support.md`.
- **Amend `docs/design-gui-testability.md` section 12** to reflect the operator-pushback path was taken. The doc still recommends a result-based readiness gate; the shipped GUI uses an init-based gate plus a queue indicator ("Agent busy - next message will queue"). Rewrite section 12 to describe the chosen contract, why result-based was rejected (30s-2min pointer turns made the Send button feel broken), and what the new `IsAgentTurnInFlight` / queue-prefix UX guarantees. Files: `docs/design-gui-testability.md`. Source: `.task/0008-session-warm-up-investigation-and-fix/progress.md` final entry 2026-05-21.
## B. Stage 2A leftover items (tasks and sessions)

- **T3 - outer-loop refactor into `HostLoopState` / `TaskActivityCoordinator`.** Extract state machine from `Program.cs` outer loop so status flips, `pendingFirstUserMessage` clearing, and skill-install-warning path are unit-testable against synthetic `ReplResult` values. Source: `20260507-discuss-and-apply/progress.md`.
- **A4 (full) - `Program.cs:169, 234` status-flip writes** still use `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` directly. Natural injection point is T3's extracted `HostLoopState`. Source: same.
- **A6 - `TaskService.MergeAsync` should go through `TaskFolder` ops** (`WriteArtifactAsync` / `ReadArtifactAsync` / `MergeArtifactsAsync`) instead of raw `File.Read/WriteAllTextAsync`. Files: `TaskService.cs:139-165`. Defer until light/full-model merge tiers ship. Source: same.
- **C6 - `parent_task_id` index.** Add `CREATE INDEX idx_tasks_parent ON tasks(parent_task_id) WHERE parent_task_id IS NOT NULL;` as a **new `Migration_002_*`** (never edit `Migration_001`). Defer until lineage queries land. Source: same.
- **T5 - `SkillInstaller` startup-integration smoke test.** Pre-create v1 `SKILL.md` in temp `HOME`, launch `infinicontext run`, assert file is now v2. Review's own confidence Low. Source: same.
- **A8(b) - `protocol_version` field on every lock/approval message** (default 1). Buys nothing today; revisit when older host meets newer sidecar. Source: same.
- **C2(b) - non-blocking dispatch in `ApprovalChannel.RunServerAsync`** so long approvals don't serialize lock releases. C2(a)'s cross-rule validator closes the foot-gun; defer the deeper refactor. Source: same.
- **Cherry-pick test gaps.** `LockAcquireResponse.FromJson` round-trip on failure shape; `LockRegistry` same-owner re-acquire semantics; `TaskFolder.DetectDriftAsync` on malformed `index.md`; `TaskStore.AllocateIdAsync` concurrent calls. Source: same.
- **Light-model and full-model merge tiers** - currently throw `NotImplementedException`; only `DeterministicConcat` ships. Source: `20260507-tasks-and-sessions-management/design.md` section 2.4, `plan.md`.
- **`InSync` / `Stale` status states.** MVP ships only `InProgress` and `Idle`. Source: `20260507-tasks-and-sessions-management/plan.md`.
- **`/task switch` while another session is running on the same task** should error cleanly. Multi-session orchestration UX is post-MVP. Source: same.
- **Task rename / archival UX.** Post-MVP. (`/task delete` shipped in 2A.) Source: `20260507-tasks-and-sessions-management/decisions.md`, `docs/cli/howto/task-commands.md`. **2026-05-24 operator concrete design (task 0012 findings #8):** GUI tree gets two root nodes - "Active" (uncollapsed by default) and "Archived" (collapsed by default); operator action to archive a task triggers a completion ceremony that scans for postponed sub-tasks / open `issues.md` entries and proposes follow-up actions (file into todo / issue tracker / parent task). Implementation touches `TaskRecord` (status enum gains `Archived`), `TaskStore` migration (new column), `TaskService.ArchiveAsync` + ceremony hook, and the GUI tree rebuild in `MainWindowViewModel.LoadTasksAsync` to partition by status. Needs a design pass before code.
- **Hard-threshold force-flush guardrail.** Design sketched in `design.md` section 8; MVP omits. If telemetry shows non-compliance, add an `InterruptAsync`-backed cancel path. Source: same (Phase 8).
- **Automatic successor-session spawn after flush** - host fires the next session without user "go". Part of the "long-running uninterruptible sessions" workstream. Source: `20260507-tasks-and-sessions-management/design.md` section 8.3.
- **Host-controlled flush on errors/cancels** - guarantee artifacts consistent even on crash paths. Future session-reliability task. Source: same.
- **Cross-machine ID coordination.** Host-local SQLite means IDs aren't globally unique across machines. Source: same.
- **Light-model bulk-regeneration of `index.md` as explicit user command.** Possible later as user-invoked feature; explicitly NOT automatic. Source: `20260507-tasks-and-sessions-management/design.md` section 3.4.
- **Slug recovery via `Slugify.For(title)`.** `TaskRecord` doesn't store the slug; recomputed from stored title. Works until rename lands; when it does, swap to folder-discovery scan over `<id:D4>-*`. Source: Phase 3 progress.
- **Multi-parent lineage.** Schema records only one `parent_task_id`; multi-parent is post-MVP. Captured as class-level note on `MergeAsync`. Source: Phase 3 progress.
- **Cross-home `/task merge`.** Guard: "All merge sources must share the same home_cwd. Cross-home merge is post-MVP." Source: `TaskService.cs:117-119`, `20260509-manual-testing-session/research.md`.
- **Author `docs/cli/howto/commands.md`** - long-form REPL-only reference; link out to `docs/cli/howto/task-commands.md` rather than duplicate. Explicitly deferred to a separate task. Source: `20260509-improve-cli-repl-help/decisions.md` D2/D8.
- **`/issue promote <n>`** - spin an open `issues.md` entry out into a new task: seed `description.md` from the issue body, mark the source issue as `filed-as-task <new-id>`, optionally record `parent_task_id` lineage. Postponed from the v1 `issues.md` design (2026-05-17 conversation). The done-gate / task-completion ceremony is also postponed pending a full requirements + design cycle; v1 has no review gate, user runs `/issue list` on demand. Source: 2026-05-17 issues.md design conversation.

## C. Deferred from Stage 2B (tracked here; not blockers)

- **Phase 4c - real Spectre `Layout` + `Live` continuous repaint** (per D29). Requires `Console.ReadKey`-based input loop (prerequisite for true `Tab`/`Shift+Tab` keybindings). Also bundles `Ui/SessionListView.cs`, `Ui/SessionView.cs`, `Ui/StatusBar.cs`, persistent schema-drift counter in status bar. Adopting `Console.ReadKey` would ripple through `StringReader`-based test fixtures and the `Process.StandardInput.Write` smoke-test pipeline; only worth landing if the slash-command UX (`/focus`/`/next`/`/prev`, `/list`) proves too rough in real use.
- **`TranscriptModel` on-disk overflow** (per D20). Currently a bounded in-memory ring (10 000 entries, `DropOldest`); D4 envisions disk-backed overflow per `~/.infinicontext/transcripts/<session_id>.log`. Pair with Phase 4c when there's a continuous-render consumer to validate the design against.
- **Throttled mid-session `last_active` updates to the session index** (per D22). Phase 3 writes only on close. The throttled `last_active` from D5 lands with Phase 4c if/when a UI consumer needs "recently used" sorting of *live* sessions.
- **True `Tab` / `Shift+Tab` keybinding for focus rotation** (per D7/D29). `/next` and `/prev` slash commands are the Phase 4b substitute.
- **"Trusted cwds" first-time-discovery banner** - explicitly deferred past Stage 2B per D15. Orthogonal to multi-session mechanics; will become its own task.
- **Per-session settings overrides** - Stage 3 UX territory per D10.
- **N1 - `r` key shortcut for `/resume <highlighted row>`.** Same `Console.ReadKey` dependency as `Tab`; lands with Phase 4c if at all.
- **`b` key + Spectre `MultiSelectionPrompt` picker for `/broadcast`.** Plan section 6 sketched a "press `b` -> checkbox-pick -> one prompt" flow as the broadcast UI; Phase 6 shipped only the slash-command surface. Same `Console.ReadKey` dependency as `Tab`/`r`; lands with Phase 4c iff comma-prefix selection feels clunky in real use for >=4 targets.
- **Real-spawn broadcast fan-out test.** Phase 6 tests drive `BroadcastAsync` against the in-memory `RecordingSession` and `FakeSession`. The end-to-end version (two FakeClaude processes via `SessionManager.CreateAsync`, one `/broadcast` line, both transcripts converge) was deferred; the exit-criteria smoke battery exercises the wiring at unit level.
- **C-1 deeper restructure (stdin/stdout deadlock).** Current `DropOldest` mitigation suffices through Phase 4b. Stage 2 should consider a dedicated stdin-writer task driven by a queue if multi-session contention surfaces deadlocks. Files: `ClaudeSession.cs` (write path). Source: `20260506-poc/decisions.md` (Pending), `20260506-review-stage1-poc/context.md`.
- **Multi-line prompt input.** Operator wants either trailing-backslash continuation (Claude Code style — last char `\` swallows the newline and adds a row to the same prompt) or `Shift+Enter` to start a new line. `Shift+Enter` requires `Console.ReadKey`-based input (the same dependency as Phase 4c keybindings). Trailing-backslash continuation works on top of the existing `Console.ReadLine` loop and could ship without the Phase 4c rework — recommend doing it first as a low-risk increment, then revisit `Shift+Enter` when Phase 4c lands. Files: `src/InfiniContext.Cli/Ui/MultiSessionHost.cs` (input loop around lines 174–235). Source: `.task/0005-manual-testing-review-notes/issues.md` #0002.
- **External editor for long prompts.** Operator wants a `/edit` (or `/e`) slash command that opens a configurable external editor — `$VISUAL` then `$EDITOR` then `notepad` (Windows) / `nano` (Unix) as the fallback chain — writes the current draft to a temp file, blocks on the editor's exit, reads the result, and sends it as the next user message. Settings: a new `editor.command` (string, optional) under settings.schema.json. Files: new `src/InfiniContext.Cli/SlashCommands/EditCommand.cs` plus a temp-file helper; settings layer additions in `InfiniContextSettings`. Source: `.task/0005-manual-testing-review-notes/issues.md` #0009.

## D. Stage 3 (Avalonia GUI)

### UI-testing follow-ups (task 0020; POC shipped 2026-06-12 - `Gui.HeadlessTests`, `Gui.UiaDriver`, `Gui.UiaTests`, `ci-uia.yml` all green)

- **P1 remainder: AutomationId coverage.** Ids exist only for the POC control set; the rest (approval banner buttons, approval-mode combo, auto-scroll checkbox, inspector controls, tree-row action buttons + `AutomationProperties.Name` on row templates, ConfirmDialog) are listed under "Not yet covered" in `docs/gui-automation-ids.md`. Add the UIA tree-contract test (design 6.3 #1 full form) once coverage is complete so drift is caught at dispatch time.
- **P3: session-index path override.** `SessionIndex` resolves `~/.infinicontext/index.json` unconditionally; until a `sessions.indexPath` setting (env: `INFINICONTEXT__SESSIONS__INDEXPATH`) exists, UIA tests MUST NOT spawn sessions or they pollute the operator's real index. Blocks most of the design 6.3 seed list. Files: `src/InfiniContext.Core/Sessions/SessionIndex.cs`, settings schema/loader, `GuiAppHarness`.
- **Grow the seed test lists.** Headless: design 5.5 items 3-8 (approval banner, session switch / 0016 cached-view regression, NewTaskDialog validation, ConfirmDialog keyboard contract, transcript render incl. the Markdown.Avalonia-under-headless canary, busy/queue semantics). UIA: design 6.3 items 2-5 (full icx-smoke scenario, foreground/focus after dialog close, dead-session UX, approval flow E2E) - all but #3 need P3 first.
- **UiaProbe tool (design Phase 3).** `tools/InfiniContext.UiaProbe` console exe over `Gui.UiaDriver` (launch/dump/click/settext/invoke/screenshot/kill verbs, pid+sandbox state file); delete `tmp/icx-smoke` once verb parity is reached.
- **Promote `ci-uia.yml` to PR-triggered-on-GUI-paths** after a few weeks of green manual dispatches (design 6.4).

- **Multi-host support (real, not the cheap sentinel).** Task 0023 / audit C1 shipped the *cheap path*: a startup sentinel lock (`HostInstanceLock`, `~/.infinicontext/host.lock`, `FileShare.None` + `DeleteOnClose`) makes a second host (GUI alongside CLI, or two GUIs) fail fast with "another InfiniContext host is already running" instead of silently corrupting the shared SQLite registry / `.task/` markdown that the in-memory `LockRegistry`/`TaskActivityCoordinator` only coordinate per-process. The *real* multi-host path is deferred and is a deliberate Stage 3 design item, not an accident: move task claims (holder + heartbeat) into SQLite, enable WAL on `infinicontext.db`, back `LockRegistry` with a cross-process mechanism, and make `AllocateIdAsync`'s read-then-update safe under cross-process contention. Only then can the sentinel be relaxed to allow concurrent hosts. Files: `src/InfiniContext.Core/Sessions/HostInstanceLock.cs` (the guard to relax), `src/InfiniContext.Core/Tasks/TaskStore.cs`, `src/InfiniContext.Core/Mcp/Tasks/LockRegistry.cs`, `src/InfiniContext.Core/Tasks/TaskActivityCoordinator.cs`. Source: `docs/architecture-review-2026-06-11.md` C1 + thematic improvement #1.
- **Avalonia 11.x app on Win + Mac.** Begins with a written design proposal; supersedes `infinicontext-initial-design.md` section 9 Stage 3. Estimated 4-8 weeks.
- **MVVM flavour decision: CommunityToolkit.Mvvm vs ReactiveUI.** Deferred to Stage 3 spike. Source: `infinicontext-initial-design.md` section 8 item 5.
- **Markdown library decision: Markdown.Avalonia vs alternative.** Source: `infinicontext-initial-design.md` section 8 item 6.
- **Collapsible tree view per session.** Each turn = root; assistant text/thinking/tool calls/sub-agents = children. Markdown rendering with syntax-highlighted code blocks. Source: `infinicontext-initial-design.md` section 4.7 / 9.
- **Persistence escalation decision.** Flat JSON vs SQLite for non-task host state - stay flat unless query patterns force the upgrade. (Task registry is already SQLite per Stage 2A.) Source: `infinicontext-initial-design.md` section 9.
- **Settings UX scope.** Model per session, allowed-tools selection, env overrides - table editor or per-spawn dialog. Source: same.
- **Lazy-loading large tool results.** `VirtualizingStackPanel` + on-expand `Read` from Claude's jsonl. Source: `infinicontext-initial-design.md` section 6.2 / 9.
- **Host-side slash commands.** `/clear`, `/cost`, `/model`, `/resume <id>` - not a terminal. Source: same.
- **Aggregate status strip.** Counts (idle / working / awaiting-approval / schema-drift); full dashboard v2 if it adds value. Source: same.
- **Decoded `stream_event` inner discriminator.** Modeled as `JsonElement` for now; decode only when Stage 3 needs it. Files: `Events/ClaudeEvent.cs`. Source: `20260506-poc/stage-1-plan.md`.
- **Hand-rolled parser converters revisited.** Parser uses `JsonNode` round-trip; hand-rolled can come back if Stage 3 needs typed access to deeply nested fields (e.g. `assistant.message.content[i].input`). Source: `20260506-poc/decisions.md`.
- **Tree-view UI.** Source: `20260507-tasks-and-sessions-management/decisions.md`, `design.md` section 6.
- **Auto-start mode.** UI checkbox firing off agents without confirmation - footgun to design carefully. Source: same.
- **Background-worker / sub-agent collapsible visibility.** Operator called out that CLI output for sub-agents (Task-tool spawns, in-progress workers, etc.) is verbose and hard to scan. In a GUI this should fold into collapsible per-worker rows showing status (idle / working / done / failed) with on-demand output reveal. Implicit in the existing "Collapsible tree view per session" bullet but worth tracking as an explicit GUI requirement so it isn't dropped. Source: `.task/0005-manual-testing-review-notes/issues.md` #0008.
- **XAML `IsQueued` badge rendering.** `TurnNodeViewModel.IsQueued` is wired on the VM side (set by `SessionRowViewModel` when a second operator send arrives while the first turn is still in flight, cleared when the queued turn is promoted to in-flight or when process exit flushes the queue). Avalonia view binding to actually render a "queued" badge on the turn card is not yet done. Files: `src/InfiniContext.Gui/Views/MainWindow.axaml` (turn card data template). Source: `.task/0011-session-workflow-architecture-debugging-and-fixes/progress.md` "Out of scope" section.
- **"Why & What Happened" causality view.** Operator-facing surface that explains, for each visible operation, what triggered it (operator prompt? context-pressure nudge? agent self-decision? tool-result reaction?) and why the agent picked that path. Long-term goal flagged by the operator 2026-05-24; abstract requirements need a design pass before implementation. Likely touches the turn-tree VM (event-to-cause classifier), the inspector pane (causality breadcrumb), and possibly an opt-in detail mode that surfaces reasoning excerpts from `thinking` blocks. Source: `.task/0012-gui-development-phase-3-design-documentation/findings.md` #1.

## E. Stage 4 (release engineering)

- **macOS code-signing / notarization.** Flagged as a Stage 4 problem. Source: `infinicontext-initial-design.md` section 6.1.

## F. Light-model research follow-ups (beyond items 2-5 above)

- **Investigate single-shot wall-clock variance.** Native p95 of 17 s on uncapped variant still rough; check if nothink+cap closed the tail enough. Source: `20260507-manual-test-session/findings.md` section 7.
- **How does long-lived child interact with rate limits across the rest of InfiniContext?** Source: same.
- **Long-lived process supervision details.** Crash restart, drain stdin/stdout cleanly on host shutdown, integrate with `IProcessSupervisor` Job Object on Windows. Source: same.
- **Track GH `anthropics/claude-code#38022`.** If Anthropic ships `--bare --auth` or `--no-context` with OAuth support, revisit immediately. Source: same, `docs/research/claude-cli-light-model.md` Appendix B.
- **First-party consumer for `LongLivedCliLightModelService`.** Service exists per user directive to support all four corners but has no consumer yet; wire as features land. Source: `20260507-implement-light-model-for-non-api-subscriptions/context.md`.
- **Larger-scale labelled eval before adversarial use.** Small synthetic probe is a sanity check, not a quality benchmark. Source: `docs/research/claude-cli-light-model.md` section 5.3 / 8.
- **N=100 follow-up pass.** N=10 enough for 4x shift, not for tight CIs. Source: `docs/research/claude-cli-light-model.md` section 8.
- **Identify the actual `/clear` reset trigger.** Hypothesized 5-min cache TTL but never directly tested by timing the turns. Source: same.
- **Re-bench on macOS / Linux.** All measurements Windows 11 only. Source: same.
- **Re-bench Sonnet / Opus.** Only Haiku 4.5 measured. Source: same.
- **Strip the cosmetic ```json fence wrapper.** Tighten the prompt further. Source: `docs/research/claude-cli-light-model.md` section 5.3.
- **Watch for upstream Anthropic changes.** `CLAUDE_CODE_DISABLE_THINKING`, `--no-session-persistence`, etc. are not all documented stable contracts. Apply `MinClaudeVersion` / `TestedAgainstVersion` discipline. Source: same.

## G. Fixture and testing follow-ups (beyond item 6 above)

- **Manual smoke test of `/task new` end-to-end under OAuth.** Unit/integration suite passes but failing command not re-run in live session post-fix. Source: `20260507-manual-test-session/progress.md` session 4.
- **Record `compaction.jsonl`.** Deferred - needs context fill. Source: `tests/fixtures/README.md`.
- **Record `api-retry.jsonl`.** Deferred - hard to induce (rate-limit). Source: same.
- **Record `cancel-mid-stream.jsonl`.** Deferred - recorder needs interrupt support. Files: `tools/InfiniContext.Recorder/`. Source: same.
- **Produce state-machine `<scenario>.expected-trace.txt` files** as part of state-machine tests. Source: same.
- **Lock recordings in `tests/fixtures/`** - confirm exact NDJSON event schema vs current SDK source. Source: `infinicontext-initial-design.md` section 4.2 / 8 item 2.
- **Confirm 60 s approval-MCP timeout.** Pick another if real users find it too short. Source: `infinicontext-initial-design.md` section 8 item 4, `20260506-poc/decisions.md` (Pending).

## I. /task review and /task spawn follow-ups (2026-05-17 onward)

`/task review` v1 shipped over 12 small commits plus 9 follow-ups. `/task spawn <id>` shipped 2026-05-17 (commit `b450ae8`). Items below are the residual follow-ups that didn't fit v1.

### /task review

- **Walk the 11 user-decision-blocked findings from `review-2026-05-18T041337.md`** plus 3 meta findings: sec-002 remainder (the borderline secret patterns: `settings.json`, `appsettings.Development.json`, `*.crt`, `.kube/config`), sec-003 (raw reviewer output scrubbing), sec-005 (git PATH trust model), arch-002 (CliReviewerAgentService / CliLightModelChild coupling), arch-005 (`.task/` lock-contract enforcement at executor), arch-006+007 (cross-cutting options-record refactor in CLI), arch-008 (intentional duplication of unparseable-reviewer synthesis), arch-009 (Core layering for decisions.md discovery), arch-011 (configurable secret patterns via `ReviewSettings.ExcludedFilePatterns` ctor seam), plus 3 reviewer-prompt parser failures (completeness / codelogic / tests). Each one is blocked on a product / policy / scope decision. Source: `.task/0003-infinicontext-phase-2b-continuation-plan/review-2026-05-18T041337.md` and `progress.md` handoffs of 2026-05-18 and 2026-05-20.
- **Re-run `/task review` after tightening `ReviewerPrompts.cs`** so completeness, codelogic, and tests reviewers actually produce output (the 1-of-21 auto-apply ratio in the 2026-05-18 dogfood was partly a parser-failure artifact). Likely 2-3 prompt-tightening commits before the surface is shippable. Source: same.
- **Configurable secret-file patterns via `ReviewSettings.ExcludedFilePatterns`.** v1 hardcodes a list in `ScopeResolver` (expanded 2026-05-20 to add credentials / netrc / npmrc / pypirc / git-credentials / kubeconfig / htpasswd / jks / keystore / asc / gpg / tfvars). Add the configurable seam when dogfood surfaces a false-positive / missed-positive that the user wants to override per-project. Files: `src/InfiniContext.Core/Review/ScopeResolver.cs` (static arrays), `InfiniContextSettings.ReviewSettings`. Cross-references arch-011 in the review report.
- **`.gitignore` integration for ScopeResolver.** No `git check-ignore` plumbing today; the secret-file guard is heuristic-only. If a project's `.gitignore` adds new secret shapes the hardcoded list misses, ScopeResolver won't know. Files: new `IGitRunner.CheckIgnoreAsync`.
- **Per-token "matched nothing" diagnostic in `--scope` glob expansion.** When `--scope "src/A.cs;ghost/**"` matches A.cs but `ghost/**` matches zero files, the user gets just `["src/A.cs"]` with no feedback that `ghost/**` was a typo. Empty-scope short-circuit catches the all-zero case; partial-zero is silent. Files: `src/InfiniContext.Cli/Tasks/ScopeGlobExpander.cs`.
- **Negation patterns (`!exclude/**`) in `--scope` glob expansion.** `Matcher.AddExclude` supports them but the expander doesn't expose the syntax yet. Files: same.

### /task spawn

- **Wire `TaskActivityCoordinator.LeaveAsync` into `SessionManager.CloseAsync`.** When a `/task spawn`-spawned (or `/n`-spawned) session is closed mid-host-lifecycle, the coordinator's task claim is NOT released. The task row stays at `InProgress` until host restart. This gap pre-exists for every non-focused multi-session path; `/task spawn` makes the gap more visible because it's now a routine flow. Files: `src/InfiniContext.Core/Sessions/SessionManager.CloseAsync` + a `SessionContext.OnDispose` hook. (**Cross-ref:** numbered item 7 above — multi-pane non-focused-session auto-rotation — depends on this fix; the two should be picked up together.)
- **End-to-end test for `/task spawn`** against real `MultiSessionHost` + `SessionManager` + `TaskActivityCoordinator`. v1 ships with only parser tests; the helper-side flow (`TrySpawnTaskSessionAsync` - local pre-check, plan build, `CreateAsync`, coordinator Enter with rollback-on-refusal) is integration-only and uncovered. Files: new `tests/InfiniContext.Tests/Cli/SlashCommands/TaskSpawnSlashCommandTests.cs` or fold into the existing `TaskCommandHandlerReviewTests` scaffold.

