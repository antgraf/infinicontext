# Progress

## 2026-06-12

- Task created.
- Researched authoritative context-window specs via the `claude-api` skill
  (Models API). Findings recorded in `research.md`.
- **Conclusion:** Opus 4.8 and Sonnet 4.6 are both 1M-context models; the
  code's 200K values for them are wrong. Haiku 4.5 (200K) and Fable 5 (1M) are
  already correct.
- **Files to edit** (not yet changed вЂ” flushed at critical context before
  starting edits):
  1. `src/InfiniContext.Core/Configuration/KnownModels.cs` вЂ” `Catalog`: Opus 4.8
     `200_000`в†’`1_000_000`, Sonnet 4.6 `200_000`в†’`1_000_000`.
  2. `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` вЂ”
     `ModelContextWindows`: `[Main]` (opus) and `[Reviewer]` (sonnet)
     `200_000`в†’`1_000_000`. Leave `[MainLargeContext]`/`[Light]`/`[Fable]`.
  3. `settings.json` вЂ” mirror both: `models.available` entries for opus/sonnet
     and the `tasks.modelContextWindows` map (keep in lockstep with code).
  4. Update the XML doc comment on `KnownModels.Fable` (line ~32) only if it
     implies Fable is the sole 1M model вЂ” re-read before editing.
  5. Check `settings.schema.json` for any hard-coded window bounds/examples.
- **Tests to check/update:** `ApprovalAndModelsSettingsTests`,
  `TasksOptionsResolveThresholdsTests`, `InfiniContextSettingsLoaderTests`,
  `ContextPressureMonitorTests` вЂ” grep for `200_000`/`200000` asserting on
  opus/sonnet windows.
- **Open risk (verify before closing):** unclear whether a non-beta `claude`
  session caps Opus/Sonnet at 200K vs 1M (the `[1m]` beta question). Monitor
  prefers claude's self-reported window, so this only affects the pre-first-
  result fallback. Confirm against a fresh `system/init` capture or fixture.
- **Next session:** apply the edits above, run `dotnet build` + targeted tests,
  `dotnet format`, then update progress.

### Handoff to next session
Apply the 4 edits listed under "Files to edit", reconcile any failing tests
(grep `200_000` near opus/sonnet), build + format, and resolve the `[1m]`
non-beta open risk. All research is in `research.md`.

## 2026-06-11

- **`[1m]` open risk RESOLVED via the `claude-api` skill** (authoritative
  `shared/models.md`, cached 2026-06-04). API `max_input_tokens`: Opus 4.8 =
  **1M**, Sonnet 4.6 = **1M**, Haiku 4.5 = **200K**, Fable 5 = **1M**. Opus
  4.7+ docs: "1M context window at standard API pricing, no long-context
  premium" вЂ” i.e. 1M is the **standard/default** for Opus, not a beta opt-in.
  Decision is firm: set Opus 4.8 and Sonnet 4.6 base ids to `1_000_000`.
  The monitor prefers claude's self-reported `result.modelUsage[].contextWindow`
  anyway, so the settings map is only a pre-first-result fallback вЂ” even if a
  non-beta session somehow capped sonnet at 200K, it self-corrects after the
  first result event. Net: the spec values (1M) are correct; `[1m]` suffix key
  stays 1M (redundant for the window value but kept because claude still emits
  the suffixed id and the fallback map needs a key for it). Haiku stays 200K.
- **No code edits applied yet** вЂ” flushed at hard context nudge before editing.
- Confirmed exact edit sites by reading the files this session:
  1. `src/InfiniContext.Core/Configuration/KnownModels.cs:45` вЂ” `Catalog`:
     `new ModelCatalogEntry(Main, "Opus 4.8", 200_000)` в†’ `1_000_000`.
  2. `src/InfiniContext.Core/Configuration/KnownModels.cs:46` вЂ” `Catalog`:
     `new ModelCatalogEntry(Reviewer, "Sonnet 4.6", 200_000)` в†’ `1_000_000`.
     (Line 47 Haiku stays `200_000`; line 48 Fable already `1_000_000`.)
  3. `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs:329` вЂ”
     `ModelContextWindows`: `[KnownModels.Reviewer] = 200_000` в†’ `1_000_000`.
  4. `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs:330` вЂ”
     `[KnownModels.Main] = 200_000` в†’ `1_000_000`. (Line 331 `MainLargeContext`
     already `1_000_000`; line 332 `Light` stays `200_000`; line 333 Fable
     already `1_000_000`.)
  5. `settings.json` вЂ” mirror both: `models.available` opus/sonnet entries and
     the `tasks.modelContextWindows` map. NOT yet inspected this session вЂ” read
     it before editing to get exact line/shape.
- **Side effect to note (not a blocker):** with Opus 4.8 в†’ 1M, the Catalog
  entry now meets `LargeContextWindowTokens` (1,000,000), so Opus sessions will
  use the tighter large-context thresholds (`SoftThresholdPctLargeContext` 45 /
  `HardThresholdPctLargeContext` 65) instead of 60/85. This is the *intended*
  behavior per Task 0012 finding #2 (1M windows need an earlier nudge), but it
  changes runtime behavior for the default model вЂ” call it out in the final
  progress entry / any review.
