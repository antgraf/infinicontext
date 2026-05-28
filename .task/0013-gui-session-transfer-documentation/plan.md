# Implementation plan тАФ GUI session transfer

Implements `_bmad-output/specs/spec-gui-task-session-handoff/FIX-DESIGN.md`. Investigation +
fix design are already complete (no code in this run); this task implements them.

## Anchor points (verified against current `main`, commit 004261b + recent Phase 3B commits)

- **`SessionManager.CreateAsync`** at `src/InfiniContext.Core/Sessions/SessionManager.cs:158`
  builds `ContextPressureMonitor`, `ThresholdInjector`, `TranscriptModel`, slot/sink, then
  starts the session. Wires `pressure.SoftCrossed` / `HardCrossed` to
  `ctxRef.MarkRotationReason(...)`. No `SessionCreated` event on the manager today; subscribers
  reach the per-session state machine only via `Active` snapshots.
- **`SessionContext.RotationReason`** at `src/InfiniContext.Core/Sessions/SessionContext.cs:84`
  is the public escalation-only flag set by the monitor. Also exposes
  `EventObserved` and is the GUI's seam into the per-session stream.
- **CLI auto-rotation** lives in `MultiSessionHost`:
  - `OnStateTransition` at `src/InfiniContext.Cli/Ui/MultiSessionHost.cs:389`
  - `TryStartAutoRotation` at `MultiSessionHost.cs:414`
  - `AutoRotateAsync` at `MultiSessionHost.cs:437`
  - Constructed in `Program.cs:284-361` with `autoRotationGuard: new AutoRotationGuard(3, 5min)`
    and `prepareTaskSwitch: taskService.PrepareSwitchAsync`.
- **GUI host** has **no** auto-rotation. `MainWindowViewModel.OnSessionClosed` at
  `MainWindowViewModel.cs:561` only removes the row and re-points focus.
  `SessionRowViewModel.OnStateTransition` at `SessionRowViewModel.cs:339` renders a
  "(session ended)" card and flushes queued/pending prompts as "not sent" markers.
- **GUI session spawn under a task** is `SpawnSessionForTaskAsync` at
  `MainWindowViewModel.cs:396` тАФ calls `_host.TaskService.PrepareSwitchAsync(tid)`, then
  `_host.CreateSessionAsync(cwd, taskId, firstUserMessage)`. The seed prompt is rendered by
  `row.BeginUserTurn(firstUserMessage)` + `ctx.Transcript.Append("\n> ... [seed prompt]\n")`.
- **`SessionHost`** at `src/InfiniContext.Gui/Services/SessionHost.cs:91` constructs the
  manager and currently fires `SessionCreated` / `SessionClosed` events the VM consumes.
- **AutoRotationGuard** lives in `src/InfiniContext.Core/Tasks/AutoRotationGuard.cs`. Tests
  in `tests/InfiniContext.Tests/Tasks/AutoRotationGuardTests.cs` already cover cap/window/multi-task.
- **CLI rotation tests** in `tests/InfiniContext.Tests/Cli/Ui/MultiSessionHostTests.cs`
  cover hard nudge, soft nudge, baseline (no rotation), guard cap. They drive
  `fake.State.HandleProcessExit(0)` and assert `HostResult.SwitchPlan` is populated. After
  the refactor (Fix 1) these will need to switch to asserting `AutoRotationProposed` fires
  *and* that `MultiSessionHost` still surfaces the `SwitchPlan` (the CLI's host adapter still
  consumes the proposal via `RequestSwitch`).

## Render-pane structure (Fix 2 territory)

- `Views/MainWindow.axaml:273-341` тАФ central `ScrollViewer` + `ItemsControl` bound to
  `FocusedSession.Turns`. Each turn card uses `md:MarkdownScrollViewer` for `UserPrompt`
  (operator block, line 287) and `Headline` (agent block, line 298) from Markdown.Avalonia
  **12.0.0-a2**. The window already carries a `Window.Styles` workaround at lines 34тАУ43 for
  the alpha's missing `TextWrapping="Wrap"` defaults.
- Each `SessionRowViewModel` owns its own `Turns` (`ObservableCollection<TurnNodeViewModel>`),
  so cross-session data-flow leaks are unlikely. The investigation's CAP-3 hypothesis is
  that the misrender is a Markdown.Avalonia 12.0.0-a2 layout/cache bug across rebinds, with
  `ItemsControl` virtualization as a secondary suspect.

## Implementation sequencing

Per FIX-DESIGN.md "Sequencing recommendation" тАФ adapted to defer the UI-only diagnostic
until Fix 1 is in place (Fix 1 is independent and unblocks the GUI-mode user immediately).

### Slice 1 тАФ Fix 1: `AutoRotationService` in Core + tests

Goal: promote CLI's `TryStartAutoRotation` policy into Core so the GUI can subscribe.

