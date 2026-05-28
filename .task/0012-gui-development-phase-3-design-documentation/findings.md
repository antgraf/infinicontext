# Findings and Ideas

## 1. Why & What Happened
It's a long term goal. Make sure that operator can easily understand what and why happened in the task. Was some event issues that triggered an operation? Did agent decide to do a task? For now it's abstract but it will need more details and refinment later.

## 2. Reduce context thresholds
For 1M models spft and hard context size thresholds should be lowered to 45% anf 65% accordingly. Leave current thresholds for smaller context models.

## 3. Allow rm om project files
Make sure that any files and folders operations are allowed inside of home cwd folder. It may be fixed already.

## 4. Support dark theme for permission banner
Currently permission banner is always light-yellow collor. However in dark theme the font is white which is mostly invisible on light-yellow background. It may be fixed already.

## 5. New task button
Make "+ New task" button functional.

## 6. Store main window size in settings [SHIPPED 2026-05-24; close-while-maximized follow-up SHIPPED 2026-05-25]
Store  main window state (windowed / maximized) in gui settings json.

**Follow-up (2026-05-25):** the original 2026-05-24 slice documented a known limit -- closing the GUI while maximized persisted screen dimensions instead of the pre-maximize windowed size, because the two-way `Window.Width`/`Height` binding had been pushing screen dims into the VM throughout the maximized session. Fixed in this session: `MainWindow.OnClosed` snapshots the last `WindowState == Normal` Width/Height in `_lastNormalWidth`/`_lastNormalHeight` (captured on `Width/HeightProperty` changes while Normal, seeded on `DataContextChanged` from the persisted VM values), and restores them into the VM before `SaveLayoutAsync` runs. Re-launch + un-maximize now restores to the right size. See `progress.md` 2026-05-25 entry for details.

## 7. Improve message enqueing [SHIPPED 2026-05-25]
Currently if session is processing (sending / receiving data to / from agent) operator's messages are queued. However there is no any indicator of such messages. So operator send the same message several times confused. Also, need to check that such messages are sent automatically when user input is requested. I have an impression that they are not sent until any new user message is sent. Also, if several messages are enqued they are processed separately (at least this is how they are represented in UI). They must be just glued together and sent and processed as a single message.

**Investigation result (2026-05-24):** auto-flush is NOT broken - `ClaudeSession.SendUserMessageAsync` already writes+flushes under `_writeLock`; claude buffers FIFO on its side. The "not sent until new user message" perception is incorrect.

**Shipped 2026-05-25:** glue feature landed in `SessionRowViewModel`. New `_pendingPrompts` + `_pendingBundleTurn` host-side buffer + `[ObservableProperty] QueuedPromptCount` + `HasQueuedPrompts`. New public `SendOrQueueAsync(text)` decides Idle (immediate write) vs Glue (buffer). Flush hook in `CloseInFlightTurnWith` writes ONE combined `SendUserMessageAsync(joined)` on next `ResultEvent`. Exited branch in `OnStateTransition` flushes the bundle with a "(not sent - session exited with N prompt(s) buffered)" marker. `MainWindowViewModel.SendToSessionAsync` now thin wrapper around `SendOrQueueAsync`. Row template gets a small `+N` badge between StatusGlyph and ShortId; status-strip "Agent busy" badge widens to "Agent busy - N message(s) queued, will glue on Idle" via the new `AgentBusyHint` derived property. `FakeClaudeSession` updated to mirror real `ClaudeSession`'s `_state.BeginSend()` so tests see realistic `IsAgentTurnInFlight` transitions.

Tests: 6 new `SessionRowViewModelTests` cover idle-sends-immediately, inflight-buffers, multi-buffered-join, result-flushes-bundle, exit-flushes-with-marker, property-change-fires. Existing FIFO `_queuedTurns` tests (`Operator_send_during_inflight_turn_is_queued` etc.) still pass - the legacy `BeginUserTurn` primitive is preserved for the seed-pointer path. **Full suite: 1036 passed / 0 failed / 3 skipped (POSIX-only).** `dotnet format --verify-no-changes` clean.

## 8. Task archiving
Need to introduce a way to archive or "complete" tasks. Tree view should have two root nodes "Active" - uncollapsed by default, "Archived" - collapsed by default. Also, such an operation need to perform some task completion operations. For istance, check if there any deffered or posrponed tasks, reported issues. And propose actions on them. Ususally saving them in todo list or issue-tracker.

## 9. Task tree view improvements
Currrently there is no way to collapse / uncollapse tree nodes. Need this ability.

