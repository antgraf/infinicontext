# InfiniContext — Open Engineering Items

Snapshot date: **2026-05-08** (post Session 5 of `.claude/tasks/20260507-manual-test-session/`).

This list is the carry-over from the manual-test-session task after auto-approval performance and `/task` home_cwd realignment landed. Each item is written to be picked up cold — file paths, decisions to consult, acceptance criteria, and cost estimates inline. Items are independent unless noted.

When you complete an item, **delete its section from this file** (the canonical truth lives in code + decision records; this file is a queue, not a log).

---

## 1. `AutoApprovalSink` → `IAsyncDisposable` (close the long-lived approval leak)

**Status:** actionable. Bounded scope.

**Motivation.** `LongLivedCliLightModelService` holds a `claude` child alive across calls. With default settings (`approval.auto.mode = "SingleShot"`) the auto-mode safety reviewer doesn't hit this — it constructs a `SingleShotCliLightModelService` which has no long-lived state. But if a user sets `approval.auto.mode = "LongLived"` (legal, supported, just not the default), every `/approval auto` toggle in the REPL constructs a fresh `LongLivedCliLightModelService` via `ApprovalSinkFactory.BuildInner` → `_lightModelFactory(effort)`. The old `AutoApprovalSink` (and its inner light model) becomes unreferenced; its `claude` child is never killed until host exit (Job Object reaps it on Windows). Cumulative cost: one orphan `claude` process per `/approval` toggle.

Session 5 hit the equivalent issue for `lightModelForTasks` and fixed it via `await using var _ = … as IAsyncDisposable;` in `Program.cs` (commit `c5e6525`). The `light` inside `ApprovalSinkFactory` has the same shape but is harder to fix because (a) `AutoApprovalSink` doesn't currently implement `IAsyncDisposable`, (b) `SwitchableApprovalSink` swaps its inner sink without disposing the old one, and (c) `ApprovalSinkFactory.Build` returns `IApprovalSink` (an interface that doesn't extend `IAsyncDisposable`).

**Files / lines:**
- `src/InfiniContext.Cli/AutoApprovalSink.cs` — make it `IAsyncDisposable`; if `_lightModel is IAsyncDisposable d` then dispose on `DisposeAsync`.
- `src/InfiniContext.Cli/ConsoleEchoingApprovalSink.cs` — wrapper. Forward `DisposeAsync` to the inner sink if it's `IAsyncDisposable`.
- `src/InfiniContext.Core/Mcp/IApprovalSink.cs` (or wherever the interface lives) — leave alone; do **not** make `IApprovalSink` extend `IAsyncDisposable`. Keep disposal opt-in via cast.
- `src/InfiniContext.Cli/SwitchableApprovalSink.cs` — on `Switch(newSink)`, schedule the OLD inner sink for background disposal (fire-and-forget `Task.Run` like D13's pattern, so /approval toggles don't hang while the previous long-lived child winds down).
- `src/InfiniContext.Cli/ApprovalSinkFactory.cs` — no change needed; the wrapping happens at sink-build time.
- `src/InfiniContext.Cli/Program.cs` — `await using` the initial sink and the SwitchableApprovalSink so end-of-program disposes the current chain.

**Decisions to consult:**
- D13 in `.claude/tasks/20260507-manual-test-session/decisions.md` — the background-disposal pattern. Apply it for `/approval` toggles; the user explicitly liked dynamic switching feel.

**Acceptance:**
- Test: switch approval mode auto → ask → auto repeatedly with `approval.auto.mode = "LongLived"`; assert no orphan `claude` processes accumulate (compare `Process.GetProcessesByName("claude")` count before/after, with grace for startup races).
- Test: at host shutdown, AutoApprovalSink.DisposeAsync runs and the long-lived child terminates within 1 s.
- Manual: full suite (`dotnet test InfiniContext.slnx`) green; no regression.

**Cost:** ~30–60 minutes. Pure code; no API calls.

---

## 2. `MAX_OUTPUT_TOKENS` ceiling for thinking-mode (bench)

**Status:** actionable. Wall-clock + ~$0.22 of API tokens.

**Motivation.** `CliLightModelOptions.ThinkingMaxOutputTokens` is currently `1024` — a placeholder per **D9** sub-question in `.claude/tasks/20260507-manual-test-session/decisions.md`. The reasoning was: non-thinking is comfortable at 128 tokens (matches a 4-field JSON verdict), but thinking-on needs room to think before emitting JSON. 1024 was a safe-but-untested upper bound. Goal: find the smallest cap that doesn't truncate verdicts on the standard 10-payload probe set, commit the data, and bump the default if appropriate.

