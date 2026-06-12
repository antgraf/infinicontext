# GUI per-session model selection

Let the operator choose which Claude model a session runs, instead of every session
silently inheriting the single pinned `KnownModels.Main`.

> **Folder note.** This feature was scaffolded twice: `.task/0016-gui-per-session-model-selection/`
> (this folder, the one the host tracks) and `.task/0017-gui-per-session-model-selection/` (a
> duplicate, never registered in the DB). The DB id `0016` actually belongs to the committed
> `gui-multi-session-transcript-rendering-bugs` task; this model-selection feature was never
> registered. We work in this folder; the operator can delete the `0017` duplicate at their leisure.

## Motivation

Task 0015 centralized model ids in `KnownModels` and made the host *pin* the main model
(`effectiveModel = --model ?? session.model ?? KnownModels.Main`, today `claude-opus-4-8`).
That made the upgrade take effect but left model choice global and code-only. New models keep
shipping (e.g. Fable 5, `claude-fable-5`) and have different cost/behavior profiles, so the
operator should be able to pick per session -- and set the default for new sessions -- from the GUI
rather than editing settings.

## Scope

Two UI entry points, one underlying setting:

1. **Right panel (per-session).** The right info panel (`MainWindow.axaml` `Grid.Column="3"`)
   currently shows brief session info. Add a model-selection dropdown there bound to the focused
   session. This sets the model for the focused session.
2. **Task-creation dialog (default).** The same dropdown in the task-creation dialog, setting the
   **default model** that new sessions for that task spawn with.

## Key design constraints (from the 0015 discussion)

- **Per-session model is already plumbed at spawn.** `SpawnRequest.Model` is per-session and
  `SessionHost.CreateSessionAsync` already sets `Model = Settings.Session.Model ?? Settings.Models.Main.Id`.
  What's missing is the UI and the wiring from the dropdown into the spawn. The dropdown feeds spawn.
- **You cannot change a running session's model in place.** `claude` is launched with `--model`
  fixed for the life of the process. Changing the dropdown for a *live* session requires a
  respawn/resume, not a hot-swap -- and the context-pressure thresholds must be re-resolved against
  the new model's window (`TasksOptions.ResolveThresholds`, keyed by model id via
  `ModelContextWindows`). So: trivial for new sessions; an explicit "apply = resume with new model"
  path for live ones.
- **Model list source -- static-first (recommended).** The authoritative live source is the
  Anthropic Models API (`GET /v1/models` -> id, display_name, max_input_tokens, max_tokens,
  capabilities), but InfiniContext spawns the `claude` CLI and does not call the API directly (no
  API-key/HTTP path in the host; even `ILightModelService` is CLI-backed). So drive the dropdown
  from a static curated list -- extend `KnownModels` / a settings list + the `ModelContextWindows`
  map -- which is exactly the maintenance win 0015's centralization bought (a new model = one edit).
  A live Models-API fetch is a possible follow-on enhancement, not a gate for this feature.
- **Adopting a new model (e.g. Fable 5) is a separate decision.** Whatever models appear in the
  dropdown also need a `ModelContextWindows` entry so threshold resolution picks the right
  soft/hard pair. Fable 5 specifics if added: 1M context, ~2x Opus 4.8 cost, new tokenizer
  (~30% more tokens) -- flagged for awareness, not required by this task.

## Out of scope

- Live Models-API querying (follow-on).
- Actually switching the default-shipped model away from Opus 4.8.
