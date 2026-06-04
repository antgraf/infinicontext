# Progress

## 2026-06-04

- Task created.

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
- Not yet committed - awaiting operator review. Suggest a `/task review` before close.