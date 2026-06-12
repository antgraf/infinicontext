# Progress

## 2026-06-12 (session 7: "Send always disabled" root-caused — keyboard focus, NOT the detach fix)

Operator reported: "Fix for Send button completely broke it. It is always disabled for all sessions
even if all sessions are idle." Investigated to root cause, fixed, and verified end-to-end at the
pixel level. (Note: edits made without the `tasks/lock` channel — this session runs outside the
InfiniContext host, so the MCP lock tools were not available.)

**The detach fix is innocent.** Verified the operator's exact Debug binary (built 22:56:38, contains
`HostCommandRunning`) against FakeClaude AND real claude via a UIA driver (reusing the
`tmp/icx-smoke/` harness from task 0016): Send enables on typing when idle, during an in-flight
turn (glue path queues), during a 90s seed turn, after markdown rendering (0019 SelectionEnabled),
with two sessions and focus switches. All pass. The operator's `gui-20260611-225705.log` confirms a
healthy app: both task sessions reached `isReady=true isAlive=true`, `queued=0` at every result,
and ZERO operator `tx:` lines — no send and no glue-queue ever fired in their whole 4-minute run.

**Real root cause (reproduced, then fixed): keyboard focus never reaches the prompt box.** Spawning
("+") or selecting a session happens by clicking in the TreeView; keyboard focus then sits on the
Window/TreeItem. Typing goes nowhere — UIA proof: `kbFocus=Window`, `box.text=''` after blind
typing. PromptText stays empty, so `CanSendPrompt` (correctly gated on non-empty text) is false and
the Send button reads as "always disabled, for every session, even idle" — the exact complaint.

