# Progress

## 2026-06-11 (session: reconcile Issue 3 vs task 0016)

- **Issue 3 (GUI blank transcript) is SUPERSEDED by task 0016** ("gui-multi-session-transcript-
  rendering-bugs"). The narrow `RebuildTranscript`-rebuilds-a-fresh-`ItemsControl` fix described in
  the earlier 2026-06-11 entry below turned out to be insufficient: detaching realized
  Markdown.Avalonia visuals on every focus switch tripped upstream bug Markdown.Avalonia#183 (a2's
  converter publishes a raw `Color` where an `IBrush` is expected mid-detach), producing corrupted
  renders, a blank second-session pane, and an `InvalidCastException` *inside* `RebuildTranscript`.
- Task 0016 owns the current uncommitted working-tree GUI changes (`MainWindow.axaml`/`.axaml.cs`,
  `MainWindowViewModel.cs`, `InfiniContext.Gui.csproj`): cached one-`ItemsControl`-per-session that
  is never detached while the session lives, each wrapped in a `ThemeVariantScope` detach firewall,
  plus Markdown.Avalonia a2 -> a3. See `.task/0016-.../progress.md` + `decisions.md`.
- **Net for task 0015:** Issues 1 (Opus 4.8 model upgrade + centralization) and 2 (CLI nudge
  auto-rotation hang) are committed in `44c6333`. Issue 3's GUI rendering work has moved to task
  0016; nothing further to do for it under 0015. Task 0015 is code-complete pending an optional
  `/task review`.

## 2026-06-11 (session: re-verify in-tree state)

- Re-confirmed the in-tree state for Issue 3 (GUI blank-transcript fix): Release build clean,
  full suite 1069 passed / 0 failed / 3 skipped (POSIX-only), `dotnet format --verify-no-changes`
  clean. Reviewed the `MainWindow.axaml` + `.axaml.cs` diff - `RebuildTranscript` is correctly
  wired into `WatchSession` (fires on every focus change + initial bind) and hosts a freshly-built
  `ItemsControl` as a realized `Control`, so no `ContentPresenter` recycling. Fix is sound and ready;
  **still uncommitted, still gated on operator GUI verification** (rendering can't be asserted
  headlessly - operator must restart the GUI, spawn a 2nd session, flip focus a few times, confirm
  the focused transcript renders).
- Logged `issues.md#0001`: the per-turn "Details" Expander uses `IsNotNull` on `Children.Count`
  (an int, never null), so it's always visible even for childless turns. Pre-existing, preserved
  verbatim through the template move; out of scope here.

## 2026-06-04

- Task created.

## 2026-06-03 (session: centralize slash-command registration; fix GUI "Unknown command")

Follow-on bug from the same session: in the GUI, `/task` (and every host command) returned
"Unknown command" because the GUI sent typed text straight to claude's stdin with no slash
dispatch, while the CLI routed through `SlashCommandRegistry`. The command CLASSES also lived in
`InfiniContext.Cli`, which the GUI can't reference - so they could never be shared. Fixed across
three slices (the unfinished slices C/D/E of the task 0012 design):

- **Slice C - promote to Core.** `git mv`'d the 14 command classes to `Core/SlashCommands/` and the
  task/issue subsystem (`TaskCommand`, `TaskCommandHandler`, `ScopeGlobExpander`, `IssueCommand`,
  `IssueCommandHandler`) to `Core/Tasks/` + `Core/Issues/`; made them `public`. Extracted
  `RunArgs.TryParseApprovalMode` to a Core `ApprovalModeParser` (ApprovalCommand had an implicit
  `InfiniContext.Cli` namespace dependency that only resolved while it lived under that namespace).
  Added the `FileSystemGlobbing` package to Core (ScopeGlobExpander). Removed the now-redundant
  `<Compile Include>` links from the test csproj (types come via the Core reference now).
- **Slice D - single registration site + drift guard.** New `SlashCommandRegistryFactory.Build()`
  in Core is the ONE place commands are registered; CLI's `MultiSessionHost` now calls it instead
  of its inline list. Tightened `SlashCommandContext.TaskHandler`/`IssueHandler` from `object?` to
  the concrete Core handler types. New `RegistrySnapshotTests` pins the 16-command set so adding a
  command to the factory without the snapshot (or vice-versa) fails loudly.
