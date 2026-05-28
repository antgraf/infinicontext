# Progress

## 2026-05-28

- Task created.
- Read SPEC.md / INVESTIGATION.md / FIX-DESIGN.md under
  `_bmad-output/specs/spec-gui-task-session-handoff/`. The investigation + fix design are
  already done; this task implements them.
- Surveyed the anchor code in `main` (commit 004261b + recent Phase 3B commits): confirmed
  `MultiSessionHost.TryStartAutoRotation` is the policy to promote; `SessionManager` has
  no `SessionCreated` event today (needs one for Fix 1's service to subscribe);
  `SessionContext.RotationReason` is already public; `MainWindowViewModel.OnSessionClosed`
  has no rotation handling; `Views/MainWindow.axaml:287,298` uses
  `md:MarkdownScrollViewer` (Markdown.Avalonia 12.0.0-a2) for turn cards.
- Wrote `plan.md` capturing anchor file:line refs, dependency order, and per-slice
  acceptance criteria. No production code changed.

### Slice 1 production code landed (later 2026-05-28 session)

- `src/InfiniContext.Core/Sessions/AutoRotationService.cs` -- new file. Records
  `AutoRotationStatus { Approved, Refused, Failed }` and `AutoRotationProposal`
  (Session, TaskId, Reason, Status, Plan, RefusedCap, RefusedWindow, Failure).
  Subscribes to `SessionManager.SessionCreated` and to each ctx's
  `Session.State.Transitioned`; on `Exited` with `RotationReason != None` AND
  `ActiveTaskId != null` calls `guard.TryRegister`, then `prepareSwitch`, raises
  `AutoRotationProposed`. Unsubscribes per-ctx on first qualifying transition so a
  flapping state machine cannot double-fire. `IDisposable`.
- `src/InfiniContext.Core/Sessions/SessionManager.cs` -- added
  `event Action<SessionContext>? SessionCreated` raised at the end of `CreateAsync`
  (after reader is started, with try/catch around the invoke). No other change.
- `src/InfiniContext.Cli/Ui/MultiSessionHost.cs` -- added optional
  `AutoRotationService? autoRotationService` ctor param + `_autoRotationService` field
  + `_onAutoRotationProposal` handler. In `RunAsync`, subscribes the handler before the
  input loop and unsubscribes in the finally. `OnStateTransition` now branches: if a
  service is wired and `WillServicePropose()` returns true (focused has `RotationReason`
  + `ActiveTaskId`), suppress `_exitSignal` and let the async proposal arrive; otherwise
  fall back to the legacy `TryStartAutoRotation` path so existing
  `MultiSessionHostTests` keep passing without changes. New `OnAutoRotationProposal`
  writes the same console strings the legacy path did: cap-refused, failed
  (`failure.Message`), task not found, and the `hard-nudge`/`soft-nudge`/`nudge`
  "flush detected" line followed by `RequestSwitch(plan)`. Filter:
  only acts on `proposal.Session.Id == _focused.Id` -- single-focus CLI policy;
  GUI's adapter (Slice 2) will have the opposite policy.
  **Em-dash gotcha:** the literal long-dash character got mojibake'd through the
  Edit tool to `\xd1\x82\xd0\x90\xd0\xa4` bytes; fixed via Python rewrite to use
  `тАФ` (matching the legacy line at MultiSessionHost.cs:533). Any future edit
  that introduces non-ASCII characters in this file should prefer `\u####` escapes.
- `src/InfiniContext.Cli/Program.cs` -- after `SessionManager.OpenAsync` and before
  the while-loop, constructs
  `using var autoRotationService = taskService is null ? null :
    new AutoRotationService(sessionManager, autoRotationGuard,
      (id, ct) => taskService.PrepareSwitchAsync(id, ct), logging.Logger);`
  and passes it into the `MultiSessionHost` ctor as `autoRotationService:
  autoRotationService`. The legacy `autoRotationGuard:` param is still passed (the
  service uses the same guard instance; the host's legacy `TryStartAutoRotation` is
  only reachable when no service is wired, which in production never happens once
  `taskService` is non-null -- matching the legacy preconditions).

### Slice 1 tests landed + mojibake fix (next session)

- **Mojibake repair.** `AutoRotationService.cs:58` carried the same em-dash mojibake
  noted in the prior handoff (cp866-flavored bytes `\xe2\x95\xa4\xd0\x92\xe2\x95\xa8\xd0\xa0\xe2\x95\xa8\xd0\xb4`).
  Replaced with the proper U+2014 em-dash bytes `\xe2\x80\x94` via Python rewrite
  (the Edit tool's roundtrip is what created the mojibake in the first place).
  MultiSessionHost.cs uses the literal em-dash in doc comments and `тАФ` in C# string
  literals -- match that convention going forward.
- **`tests/InfiniContext.Tests/Sessions/AutoRotationServiceTests.cs`** -- 8 cases:
  Hard approved + plan, Soft approved, RotationReason.None no-fire, no ActiveTaskId
  no-fire, guard cap Refused (with `RefusedCap`/`RefusedWindow` populated, prepareSwitch
  not invoked), prepareSwitch throws -> Failed with same exception instance, service
  constructed *after* session still observes exit (covers the `Active` snapshot path
  in the ctor), Dispose detaches handlers. `FakeSession` was copied inline (not hoisted
  to a shared helper -- that's a separate follow-up if anything else needs it).
  Pattern: `SessionManager.CreateAsync` via `QueueingFactory`, then
  `ctx.MarkRotationReason(...)`, then `fake.State.BeginSend(); fake.State.HandleProcessExit(0);`.
  Async proposals signal via `TaskCompletionSource<AutoRotationProposal>`; no-fire
  cases rely on `Task.Yield()` to flush continuations before asserting.
- **Build + tests verified.** Stale CLI processes (PIDs 27324, 26060 -- different
  from the 26104/9636 in the prior handoff but same root cause) still lock
  `src/InfiniContext.Cli/bin/Debug/net10.0/InfiniContext.Core.dll`. Workaround:
  `dotnet build tests/InfiniContext.Tests/InfiniContext.Tests.csproj -p:BuildProjectReferences=false`
  after Core has been built individually. Test results:
  - `AutoRotationServiceTests` (8 cases) -> all pass.
  - `MultiSessionHostTests` (15 cases, including the 4 rotation tests) -> all pass via
    the legacy `TryStartAutoRotation` fallback (host constructed without
    `autoRotationService:` in those tests).
  - `SessionManagerTests` (15 cases) -> all pass; the new `SessionCreated` event has
    no subscribers in those tests so the empty-invocation-list path is exercised.
  - `Tasks.AutoRotationGuardTests` (5) -> green; the guard contract hasn't shifted.
  No format-gate run (the `dotnet format` check needs the full solution build, which
  requires the Cli copy to succeed -- defer to whenever the Cli processes are freed).

### Handoff to next session

**Where I stopped:** Slice 1 is complete end-to-end (production code + tests + build
+ green tests). Slice 2/3/4 untouched.

**What's left:**

1. **Full solution build / format gate.** Once the stale Cli processes are freed,
   run `dotnet build InfiniContext.slnx` + `dotnet format InfiniContext.slnx
   --verify-no-changes`. The new test file follows the established conventions
   (no `\r\n` literals, 4-space indent, `using` declarations in the same order as
   neighbors) so the format gate should pass clean, but CI is the canonical authority.
2. **Slice 2 (Fix 1 GUI wiring)** -- `SessionHost` constructs `AutoRotationService`
   and re-exposes the event. `MainWindowViewModel` subscribes and spawns the new row
   using the proposal's `FirstUserMessage` (reuse the tail of `SpawnSessionForTaskAsync`
   after `PrepareSwitchAsync` -- extract a private helper). On Refused/Failed, update
   `StatusText` rather than triggering exit (no CLI-style RequestSwitch in GUI).
   GUI filter is the *opposite* of the CLI: GUI handles proposals for *any* session
   (all rows visible simultaneously); CLI ignored proposals for non-focused sessions.
3. **Slice 3 (Fix 2)** -- Diagnostic + renderer or binding fix. Defer until 1+2 are
   in place; the pragmatic shortcut is to replace the two `md:MarkdownScrollViewer`
   instances at `MainWindow.axaml:287,298` with `SelectableTextBlock TextWrapping="Wrap"`
   (since the codebase's own style comment at lines 34-43 already flags 12.0.0-a2 as
   alpha-quality).
4. **Slice 4 (conditional Fix 3)** -- Task exclusivity in GUI. Only if reproduction
   shows two concurrent sessions on the same task corrupt each other's reads.

**Cautions for the next session:**

- Don't break `MultiSessionHostTests.Hard_nudge_then_session_exit_auto_rotates_via_SwitchPlan`
  (and its soft / baseline / cap siblings). They run via the legacy fallback path
  because they construct `MultiSessionHost` without an `autoRotationService:` arg.
  The fallback is preserved in `OnStateTransition` exactly so these tests pass
  unchanged.
- The CRLF + Spectre.Console-width + smoke-test-drain pitfalls in `CLAUDE.md` apply
  to any new tests.
- `tests/InfiniContext.Tests/Cli/Ui/MultiSessionHostTests.cs:351` (`FakeSession`) is
  the reusable transport stub. `AutoRotationServiceTests` already has its own inline
  copy; if a third consumer arrives, hoist `FakeSession` to a shared
  `tests/InfiniContext.Tests/_Shared/FakeSession.cs` rather than triple-copying.
- The em-dash in source files is brittle through the Edit tool from this host
  environment. Convention in the codebase: actual U+2014 bytes in XML doc comments
  (e.g. `MultiSessionHost.cs:22`) and `\u2014` escapes in C# string literals
  (`MultiSessionHost.cs:533`). If mojibake patterns like `тАФ`
  or `╤В╨...` appear, fix via Python byte rewrite,
  not via Edit (the roundtrip is what created them in the first place).

**Open question** (not blocking): Whether `MainWindowViewModel` should subscribe to
the `SessionHost.AutoRotationProposed` event or whether `SessionHost` should call
`CreateSessionAsync` directly and just notify the VM via the existing `SessionCreated`
event. FIX-DESIGN.md "Fix 1 - Files / types to change" #3 leans toward the VM owning
the spawn so it can also set focus / status text; keep that split.

## 2026-05-27

### Slice 2 landed (Fix 1 GUI wiring)

- **Resolved the open question above** in favour of "VM owns the spawn." `SessionHost`
  exposes `event Action<AutoRotationProposal>? AutoRotationProposed` as a thin re-publish
  of the underlying `AutoRotationService` event (the field uses `add`/`remove` accessors
  that no-op when the service is null, so the GUI degrades gracefully without
  `TaskService`). `MainWindowViewModel` subscribes; spawn / focus / status-text happens
  in the VM.
- **Pre-flight checks.** Full solution build green, format gate (`dotnet format
  --verify-no-changes`) clean, and the 31 rotation-relevant tests (AutoRotationServiceTests
  + MultiSessionHostTests + SessionManagerTests) pass on `main` before any Slice 2 code
  touched. The "stale Cli processes lock dll" hazard noted in the prior handoff did not
  reproduce this session.
- **`SessionHost.cs`** -- constructs `AutoRotationService` in `StartAsync` with the same
  defaults the CLI uses at `Program.cs:284` (3 rotations / 5-minute window). Disposes it
  before tearing down the `SessionManager`. Field is nullable because the service
  requires a non-null `TaskService.PrepareSwitchAsync` delegate; mirrors the CLI's
  `taskService is null ? null : new AutoRotationService(...)` gate.
- **`MainWindowViewModel.cs`** -- subscribes to `host.AutoRotationProposed` in the
  host-attached ctor, unsubscribes in `DisposeAsync`. New handler
  `OnAutoRotationProposed(AutoRotationProposal)` marshals through `_dispatcher` (the
  service fires on the state-machine thread, same as `EventObserved`) and dispatches by
  status:
  - **Refused** -> `StatusText = "Auto-rotation refused: cap of {N} within {M} min hit on
    task {NNNN}. Spawn manually from the tree."`. No tree mutation.
  - **Failed** -> `StatusText = "Auto-rotation failed for task {NNNN}: {message}"`. No
    tree mutation.
  - **Approved + plan==null** -> `StatusText = "Auto-rotation: task {NNNN} not found ..."`.
  - **Approved + plan** -> calls `_host.CreateSessionAsync(cwd, taskId, plan.FirstUserMessage)`,
    then `AttachRowForContext` (see below). Status text mirrors the CLI's
    "hard-nudge / soft-nudge / nudge flush detected тАФ auto-rotated to task NNNN \"Title\"
    (shortId). drift: ...; warming up..." pattern.
- **`AttachRowForContext(SessionContext, string?)`** -- extracted from
  `SpawnSessionForTaskAsync`'s tail (originally MainWindowViewModel.cs:462-480). Creates
  the `SessionRowViewModel`, hangs it under its `ResolveParentNode(ctx.ActiveTaskId)`,
  focuses it, sets `SelectedNode`, and renders the seed prompt via `row.BeginUserTurn`
  + `ctx.Transcript.Append`. Both spawn paths (manual + auto-rotation) now go through
  this helper so the row appears identically.
- **GUI vs CLI filter contrast.** The CLI's `OnAutoRotationProposal` filters
  `proposal.Session.Id != _focused.Id` (single-focus policy); the GUI's handler has the
  opposite: every row is visible simultaneously, so every qualifying proposal spawns a
  successor regardless of which row the operator is currently reading. This is what the
  earlier handoff anticipated and matches FIX-DESIGN.md's GUI filter section.
- **`MainWindowViewModelTests.cs`** -- 4 new cases covering Refused / Failed /
  Approved-but-task-not-found / dispatcher-marshal-when-off-thread. Approved-with-plan
  is deferred to manual GUI smoke per `plan.md`'s "skip the VM test and rely on the Core
  service tests + manual GUI smoke" escape hatch (testing it requires either a real
  SessionHost or a refactor to inject the CreateSessionAsync callback, neither of which
  pays for itself when the Core service has full coverage already).
- **`InfiniContext.Gui.csproj`** -- added `<InternalsVisibleTo Include="InfiniContext.Tests" />`
  matching the Core project's pattern at `InfiniContext.Core.csproj:22`. The
  `OnAutoRotationProposed` handler is `internal` (event subscriber, not part of the
  public VM surface) and the test asserts against it directly.
- **Build + tests verified.**
  - `dotnet build InfiniContext.slnx` -> green.
  - `dotnet format InfiniContext.slnx --verify-no-changes` -> clean (EXIT=0).
  - `dotnet test InfiniContext.slnx --no-build` -> 1049 passed, 3 skipped (POSIX-only on
    Windows), 0 failed. The full suite (not just rotation tests) ran green.

### Handoff to next session

**Where I stopped:** Slice 1 + Slice 2 complete end-to-end. Slice 3 (Fix 2 markdown
renderer) and Slice 4 (conditional Fix 3) untouched.

**What's left:**

1. **Slice 3 (Fix 2)** -- markdown rendering bug across rebinds. Per
   `_bmad-output/specs/spec-gui-task-session-handoff/FIX-DESIGN.md`, the diagnostic
   call (general renderer bug vs binding-specific) requires running the GUI app
   end-to-end and exercising the auto-rotation path now that it's wired (slice 1+2 land
   the prerequisite). The pragmatic shortcut from `plan.md` Slice 3 is still on the
   table: replace `md:MarkdownScrollViewer` at `Views/MainWindow.axaml:287,298` with
   `SelectableTextBlock TextWrapping="Wrap"` (Markdown.Avalonia 12.0.0-a2 is the only
   suspect named alpha-quality in the codebase's own comments at axaml:34-43). If the
   operator wants markdown preserved, pin to a stable Markdown.Avalonia 12.x once
   whistyun ships one; `SelectableTextBlock` is the smaller reversible change.
2. **Slice 4 (Fix 3, conditional)** -- only if reproduction shows two concurrent sessions
   on the same task corrupt each other's task-doc reads. Code change is minimal: thread
   `TaskActivityCoordinator` into `SessionManager.CreateAsync` so both hosts inherit the
   guard. Skip until evidence demands it.
3. **Suggested next: a manual GUI smoke**. Spin the GUI (`dotnet run --project
   src/InfiniContext.Gui`), open a task-bound session, hammer it until a soft / hard
   nudge fires (or trip the threshold by lowering it in `settings.user.json`), and watch
   the new row appear under the same task header. That's the only path testing the
   Approved-with-plan branch of `OnAutoRotationProposed`. Then ask the operator
   whether they observed the Fix 2 rendering glitch on the new row -- the data point that
   decides 2a (general renderer bug -> swap to SelectableTextBlock or bump Markdown.Avalonia)
   vs 2b (binding-specific -> deeper investigation).

**Cautions for the next session:**

- The auto-rotation event handler is `internal`, not `private` -- the
  `InternalsVisibleTo` shim in `InfiniContext.Gui.csproj` is what lets
  `MainWindowViewModelTests` invoke it directly. Don't promote it back to `private`
  without first refactoring the tests.
- `AttachRowForContext` is now shared between `SpawnSessionForTaskAsync` and the
  auto-rotation path. Any future change to row-attachment ordering (focus, expand, seed
  prompt) MUST be made in the helper -- splitting the two paths would re-introduce the
  drift we just removed.
- The GUI's filter policy is "spawn for any proposal, regardless of focus." If a future
  refactor adds focus-aware filtering, double-check it doesn't accidentally suppress
  auto-rotation for a non-focused pane -- that's the CLI's policy, not the GUI's.
- The `AutoRotationProposed` event re-publish on `SessionHost` is intentionally
  null-safe via `add`/`remove` accessors. If the task subsystem is unavailable
  (`taskService is null` -> `_autoRotationService is null`), subscribing is a no-op
  rather than throwing. The GUI degrades to "manual /task switch only" in that branch
  with no crash surface.
- CLAUDE.md's CRLF + Spectre.Console-width + smoke-test-drain pitfalls still apply to
  any new tests touching CLI-shaped paths. The new VM tests don't touch any of those.
