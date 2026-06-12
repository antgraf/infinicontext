# Research вЂ” model context window sizes

## Authoritative spec (Claude Models API, via `claude-api` skill, cached 2026-06-04)

| Model id | Context window | Notes |
|----------|---------------|-------|
| `claude-opus-4-8`   | **1M** (1,000,000) | "1M context window at standard API pricing, no long-context premium" |
| `claude-sonnet-4-6` | **1M** (1,000,000) | |
| `claude-haiku-4-5`  | **200K** (200,000) | unchanged |
| `claude-fable-5`    | 1M (1,000,000) | already correct in code |

Source: `claude-api` skill "Current Models" + `shared/models.md` capability table
(`max_input_tokens`). These are the API's `max_input_tokens` values.

## Current (incorrect) values in code

- `src/InfiniContext.Core/Configuration/KnownModels.cs` в†’ `Catalog`:
  - Opus 4.8 = `200_000`  вќЊ should be `1_000_000`
  - Sonnet 4.6 = `200_000` вќЊ should be `1_000_000`
  - Haiku 4.5 = `200_000`  вњ… correct
  - Fable 5 = `1_000_000`  вњ… correct
- `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` в†’ `ModelContextWindows`:
  - `[KnownModels.Reviewer]` (sonnet) = `200_000` вќЊ в†’ `1_000_000`
  - `[KnownModels.Main]` (opus)        = `200_000` вќЊ в†’ `1_000_000`
  - `[KnownModels.MainLargeContext]` (`claude-opus-4-8[1m]`) = `1_000_000` вњ…
  - `[KnownModels.Light]` (haiku)       = `200_000` вњ…
  - `[KnownModels.Fable]`               = `1_000_000` вњ…
- `settings.json` mirrors both of the above (lines ~47-50 in models.available,
  plus the tasks.modelContextWindows map) вЂ” must be updated in lockstep.

## The `[1m]` variant (`MainLargeContext = "claude-opus-4-8[1m]"`)

- `claude` reports the active context window in `system/init` and
  `result.modelUsage[<model>].contextWindow`. When the 1M-context beta is active
  it reports the model id with a `[1m]` suffix.
- `ContextPressureMonitor.CurrentWindow()` prefers claude's self-reported
  `result.modelUsage[model].contextWindow` (accurate, reflects `[1m]`); the
  settings `ModelContextWindows` map is only a fallback used before the first
  result event arrives, keyed by the (possibly suffixed) model id from
  `system/init`.
- Open question that drove the original 200K base value: does a `claude` CLI
  session **without** the 1M beta report 200K or 1M for `claude-opus-4-8` /
  `claude-sonnet-4-6`? The committed fixtures use `claude-opus-4-7` and were not
  inspected for a `contextWindow` value before the context-flush. NOT yet
  confirmed empirically.

## Decision (provisional вЂ” see decisions below)

Per the API spec, Opus 4.8 and Sonnet 4.6 are 1M-context models, so the base
ids should map to `1_000_000`, matching the spec the task cited
(platform.claude.com models overview). The `[1m]` suffixed key stays at 1M.
Net effect: base id and `[1m]` id both resolve to 1M for Opus/Sonnet вЂ” the
suffix becomes redundant for the *window value* but is kept because claude still
emits the suffixed id and the map must contain a key for it. Haiku stays 200K.

Risk to verify before closing: if a non-beta `claude` session actually caps
Opus/Sonnet at 200K (i.e. the 1M is a Claude-Code opt-in beta, not the default),
then the fallback map would over-estimate the window pre-first-result and the
soft/hard nudges would fire late. Monitor's self-report preference mitigates
this for all turns after the first result event. Recommend confirming against a
fresh fixture or a live `system/init` capture before marking the task done.
