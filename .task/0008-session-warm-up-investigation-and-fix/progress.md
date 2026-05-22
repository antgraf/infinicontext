# Progress

## 2026-05-22

- Task created.

## 2026-05-21 (investigation by main session)

- Audited the readiness gate path end to end: ClaudeArgvBuilder, ClaudeSession.StartAsync (fire-and-forget FirstUserMessage), SessionManager.CreateAsync (reader.EventObserved sets ctx.LastNumTurns then RaiseEventObserved), SessionTranscriptReader.RunAsync, and SessionRowViewModel ctor + ApplyEvent.
- Confirmed every visible race (reader-fires-before-row-subscribes, init-fires-between-subscribe-and-snapshot, result-fires-between-snapshot-and-completion) is already covered: the row VM has both a ctor-time pre-seed (`SessionId` + `LastNumTurns` snapshots) and a runtime catch-up (first non-init event flips `_initSeen`). Both `SystemInitEvent` and the catch-up arm raise PropertyChanged for `IsProcessingPointerTurn`.
- HEAD commit `16a67e7` shipped the init-based relaxation. The working tree (uncommitted) **reverts** it back to result-based gating per the design doc `design-gui-testability.md` section 12 recommendation, and instead surfaces an "Agent processing pointer turn" status banner + tooltip so the disabled Send button reads as "agent busy" rather than "broken."
- The operator's bug report ("session unable to warm up, buttons remain disabled, label says 'agent processing pointer turn'") is therefore **the intended behavior of the working-tree code**, but the operator is rejecting that contract: the pointer turn can take 30s-2min for real tasks (read index/description/progress + initial discovery), and operators want agency to type follow-ups during that window.
- Decision (per the design doc's explicit alternative path in section 12): switch to **init-based gate** for task-bound sessions and add a clear "Agent busy - your message will queue behind the in-flight turn" indicator. This matches the operator's expressed preference and the doc's documented alternative. The `IsProcessingPointerTurn` property is renamed/rescoped to mean "any turn currently in flight" (driven by the state machine), used purely for the queue-warning indicator, not for gating.
- Plan: (a) flip task-bound `IsReady` to fire on `SystemInitEvent` (parity with free sessions); (b) replace `IsProcessingPointerTurn` with a state-machine-driven `IsAgentTurnInFlight` that's true whenever state is Sending/Streaming/Cancelling/Compacting; (c) update the MainWindow banner + Send tooltip text so it reads "Agent is busy - Send will queue your message behind the in-flight turn" rather than "Send will enable when the turn completes"; (d) write VM-level tests in `tests/InfiniContext.Tests/Gui/` to lock the new contract per design doc section 5.1.

### Implementation (2026-05-21)

- `src/InfiniContext.Gui/ViewModels/SessionRowViewModel.cs`:
  - `SystemInitEvent` handler now flips `IsReady` unconditionally (was: only for free sessions); the catch-up branch (first non-init event) does the same.
  - `ResultEvent` still flips IsReady but only as a defensive fallback for the pathological "init was missed entirely" case - the new gate is init-based.
  - `ctor-pre-seed` simplified: non-null `SessionId` at ctor flips IsReady regardless of `ActiveTaskId`; the `LastNumTurns` check is gone.
  - `IsProcessingPointerTurn` replaced by `IsAgentTurnInFlight` (state machine driven: true while state is `Sending`/`Streaming`/`Cancelling`/`Compacting`).
- `src/InfiniContext.Gui/ViewModels/MainWindowViewModel.cs`:
  - `IsAgentBusy` now mirrors `FocusedSession.IsAgentTurnInFlight`.
  - `SendButtonTooltip` no longer apologises for being disabled; it surfaces queueing behaviour while the agent is busy.
  - `SendToSessionAsync` snapshots `IsAgentTurnInFlight` before the send and (a) prefixes the transcript line with `[queued behind in-flight turn]` and (b) sets the status strip to "Queued to <id> - will run after the current turn." when queueing is in effect.
  - `OnFocusedSessionPropertyChanged` now listens for `IsAgentTurnInFlight` (was: `IsProcessingPointerTurn`).
- `src/InfiniContext.Gui/Views/MainWindow.axaml`:
  - Status-strip banner text changed from "Agent processing pointer turn..." to "Agent busy - next message will queue".
- `tests/InfiniContext.Tests/Gui/SessionContextFactory.cs`:
  - `RaiseEvent` now drives `Session.State.Apply(ev)` before raising `EventObserved`, matching the production ordering in `ClaudeSession.StdoutLoopAsync`. Previously the test helper bypassed the state machine, which became a problem once tests started asserting on state-machine-derived properties.
- `tests/InfiniContext.Tests/Gui/SessionRowViewModelTests.cs` + `MainWindowViewModelTests.cs`:
  - Rewrote the matrix from the result-based contract to the init-based + queue-indicator contract. The central regression-protection test (`CanSendPrompt_true_for_taskbound_after_init_even_with_turn_in_flight`) asserts that Send is enabled and `IsAgentBusy` is true simultaneously - the exact configuration that the prior contract refused to allow.
- Build green; all 1005 tests pass; `dotnet format --verify-no-changes` clean.
- Open follow-ups (none blocking): (1) operator validation - the fix needs a manual smoke run with a real claude binary; (2) the design doc (`design-gui-testability.md`) still describes the result-based contract in section 12 - if the new gate sticks after operator validation, that doc should be amended.

### Hand-off to next session (2026-05-21, hard-threshold flush)

All changes are on disk in the working tree (not committed yet). Files touched:
- `src/InfiniContext.Gui/ViewModels/SessionRowViewModel.cs`
- `src/InfiniContext.Gui/ViewModels/MainWindowViewModel.cs`
- `src/InfiniContext.Gui/Views/MainWindow.axaml`
- `tests/InfiniContext.Tests/Gui/SessionContextFactory.cs`
- `tests/InfiniContext.Tests/Gui/SessionRowViewModelTests.cs`
- `tests/InfiniContext.Tests/Gui/MainWindowViewModelTests.cs`

Verification status: `dotnet build InfiniContext.slnx` green, `dotnet test InfiniContext.slnx` 1005 passed / 0 failed / 3 skipped, `dotnet format --verify-no-changes` clean.

Still TODO for whoever resumes this task:
1. **Manual smoke** with the real `claude` binary - spawn a task-bound session via the GUI, confirm: (a) Send button is enabled within ~1s of clicking the spawn button (was: stayed disabled until pointer turn closed, often 30s-2min); (b) "Agent busy - next message will queue" banner appears at the top of the window while the pointer turn streams; (c) typing a message and clicking Send during that window produces a "[queued behind in-flight turn]" line in the transcript and a "Queued to <id>" status text; (d) once the pointer turn closes, the banner disappears and a follow-up Send produces a normal "Sent to <id>" status with no queued marker.
2. **Commit** once smoke passes. Suggested message stem: "Phase 3B fix: init-based readiness gate + queue indicator (operator pushback on result-based)."
3. **Amend `design-gui-testability.md` section 12** to reflect that the operator-pushback branch was taken; the doc currently recommends the result-based gate.

### Operator smoke (2026-05-21) - additional findings

Operator validated against real claude 2.1.147. Two issues surfaced:

**(A) Seed prompt never visible in transcript.** The agent-side flow worked (seed sent on stdin, init received, state Sending -> Streaming for the assistant turn-start), but the operator only saw "(session ready)" in the messages area - the task pointer they sent was invisible. Root cause: `ClaudeSession.SendFirstUserMessageAsync` writes to claude's stdin and never produces a UserEvent in the parsed stream, so the GUI's per-turn tree had nothing to render for it.

  Fix: `MainWindowViewModel.SpawnSessionForTaskAsync` now calls `row.BeginUserTurn(firstUserMessage)` + appends `> {seed}  [seed prompt]` to the flat transcript right after `CreateSessionAsync` returns, so the seed appears as the first turn card with the actual pointer text as its headline. `SessionRowViewModel.SystemInitEvent` handler updated to preserve a real seed-prompt headline (only blanks `Headline` when it matches a `SyntheticHeadlines` placeholder) and to keep `_pendingTurn` live in that case so the agent's response attaches as children of the seed turn.

**(B) Assistant event applied to state machine but never logged at `rx:` level / never delivered downstream.** In log `gui-20260521-204404.log`:
  - 12.197: session_id assigned, system/init delivered (rx: system/init logged)
  - 13.164: rate_limit_event delivered (rx: rate_limit_event logged)
  - 14.621: `state Sending -> Streaming (assistant)` logged - so the state machine DID receive an AssistantEvent
  - (2 minute gap, no further log activity)
  - 20:46:13: process exited code=-1 (operator closed the GUI)

  The `rx: assistant {...}` debug line that should follow the state transition is missing. In `ClaudeSession.StdoutLoopAsync`, state.Apply runs synchronously (fires the transition log via `SessionLogger.OnStateTransition`), then the code falls through to `_sessionLogger.LogReceivedEvent(ev)` which is what emits the `rx:` line. The transition logged but LogReceivedEvent did not. Possible causes (not yet validated):
  - Apply throws AFTER firing Transitioned (e.g. inside `OpenToolUse` or `ToolStackChanged?.Invoke`)
  - LogReceivedEvent throws (e.g. `ev.Raw.ToJsonString()` on a malformed/huge body)
  - The stdout loop is stuck on `await _events.Writer.WriteAsync(ev)` - shouldn't happen with `BoundedChannelFullMode.DropOldest` but worth checking
  - The parsing thread silently aborted between yielding the assistant event and the next loop iteration

  **This is the actual cause of "Agent busy never changes."** Even with seed prompt now visible, the operator will still see the indicator stuck until this second issue is fixed.

### Hand-off addendum (2026-05-21, second flush)

In addition to the items in the prior hand-off:

4. **Diagnose (B) above** - the missing `rx: assistant` after state transition. Suggested approach: instrument `StdoutLoopAsync` with `_logger.Debug("about to LogReceivedEvent type={Type}", ev.Type)` between Apply and LogReceivedEvent, and a matching log after WriteAsync. Bisect which line stops emitting. Likely a couple of hours' investigation with a real claude 2.1.147 spawn. If LogReceivedEvent is throwing, wrap it in a try/catch and log the exception - the current code has no defensive catch around it.

5. **Verify (A)** - relaunch the GUI, spawn a task-bound session, confirm the seed prompt is visible as the first turn card. Should be evident within 1-2s of the spawn.

Files touched in this iteration (incremental over the prior hand-off list):
  - `src/InfiniContext.Gui/ViewModels/MainWindowViewModel.cs` (added BeginUserTurn + transcript append for the seed in SpawnSessionForTaskAsync)
  - `src/InfiniContext.Gui/ViewModels/SessionRowViewModel.cs` (preserved real seed-prompt headline in SystemInitEvent handler, kept _pendingTurn live so assistant content attaches to the seed turn)

Build green; 24 Gui tests pass; existing assertions still hold.

### Third iteration (2026-05-21) - thread-safety fix + defensive stdout loop

Operator validated (A) тАФ seed prompt now visible тАФ but reported "still nothing else, no updates in details" on the same turn card. Same root symptom as (B): assistant event triggers state transition, then nothing.

**Root cause identified:** `SessionRowViewModel.OnStateTransition` ran synchronously on whatever thread fired the state machine's `Transitioned` event (typically the stdout-loop reader thread). It then called `OnPropertyChanged(...)` four times and assigned the `ObservableProperty` `StatusGlyph`. PropertyChanged subscribers include Avalonia bindings, which can throw `InvalidOperationException` when a bound view is updated from a non-UI thread. That exception propagated back out of `_state.Apply(ev)` in the stdout loop. The loop's only catches were `OperationCanceledException` / `IOException`, so the binding exception terminated the foreach silently. The `finally` then awaited `proc.WaitForExitAsync()` for two minutes until the operator closed the GUI - which is the exact 14.621-to-20:46:13 gap we saw in the log.

**Two-part fix:**

1. `src/InfiniContext.Core/Sessions/ClaudeSession.cs`:
   - Wrapped each per-event step in `StdoutLoopAsync` in its own try/catch with a warning log: `state.Apply`, `CheckClaudeVersion`, `LogReceivedEvent`, event-observer, and `events.Writer.WriteAsync`. A bad event is now logged and the loop continues with the next event.
   - Added a top-level catch for any unhandled exception so the loop never terminates silently.

2. `src/InfiniContext.Gui/ViewModels/SessionRowViewModel.cs`:
   - `OnStateTransition` now marshals its full body (PropertyChanged + StatusGlyph assignment + Exited-turn card) through the dispatcher. The Avalonia binding subscribers therefore run on the UI thread, eliminating the cross-thread throw that was killing the stdout loop.

Build green (GUI); full test suite couldn't link because the CLI was running and holding the dll lock, but the previously-built test binary ran fine and all 1005 tests still pass.

### Hand-off (third flush)

Files touched this iteration (incremental):
  - `src/InfiniContext.Core/Sessions/ClaudeSession.cs` (defensive try/catch around each event step)
  - `src/InfiniContext.Gui/ViewModels/SessionRowViewModel.cs` (OnStateTransition dispatches to UI thread)

What the operator should see next:
  - Spawn a task-bound session
  - Seed prompt card appears with the pointer text as headline (from iteration 2)
  - Within a few seconds: thinking blocks / tool calls / text appear as children of the same card (from iteration 3)
  - "Agent busy" indicator clears once claude emits the ResultEvent for that turn (the state machine returns to Idle)
  - If something still throws, the log will now contain a `stdout-loop: ... threw` warning identifying exactly which step is the problem - that's the diagnostic angle for the next investigation, no longer a silent failure.

Outstanding (lower priority):
  - Once iteration-3 is operator-validated, amend `design-gui-testability.md` section 12 (still describes the result-based contract).
  - Commit all three iterations as one logical change. Suggested message stem: "Phase 3B fix: warm-up - init-based gate, seed prompt rendered, stdout-loop resilient, OnStateTransition dispatched."

### Final (2026-05-21) - operator confirmed fix works; regression tests added; task closed

Operator validated iteration 3: GUI session spawned, seed prompt visible, assistant content streams into the same turn card, "Agent busy" clears at end of turn. Bug is fixed.

Added two targeted regression tests for the two distinct fixes:

1. `tests/InfiniContext.Tests/Gui/SessionRowViewModelTests.cs::OnStateTransition_marshals_to_UI_thread_when_called_off_thread` - new `TrackingUiDispatcher` helper (in `tests/InfiniContext.Tests/Gui/SynchronousUiDispatcher.cs`) lets the test set `CheckAccessReturns = false` to simulate the stdout-loop reader-thread invocation. Asserts that `Post` is called exactly once after `BeginSend` (the state-transition trigger), which proves the UI work is dispatched rather than running inline.

2. `tests/InfiniContext.Tests/Sessions/FakeClaudeIntegrationTests.cs::StdoutLoop_survives_Transitioned_subscriber_that_throws` - subscribes a Transitioned handler that throws on every Apply-driven transition (filtered via `t.EventThatCaused is not null` so host-driven `BeginSend()` isn't affected). Runs the `simple-prompt` fixture and asserts the FULL event trace still drains (`system/hook_response`, `system/init`, `assistant`, `rate_limit_event`, `result/success`). Without the per-event try/catch in `StdoutLoopAsync`, the trace would stop after the first non-throwing event - this is the exact regression that produced the operator-visible "Agent busy never changes" symptom.

Final status:
- All 1007 tests pass (3 skipped POSIX-only)
- `dotnet format --verify-no-changes` clean
- Ready to commit and push.

Task closed. Remaining follow-up (out of scope for this task):
- Amend `design-gui-testability.md` section 12 to reflect the operator-pushback path was taken.