**Fix (3 small pieces, all view-layer):**
1. `MainWindow.axaml.cs` `OnVmPropertyChanged`: on `FocusedSession` change → `FocusPromptBoxDeferred()`
   (post at Input priority so it runs after the click's own focus handling).
2. `MainWindow.axaml.cs` ctor: `PointerReleased` (handledEventsToo) on the now-named `SessionTree`
   → refocus the box. Covers re-clicking the already-selected row, which raises no PropertyChanged.
3. `MainWindow.axaml.cs` ctor: Click on the now-named `SendButton` → refocus the box so a
   mouse-click send doesn't strand focus on the button. (`MainWindow.axaml`: added the two Names.)

**Diagnostics added (MainWindowViewModel):** transition-only Debug log
`gui-main: prompt-text len=.. canSend=.. focused=.. isReady=.. isAlive=..` in OnPromptTextChanged
(decisive datum for any future "Send disabled" report: if these lines never appear, typing never
reached the VM) and `gui-main: operator send target=.. len=.. sentNow=.. queued=..` after each send.

**Verification (UIA, Release + Debug rebuilt):** spawn via tree click → blind typing lands in the
box, Send enables → mouse-click Send transmits (tx in log) → focus returns to the box → second blind
typing enables again → re-click the selected row → blind typing still lands in the box. Screenshot:
`tmp/icx-smoke/0017-acceptance.png`. Gate: build 0 warn/0 err (Debug + Release), full suite
**1103 passed / 0 failed / 3 skipped**, `dotnet format --verify-no-changes` exit 0.

**Caveat for future GUI smoke authors:** synthesized clicks (SetCursorPos + mouse_event) on Avalonia
buttons are flaky if the cursor parks on the button between attempts (tooltip/hover interference) —
move the cursor off after clicking, or use UIA InvokePattern. This cost an hour of false leads; the
flakiness does NOT affect real mice (the operator's tree clicks always processed).

Repro/verify scripts: `tmp/icx-smoke/launch-0017.ps1`, `repro-0017-*.ps1`, `verify-0017-final3.ps1`
(gitignored). New fix files join the 0017 uncommitted set: `MainWindow.axaml` (two Name attributes),
`MainWindow.axaml.cs`, `MainWindowViewModel.cs`. Remaining: operator re-test, then commit.

## 2026-06-12 (session 6: re-verified green; tree now cleanly scoped to 0017)

- Re-ran the full gate in Release (2 operator GUIs PIDs 9460/18092 lock the Debug exe): build
  **0 warn / 0 err**; full suite **1103 passed / 0 failed / 3 skipped**; `dotnet format
  --verify-no-changes` exit 0. No code changes this session.
- **Working tree is now cleanly scoped to 0017.** The unrelated 0018/0019/Core-config changes that
  sessions 4-5 warned about have since been committed (e8d0eb6 task 0018, 28ec307 task 0019), so the
  `git add -A` hazard is gone. Current uncommitted set is exactly the 0017 fix files: Core
  `LightModel/CliLightModelInvocation.cs`, `Review/ReportRenderer.cs`; GUI `MainWindowViewModel.cs`,
  `SessionRowViewModel.cs`, `Views/MainWindow.axaml.cs`, new `Services/ConfirmTextReader.cs`,
  `Views/ConfirmDialog.axaml`/`.cs`; tests `Gui/SessionRowViewModelTests.cs`,
  `Gui/ConfirmTextReaderTests.cs`, `LightModel/CliLightModelInvocationTests.cs`,
  `Review/ReportRendererTests.cs`; plus task artifacts `index.md`, `progress.md`, and the (stale but
  valid audit-trail) `review-2026-06-12T020943.md`.
- Re-read the Finding-B fix end to end: `TryConsumeTurnLine` accumulates assistant text across every
  event, sets `sawAssistant` on any assistant event, breaks on `result`, preserves the `is_error`
  throw. Sound and covered.
- **Operator-gated remaining items (unchanged):** (1) manual GUI re-test against REAL claude to
  confirm reviewers now yield real findings; (2) commit the 0017 set. Asked the operator how to
  proceed re: commit; left uncommitted pending their call.

## 2026-06-12 (session 5: operator re-test вЂ” two new findings, one fixed)

Operator ran `/task review` in the GUI, confirmed the **confirm dialog now appears** (session-4
fix works) and clicked OK. Reported two problems + a screenshot. Investigated both to root cause.

**Finding A вЂ” Send button disabled for ALL sessions until the command ends (REAL BUG, FIXED).**
- Root cause: `SendPromptCommand` is a single window-level `AsyncRelayCommand`. The slash path
  `SendToSessionAsync в†’ await TryHandleSlashCommandAsync в†’ await cmd.ExecuteAsync` awaited the whole
  `/task review` (6 agents, minutes) *inside* the command's task, so `AsyncRelayCommand.IsRunning`
  stayed true and disabled the one Send button for the entire window вЂ” every session, not just the
  one running the review.
- Fix: split the handler into a **synchronous** `TryHandleSlashCommand` (recognise + dispatch) and a
  **detached** `RunHostSlashCommandAsync` (fire-and-forget, catches its own exceptions). Recognition
  is still synchronous so unknown commands still forward to claude. Added a per-row
  `SessionRowViewModel.HostCommandRunning` guard (the card uses single `_hostCommandTurn`/
  `_hostCommandContent` fields, so a 2nd concurrent command on the same row would clobber the card);
  the dispatcher rejects re-entry with a status message. Files: `MainWindowViewModel.cs`,
  `SessionRowViewModel.cs`, new test in `SessionRowViewModelTests.cs`.

> **CORRECTION (appended after operator pushback) - the Finding B "FakeClaude / CLAUDE_EXE" root
> cause below is WRONG.** It was drawn from `logs/gui-20260611-202849.log`, which ends at 20:35 and
> references `tmp\icx-smoke\task-home` -> a GUI smoke-TEST log, not the operator's interactive GUI.
> Operator confirmed `set CLAUDE_EXE` -> not defined, and settings.json has no executable override,
> so the real GUI spawns real `claude`. **Finding B's true cause is still UNKNOWN.** I could not find
> the operator's actual review-run log (no log modified in the 22:05-22:20 window holds
> `cli-light-model:` / `review: reviewer` lines). Needed: the log file shown in the GUI bottom strip
> (`MainWindow.axaml:147`); at Debug level it already records `cli-light-model: spawn exe=<path>
> model=<id>` and `review: reviewer <Role> failed <exception>` = the real exe + error. Also a
> confirmed in-scope diagnostic gap: `ReportRenderer.AppendAwaitingFinding` renders `Why ?? Summary`,
> so the synthesized failure finding's `Summary` (which carries the real error) is dropped from the
> report - proposed fix, not yet applied.

> **FINDING B - TRUE ROOT CAUSE FOUND & FIXED.** Operator supplied the real GUI log
> (`src/InfiniContext.Gui/bin/Debug/net10.0/logs/gui-20260611-220204.log`). It shows the 5 reviewers
> spawned **real `claude`** at 22:06:25 with `model=claude-opus-4-8 thinking=true maxOutputTokens=32768`
> (cwd `~/.infinicontext/review-cwd`), ran ~3 min, then `[WRN] review: every reviewer failed` at
> 22:09:43 - with **no** per-reviewer `failed` warning, **no** stderr, **no** `is_error`. So none threw;
> all returned empty text.
> - **Mechanism:** `CliLightModelChild.RunTurnAsync` captured only the FIRST assistant event
>   (`assistantText is null` guard) and `ExtractAssistantText` skips `thinking` blocks. With thinking
>   ON, `claude` emits thinking as its OWN assistant event (confirmed via `tests/fixtures/nested-tool.jsonl`:
>   `content:[{type:thinking}]`, zero events carry both thinking+text) BEFORE the answer event. So the
>   first (thinking-only) event yielded `""`, the guard then SKIPPED the real answer -> empty -> parse
>   fail -> "(no output)" for all 5. The light-model approval path was immune because it runs
>   `thinking=false` (logs `single-shot turn ok` every time in the same log). Reviewers default
>   `ReviewerAgentOptions.ThinkingEnabled=true`.
> - **Fix (Core, affects light-model + review):** `RunTurnAsync` now ACCUMULATES assistant text across
>   every event until `result` (extracted to testable `CliLightModelChild.TryConsumeTurnLine`; still
>   breaks on `result`, required for the long-lived reused child). 4 regression tests in
>   `CliLightModelInvocationTests` (thinking-then-text survives, multi-text concatenates, error result
>   throws, completion only on result). Non-thinking callers unchanged (single text event accumulates
>   identically).
> - This is the actual reason `/task review` "did nothing". The earlier rendering + confirm-dialog +
>   Send-button fixes were all real but were upstream symptoms/separate bugs; THIS is why the review
>   produced no findings.

## 2026-06-12 (session 5 cont'd: full green after Finding-B fix)

- Files now in the 0017 fix set (all uncommitted): GUI - `ConfirmTextReader.cs`, `ConfirmDialog.axaml`/`.cs`,
  `MainWindowViewModel.cs`, `MainWindow.axaml.cs`, `SessionRowViewModel.cs`; Core -
  `LightModel/CliLightModelInvocation.cs`; tests - `ConfirmTextReaderTests.cs`,
  `SessionRowViewModelTests.cs`, `LightModel/CliLightModelInvocationTests.cs`; plus `progress.md`.
  (Still must NOT sweep the unrelated 0018/0019/Core-config changes in the mixed tree.)
- Green (Release): build 0/0; full suite **1101 passed / 0 failed / 3 skipped** (+4 new); `dotnet
  format --verify-no-changes` exit 0.
- Pending operator re-test with the rebuilt GUI: run `/task review 0017` and confirm reviewers now
  produce real findings (not "(no output)"). Then commit the 0017-scoped set.

- **ReportRenderer diagnostic fix applied (operator said "fix").** `AppendAwaitingFinding` now renders
  a `Detail:` line carrying `Summary` when it differs from `Why`, so a synthesized reviewer-failure
  finding's real error (held in `Summary`) is no longer dropped. Guarded against double-print when
  `Why` already fell back to `Summary`, and against duplication when the two are equal. +2 tests in
  `ReportRendererTests` (surfaces-detail-when-differs, no-duplicate-when-equal). Adds
  `Review/ReportRenderer.cs` + `Review/ReportRendererTests.cs` to the 0017 fix set.
- Green (Release): build 0/0; full suite **1103 passed / 0 failed / 3 skipped** (+2 new); `dotnet
  format --verify-no-changes` exit 0.

**Finding B вЂ” "still no command output" / review produced nothing useful (NOT a code bug вЂ” env).**
- The card DID render output (screenshot shows scope + "running 5 reviewersвЂ¦" + "done."). A report
  *was* written: `review-2026-06-12T020943.md`. But **every reviewer + the coordinator = "(no
  output)"** в†’ 5 synthesized "reviewer did not produce a usable report" findings.