**Files / artifacts:**
- Bench harness: `.claude/tasks/20260507-manual-test-session/artifacts/bench-singleshot-tight.py` — currently configured for nothink+cap. Fork to a thinking-on variant; pass `effort=high` semantically.
- Standard probe set: same 10 payloads as the published research (see `docs/research/claude-cli-light-model.md` §6 / Appendix A).
- Setting to potentially update: `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` — `CliLightModelOptions.ThinkingMaxOutputTokens` default.
- Settings schema: `settings.schema.json` for the matching default + description update.
- Result CSVs: write to `docs/research/claude-cli-light-model/results-thinking-cap-{512,1024,2048}-n10.csv`.

**Procedure:**
1. Fork `bench-singleshot-tight.py` → `bench-singleshot-thinking.py`. Drop `CLAUDE_CODE_DISABLE_THINKING=1`. Parameterize `CLAUDE_CODE_MAX_OUTPUT_TOKENS`.
2. Run N=10 at each of {512, 1024, 2048}. Record wall_ms, api_ms, in_tok, out_tok, total_cost_usd, and **whether the JSON verdict parsed** (the truncation signal — if the model ran out of tokens mid-JSON, parsing fails).
3. If 512 produces ≥1 truncated verdict on the standard set, recommend 1024. If 1024 produces ≥1 truncated, recommend 2048. The smallest cap with zero truncations across the probe set wins.
4. Update `CliLightModelOptions.ThinkingMaxOutputTokens` default to the chosen value.
5. Append a "Thinking-mode cap bench" subsection to `docs/research/claude-cli-light-model.md` and reference the new CSVs.

**Decisions to consult:**
- D8 — recipe for nothink+cap (the baseline this is symmetric to).
- D9 — four-corner matrix; the open sub-question this bench answers.

**Acceptance:**
- Three CSVs committed under `docs/research/claude-cli-light-model/`.
- Recommended cap committed in `InfiniContextSettings.cs` + schema, with a one-line decision update in the manual-test-session `decisions.md` (extend D9 or add D14).
- Research doc updated.

**Cost:** ~30 calls × ~$0.0074/call ≈ **$0.22 in API tokens** (less if a cap truncates and the call costs less). ~10 minutes wall time + write-up.

---

## 3. Two-tier escalation: `effort=low` auto-retry as `effort=high` on `confidence=low`

**Status:** **deferred — design open question.** Do not implement without explicit user buy-in.

**Motivation.** D10 added `approval.auto.effort: "low" | "high"`. Low is fast/cheap (D8 recipe, ~2.3 s/call). High is slower (~9 s/call) but better on ambiguous cases. The open idea: when `effort=low` returns `"confidence": "low"`, the host could automatically re-call with `effort=high` for a smarter verdict. Better UX in the common case (fast); smart when it matters.

**Why this is deferred:** Doubles worst-case latency. Doubles cost on the ambiguous-but-actually-fine path (today: low-confidence allow → ask the human). The current behavior (`onLowConfidence: "Deny"` default; `"FallbackToAsk"` opt-in) is well-defined. Auto-escalation is "smart but slow" instead of "fast but pessimistic." Not obviously the right trade-off without user feedback that the deny/ask fallback is annoying in practice.

**What an agent picking this up would do:**
1. **First, discuss with the user.** Confirm there's enough pain with the current low-confidence handling to justify the latency hit on ambiguous calls. If not, leave deferred.
2. If proceeding: add `approval.auto.escalateOnLowConfidence: bool` (default `false` to preserve current behavior). When true and `effort=low` returns confidence=low, AutoApprovalSink re-invokes the light model with thinking enabled, accepts the high-effort verdict.
3. Wire into `AutoApprovalSink.RequestAsync` after the `review.Confidence == Low` branch.

**Files (when ready):**
- `src/InfiniContext.Cli/AutoApprovalSink.cs`
- `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` (`AutoApprovalOptions`)
- `settings.schema.json`

**Decisions:** D10 — the framing this would extend.

**Cost:** ~1–2 hours code + tests + a few real-world API calls to validate. Don't start without user say-so.

---

## 4. Per-tool effort overrides

**Status:** **deferred — explicitly post-v1.** Do not implement without explicit user buy-in.

**Motivation.** Idea: `approval.auto.effort.bash: "high"`, `approval.auto.effort.read: "low"`, etc. — different tools could carry different latency/cost trade-offs.