- **Slice E - GUI dispatch.** `SessionHost` now builds the review + issue stack (ported from
  Program.cs) and exposes `CreateTaskCommandHandler`/`CreateIssueCommandHandler` factories.
  New `GuiSlashCommandRenderer` (writes inline into the focused transcript) + `TranscriptTextWriter`
  + `GuiReplActions`. `MainWindowViewModel` builds the registry via the factory and intercepts
  `/`-input in `SendToSessionAsync`: registered commands run through the registry; unknown ones fall
  through to claude (D6). New `MainWindowViewModelSlashTests` assert `/task`/`/help` are intercepted
  and unknown/plain text is forwarded.

### Working / known-limits in the GUI
- Working: `/help`, `/task list|new|review|...`, `/issue`, `/cost`, `/state`, `/settings`,
  `/approval`. `/task review` requires `--yes` (the handler's interactive confirmation reads a
  `TextReader`, which the GUI leaves null; renderer prompt dialogs are a follow-up).
- Status-only (follow-up): `IReplActions` in the GUI (`/quit`, `/resume`, `/task switch`, `/focus`)
  currently surface a StatusText hint rather than driving focus/spawn - wiring them into the GUI's
  focus/spawn model is a follow-up.

### Verification
- Build clean (Release; running GUI Debug build locks bin/Debug). Full suite 1069 passed / 0 failed
  / 3 skipped (POSIX-only). `dotnet format --verify-no-changes` clean.
- Committed as `50bc122` ("Centralize slash-command registration; fix GUI 'Unknown command'").

## 2026-06-03 (session: model upgrade + nudge hang)

### Issue 1 - model upgrade to Opus 4.8 + centralization (DONE)
- Added `src/InfiniContext.Core/Configuration/KnownModels.cs` - single source of truth for model
  ids: `Main = claude-opus-4-8`, `MainLargeContext = claude-opus-4-8[1m]`, `Light = claude-haiku-4-5`,
  `Reviewer = claude-sonnet-4-6`. Future model bumps = one edit here.
- Rewired the scattered literals to the constants: `InfiniContextSettings` (`MainModelOptions.Id`,
  `LightModelOptions.Id`, the `ModelContextWindows` fallback map) and `ReviewerAgentOptions.Model`.
- **Decision (operator did not answer the AskUserQuestion):** chose to *pin* the main model rather
  than only relabel it. Today `session.model` is null, so the host let `claude` pick its own default
  (`claude-opus-4-7[1m]` per recent init reports) and `models.main.id` was purely informational - so
  bumping it alone would not have changed which model runs. Now the effective-model resolution falls
  back to `Models.Main.Id` when neither `--model` nor `session.model` is set, in **both** adapters:
  CLI `Program.cs` (`effectiveModel`) and GUI `SessionHost.cs` (`CreateSessionAsync`). This makes the
  upgrade actually take effect. Behavior change: the host now forces the main model instead of
  deferring to claude's default.
- `settings.json` `models.main.id` -> `claude-opus-4-8`; `docs/quickstart.md` example updated; CLI
  `--model` help-text example bumped to opus-4-8.
- Tests updated for new defaults: `ApprovalAndModelsSettingsTests` (defaults snapshot + window map),
  `TasksOptionsResolveThresholdsTests` (opus-4-8[1m]), `SessionManagerTests` (pressure-crossing test
  relied on opus-4-7 being in the default window map).

### Issue 2 - CLI hard-nudge auto-rotation hang (DONE)
- **Root cause:** on a context-pressure nudge the agent flushes and calls `session_exit`, which closes
  claude's stdin. Claude finishes its final turn -> state goes **Idle** -> `MultiSessionHost.InputLoopAsync`
  wakes to the prompt and blocks inside `await _input.ReadLineAsync()`. *Then* claude exits -> `Exited`
  transition -> `AutoRotationService` proposes -> `OnAutoRotationProposal` prints
  "...flush detected, auto-rotating..." and calls `RequestSwitch`, which fires `_exitSignal`. But a bare
  `await ReadLineAsync()` does not watch `_exitSignal`, so the loop hung forever (until the user pressed
  Enter). The pre-existing rotation tests deliberately moved state out of Idle to *avoid* this path
  (comment at MultiSessionHostTests ~L171), which is why it was never caught.
