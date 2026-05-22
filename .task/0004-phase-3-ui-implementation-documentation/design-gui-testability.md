# Design - GUI testability (Phase 3B)

Drafted 2026-05-21 in response to the operator's observation that successive readiness-gate fixes have regressed the GUI in non-obvious ways (slice 4i banner stickiness, the bring-up deadlock, the free-session deadlock, the result-after-init too-strict gate, and now the init-relaxed too-loose gate). Each was diagnosed only after the operator manually reproduced and attached a log. The prior session's relaxation flip (init -> ready instead of result -> ready) is now visibly worse: buttons enable but the session is not operational because the operator can fire a prompt while the pointer turn is still streaming, which queues silently behind the in-flight turn with no UI signal.

The pattern is consistent: each gate change is *locally correct* under some assumption about event ordering, but the interaction surface (gate state, button enablement, transcript visibility, stdin queue, in-flight turn) has too many axes to reason about by hand. We need tests.

This document is a **design**, not an implementation. It scopes the smallest scaffolding that lets us drive `SessionRowViewModel` + `MainWindowViewModel` through their full readiness/send/approval state space without spinning up Avalonia, then enumerates the regression-catching tests that scaffolding enables.

## 1. What the recent regression tells us

The init-relaxed gate flipped one Boolean (`IsReady`) before the agent could accept a new turn. That is not a bug in the gate logic - it's a bug in the *operator-facing contract* between gate state and the Send button. Specifically:

| Phase | `IsReady` | `IsAlive` | Pointer turn | Send button | What happens on Send |
|---|---|---|---|---|---|
| Pre-init | false | true | not started | disabled | n/a |
| Init seen, pointer turn streaming | **true (NEW)** | true | in flight | **enabled** | prompt queues in stdin behind pointer turn, no visible feedback |
| Pointer turn closed | true | true | done | enabled | prompt processed normally |
| Exited | true | false | n/a | disabled | n/a |

