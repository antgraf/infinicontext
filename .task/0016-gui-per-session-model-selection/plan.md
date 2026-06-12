# Implementation plan

Decomposed into slices that each build + test green on their own, so progress survives session
rotation. Slice A is the foundation everything else binds to.

## Slice A -- Model catalog (Core) [foundation]

Give the host an enumerable, curated list of selectable models with display names, so the GUI
dropdowns have something to bind to. Today `KnownModels` has only bare id consts.

- **New `ModelCatalogEntry` record** (`Core/Configuration/ModelCatalogEntry.cs`): `Id`,
  `DisplayName`, `ContextWindowTokens`.
- **`KnownModels`**: add `Fable = "claude-fable-5"` const + a `Catalog` `IReadOnlyList<ModelCatalogEntry>`
  (Opus 4.8, Sonnet 4.6, Haiku 4.5, Fable 5). Opus 4.8 stays `Main` (default unchanged -- out of scope to switch).
- **`ModelsOptions.Available`** settings list, defaulting to `KnownModels.Catalog`; operator-overridable
  in `settings.json` / `settings.user.json`. The GUI binds to this, not to `KnownModels` directly.
- **`ModelContextWindows`**: add the Fable 5 entry (1M) so `ResolveThresholds` picks the
  large-context (45/65) nudge pair for it. Confirm every `Available` id has a window entry.
- **Loader validation**: each `Available` entry has a non-blank id + display name; warn (not fail)
  if an `Available` id is missing from `ModelContextWindows`.
- **settings.json + settings.schema.json** mirror the new `models.available` array.
- **Tests**: catalog non-empty, contains Main; every catalog id resolves a context window;
  settings round-trip of `models.available`.

## Slice B -- Right-panel per-session model dropdown (GUI)

- `SessionRowViewModel`: expose current `Model` (from the spawn request / SessionContext) +
  `SelectedModel`. SessionContext needs to remember the model it was spawned with (add `Model` to
  `SpawnRequest` consumer -> `SessionContext.Model`).
- Inspector (`MainWindow.axaml` Grid.Column="3"): add a `ComboBox` bound to
  `MainWindowViewModel.AvailableModels` with `SelectedItem` = focused session's model, plus an
  "Apply (resume with new model)" button enabled only when the selection differs from the running model.
- **Apply path** = respawn: close the focused session, `CreateSessionAsync` with the new model into
  the same cwd/task (thresholds auto-re-resolve via `SpawnRequest.Model`). Surface that transcript
  history does not carry across the respawn (live `claude --model` is fixed for process life).
  Confirm before killing a live session.

## Slice C -- Task-default model (persist + dialog dropdown)

- **`Migration_003_AddDefaultModel`**: `ALTER TABLE tasks ADD COLUMN default_model TEXT NULL`.
- `TaskRecord.DefaultModel` (nullable); `TaskStore` read/write; `TaskService.CreateAsync` gains an
  optional model arg.
- **`NewTaskDialog`**: add a model `ComboBox` (bound to the catalog); `NewTaskRequest.Model`.
- `MainWindowViewModel.NewTaskAsync` passes the chosen model to `CreateAsync`.
- **Spawn wiring**: `SpawnSessionForTaskAsync` / `CreateSessionAsync` resolve the model as
  `task.DefaultModel ?? Settings.Session.Model ?? Settings.Models.Main.Id`.
- Tests: migration applies idempotently; create-with-model round-trips; spawn uses task default.

## Sequencing / status

- [x] Slice A -- Core model catalog **(code complete + verified; pending a clean full build/test
  once the GUI bin lock clears, then commit -- see progress.md). NOTE: shipping Slice A also fixed a
  latent loader bug -- M.E.C appends JSON arrays onto pre-populated read-only collection defaults, so
  `models.available` was concatenating onto the catalog instead of replacing it. Fixed in
  `InfiniContextSettingsLoader.Load` by re-binding the section.**
- [x] Slice B -- right-panel per-session dropdown + respawn **(DONE; Release build+tests green).**
  `SessionContext.Model`, inspector ComboBox + "Apply (respawn)" button, `ApplyModelAsync` closes +
  re-spawns into the same cwd/task with the chosen model. Respawn flow itself awaits operator manual smoke.
- [x] Slice C -- task-default model **(DONE; Release build+tests green).** Core: Migration_003,
  `TaskRecord.DefaultModel`, store read/write, `TaskService.CreateAsync` model arg. GUI: `NewTaskDialog`
  model ComboBox, `NewTaskRequest.Model`, `NewTaskAsync` passes it, spawn resolves `task.DefaultModel`
  via `SessionHost.CreateSessionAsync(model)`.

**All three slices code-complete. Remaining: operator manual GUI smoke of the respawn flow + commit.
The model-selection UI lives in the new-task dialog (per-task default) and the right-hand Inspector
(per-session current model + Apply-respawn).**

Slice A first (no UI risk, pure additive Core). B and C are independent of each other and can land
in either order once A is in.