- **Fix:** `InputLoopAsync` now races the read against `_exitSignal` via `Task.WhenAny`; on exit it
  abandons the read (observing any later fault) and breaks. (`MultiSessionHost.cs`)
- **Regression test:** `Nudge_rotation_completes_even_when_input_loop_is_parked_in_ReadLineAsync` uses a
  `BlockingReader` whose `ReadLineAsync` never returns, reproducing the production hang. Verified it
  TIMES OUT without the fix and passes with it.

### Verification
- Build clean (Release - the running GUI Debug build locks `bin/Debug`, so Release is the workable
  config locally; it is also what CI uses).
- Full suite: 1063 passed / 0 failed / 3 skipped (POSIX-only). `dotnet format --verify-no-changes` clean.
- Committed as `44c6333` ("Task 0015: Opus 4.8 upgrade + centralized model constants; fix CLI nudge
  auto-rotation hang"). Suggest a `/task review` before close.

## 2026-06-03 (session: GUI empty-transcript on session flip вЂ” task 0014 Bug 2 residual)

### Issue 3 - newly-spawned/flipped GUI session shows a blank, unresponsive transcript (FIX IN TREE, needs GUI verify)
- **Report:** operator spawned a session from the tree; it warmed up, then its chat pane went blank
  and "didn't react to input (at least visually)"; an earlier session stayed fine.
- **Diagnosis from the live GUI log** (`src/InfiniContext.Gui/bin/Debug/net10.0/logs/gui-20260603-225904.log`):
  three concurrent sessions (host `7d07b92d` task 15, `8f39477c` task 14, `5ef9429d` task 15).
  The blank one is `5ef9429d` (2nd spawn for task 15, focused after two flips). Log line 611-621:
  operator sent "reply ok if you are functional" в†’ agent replied "Ok вЂ” I'm functional and ready"
  and the turn completed (Sendingв†’Streamingв†’Idle). **So input WAS processed and the session WAS
  alive; only rendering was dead.** Its `Turns` collection populated normally (init + assistant +
  tool events all observed by `gui-row[5ef9429d]`). Pure view-layer bug.
- **Root cause:** task 0014 Bug 2's committed fix wrapped the transcript in
  `ContentControl Content="{Binding FocusedSession}"` expecting the presenter to rebuild the
  subtree on a session flip. But Avalonia's `ContentPresenter` RECYCLES: two `SessionRowViewModel`
  instances share one `DataTemplate` в†’ same recycling key в†’ it reuses the realized `ItemsControl`
  and only re-points its `DataContext`. That is the exact cross-session ItemsSource swap 0014 set
  out to kill вЂ” with the alpha Markdown.Avalonia 12.0.0-a2 visuals it corrupts and, after repeated
  flips, blanks the pane. So 0014's fix was effectively a no-op against the recycler.
- **Fix:** `MainWindow.axaml` - extracted the per-turn card to a keyed resource `TurnNodeTemplate`
  and replaced the binding-driven `ContentControl` with a named host `TranscriptHost`.
  `MainWindow.axaml.cs` `RebuildTranscript(row)` (called from `WatchSession`, which already fires on
  every focus change + initial bind) builds a BRAND-NEW `ItemsControl` per focus change and assigns
  it to `TranscriptHost.Content`. Content is then a realized `Control`, so the presenter hosts it
  verbatim with zero container recycling вЂ” no previous session's containers/markdown visuals can
  leak in. Streaming within one session still updates live via the `Turns` ObservableCollection.
- **Verification:** Gui builds clean (Release; compiled-XAML pass OK). Full suite 1069/0/3.
  `dotnet format --verify-no-changes` clean. **Not committed.** Rendering can't be asserted
  headlessly (per 0014) вЂ” operator must restart the GUI (the live one runs the OLD build), spawn a
  2nd session, flip focus a few times, and confirm the transcript renders for the focused session.