- XML doc on `KnownModels.Fable` (lines 29-33) says Fable is "a newer,
  large-context model" вЂ” does NOT claim Fable is the *sole* 1M model, so no
  edit needed there (plan item 4 can be dropped).
- **Tests to check after edits:** grep `200_000`/`200000` in tests near
  opus/sonnet вЂ” `ApprovalAndModelsSettingsTests`,
  `TasksOptionsResolveThresholdsTests`, `InfiniContextSettingsLoaderTests`,
  `ContextPressureMonitorTests`. Any test asserting Opus/Sonnet window == 200K
  or asserting the default-threshold pair for Opus must flip to 1M / the
  large-context pair.
- **Then:** `dotnet build InfiniContext.slnx`, targeted tests, `dotnet format
  InfiniContext.slnx`, update progress.

### Edits applied this session
All five spec edits from the plan are now in the working tree:
1. `KnownModels.cs:45` Catalog Opus 4.8 `200_000`в†’`1_000_000`. вњ”
2. `KnownModels.cs:46` Catalog Sonnet 4.6 `200_000`в†’`1_000_000`. вњ”
   (Haiku 4.5 stays `200_000`; Fable already `1_000_000`.)
3. `InfiniContextSettings.cs:329` `ModelContextWindows[Reviewer]` `200_000`в†’`1_000_000`. вњ”
4. `InfiniContextSettings.cs:330` `ModelContextWindows[Main]` `200_000`в†’`1_000_000`. вњ”
   (`MainLargeContext`/`Fable` already 1M; `Light` stays 200K.)
5. `settings.json:47-48` `models.available` opus/sonnet `contextWindowTokens`
   `200000`в†’`1000000`. (No `tasks.modelContextWindows` block exists in
   settings.json вЂ” it relies on code defaults; nothing else to mirror.)
   `settings.schema.json` has no hard-coded window values вЂ” no edit needed.

Tests updated to match the new spec:
- `ApprovalAndModelsSettingsTests.Tasks_defaults_snapshot` вЂ” opus/sonnet map
  entries flipped to `1_000_000`.
- `TasksOptionsResolveThresholdsTests.Resolves_to_default_pair_for_small_context_model`
  вЂ” switched the probe id from `claude-sonnet-4-6` (now 1M в†’ large pair) to
  `claude-haiku-4-5` (still the only 200K catalog model).
- `SessionManagerTests.CreateAsync_wires_pressure_crossings_to_RotationReason_on_the_context`
  вЂ” opus now resolves to the 1M large-context pair (soft 45% / hard 65%), so
  the `used` token drivers were rescaled to a 1M window (500K = 50% soft,
  700K = 70% hard); stale `-> 200_000` comment in `BuildAssistantEvent` fixed.
- Other `200_000` hits in tests pass explicit windows and don't depend on the
  defaults map вЂ” left as-is.

- **BLOCKED on build/test:** the operator's InfiniContext GUI app is running
  (4 `InfiniContext.Gui` processes) and holds a lock on
  `src/InfiniContext.Gui/bin/.../InfiniContext.Core.dll`; the test project
  references the GUI project, so `dotnet build` / `dotnet test` fail with
  MSB3021 (file locked). Edits are complete and reviewed by eye. **Next:** once
  the GUI is closed, run `dotnet build InfiniContext.slnx`, the targeted tests
  (`ApprovalAndModelsSettingsTests`, `TasksOptionsResolveThresholdsTests`,
  `SessionManagerTests`, `ContextPressureMonitorTests`), then
  `dotnet format InfiniContext.slnx`.
- **Behavior-change note (carry into review):** Opus 4.8 в†’ 1M means the default
  model now uses the tighter large-context thresholds (soft 45 / hard 65)
  instead of 60/85 вЂ” intended per Task 0012 finding #2, but it changes runtime
  nudge timing for the shipped default.

### Build/test unblock + test fix (2026-06-11, later session)

- The GUI DLL lock cleared; `dotnet build InfiniContext.slnx` succeeds again.
- Targeted suites run: `SessionManagerTests` (16), `ApprovalAndModelsSettingsTests`
  + `TasksOptionsResolveThresholdsTests` + `ContextPressureMonitorTests` (36).
  All pass after one fix:
- **Fix:** `SessionManagerTests.CreateAsync_wires_pressure_crossings_to_RotationReason_on_the_context`
  failed (`RotationReason` stayed `None` at 50%). The 1M rescale assumed the
  large-context pair (45/65), but `SessionManager.CreateAsync` resolves the
  pair from `request.Model ?? settings.Session.Model` at create time, and both
  were null in the test, so `ResolveThresholds(null)` returned the small pair
  (60/85). Fixed by passing `model: "claude-opus-4-8"` in the spawn request
  (with a comment), consistent with the `system/init` the test emits.