**Why deferred:** Probably overkill. The current single dial is simple and the per-tool risk landscape isn't dramatically different — a destructive Bash and a destructive Edit are both destructive; thinking-mode helps both equally.

**What an agent picking this up would do:** Discuss with the user first. If they confirm, design the override schema (`approval.auto.effortOverrides: { bash: "high", … }`?) and wire through `AutoApprovalSink` to per-tool effort lookup before each call.

**Decisions:** D10. This would amend it.

**Cost:** ~2–3 hours including schema + tests.

---

## 5. Long-lived crash-recovery retry policy

**Status:** **open sub-question.** Code today: `LongLivedCliLightModelService.ExecuteAsync` kills the child on any mid-call exception and rethrows; the next call respawns transparently. No automatic mid-call retry.

**Motivation.** A child crash mid-call surfaces as an exception to the caller (auto-mode → fallback to ask; title generation → user sees an error). One transparent respawn-and-retry would smooth over rare crashes at the cost of doubling the worst-case latency for the affected call.

**Open questions for the user before any code:**
1. Has anyone observed a real `claude` child crash mid-call in the wild? If not, this is a theoretical fix; defer until evidence.
2. Should retry apply only to title generation (where a one-off slowdown is fine) or also auto-mode safety review (where a 2× latency hit might be worse than just falling through to ask)?
3. Single retry, or exponential?

**Files (when ready):**
- `src/InfiniContext.Core/LightModel/LongLivedCliLightModelService.cs` — wrap `child.RunTurnAsync` in a retry loop guarded by `RetryOnCrash` setting.
- Settings: a new `CliLightModelOptions.RetryOnCrash: bool` (default `false`)?

**Cost:** ~1 hour code + tests once the policy is decided.

---

## 6. Re-record fixtures vs `claude` 2.1.133 (and bump `TestedAgainstVersion`)

**Status:** actionable but **non-trivial risk** — `tests/fixtures/README.md` and `CLAUDE.md` both flag "do not regenerate casually."

**Motivation.** Committed NDJSON fixtures under `tests/fixtures/` are from `claude` 2.1.112. Current binary on this machine is 2.1.133. The host warns at startup when the live version differs from `versioning.testedAgainstVersion`. Real protocol drift (new event types, schema changes) becomes visible via the parser drift counter, but regression coverage benefits from fixtures matching the version we ship against.

**Procedure:**
1. Read `tests/fixtures/README.md` and `tests/fixtures/NOTES.md` end-to-end first. They contain the "why this fixture exists" for each one.
2. Use `tools/InfiniContext.Recorder/` (already exists) to re-record each scenario. Recorder spawns real `claude` and writes raw NDJSON.
3. **Eyeball-diff** old vs new for each fixture before replacing. Schema drift between 2.1.112 and 2.1.133 is the whole reason this exists; understanding the diffs is the deliverable.
4. Run `dotnet test InfiniContext.slnx` after replacement. Failing tests are real (parser regression) — fix or document.
5. Bump `versioning.testedAgainstVersion` in `settings.json` to `2.1.133`. Update `versioning.minClaudeVersion` only if a real incompatibility is found (otherwise keep at `2.0.0`).

**Decisions to consult:**
- The "Versioning policy" and "Parser tolerance is load-bearing" sections of `CLAUDE.md`. Both apply.

**Risk:** Fixture replacement could mask regressions if done sloppily. The point of the fixtures is to be a **frozen** reference; updating them resets that reference. Worth doing only when (a) we're going to ship soon and want the regression surface tight, or (b) protocol drift has accumulated enough that the existing fixtures don't exercise current-shape events.

**Cost:** ~2–4 hours. Mostly review work, not code.

---

## Cross-cutting context

- **Memory files** at `~/.claude/projects/C--src-infinicontext-poc/memory/`:
  - `feedback_avoid_kill_process_tree_windows.md` — relevant for any new `Process.Kill` call.
  - `feedback_cli_flags_before_freeform.md` — relevant for any new slash command with a long free-form text arg.
- **Decision records** at `.claude/tasks/20260507-manual-test-session/decisions.md` — D1–D13 cover the light-model architecture. New decisions should append.
- **Research doc** at `docs/research/claude-cli-light-model.md` — canonical, externally-publishable. Any new bench data goes into the sister `docs/research/claude-cli-light-model/` folder.
- **Build/test commands** are in `CLAUDE.md` at the repo root.
