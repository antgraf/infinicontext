# Progress

## 2026-06-11

- Task created.

### Session: orientation + Slice A start

**Folder reconciliation.** This feature was scaffolded twice: `0016-gui-per-session-model-selection`
(this folder, host-tracked) and `0017-gui-per-session-model-selection` (duplicate, never DB-registered).
The DB id `0016` actually belongs to the committed `gui-multi-session-transcript-rendering-bugs`
task; the model-selection feature was never registered in the DB at all. **Decision:** work in this
(0016) folder since the host launched it. Imported the real spec from 0017 into `description.md`
(with a folder note). **Operator action item:** the `0017-gui-per-session-model-selection` folder is
a redundant duplicate and can be deleted; I did not delete it (untracked-folder removal left to you).

**Plan written** (`plan.md`): 3 slices вЂ” A: Core model catalog (foundation), B: right-panel
per-session dropdown + apply-via-respawn, C: task-default model (Migration_003 + dialog dropdown).

**Slice A вЂ” IN PROGRESS (Core builds green; settings/loader/tests remain).**
Done so far:
- `src/InfiniContext.Core/Configuration/ModelCatalogEntry.cs` вЂ” new record `(Id, DisplayName, ContextWindowTokens)`.
- `KnownModels.cs` вЂ” added `Fable = "claude-fable-5"` const + `Catalog` (`IReadOnlyList<ModelCatalogEntry>`):
  Opus 4.8 (200k), Sonnet 4.6 (200k), Haiku 4.5 (200k), Fable 5 (1M). `Main` stays Opus 4.8 (default
  switch is out of scope). `dotnet build InfiniContext.Core` succeeds, 0 warnings.

**Remaining Slice A steps (precise) вЂ” do these next:**
1. `InfiniContextSettings.cs` `ModelsOptions` (~line 124): add
   `public IReadOnlyList<ModelCatalogEntry> Available { get; init; } = KnownModels.Catalog;`