## 10. Incorrect symbols in CLI auto-mode output [SHIPPED 2026-05-24 - 62 mojibake sequences across 28 files swept; see progress.md]
CLI auto-mode verdict lines render mojibake. Example: `[approval] Edit ╨▓тФ╝' ALLOW (2440ms)`. The three chars between `Edit` and `ALLOW` (`╨▓`, `тФ╝`, `'`) are a CP1251 misread of UTF-8 bytes `E2 86 92` = U+2192 RIGHTWARDS ARROW (`тЖТ`). Same root cause as the GUI fix in slice 4c: C# source files without a UTF-8 BOM are parsed by the C# compiler under the system's ANSI locale (win-1251), so any non-ASCII literal in source gets corrupted at compile time. Fix shape: scan `src/InfiniContext.Cli/` for non-ASCII literals (arrow symbols, em-dashes, etc.), replace with `char.ConvertFromUtf32(0xNNNN)` runtime construction OR add a UTF-8 BOM to the affected source files. The arrow used in `ConsoleEchoingApprovalSink`'s verdict formatter is the obvious starting point.

## 11. Permission banner: "Deny" link still white-on-light-yellow [SHIPPED 2026-05-25]
Follow-up to finding #4 (dark-theme banner support). The Deny button used to inherit the dark Fluent default-button styling (transparent bg, white fg), invisible on the light-yellow banner. Slice 4k's claim that "Buttons unaffected: Fluent's default and accent button styles render opaque backgrounds that override the banner color" was wrong about the default button in dark Fluent.

Fix: explicit `Background="#FFE8C770"` + `Foreground="#FF6B4E00"` + `BorderBrush="#FFD9B441"` on the Deny `Button` in `MainWindow.axaml`. Reads as a flat amber button alongside the accent (blue) Approve. Theme-agnostic.

Build verified: XAML compile succeeded; bin-copy step was blocked by the operator's running GUI instances holding `InfiniContext.Core.dll` open. Visible to operator on next GUI restart.

## 12. Permission banner sits inside scrollable transcript, not "on top" [VERIFIED 2026-05-25 - stale binary, no code change]
Current XAML (`MainWindow.axaml:208`) has the banner at `DockPanel.Dock="Top"` on the central column's outer DockPanel, OUTSIDE the ScrollViewer at line 259. Per the docked-children-before-fill rule, the banner is sticky at the top regardless of transcript scroll.

Operator re-tested against a fresh build on 2026-05-25 and confirmed: the banner is shown on top of the scrollable area as intended. Original report was against a stale binary (pre-slice 4i, commit 9f53d80, which moved the banner out of the ItemsControl). No code change required.

## 13. Agent access to harness settings (e.g. switch approval mode) [MOVED to docs/todo.md - separate ticket]
Today the agent cannot inspect or change host/harness settings - approval mode, model selection, thresholds, etc. - despite being the entity that most often discovers it needs to. Example: an agent doing an extended autonomous loop knows it would like to flip to `auto` approval to avoid stalling, but has no surface to do so. Operator flagged this as a larger feature deserving its own ticket; full design (security boundary, scope of settable surface, audit trail, UI surfacing of agent-initiated changes) lives in `docs/todo.md` under its own item.

## 14. Slash commands not wired in GUI input
The CLI (`MultiSessionHost`) parses `/n`, `/focus`, `/next`, `/prev`, `/broadcast`, `/approval`, `/task ...` (incl. `/task switch`, `/task review`) etc. from its input loop in `Program.cs`. The GUI input box (`MainWindowViewModel.SendToSessionAsync`) routes whatever the operator types straight to `SessionRowViewModel.SendOrQueueAsync` -- there is no `/`-prefix dispatcher, so `/task review` is sent to claude as a chat message instead of being intercepted by the host.

Fix shape: in `MainWindowViewModel.SendToSessionAsync` (or a new `TryHandleSlashCommandAsync` helper), short-circuit on `text.StartsWith("/")` and dispatch to the same command handlers the CLI uses. Cleanest factoring: promote `MultiSessionHost`'s command parser into a shared component in `InfiniContext.Core` (see [[feedback_no_cli_code_duplication]]) -- both the CLI and GUI consume it. Bounded but non-trivial because the CLI parser is tangled with `Spectre.Console` rendering today; the shared component would need to return structured `CommandResult` records that the GUI can render its own way.

Out of scope for current task 0012; logging as a new finding so it doesn't get lost. Candidate for its own slice (or a fresh task) given the refactor scope.