The middle row is the operator's "buttons enable but session is not operational" report. The relaxation didn't break anything mechanically; it exposed an axis the prior gate had been *implicitly* covering (the pointer turn's in-flight-ness) and removed the coverage. The result-event gate was too strict because it conflated "agent is processing my pointer" with "session is not yet operational" - they're related but not identical signals.

The correct readiness signal probably needs to combine multiple axes:
- `_initSeen` (claude has acknowledged spawn)
- `State.Current` (the session-level state machine - in particular, whether we're `Idle` rather than `Sending`/`Streaming`/`Cancelling`)
- maybe `LastNumTurns` (proves at least one turn has fully closed)

But - **and this is the point of this document** - we should stop guessing which combination is right and write tests that enumerate all the combinations.

## 2. Goal

A test project (or test subfolder) that can:

1. Construct a real `SessionRowViewModel` against a fake `IClaudeSession` and assert `IsReady` / `IsAlive` / `CanSend` / button-CanExecute state after each event observation.
2. Construct a real `MainWindowViewModel` with a fake `SessionHost` (or a real one against fake Core deps) and drive the spawn / focus / send / interrupt commands end-to-end.
3. Assert the **operator-facing contract** ("after Send, the user's prompt is on stdin AND the transcript shows it AND if the agent is busy the UI says so") rather than internal gate flips.

Tests should run on the existing xUnit + FluentAssertions stack, in `InfiniContext.Tests`, without requiring Avalonia to be initialized. View-level concerns (XAML data-template selection, binding error reporting, dispatcher priority quirks) stay in operator smoke; the test suite covers VM logic only.

## 3. Substitution surface - what's already there

Surveyed `IClaudeSession`, `SessionContext`, `MainWindowViewModel`, `SessionRowViewModel` for what's already testable:

**Already substitutable (no Core changes needed):**
- `IClaudeSession` - already an interface (`src/InfiniContext.Core/Sessions/IClaudeSession.cs`). All session interactions go through it. A `FakeClaudeSession` test double can expose: `SessionId` set on demand, `State` (a real `SessionStateMachine`), `Events` (a channel the test writes to), `SendUserMessageAsync` records calls, `InterruptAsync` records calls.
- `SessionContext` - public class, public constructor that takes `IClaudeSession` + Core types. Tests build it directly. The internal `RaiseEventObserved` is accessible via the existing `[InternalsVisibleTo("InfiniContext.Tests")]` in `InfiniContext.Core.csproj`.
- `TranscriptModel`, `PendingApprovalSlot`, `SwitchableApprovalSink`, `SessionStateMachine`, `ContextPressureMonitor`, `ThresholdInjector` - all pure C#, all already constructible in tests.
- `ILightModelService` - interface, fake test impl is straightforward.
- `TaskStore`, `TaskService` - already tested in Core; reuse the same fakes / temp-dir patterns.

**NOT substitutable today (the gap):**
- `Avalonia.Threading.Dispatcher.UIThread` - static; calling `CheckAccess()` outside the dispatcher returns false, calling `Post` queues to a dispatcher that isn't running so the action never fires. Tests would silently no-op every cross-thread marshalling site. There are 7 such sites across the row VM + main VM + transcript VM.
- `Avalonia.Threading.DispatcherTimer` - directly instantiated in `SessionRowViewModel` line 117 for the banner countdown. Same dispatcher-not-running problem; the timer never ticks.

That's it. Two specific framework dependencies; everything else is already either an interface or a constructible class.

## 4. Proposed scaffolding (the minimum delta)

Two new abstractions in `InfiniContext.Gui`, both injectable, both have an Avalonia-backed real impl that the existing `Program.cs` wires up by default and a synchronous test impl tests construct directly.

### 4.1 `IUiDispatcher`

```csharp
public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    Task InvokeAsync(Func<Task> action);
}
```

- **`AvaloniaUiDispatcher`** (real impl, in `src/InfiniContext.Gui/Services/`): forwards to `Dispatcher.UIThread`. Bound at app startup.
- **`SynchronousUiDispatcher`** (test impl, in `tests/InfiniContext.Tests/Gui/`): `CheckAccess() => true`, `Post(action) => action()`, `InvokeAsync(action) => action()`. Test code never has to think about threading.

The 7 `Dispatcher.UIThread.CheckAccess() / Post` sites collapse to a single helper on the interface or inline `_dispatcher.Post(...)` calls. The conditional `CheckAccess()` branch goes away: the synchronous dispatcher answers true, the Avalonia dispatcher answers correctly at runtime, both paths converge. Code gets simpler, not more cluttered.

### 4.2 `IUiTimer` (only used by the banner countdown today)

```csharp
public interface IUiTimer
{
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    void Start();
    void Stop();
    event EventHandler? Tick;
}

public interface IUiTimerFactory
{
    IUiTimer Create(TimeSpan interval);
}
```

- **`AvaloniaUiTimer`** wraps `DispatcherTimer`.
- **`TestUiTimer`** exposes a `FireTick()` method tests call manually to advance the countdown without real time passing.

`SessionRowViewModel`'s ctor takes `IUiTimerFactory`, not a concrete `DispatcherTimer`.

### 4.3 What the ctor signatures look like after the refactor

```csharp
// Was:
public SessionRowViewModel(SessionContext context, ILogger? logger = null)

// Becomes:
public SessionRowViewModel(SessionContext context, IUiDispatcher dispatcher, IUiTimerFactory timerFactory, ILogger? logger = null)
```

```csharp
// Was:
public MainWindowViewModel(SessionHost host, /* delegates */)

// Becomes:
public MainWindowViewModel(SessionHost host, IUiDispatcher dispatcher, IUiTimerFactory timerFactory, /* delegates */)
```

`SessionHost.StartAsync` (or `App.axaml.cs`) builds the real dispatcher/factory once and passes them down. Tests build their synchronous equivalents and pass them directly.

### 4.4 What we deliberately do NOT abstract

- **`PropertyChanged` event plumbing.** It's already pure .NET; `CommunityToolkit.Mvvm`'s `ObservableObject` raises events synchronously on the calling thread. Tests just subscribe directly.
- **`ObservableCollection<T>`.** Pure .NET. `CollectionChanged` fires synchronously.
- **`RelayCommand` / `AsyncRelayCommand`.** Pure C# from CommunityToolkit. `Execute` / `CanExecute` are direct calls.
- **The storage provider (folder picker).** Already abstracted via a callback the view injects into the VM; tests pass their own callback.
- **`ILogger`.** Already a Serilog interface; use a `LoggerCapture` test impl or pass `null`.

These are all already test-friendly; abstracting them further would be ceremony for no gain.

## 5. The tests this enables (regression coverage for the bugs we've shipped)

### 5.1 Readiness gate state machine (the central source of the recent regressions)

Each row is one xUnit `[Theory]` case driving a single `SessionRowViewModel`:

| Setup | Event sequence | Expected IsReady | Expected IsAlive |
|---|---|---|---|
| Free session, sessionId=null at ctor | none | true (ctor-free-deferred-init) | true |
| Free session, sessionId="abc" at ctor | none | true (ctor-pre-seed) | true |
| Task-bound, sessionId=null at ctor | none | false | true |
| Task-bound, sessionId="abc" at ctor | none | true (ctor-pre-seed, new policy) | true |
| Task-bound, sessionId=null | SystemInit | true (init) | true |
| Task-bound, sessionId=null | Assistant before init | catch-up fires; IsReady true | true |
| Task-bound, sessionId=null | SystemInit, Assistant, Result | true, in-flight tracked | true |
| Any | Exited transition | unchanged | false |

**Crucial new test the current regression would have caught:** "task-bound session, init seen, pointer turn streaming, operator clicks Send" - assert that *something* visible happens (transcript marker, status text, button state). The test forces us to decide what the operator-facing contract should be.

### 5.2 MainWindowViewModel CanExecute

- `SendPromptCommand.CanExecute` false when `FocusedSession is null`.
- `SendPromptCommand.CanExecute` false when `FocusedSession.IsReady == false`.
- `SendPromptCommand.CanExecute` false when `FocusedSession.IsAlive == false`.
- `SendPromptCommand.CanExecute` false when `PromptText` empty/whitespace.
- All four CanExecute paths re-fire when their input flips.

### 5.3 Pointer injection (slice 4h cc42c1a)

- Spawn task-bound: `TaskService.PrepareSwitchAsync` is called, `plan.FirstUserMessage` flows into `SpawnRequest`.
- `TaskService` null: spawn succeeds without pointer, status text says "subsystem unavailable".
- `PrepareSwitchAsync` throws: warning surfaced, spawn happens without pointer.

### 5.4 Turn tree assembly (slice 4b)

- `BeginUserTurn(text)` creates a TurnNode with operator message.
- AssistantEvent.text -> Headline.
- AssistantEvent with tool_use blocks -> child rows.
- UserEvent with tool_result -> child row attached to the matching tool_use by id.
- ResultEvent -> turn closes, footer populates.
- Multi-step assistant turn (text/tool/result/text) - last text becomes headline, earlier text demoted to a child.

### 5.5 Approval banner flow (slice 4i, 4l, 4m)

- `PendingApprovalSlot.Set(...)` -> row VM's `PendingApproval` populates; banner properties (PendingDeadlineText etc.) raise PropertyChanged.
- `ApproveCommand.Execute` -> slot resolves Allow.
- `DenyCommand.Execute` -> slot resolves Deny.
- Timer-tick (fired manually via `TestUiTimer.FireTick()`) -> deadline text updates.
- Slot clears -> timer stops.

### 5.6 Dead-session marker (slice 4-fix)

- State transition to `Exited` -> `IsAlive` flips, `(session exited)` synthetic turn appears.
- Send button CanExecute re-fires false.

## 6. Tests we are NOT writing in this scaffolding

- **View-level**: XAML data-template selection, `TreeDataTemplate` vs `DataTemplate` (the bug from slice 4-fix where session rows weren't selectable), binding errors, dispatcher priority issues. These need Avalonia.Headless and are a separate, larger investment. Operator smoke still owns this layer.
- **Real claude integration**: stays in `tests/InfiniContext.Tests/Sessions/*` against `FakeClaude` per Stage 2B convention.
- **End-to-end UI tests**: out of scope; the GUI test pyramid stays bottom-heavy (VM tests) for the foreseeable future.

## 7. Sequencing the work

1. **Land `IUiDispatcher` first.** Smallest blast radius; touches 7 sites in 3 files. No behavior change once `AvaloniaUiDispatcher` is wired in. Verify with one smoke launch.
2. **Land `IUiTimer` / `IUiTimerFactory` next.** Smaller still; one site (the banner countdown). Verify with manual banner test.
3. **Write the readiness gate test matrix (Section 5.1).** This is the highest-value table - the regressions we've shipped most often. The test matrix forces us to write down the operator-facing contract, which we have been making up on the fly.
4. **Decide the operator-facing send contract** from the test results. Likely outcome: `IsReady` stays simple (init-based), but the Send button gates on `IsReady && State.Current == Idle`, or the prompt-box visibly shows "Agent busy - your message will queue". Either is testable.
5. **Backfill the rest of Section 5** in priority order: CanExecute, pointer injection, turn tree, approvals, dead-session.

Steps 1-2 are mechanical refactors. Step 3 is the substantive work. Steps 4-5 follow naturally.

## 8. Open decisions

1. **Test project layout.** `tests/InfiniContext.Tests/Gui/` (inline with `tests/InfiniContext.Tests/Cli/`) - **Recommended.** Matches existing convention. Same csproj, same xUnit + FluentAssertions stack, no new build artifact. Alternative: a separate `InfiniContext.Gui.Tests` csproj for isolation - more ceremony, no real upside.

2. **`IUiDispatcher` shape.** Three methods (`CheckAccess` / `Post` / `InvokeAsync`) cover every existing call site. Alternative: a single `Marshal(Action)` that handles the check-and-post internally - slightly smaller surface, but `InvokeAsync` returning Task is still needed for `App.axaml.cs`'s startup path. **Recommended:** three methods, matches what we already write.

3. **`IUiTimer` shape.** Match `DispatcherTimer`'s surface (`Interval`/`IsEnabled`/`Start`/`Stop`/`Tick`). Alternative: simpler ("`Schedule(TimeSpan, Action)` returning a token to cancel") - less mechanical, but breaks the existing timer-lifecycle pattern. **Recommended:** match DispatcherTimer.

4. **Where the real `AvaloniaUiDispatcher` is constructed.** Three options: `App.OnFrameworkInitializationCompleted` (top of stack); `SessionHost.StartAsync` (current host bring-up); a static singleton `UiDispatcher.Avalonia`. **Recommended:** `App.OnFrameworkInitializationCompleted` builds it once and threads it via `SessionHost` -> VMs as a ctor arg. Singleton is convenient but is the classic test-friendliness anti-pattern.

5. **Real-time `DispatcherTimer` vs. `Task.Delay` for the banner countdown.** Out of scope of this design; keep the existing `DispatcherTimer` (now behind `IUiTimer`) - tests fire `Tick` manually, real impl is unchanged.

6. **Do we extend test coverage to `App.axaml.cs` / `Program.cs` lifecycle?** **Recommended: no** - those are bootstrap glue; their bugs (sidecar short-circuit in Phase 3B early slice) are best caught by manual smoke. Not where time pays off.

## 9. Risk: tests will be too coupled to internal state

The biggest pitfall in MVVM testing is asserting against `_initSeen` / `_isReady` / etc. (implementation details) instead of operator-facing properties. The mitigation is to assert against the **CanExecute of the buttons the operator sees** and the **public observable properties** (IsReady, IsAlive, PendingApproval, Turns.Last().Headline) rather than the private fields.

The test matrix in 5.1 will be rewritten to assert against `MainWindowViewModel.SendPromptCommand.CanExecute(null)` (after setting up the FocusedSession + PromptText) rather than `SessionRowViewModel._initSeen`. That tightens the contract while keeping the implementation free to refactor.

## 10. Risk: Avalonia headless drift

If/when we adopt Avalonia.Headless for view-level coverage (out of scope here), some VM tests may need to migrate to it (e.g. if `ObservableCollection`'s `CollectionChanged` handler runs on a different thread under headless than under the synchronous dispatcher). Plan accordingly: when adding Avalonia.Headless, audit the existing VM tests for any that assumed a synchronous Post and document the migration cost.

## 11. Estimated cost

- `IUiDispatcher` interface + Avalonia impl + sync test impl: ~80 lines.
- `IUiTimer` / `IUiTimerFactory`: ~60 lines.
- VM ctor updates + `App.axaml.cs` wiring: ~30 lines edited across 4 files.
- Initial readiness-gate test matrix (Section 5.1): ~200 lines, ~12-15 [Theory] cases.
- Operator-facing contract decision in Section 7 step 4: depends on the conclusion, but the test matrix forces it onto paper rather than into someone's head.

Total scaffolding: under 200 lines of GUI changes + a test project subfolder. Cheap compared to the recurring cost of operator-driven regression hunts.

## 12. Readiness-gate contract (resolved)

**Outcome:** init-based gate + state-machine-driven "Agent busy - your message will queue" indicator. Operator pushback path was taken after the slice 5a result-based revert failed smoke - the pointer turn for a real task runs 30s-2min while the `infinicontext-task` skill reads `index.md` / `description.md` / `progress.md`, and gating Send for that window leaves the operator with zero agency. Shipped in commit `6c1ab13` (task 0008).

**The contract the test matrix locks in:**

- `IsReady` flips on `system/init` for both free and task-bound sessions (the `ctor-pre-seed` / `init` / `catch-up` / `result-fallback` paths in `SessionRowViewModel.SetIsReady`). Once true, monotonic for the session's lifetime.
- `IsAgentTurnInFlight` (new, on `SessionRowViewModel`) is true whenever `IsAlive` AND `SessionState` is `Sending`, `Streaming`, `Cancelling`, or `Compacting`. It is a state-machine derivation, not a Boolean field; PropertyChanged fires on every state transition.
- `MainWindowViewModel.IsAgentBusy` mirrors the focused row's `IsAgentTurnInFlight` and drives the status-strip badge "Agent busy - your message will queue behind the in-flight turn". `SendButtonTooltip` swaps between the busy and idle copy.
- Send stays **enabled** while `IsAgentTurnInFlight` is true - a prompt fired during a turn-in-flight buffers on claude's stdin and runs once the current turn closes. The indicator surfaces the queue behaviour visually so the operator knows what their click did.
- Interrupt is independent (`IsAlive` only), same as before.

**What this rejected from the earlier recommendation:** the result-based gate hard-disabled Send during the pointer turn. That removed implicit agency and produced the "buttons disabled forever - bug" misread the recommendation itself flagged. The fix kept the diagnostic intent (operator should know the agent is busy) but moved it from a *disabled-button signal* to an *enabled-button-plus-visible-queue-marker* signal. Same information, but the operator can act on it.

**Why init and not composite (init AND state==Idle):** a composite would re-introduce the disable-during-turn problem the slice 5a smoke flagged. The init-based gate trusts the state machine to surface "in-flight" separately rather than folding it into IsReady.

**What the test matrix now asserts (Section 5.1 + 5.2):** IsReady flips on init (free + task-bound, both event-observed and ctor-pre-seeded paths); IsAgentTurnInFlight tracks the state machine across Sending/Streaming/Cancelling/Compacting/Idle transitions; CanExecute on `SendPromptCommand` requires `IsReady && IsAlive` and NOT `!IsAgentTurnInFlight` (Send stays enabled during turns); IsAgentBusy on the host VM flips with focused-row IsAgentTurnInFlight; SendButtonTooltip swaps copy. The 23 tests added in slice 5a + the two regression tests added in task 0008 (`OnStateTransition_marshals_to_UI_thread_when_called_off_thread`, `StdoutLoop_survives_Transitioned_subscriber_that_throws`) cover this contract.

**The artifact that prevents the next regression:** the test matrix itself. If anyone ever flips back to result-based gating or removes IsAgentTurnInFlight, the `CanSendPrompt_true_while_taskbound_turn_in_flight` and `IsAgentBusy_true_when_focused_row_in_flight` cases fail loudly. The matrix is the durable contract; this section is just the prose explanation of what it asserts.