2. `InfiniContextSettings.cs` `TasksOptions.ModelContextWindows` (~line 315): add
   `[KnownModels.Fable] = 1_000_000,` so `ResolveThresholds` gives Fable the large-context (45/65) pair.
   (Skip a `Fable + "[1m]"` variant unless we confirm claude reports one вЂ” Fable's base window is already 1M.)
3. `InfiniContextSettingsLoader.cs` validation (~line 155, after the light-model block): for each
   `Models.Available` entry, error if `Id` or `DisplayName` is blank. Coverage check (Available id
   missing from `ModelContextWindows`) вЂ” prefer a **warning**, but the loader currently only has a
   hard-fail `errors` list; check whether a warnings channel exists before adding one (if not, either
   skip the coverage check or make it a soft no-op вЂ” do NOT hard-fail on it, since user overrides may
   legitimately add a model without a window entry and fall back to default thresholds).
4. `settings.json` (`"models"` block, ~line 34): add a `"available": [ { "id":..., "displayName":..., "contextWindowTokens":... }, ... ]` array mirroring the catalog.
5. `settings.schema.json`: add the `models.available` array schema for editor autocomplete.
6. **Tests** (`tests/InfiniContext.Tests/Configuration/ApprovalAndModelsSettingsTests.cs`):
   - **MUST UPDATE** `Tasks_defaults_snapshot` (line ~59): it pins `ModelContextWindows` to exactly 4
     entries; add `["claude-fable-5"] = 1_000_000`.
   - Add: catalog non-empty + contains `Main`; every `Models.Available` id resolves a
     `ModelContextWindows` window; `models.available` round-trips from JSON.
   Then `dotnet test InfiniContext.slnx` and `dotnet format` before commit.

**Then Slice B and C per `plan.md`.** Key facts confirmed this session:
- Model resolves at spawn in `SessionManager.CreateAsync` from `request.Model ?? settings.Session.Model`;
  `ResolveThresholds(effectiveModel)` auto-re-derives nudge %s. So feeding `SpawnRequest.Model` is all
  that's needed вЂ” thresholds follow automatically.
- GUI spawn chokepoint: `SessionHost.CreateSessionAsync` (`Model = Settings.Session.Model ?? Settings.Models.Main.Id`).
- Inspector for the per-session dropdown: `MainWindow.axaml` `Grid.Column="3"` (~line 361).
- Per-task default model needs DB persistence: `Migration_003` (last is `Migration_002_AddBaseCommitSha`),
  `TaskRecord.DefaultModel`, `TaskService.CreateAsync` model arg, `NewTaskDialog` ComboBox + `NewTaskRequest.Model`.
- `SessionContext` does NOT currently store its model вЂ” Slice B needs to add it (for the dropdown to
  show the running session's current model).

**No commit yet** вЂ” Slice A incomplete (settings/tests pending). Nothing staged.

### Session: Slice A finished + loader append-bug fixed (correcting the stale note above)

Picked up the working tree and found Slice A's code (steps 1-6 above) was actually **all present** --
the prior note was stale. `ModelsOptions.Available`, the `ModelContextWindows` Fable entry, loader
validation, `settings.json`, `settings.schema.json`, and all the tests were already written. Verified
each by inspection, then ran the tests.

**Found + fixed a real bug.** First test run: 15 passed, **4 failed** (`Available_models_round_trip_from_json`
and the three `Available_model_with_blank_*_is_rejected`). Root cause: `Microsoft.Extensions.Configuration`
**appends** to a pre-populated read-only collection when binding a JSON array instead of replacing it.
So binding `models.available` over the in-code `KnownModels.Catalog` default *concatenated* them --
user entries landed at index `[4]`, and a normal load (settings.json mirrors the 4-entry catalog)
would silently produce **8** entries, not 4. Latent production bug, not just a test artifact.

**Fix** (`InfiniContextSettingsLoader.Load`, right after `configuration.Bind`): when `models:available`
exists in config, re-bind that one section into a fresh `List<ModelCatalogEntry>` and replace via
`settings with { Models = settings.Models with { Available = available } }`. Dictionaries
(`tasks.modelContextWindows`) are left alone -- key-merge is the intended per-model override semantics;
only the array needed replace-not-append. `ExtraArgs` is the only other list default and is empty, so
unaffected.

**Verification.** Rebuilt Core (clean, 0 warnings). Full solution/test build is **blocked**: the GUI
project's bin output is locked by two running `InfiniContext.Gui` instances (PIDs 5036, 10008) plus VS
Insiders (19204), and the GUI has no compiled `obj` ref assembly in this tree, so Tests can't link it.
Worked around without disturbing the operator's live processes: the loader fix is an internal-only
change (no public-API delta), so I dropped the freshly-built `Core.dll` into the existing Tests output
and ran `dotnet test --no-build`. Result: **ApprovalAndModelsSettingsTests 19/19 pass; all Configuration
tests 58/58 pass.** `dotnet format` clean on the changed file.

**Still pending (operator action needed):** a clean full `dotnet build` + `dotnet test` once the GUI
lock is released (close the running GUI instances), then commit Slice A. Slice A is functionally
complete and verified; only the from-scratch build gate is outstanding.

**Slice A status: CODE COMPLETE + VERIFIED (pending clean-build gate + commit). Next: Slice B or C per `plan.md`.**

### Session: Slice C Core portion landed (DB + store + service), GUI dialog deferred

**Build-lock context.** Operator has two live `InfiniContext.Gui` instances (PIDs 8576, 25116) plus
VS Insiders (19204) holding `InfiniContext.Gui.dll`/`.exe`, so any build that touches the GUI project
fails at the objв†’bin copy. Asked the operator how to proceed; they didn't answer, so per autonomy
default I took the path that makes **verifiable** progress without touching the locked GUI or
committing: **Slice C's Core portion** (DB migration + persistence + service), which is GUI-free.

**Worked around the lock for verification** (didn't disturb operator's processes): Core builds clean
on its own. To compile+run Tests (which transitively references GUI), I (1) built GUI once with
`-p:OutDir=<obj/_guiout>` so the bin copy lands in a throwaway dir instead of the locked `bin` вЂ” this
regenerated the missing `obj/.../ref/InfiniContext.Gui.dll` reference assembly; (2) built Tests with
`-p:BuildProjectReferences=false` so it links the fresh ref + Core without rebuilding GUI; (3) ran
`dotnet test --no-build`. **Result: 150/150 pass** (migration + store + service + all Configuration).

**Slice C Core changes (done, verified):**
- `Migrations/Migration_003_AddDefaultModel.cs` вЂ” `ALTER TABLE tasks ADD COLUMN default_model TEXT NULL;` (version 3).
- `Migrations/MigrationRunner.cs` вЂ” registered `Migration_003_AddDefaultModel` in `BuiltIn`.
- `TaskRecord.cs` вЂ” added `string? DefaultModel = null` (last positional param, after `BaseCommitSha`).
- `TaskStore.cs` вЂ” INSERT/GetAsync/ListAsync SELECT + `ReadRecord` (col index 8) all carry `default_model`.
- `TaskService.CreateAsync` вЂ” new optional `string? defaultModel = null` param (inserted **before** `ct`);
  `NormalizeModel` trims and nulls-out blanks so the column stores NULL not "". Fixed the one positional-`ct`
  caller (`TaskCommandHandler.HandleNewAsync` в†’ `CreateAsync(homeCwd, description, ct: ct)`).
- Tests: `Migration_003_adds_default_model_column_to_tasks`, updated `Built_in_migrations_apply_to_fresh_db`
  to expect `{1,2,3}`, `Create_round_trips_default_model` (TaskStore), `Create_persists_default_model_when_supplied`
  + `Create_stores_null_default_model_when_blank_or_missing` (TaskService).
- `dotnet format --verify-no-changes` clean on all changed files.

**Slice C remaining (GUI + spawn wiring вЂ” BLOCKED on the GUI bin lock, do when it clears):**
- `NewTaskDialog` model `ComboBox` (bound to the catalog) + `NewTaskRequest.Model`.
- `MainWindowViewModel.NewTaskAsync` passes the chosen model into `CreateAsync` (currently calls
  `svc.CreateAsync(req.HomeCwd, req.Description)` at `MainWindowViewModel.cs:374` вЂ” add the model arg).
- Spawn wiring: `SessionHost.CreateSessionAsync` / `SpawnSessionForTaskAsync` resolve model as
  `task.DefaultModel ?? Settings.Session.Model ?? Settings.Models.Main.Id`.
- Test: spawn uses task default.

**Still pending (operator):** clean full `dotnet build` + `dotnet test` once the GUI lock clears, then
commit Slice A **and** Slice C-Core together (both are additive Core, no behavior change to existing code).
Nothing committed this session.

### Session: Slice B + Slice C GUI landed вЂ” model-selection UI now exists (Release-built/tested)

Operator confirmed Slice A/C-Core build+tests pass on their side, said to **build/test in Release** when
Debug bin is locked by their running GUI, and reported "I still don't see any UI elements to select the
model." So this session built the actual UI for **both** slices. Full solution **builds clean in Release
(0 warnings)**; **full test suite 1087 pass / 3 skipped**; `dotnet format --verify-no-changes` clean.

**Core (enables Slice B inspector to show the running model):**
- `SessionContext.Model` (init) вЂ” the effective model the session was spawned with.
- `SessionManager.CreateAsync` sets `Model = effectiveModel` (`request.Model ?? settings.Session.Model`).
- Test: `CreateAsync_sets_context_model_from_request`.

**Slice C GUI (per-task default model):**
- `NewTaskDialog.axaml` вЂ” added a "Default model" `ComboBox` (ItemTemplate w/ `x:DataType="vm:ModelChoice"`
  вЂ” compiled bindings forbid bare `DisplayMemberBinding`). Window grew to 460/400.
- `NewTaskRequest.Model` (nullable); dialog captures the selected `ModelChoice.Id`.
- `NewTaskDialog.SetModelChoices(...)`; the View (`MainWindow.axaml.cs`) calls it with
  `_boundVm.BuildModelChoices()` before `ShowAsync`.
- `MainWindowViewModel.NewTaskAsync` passes `req.Model` into `TaskService.CreateAsync`.
- Spawn wiring: `SpawnSessionForTaskAsync` reads `realTask.Record?.DefaultModel`; refactored the spawn body
  into `SpawnIntoAsync(cwd, taskId, model)` (shared with the respawn path). `SessionHost.CreateSessionAsync`
  gained `string? model` + a public `ResolveModel` (explicit > `Session.Model` > `Models.Main.Id`).
  Auto-rotation respawn (`SpawnRotatedSessionAsync`) now passes `plan.Task.DefaultModel`.

**Slice B GUI (per-session inspector dropdown + respawn):**
- New `ViewModels/ModelChoice.cs` (`Id`/`Label`; null id = "host default") вЂ” shared by both pickers.
- `MainWindowViewModel`: `AvailableModels` (from `settings.Models.Available`, else `KnownModels.Catalog`),
  `BuildModelChoices()`/`InspectorModelChoices`, `SelectedInspectorModel`, `CanApplyModel`, `ApplyModelCommand`.
  `SyncInspectorModelToFocusedSession()` pre-selects the focused session's model on focus change so Apply
  starts disabled until a different model is picked.
- `ApplyModelAsync` = respawn: close focused session, `SpawnIntoAsync(cwd, taskId, choice.Id)` (same
  cwd/task, new model; task-bound re-seeds via pointer; transcript history not preserved вЂ” warned in the
  inspector hint). Clicking Apply IS the confirmation.
- `SessionRowViewModel.ModelDisplay` (`Context.Model ?? "(host default)"`).
- Inspector XAML (`MainWindow.axaml` Grid.Column=3): Model label + current-model text + ComboBox (bound to
  `InspectorModelChoices`/`SelectedInspectorModel`, disabled when the session is dead) + "Apply (respawn)"
  button (gated on `ApplyModelCommand`/`CanApplyModel`) + a respawn-warning hint.
- Tests (`MainWindowViewModelTests`): `BuildModelChoices_leads_with_host_default_then_catalog`,
  `Focusing_a_session_preselects_its_model_and_Apply_is_disabled`,
  `Selecting_a_different_model_enables_Apply`, `Apply_is_disabled_when_focused_session_has_exited`.
  (`ApplyModelAsync`'s actual respawn needs a real `SessionHost` в†’ manual GUI smoke, per plan's escape hatch.)
  `SessionContextFactory` gained a `model:` param.

**To SEE it now:** the operator runs Debug instances; the feature is built in **Release** at
`src/InfiniContext.Gui/bin/Release/net10.0/InfiniContext.Gui.exe` вЂ” launch that, or close the Debug
instances and rebuild Debug. New-task dialog has the model dropdown; focus a session в†’ the right-hand
Inspector shows its model + a dropdown + "Apply (respawn)".

**Still pending (operator):** manual GUI smoke of the respawn flow; commit (Slice A + C-Core + B/C-GUI).
Nothing committed this session.

### Session: clean-build gate verified green (Release)

Ran the pending from-scratch build/test gate. Two Debug GUI instances (PIDs 3600, 16680) + devenv
(19204) still hold the Debug bin, so built/tested in **Release** (separate output dir):
- `dotnet build -c Release`: **clean, 0 warnings.**
- `dotnet test -c Release --no-build`: **1087 pass / 3 skip** (the 3 skips are POSIX supervisor tests, expected on Windows).
- `dotnet format --verify-no-changes`: **clean.**

All three slices (A: catalog, B: per-session inspector dropdown + respawn, C: per-task default model)
are now code-complete AND pass the full clean-build gate. **Remaining: manual GUI smoke of the respawn
flow (operator-side), then commit** (Slice A + C-Core + B/C-GUI together; all additive Core + new GUI,
no behavior change to existing paths). Awaiting operator go-ahead to commit. Nothing committed this session.

### Session: GUI smoke of the respawn flow вЂ” PASS (hermetic, agent-driven)

Ran the pending manual smoke myself instead of waiting: launched the **Release** GUI as an isolated
instance (temp task DB via `INFINICONTEXT__TASKS__DBPATH`, sessions + light model redirected to
FakeClaude via `CLAUDE_EXE` + `FAKE_CLAUDE_SCRIPT`; operator's running instances and real
`~/.infinicontext/infinicontext.db` untouched) and drove it end-to-end with UIA (Windows PowerShell 5.1
driver scripts + screenshots under `tmp/icx-smoke/`, gitignored).

**All three claims verified at the pixels + wire level:**
1. New-task dialog shows the "Default model" ComboBox (Host default preselected) with all 5 choices
   (Host default, Opus 4.8, Sonnet 4.6, Haiku 4.5, Fable 5) вЂ” `03-dialog-dropdown-open.png`.
2. Created task with default model Sonnet 4.6 в†’ "+" spawn в†’ status bar `[model: claude-sonnet-4-6]`,
   inspector shows current model + dropdown preselected to it + Apply disabled вЂ” `06-session-spawned.png`.
   Wire proof: spawn argv contains `--model claude-sonnet-4-6` (log `gui-20260611-202849.log`).
3. Selected Fable 5 в†’ Apply enabled в†’ clicked в†’ old row replaced by new session, status
   `[model: claude-fable-5]`, inspector re-synced (Apply disabled again), task pointer re-seeded вЂ”
   `08-after-respawn.png`. Wire proof: respawn argv contains `--model claude-fable-5`.

**Probes:** killed the session's claude child в†’ row flips to "Exited", inspector dropdown AND Apply
both disable (dead-session gating works in the real app, matching the unit test). Light-model title
generation, task scaffolding in a temp home, and JobObject child cleanup (0 leftover FakeClaude after
host kill) all behaved.

**Out-of-scope finding logged to issues.md#0001:** GUI spawns ignore `settings.Session.ExtraArgs`
(CLI paths forward it into `SpawnRequest`; `SessionHost.CreateSessionAsync` does not).

**The smoke gate is now green. Only remaining step: commit (operator go-ahead).** Nothing committed this session.

### Session (cont.): issues.md#0001 fixed вЂ” GUI spawns now forward session.extraArgs

Operator said "Fix session extra args", so the out-of-scope finding became in-scope:
- `SessionHost.cs`: extracted the SpawnRequest construction into a **pure static
  `BuildSpawnRequest(settings, cwd, taskId, firstUserMessage, model)`** (CLI-parity:
  `ExtraArgs = settings.Session.ExtraArgs` now included) + a static `ResolveModel(settings, вЂ¦)`
  overload; instance `ResolveModel` delegates. `CreateSessionAsync` now calls the static builder,
  so the settings->request mapping is unit-testable without standing up a host.
- New `tests/InfiniContext.Tests/Gui/SessionHostSpawnRequestTests.cs` (4 tests): extraArgs
  passthrough (the regression), explicit-model-wins, blank-model fallthrough chain, task
  binding + seed prompt carry.
- Gates: Release build clean (0 warnings), **full suite 1091 pass / 3 skip**, `dotnet format
  --verify-no-changes` clean. issues.md#0001 marked fixed.

**Remaining: commit only** (Slices A+B+C + this fix). Nothing committed this session.

### Session: gate re-verified + committed & pushed

Operator said "push". Re-ran the full gate (Debug bin still locked by 3 live GUI instances + devenv,
so **Release**): build clean (0 warnings), **1091 pass / 3 skip** (POSIX supervisor, expected on
Windows), `dotnet format --verify-no-changes` clean. Staged only the 0016 work РІР‚вЂќ all modified tracked
files (`git add -u`) plus the new files (`ModelCatalogEntry.cs`, `Migration_003_AddDefaultModel.cs`,
`ModelChoice.cs`, `SessionHostSpawnRequestTests.cs`) and this `0016` task folder. **Deliberately
excluded** the two unrelated untracked folders (`0017-gui-per-session-model-selection` duplicate and
`0017-gui-task-review-command-error`) and never touched `settings.user.json`. Committed on
`task/0013-gui-session-transfer` and pushed.

**Task 0016 is COMPLETE** (all slices A/B/C + issues.md#0001 fix, gate green, smoke-tested, committed & pushed).