- Root cause from logs (`logs/gui-20260611-202849.log:6`):
  `cli-light-model: spawn exe=вЂ¦\tests\FakeClaude\bin\Release\net10.0\FakeClaude.exe`. The GUI was
  launched with **`CLAUDE_EXE` pointing at FakeClaude** (`ClaudeArgvBuilder.ResolveExecutable` honors
  that env var, process-wide). The reviewer agents (`CliReviewerAgentService` в†’ `CliLightModelChild`)
  therefore also spawn FakeClaude, invoked *without* `--replay`/`--script` в†’ it can't perform a review
  в†’ empty output for all six. **A real review needs the GUI launched against real `claude` (unset
  `CLAUDE_EXE`).** No code change warranted; this is a test/launch-config artifact.
  - Possible follow-up (logged for the operator's call, not done): when *all* reviewers produce no
    output, the handler could print a clearer "all reviewers empty вЂ” check that 'claude' is available"
    line instead of a terse "done." Consider for a future slice.

**Green this session (Release config вЂ” Debug exe still locked by 4 running GUIs):** build 0/0; GUI
tests **63 passed**; full suite **1097 passed / 0 failed / 3 skipped** (+1 new test); `dotnet format
--verify-no-changes` exit 0.

**Still NOT committed.** Same scoping caveat as session 4 (mixed working tree). The session-5 fix
adds `SessionRowViewModel.cs` to the 0017 file set. Pending: operator re-test with **real claude**
(unset `CLAUDE_EXE`) to confirm (a) Send stays enabled on other sessions during a review, and (b) a
real review now yields actual findings; then commit the 0017-scoped set.

## 2026-06-12 (session 4: re-verified green, fix unchanged)

- Re-read session 3's confirm-dialog fix end to end; all pieces are present and consistent in the
  working tree (`ConfirmTextReader`, `ConfirmDialog.axaml/.cs`, `MainWindowViewModel.BuildConfirmReader`
  + `ConfirmReview` callback, `MainWindow.axaml.cs` wiring, and `SessionHost.CreateTaskCommandHandler`
  already takes the `TextReader? input` param the VM now passes).
- **Green (Release config вЂ” 4 operator GUIs PIDs 19936/30684/5988/26756 still lock the Debug exe):**
  `dotnet build -c Release` 0 warn / 0 err; targeted tests (ConfirmTextReader + SessionRowViewModel +
  MainWindowViewModelSlash) **37 passed**; full suite **1096 passed / 0 failed / 3 skipped**;
  `dotnet format --verify-no-changes` exit 0.
- **Still NOT committed** and two flags for the operator:
  1. **Manual GUI verification pending** вЂ” needs operator at the keyboard: run `/task review 0017`,
     confirm the modal appears, click **Run**, confirm reviewers run and a `review-*.md` lands.
  2. **Working tree is mixed across tasks.** Besides the 0017 files, the tree carries unrelated work
     (tasks 0018/0019 folders, Core Configuration/Sessions/KnownModels/ContextPressureMonitor changes,
     `docs/architecture-review-2026-06-11.md`, and a `SelectionEnabled="True"` markdown tweak in
     `MainWindow.axaml` that belongs to task 0019's copy-limitation work). A commit must be scoped to
     the 0017 files only вЂ” do NOT `git add -A`.
- **0017 file set to stage when committing:** `src/InfiniContext.Gui/Services/ConfirmTextReader.cs`,
  `src/InfiniContext.Gui/Views/ConfirmDialog.axaml`, `src/InfiniContext.Gui/Views/ConfirmDialog.axaml.cs`,
  `src/InfiniContext.Gui/ViewModels/MainWindowViewModel.cs`, `src/InfiniContext.Gui/Views/MainWindow.axaml.cs`,
  `tests/InfiniContext.Tests/Gui/ConfirmTextReaderTests.cs`, and this `progress.md`. (The `MainWindow.axaml`
  `SelectionEnabled` edit is 0019's, not 0017's вЂ” leave it for that task.)

## 2026-06-12 (session 3: confirmation gap)

- **Operator re-tested in the GUI and shared a screenshot.** Good news: the original rendering fix
  WORKS вЂ” `/task review` output now renders as a card (Operator block "/task review" + Details child
  showing `resolving scope...` and `scope: git diff 2f678b4..de1bd027 (39 files, 4 excluded)`).
- **But the review still doesn't run.** It stops right after resolving scope. Root cause confirmed
  from code, not guesswork:
  - `SessionHost.CreateTaskCommandHandler` (src/InfiniContext.Gui/Services/SessionHost.cs:260) was
    called with `input: null` (the default) from `MainWindowViewModel.BuildSlashContext`. So the
    handler's `_input` is **always null** in the GUI.
  - `TaskCommandHandler.cs:322-332`: with a non-empty scope and no `--yes`, the cost guard needs to
    read a confirmation line; `_input is null` в†’ it writes "no input reader available; pass --yes"
    and **returns without running the review**. This was a documented known-limit (task 0015 progress:
    "`/task review` requires `--yes` вЂ¦ the GUI leaves [the reader] null; renderer prompt dialogs are
    a follow-up").
  - Confirmed via evidence: **no `review-*.md` produced**, and **no agent-spawn activity in the GUI
    logs** (only "tasks: subsystem ready; review: ready" at startup) в†’ `RunReviewAsync` was never
    reached. The operator almost certainly typed `/task review` without `--yes`.
- **Fix (finish the deferred dialog wiring вЂ” keeps confirmation single-sourced in Core):**
  1. `src/InfiniContext.Gui/Services/ConfirmTextReader.cs` (NEW) вЂ” a `TextReader` whose
     `ReadLineAsync` invokes an async predicate and returns `"y"`/`"n"`. Adapts the Core handler's
     stdin-confirmation contract to a GUI modal. (internal; `InternalsVisibleTo InfiniContext.Tests`.)
  2. `src/InfiniContext.Gui/Views/ConfirmDialog.axaml` + `.axaml.cs` (NEW) вЂ” minimal modal yes/no
     mirroring `NewTaskDialog`; `ShowAsync(parent)` в†’ `bool` (Run=true, Cancel/close=false).
  3. `MainWindowViewModel`: new `ConfirmReview` callback property + `ReviewConfirmMessage` const +
     `BuildConfirmReader()` (marshals the modal onto the UI thread via `IUiDispatcher.InvokeAsync`,
     since the handler's `ConfigureAwait(false)` can land the read on a thread-pool thread). Passes
     the reader into `CreateTaskCommandHandler(writer, BuildConfirmReader())`. Null callback (tests)
     answers "no".
  4. `Views/MainWindow.axaml.cs`: wires `ConfirmReview` to host `ConfirmDialog` (same View-hosts-
     dialog pattern as `NewTaskPrompt`).
- **Green this session (Release config вЂ” 2 operator GUIs PIDs 27804/30580 still lock the Debug exe):**
  build 0/0; full suite **1096 passed / 0 failed / 3 skipped**; new `ConfirmTextReaderTests` (3)
  pass; `dotnet format --verify-no-changes` exit 0.
- **NOT yet committed.** Remaining: operator must close the running GUI instances, rebuild, and
  manually verify вЂ” run `/task review 0017`, confirm a modal appears, click **Run**, and confirm the
  reviewers run and a `review-*.md` lands. Then commit.
- (If the operator DID pass `--yes` and it still stopped, that's a different/deeper bug вЂ” but the
  no-report + no-agent-logs evidence points squarely at the confirm-gate bail.)

## 2026-06-12 (session 2)

- **Fix is already committed** (not just in the working tree as the prior entry below assumed).
  `git log -S BeginHostCommandTurn` shows the `SessionRowViewModel`/`MainWindowViewModel` changes +
  regression tests landed in commit **b9f7d95** ("Task 0016: GUI per-session model selection").
  Working tree is clean; nothing left to commit for the code fix.
- Re-verified green this session: `dotnet build -c Release` в†’ 0 warnings / 0 errors;
  `dotnet test -c Release --no-build --filter SessionRowViewModelTests|MainWindowViewModelSlashTests`
  в†’ **34 passed, 0 failed** (incl. `HostCommand_output_lands_in_the_visible_Turns_surface` and
  `..._shows_a_marker_not_an_empty_stub`). Used Release config because 2 operator GUI instances
  (PIDs 27804/30580) hold the Debug exe lock вЂ” same workaround as before, operator's apps untouched.
- **Only remaining item:** interactive manual GUI verification (spawn a session, run `/task review
  0017 --yes`, confirm output renders as a card in the central pane). This requires the operator at
  the keyboard вЂ” recommend the operator do this check, or run `/task review` to close the task.

- Task created.
- Re-verified the fix is present in the working tree (SessionRowViewModel `BeginHostCommandTurn`/
  `AppendHostCommandOutput`/`EndHostCommandTurn`; MainWindowViewModel slash dispatch now routes
  renderer+writer to `target.AppendHostCommandOutput` and brackets with Begin/End). `dotnet build
  InfiniContext.slnx` re-run: **C# compiles clean (0 CS errors)**; build fails only at the apphost
  copy step (MSB3027/MSB3021) вЂ” still the 3 running GUI instances (PIDs 29072/4404/25844) holding
  `InfiniContext.Gui.exe`. Same blocker as the prior session. Did not kill operator's apps; operator
  declined to confirm closing them this session. **Test run still pending on closing the GUI.**
- **Unblocked via Release config** (operator suggestion): `bin/Release` is a separate output path, so
  the Debug-exe lock from the running GUIs doesn't touch it. Results:
  - `dotnet build -c Release` РІР‚вЂќ **Build succeeded, 0 warnings, 0 errors** (all 6 projects).
  - `dotnet test -c Release --no-build` (full suite) РІР‚вЂќ **1093 passed, 0 failed, 3 skipped** (the 3
    skips are POSIX supervisor tests, expected on Windows). New regression tests
    `HostCommand_output_lands_in_the_visible_Turns_surface` and `..._shows_a_marker_not_an_empty_stub`
    pass.
  - `dotnet format --verify-no-changes` РІР‚вЂќ clean, no formatting changes needed.
- **Remaining:** manual GUI verification (spawn a session, run `/task review 0017 --yes`, confirm output
  renders as a card in the central pane), then commit. Code fix is otherwise complete and green.

## 2026-06-11

### Root cause found
`/task review` (and **every** host slash-command) ran fine but its output was invisible in
the GUI; the operator saw only the status-strip `"/task done."` line and concluded it "did
nothing".

- GUI slash dispatch: `MainWindowViewModel.TryHandleSlashCommandAsync` (src/InfiniContext.Gui/ViewModels/MainWindowViewModel.cs:699)
  routed command output (renderer + `TranscriptTextWriter`) into `target.Context.Transcript.Append(...)`.
- `Context.Transcript` feeds `TranscriptViewModel.Fragments` вЂ” but **nothing in `MainWindow.axaml` binds Fragments**.
  Since task 0016, the central pane is an `ItemsControl` bound to `SessionRowViewModel.Turns`
  (built lazily in `MainWindow.axaml.cs` `ShowTranscript`). So fragment output is dead/unrendered.
- Status `"/task done."` is set at MainWindowViewModel.cs:727 on no-exception completion вЂ” exactly the reported symptom.
- (Bonus: in the GUI, `/task review` without `--yes` also can't confirm вЂ” `_input` is null, handler writes
  "no input reader available; pass --yes". Now at least that message is visible. A real confirm dialog is a
  separate follow-up, not in scope here.)

### Fix implemented (compiles clean; tests NOT yet run вЂ” see blocker)
1. `SessionRowViewModel` (src/InfiniContext.Gui/ViewModels/SessionRowViewModel.cs, after `BeginUserTurn`):
   added `BeginHostCommandTurn(commandLine)` / `AppendHostCommandOutput(text)` / `EndHostCommandTurn()`.
   They create a card in the **visible Turns surface**: operator block = typed command, output
   accumulates into an expanded System child (monospace, selectable). `(no output)` marker when empty.
   All three marshal to the UI thread internally (same pattern as `OnEvent`).
2. `MainWindowViewModel.TryHandleSlashCommandAsync`: renderer + writer now target
   `target.AppendHostCommandOutput`; `BeginHostCommandTurn(text)` before `ExecuteAsync`,
   `EndHostCommandTurn()` in a `finally`. Removed the old `Append`в†’`Context.Transcript` closure.
3. Regression tests added to tests/InfiniContext.Tests/Gui/SessionRowViewModelTests.cs:
   `HostCommand_output_lands_in_the_visible_Turns_surface` and `..._shows_a_marker_not_an_empty_stub`.

### BLOCKER for next session
- Build/test could not be completed: **3 GUI instances are running** (PIDs 29072/4404/28828) and hold a
  lock on `src/InfiniContext.Gui/bin/Debug/net10.0/InfiniContext.Gui.exe`, so `dotnet build`/`dotnet test`
  fail with MSB3027/MSB3021 on the apphost copy. C# compilation itself succeeded (Core + GUI .dll built,
  zero CS errors). I deliberately did NOT kill the operator's running app.

### Handoff to next session
1. Close the running GUI instances, then `dotnet test InfiniContext.slnx` (run at least the new
   `SessionRowViewModelTests` + existing `MainWindowViewModelSlashTests`, which use the host-less VM ctor
   and short-circuit before the new code so they should be unaffected).
2. `dotnet format InfiniContext.slnx` before pushing (CI format gate is canonical).
3. Manually verify in the GUI: spawn a session, run `/task review 0017 --yes` (or any task id) and confirm
   the scope/output lines now render in the central pane as a card, not just the status strip.
4. Then the fix is ready for `/task review` / commit.

## 2026-06-12 (review)

- Ran `/task review`. applied=0 rejected=0 awaiting=5. Report: `review-2026-06-12T020943.md`. Build/test gate: green.