1. **Create `src/InfiniContext.Core/Sessions/AutoRotationService.cs`**
   - Ctor: `(SessionManager manager, AutoRotationGuard guard,
     Func<int, CancellationToken, Task<TaskSwitchPlan?>> prepareSwitch, ILogger logger)`.
   - Exposes `event Action<AutoRotationProposal>? AutoRotationProposed`.
   - On `SessionManager` *session-created* it subscribes to
     `SessionContext.Session.State.Transitioned`. **Note: `SessionManager` has no
     `SessionCreated` event today** тАФ add one (raised at the end of `CreateAsync` before
     return). Cheap, doesn't break tests (subscribers are opt-in).
   - On `Exited` with `ctx.RotationReason != None` and non-null `ctx.ActiveTaskId`:
     check `guard.TryRegister(taskId)`; if allowed, fetch `prepareSwitch(taskId)`, raise
     `AutoRotationProposed(ctx.Cwd, taskId, plan, reason)`. On guard refusal: raise
     `AutoRotationRefused(taskId, max, window)` (or surface via a single event with a
     status enum тАФ simpler).
   - The service does **not** spawn the new session. The adapter does.
2. **Refactor `SessionManager`**: add `event Action<SessionContext>? SessionCreated` raised
   inside `CreateAsync` after registration. Keep all other contracts identical.
3. **Refactor `MultiSessionHost`**: stop reading the guard / plan loader directly. Subscribe
   to `AutoRotationService.AutoRotationProposed` and call `RequestSwitch(plan)`. Delete
   `TryStartAutoRotation` and `AutoRotateAsync` bodies; keep the `OnStateTransition` arm but
   it now only fires the exit signal (the service decides + adapter consumes). Decide whether
   to pass the service via ctor or via the existing `_autoRotationGuard` ctor param тАФ cleanest
   to add a new optional `autoRotationService` param and keep guard for back-compat in tests.
   Existing CLI tests still must pass: simplest path is to keep `TryStartAutoRotation` in
   `MultiSessionHost` as a fallback when no service is provided, and have `Program.cs`
   construct the service. Or rewrite the tests to drive the service directly. **Pick the
   former** to keep the diff scoped.
4. **Wire in `Program.cs`**: construct `AutoRotationService` after `sessionManager` and pass
   it to `MultiSessionHost`. The guard already exists at line 284.
5. **New test file**: `tests/InfiniContext.Tests/Sessions/AutoRotationServiceTests.cs`.
   - Use `SessionManager.CreateAsync` with a fake `IClaudeSession` (the same `FakeSession`
     pattern as `MultiSessionHostTests`). Set `RotationReason.Hard` via `MarkRotationReason`,
     fire `HandleProcessExit(0)`, assert `AutoRotationProposed` fires with the right taskId
     and a non-null plan.
   - Second test: prime the guard to cap, assert no fire.
   - Third test: `RotationReason.None` тЖТ no fire.
   - Fourth test: not task-bound тЖТ no fire.

### Slice 2 тАФ Fix 1 GUI wiring

1. **`SessionHost`**: construct and own `AutoRotationService`. Re-expose
   `event Action<AutoRotationProposal>? AutoRotationProposed`.
2. **`MainWindowViewModel`**: subscribe to the host's event. On fire, marshal to UI thread
   and call a helper that mirrors `SpawnSessionForTaskAsync`'s tail (after `PrepareSwitchAsync`
   has already run inside the service) тАФ i.e. spawn a row, focus it, write status. The
   `FirstUserMessage` comes from the proposal, so the seed-pointer render path
   (`row.BeginUserTurn` + transcript append) is unchanged.
3. **Test**: ViewModel-level test against a stubbed `AutoRotationService.AutoRotationProposed`
   raise тАФ assert the row appears under the right task and is focused. (Or skip the VM test
   and rely on the Core service tests + manual GUI smoke.)

### Slice 3 тАФ Fix 2 diagnostic + implementation

This requires running the GUI app. Defer until slices 1+2 land and the operator can
exercise the auto-rotation path in a real session. The diagnostic decision
(general-renderer-bug vs binding-fix) drives 2a or 2b. Document outcomes in progress.md.

**Pragmatic shortcut**: Markdown.Avalonia 12.0.0-a2 is the only suspect *named in the
codebase's own comments* as alpha-quality. Bumping to a stable Markdown.Avalonia (or
swapping the two `md:MarkdownScrollViewer` instances at axaml:287,298 for plain
`SelectableTextBlock` with `TextWrapping="Wrap"`) is the smallest reversible change. If
the operator wants markdown rendering preserved, pin to a stable release; if not,
`SelectableTextBlock` is fine тАФ the headlines are mostly short prose.

### Slice 4 тАФ Fix 3 (conditional)

Only if the operator's reproduction confirms two concurrent sessions on the same task
corrupt each other's task-doc reads. Code change is minimal: thread
`TaskActivityCoordinator` into `SessionManager.CreateAsync` so both hosts inherit the
guard. Skip until evidence demands it.

## Out-of-scope reminders

- No production code changes to `SessionRowViewModel.ApplyEvent` (hardened in task 0011).
- No new GUI test harness in this task тАФ the auto-rotation regression test is non-UI.
- No cold-start warmup changes (task 0008's territory).