- **Correction to the behavior-change note above:** the shipped default does
  NOT actually flip to 45/65. `settings.json` ships `session.model: null`, and
  the pct pair is frozen at `CreateAsync` from the *requested* model, not from
  the model `system/init` later reports. So a default session (no `--model`,
  no per-session selection) still gets 60/85 even though the monitor resolves
  the 1M window dynamically. The tighter pair only applies when the model is
  explicitly named at spawn. If 45/65-for-default is wanted, the threshold
  pair would need to re-resolve from the init-reported model (monitor-side),
  or `session.model` would need a non-null default вЂ” neither done here; flag
  for review as a follow-up decision.

### Follow-up implemented: window-driven threshold pair (2026-06-11)

Operator decision: make 1M windows actually get 45/65. Implemented monitor-side
so a default spawn (no `--model`, `session.model: null`) is covered:

- `ContextPressureMonitor` now optionally takes the large-context pair +
  cutoff (`softThresholdPctLargeContext` / `hardThresholdPctLargeContext` /
  `largeContextWindowTokens`, all default 0 = disabled, so existing call
  sites keep the old behavior). On every assistant-event check it selects the
  pair from the *resolved window* (settings map keyed by the init-reported
  model, or claude's authoritative `result.modelUsage[].contextWindow`) vs
  the cutoff. Ctor validates the large pair only when the cutoff is positive.
- `SessionManager.CreateAsync` passes both pairs + cutoff straight from
  `TasksOptions` and no longer pre-resolves a pair from
  `request.Model ?? settings.Session.Model`. (`SessionContext.Model` still
  gets that expression вЂ” display-only now.)
- `TasksOptions.ResolveThresholds(modelId)` deleted (dead code after the
  above; single source of truth for pair selection is the monitor). Its test
  file `TasksOptionsResolveThresholdsTests.cs` deleted; coverage moved to
  `ContextPressureMonitorTests` as window-driven cases: large window selects
  45/65, small window keeps 60/85 with the large pair configured, result
  self-report upgrading a stale 200K map entry to 1M switches pairs, zero
  cutoff disables, ctor validation. Doc references to `ResolveThresholds` in
  `ModelCatalogEntry` and the settings-loader comment rewritten.
- `SessionManagerTests.CreateAsync_wires_pressure_crossings_to_RotationReason_on_the_context`
  reverted to a no-model spawn request вЂ” it now pins the default-session
  shape getting 45/65 dynamically (the earlier explicit-model workaround is
  gone).
- Verified: full suite 1096 passed / 3 platform skips; `dotnet format
  --verify-no-changes` clean.

### Re-verification (2026-06-11, fresh session)

- New session opened on task 0018. Working tree confirmed to match the prior
  green state вЂ” all spec edits present: `KnownModels.Catalog` Opus/Sonnet =
  `1_000_000` (Haiku `200_000`); `InfiniContextSettings.ModelContextWindows`
  `[Reviewer]`/`[Main]`/`[MainLargeContext]`/`[Fable]` = `1_000_000`, `[Light]`
  = `200_000`; `settings.json` `models.available` opus/sonnet/fable
  `contextWindowTokens` = `1000000`, haiku `200000`.
- `dotnet build src/InfiniContext.Core` в†’ success, 0 warnings / 0 errors.
- `dotnet format InfiniContext.slnx --verify-no-changes` в†’ clean.
- Full-suite re-run NOT performed: the operator's 4 `InfiniContext.Gui`
  processes lock `InfiniContext.Gui.exe`, and the test project references the
  GUI project, so `dotnet build`/`dotnet test` on the solution fail with
  MSB3021 (same blocker as the earlier session). Nothing changed in the tree
  since the last green full-suite run, so this is a re-confirmation gap only,
  not a regression risk. Re-run once the GUI is closed.
- **Task implementation is complete.** Remaining: optional full-suite re-run
  (needs GUI closed), then `/task review` and/or commit.

### Review + close-out (2026-06-11)

- Reviewed the full diff against `research.md`/the plan — all five spec edits,
  the window-driven threshold-pair follow-up, and the test moves check out.
- **One gap found and fixed:** the description says Claude Code offers Sonnet
  as separate 200K/1M entries, so `system/init` can carry a
  `claude-sonnet-4-6[1m]` id — but the fallback map only kept the opus `[1m]`
  key. Added `KnownModels.ReviewerLargeContext` (`claude-sonnet-4-6[1m]`) and
  the `ModelContextWindows` entry (= 1M), mirroring the opus pattern; updated
  the `Tasks_defaults_snapshot` test. Without the key a sonnet-1M session had
  no fallback window until the first `result` self-report.
- **Full-suite re-run unblocked:** the GUI lock only holds `bin\Debug`, so
  `dotnet test InfiniContext.slnx -c Release` (CI's config) runs clean with
  the GUI open. Result: **1101 passed / 0 failed / 3 platform skips**.
  `dotnet format --verify-no-changes` exit 0.
- Committed the task's file set (Core config/monitor/session-manager, tests,
  `settings.json`, task artifacts) and pushed. Unrelated working-tree changes
  (GUI/0017/0019/light-model/docs) deliberately left uncommitted.
- **Task done.**