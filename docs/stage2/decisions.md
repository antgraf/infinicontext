# InfiniContext — Consolidated Decisions

**Snapshot date:** 2026-05-14 (Stage 2B Phase 7 shipped; Phase 8 in design — D36 sign-off pending)
**Scope:** every decision that constrains future implementation, gathered from `CLAUDE.md`, `README.md`, `docs/infinicontext-initial-design.md`, `docs/cli/howto/task-commands.md`, `docs/research/claude-cli-light-model.md`, `tests/fixtures/{README,NOTES}.md`, `tests/scripts/README.md`, and every `.claude/tasks/*/` and `.task/*/` folder.

Original IDs (`D1`–`D13`, `I1`–`I8`, etc.) are preserved so cross-references in the source folders still resolve. Where a later decision superseded an earlier one, both are kept with a `Superseded by:` link.

---

## 1. Wire protocol & process model

- **No first-party .NET SDK; this repo is the SDK client.** InfiniContext spawns `claude` as a child in stream-json mode and parses NDJSON. Source: `CLAUDE.md`.
- **argv is centralized in `ClaudeArgvBuilder`.** Flag order matters — `--verbose` must precede `--output-format`. Why: order-sensitive at call site. Source: `CLAUDE.md`.
- **Default mode is per-turn batched, not streamed.** True deltas only via `--include-partial-messages`. Source: `CLAUDE.md`.
- **CLI defaults to `run` when invoked with no args.** `infinicontext.exe` boots REPL with `cwd=Directory.GetCurrentDirectory()`; leading `--flag` treated as `run --flag …`. Source: 20260506-poc/decisions.md.
- **Stage 1 form: console REPL, not argv single-prompt.** Why: exit criteria require multi-turn + tool approval + Ctrl+C mid-stream. Source: 20260506-poc/decisions.md.
- **TUI/terminal embedding dropped entirely.** No ConPTY/WebView2/xterm.js/forkpty at any stage; render conversation natively as collapsible tree view at Stage 3. Why: stream-json is strictly richer than terminal escapes; removes platform code with no info gain; enables Mac. Source: 20260506-poc/decisions.md, `infinicontext-initial-design.md` Appendix A.
- **Single-mode transport: stream-json over stdin/stdout.** No second mode in the design. Source: `infinicontext-initial-design.md`.
- **Target platforms: Windows + macOS.** Stage 1 Windows-first; macOS validation is a Stage 2 exit criterion. Linux not validated. Source: `infinicontext-initial-design.md` §2.

## 2. Parser & state machine

- **Parser tolerance is load-bearing.** Unknown event types → synthetic `unknown_event` records (full raw JSON preserved); unknown fields on known events kept in `Extra` `JsonElement`. A per-session drift counter is the real signal that a `claude` upgrade broke something — not the version check. Never use closed POCOs that drop unknown data. Source: `CLAUDE.md`.
- **Parser implementation: `JsonNode` round-trip, not hand-rolled converters.** `StreamJsonParser` uses `JsonNode.Parse` per line; stores original `JsonObject` in `ClaudeEvent.Raw`. Why: simplest way to satisfy round-trip tolerance contract. Source: 20260506-poc/decisions.md.
- **State machine has tool-use correlation map + separate approval flag.** Keyed by `tool_use.id`; sub-agent events (Task tool) correlate via `parent_tool_use_id`; `awaiting-approval` is a flag, not a state. Source: `CLAUDE.md`.
- **`SessionStateMachine` internal lock.** `lock(_sync)` around `BeginSend`, `Cancel`, `HandleProcessExit`, `Apply`; handlers fire synchronously holding lock, must not re-enter. Post-`Exited` `Apply` early-returns. Source: 20260506-poc/decisions.md, 20260506-review-stage1-poc.
- **Events channel uses `DropOldest`, not `Wait`.** `ClaudeSession._events` bounded channel with `BoundedChannelFullMode.DropOldest`. Why: prevents stdin/stdout deadlock chain — slow renderer → channel fills → child stdout fills → child stops reading stdin → `SendUserMessageAsync` hangs holding `_writeLock` → `InterruptAsync` blocked. State machine is updated BEFORE channel write, so dropping is safe. Source: 20260506-poc/decisions.md. Stage 2 may revisit with a dedicated stdin-writer task.
- **Turn boundary = arrival of `result` event.** State machine transitions to Idle there. Source: `tests/fixtures/NOTES.md`.
- **`control_response` is a known event type.** Added `ControlResponseEvent` to the taxonomy. Why: preserves drift counter integrity by not counting our own protocol echoes as unknowns. Source: 20260506-poc/decisions.md.
- **`Cancel()` removed from `IClaudeSession` public surface; `InterruptAsync` is the only public interrupt path.** Supersedes the earlier 1G design where both existed. Source: 20260506-poc/decisions.md.
- **C1 — Widened token sums from `int` to `long` end-to-end** in `ContextPressureMonitor`; added coercive `TryReadLong` helper (long → int → double fallback). Why: handles `JsonValueOfElement` vs `JsonValueOfT<int>` asymmetry and future-proofs against `123.0` decimals. Source: 20260507-discuss-and-apply/decisions.md.

## 3. Persistence

- **Host does not mirror Claude's session store.** Read from `~/.claude/projects/<encoded-cwd>/<session_id>.jsonl`; write only host-only index/metadata under `~/.infinicontext/`. Why: two stores create two sources of truth and break across upgrades. Source: `CLAUDE.md`, 20260506-poc/decisions.md.
- **Path resolution discipline.** `Environment.GetFolderPath(SpecialFolder.UserProfile)` + `Path.Combine` exclusively; no string concat with separators. Why: cross-platform from day one. Source: `CLAUDE.md`.
- **Original POC design: flat JSON index, no SQLite/LiteDB.** Source: 20260506-poc/decisions.md, `infinicontext-initial-design.md` §4.5. **Superseded by:** the tasks/sessions-management task (SQLite pulled forward — see §10).
- **SQLite for host state, pulled forward to Stage 2A.** `~/.infinicontext/infinicontext.db` via `Microsoft.Data.Sqlite` holds task identity, lineage, timestamps, status. Why: global durable IDs across all repos require a real DB; folder-derived IDs are only per-repo. Source: 20260507-tasks-and-sessions-management/decisions.md.
- **DB ahead of code = refuse to start.** Either upgrade back or delete `~/.infinicontext/infinicontext.db`; on-disk `.task/` is the true source of truth. Source: `docs/cli/howto/task-commands.md`.
- **Tolerate `~/.claude/projects/` layout drift.** Layout has shifted across `claude` releases; document layout observed at `TestedAgainstVersion`. Source: `infinicontext-initial-design.md` §4.5.

## 4. Process supervision

- **`IProcessSupervisor` abstraction; Windows = Job Object with `KILL_ON_JOB_CLOSE`; Stub backend for tests.** Why: child `node` processes must die with host on Windows; abstraction lets macOS slot in at Stage 2. Source: `CLAUDE.md`, 20260506-poc/decisions.md.
- **macOS supervision via layered defenses.** `PosixSignalRegistration` + `AppDomain.ProcessExit` + stray-process startup scan; no Job Object equivalent on Mac. Source: `infinicontext-initial-design.md` §5.
- **Stage 1E: dispose-kills-child is the OS-contract test.** Test by disposing `JobObjectSupervisor` and polling, not by spawning an external host. Why: closing job handle walks same kernel path as Windows closing on host termination; external harness validates Windows not us. Source: 20260506-poc/decisions.md.
- **`<AllowUnsafeBlocks>true>` in Core** — required by `LibraryImport` source generator (no actual unsafe code in source). Source: 20260506-poc/decisions.md.
- **`Process.Kill(entireProcessTree: true)` is forbidden in CLI light-model disposal path (D12).** Always `Kill(entireProcessTree: false)`. Why: tree-walk on Windows took ~16.5 s per disposal due to AV scanning `Process.GetProcesses()`; light-model child has no descendants by construction. Source: 20260507-manual-test-session/decisions.md D12. Also captured in user memory.

## 5. Logging

- **Per-run Serilog files at `logs/{YYYYMMDD-HHmmss}.log`.** Sidecar uses a `sidecar`-prefixed file. Every `tx:`/`rx:` event logged at Debug, truncated to `maxFieldBytes` from settings. Why: log is the agent's primary debugging surface — no GUI. Source: `CLAUDE.md`, 20260506-poc/decisions.md.
- **`SecretScrubbingEnricher` at emission time.** Property-name regex `KEY|TOKEN|SECRET|PASSWORD|PASSWD|PWD|AUTH|AUTHORIZATION|BEARER|COOKIE|CREDENTIAL|CREDENTIALS`; value-shape detection for `sk-*`, JWTs, `Bearer <token>`, `Authorization: <…>`. Why: closes `ExtraArgs` leak (`--header Authorization=Bearer…`). Source: Stage 1 review.

## 6. Settings & configuration

- **Three-layer settings, loaded in order.** `settings.json` (committed) → `settings.user.json` (gitignored) → `INFINICONTEXT__SECTION__KEY` env vars. `--settings <path>` bypasses discovery. Validation hard-fails (exit 3). CLI args (`--model`, `--include-partial-messages`, …) take precedence. Why: every tunable was a code constant; M.E.Configuration was smallest path. Source: `CLAUDE.md`, 20260506-stage1-new-features/decisions.md.
- **`settings.user.json` must never be staged or committed.** Source: `CLAUDE.md`.
- **Tests must never load `settings.user.json` or env vars.** Default = `InfiniContextSettings.Defaults`; escape hatch = `LoadOptions { BaseDirectory=<temp>, IncludeUserOverrides=false, IncludeEnvironmentVariables=false }`. Why: hermetic tests. Source: `CLAUDE.md`, 20260506-stage1-new-features/decisions.md.
- **Hot-reload of settings without restart — rejected for now.** Stage 1 sessions are short-lived; not worth threading concerns. Source: 20260506-stage1-new-features/decisions.md.
- **Format-string templates with `{Placeholder}` substitution — rejected.** Too brittle for low value; only no-placeholder cosmetic strings exposed. Source: 20260506-stage1-new-features/decisions.md.
- **Hardcoded protocol constants stay hardcoded.** claude argv, MCP server names, log output template, secret regex, log filename timestamp format — all kept hardcoded as load-bearing. Source: 20260506-stage1-new-features/decisions.md.
- **D11 — Remove `Cosmetics.Repl.HelpText` setting.** Drop the field; in-REPL help is registry-generated. Source: 20260509-improve-cli-repl-help/decisions.md.

## 7. Versioning policy

- **`MinClaudeVersion` (hard fail) + `TestedAgainstVersion` (warn).** Drift counter is the load-bearing safety net, not version match. Why: hard pin doesn't survive heterogeneous installs. Source: `CLAUDE.md`, 20260506-poc/decisions.md.
- **Initial `TestedAgainstVersion` = 2.1.112** (provisional). `MinClaudeVersion = 2.0.0`. Bump on intentional upgrades. Source: 20260506-poc/decisions.md.
- **`ClaudeVersionPolicy.Check(observedVersion)` runs on first `system/init`.** Classifies into `ExactMatch | DriftAbove | DriftBelow | BelowMinimum | Unrecognised`; `BelowMinimum` currently logs error but does NOT block (POC); drift logs warning. SemVer tolerates `v` prefix + suffixes. Source: 20260506-poc/decisions.md.

## 8. Approval pipeline (MCP sidecar)

- **Built-in approval MCP server, 60s timeout → auto-deny, never modal.** Why: closed UI mid-prompt would hang `claude` forever; modal breaks multi-tab broadcast. Source: 20260506-poc/decisions.md, `CLAUDE.md`.
- **Sidecar reuses the CLI binary; in-process MCP server not feasible.** `claude` spawns the MCP server itself; re-launch `InfiniContext.Cli` with `--mcp-approval-server <pipe>`. Why: can't hand a pre-existing pipe pair to claude. Source: 20260506-poc/decisions.md (Stage 1H).
- **Sidecar stdout reserved for JSON-RPC.** Early short-circuit in `Program.cs` before any other startup. Why: protocol corruption if any other byte hits stdout. Source: `CLAUDE.md`.
- **`--permission-prompt-tool` is hidden in `claude --help` but functional in 2.1.112.** Wire it anyway; verified empirically. Risk: future claude could remove without notice. Fallback: drop flag, use `--permission-mode bypassPermissions` or Agent SDK `canUseTool`. Source: 20260506-poc/decisions.md, `tests/fixtures/NOTES.md`.
- **Approval response uses a double-encoded MCP envelope.** Tool returns `{content:[{type:"text", text:<JSON-stringified payload>}], isError:false}`. Why: anything else, claude treats as malformed and silently denies. Source: 20260506-poc/decisions.md.
- **Sequential argv overrides on approval enablement.** When `IApprovalSink` is provided, `ClaudeSession.StartAsync` overrides `PermissionPromptTool`/`McpConfigPath`/`StrictMcpConfig` (host-reserved); `AllowedTools` is merged. Source: 20260506-poc/decisions.md.
- **`McpServer` dispatch is fire-and-forget; `RunAsync` awaits in-flight before returning.** Dispatch via `Task.Run`; tasks tracked; finally awaits all. Why: slow approval handler can't stall unrelated `tools/list`. Source: 20260506-poc/decisions.md.
- **`SidecarSmokeTests`: read responses into a dict by `id`, not positionally.** Generalisation: every MCP test with multiple requests on one connection must de-multiplex by id. Why: `Task.Run` dispatch makes reply order non-deterministic. Source: 20260506-poc/decisions.md.
- **Four approval modes: `ask | deny | allow | auto`; `auto` is the default.** `auto` consults `ReviewToolCallSafetyAsync` via the light model. `allow` refused as a persisted default (CLI/REPL-only with banner). No `ANTHROPIC_API_KEY` + auto → transparent fallback to `ask`. Source: 20260506-stage1-new-features/decisions.md.
- **`ConsoleEchoingApprovalSink` decorator + `SwitchableApprovalSink` for mid-session `/approval` hot-swap.** Stage 2B Phase 5 (2026-05-13) moves `SwitchableApprovalSink` from `InfiniContext.Cli` to `InfiniContext.Core.Mcp` (D32) and routes the echoing decorator's output through an `Action<string>` so per-session transcripts get the decision line instead of a shared `Console.Out` (D34). Source: 20260506-stage1-new-features/decisions.md; Stage 2B D32/D34.
- **C4 — Wrap `_cts.Dispose()` in `try/catch (ObjectDisposedException)`** inside `ApprovalChannel.DisposeAsync`. Why: timeout path can dispose CTS while `RunServerAsync` still uses its token. Source: 20260507-discuss-and-apply/decisions.md.
- **A8(a) — Explicit `ApprovalKind` switch + `default:` returning `{"error":"unknown kind: <kind>"}` with Warning log.** Why: future sidecar emitting new kinds against older host gets a clean error rather than a crash. Source: 20260507-discuss-and-apply/decisions.md. (A8(b) `protocol_version` field deferred.)
- **C2(a) — Cross-rule validator: `tasks.lockTtlSeconds > approval.timeoutSeconds` rejected.** Only fires when both individual ranges are valid (avoids double-reporting). Why: TTL<approval-timeout would mask lock starvation. Source: 20260507-discuss-and-apply/decisions.md.

## 9. Light-model service (D1–D13, I1–I8)

The light-model is a separate, cheap channel for one-shot AI tasks (auto-approval safety review, title generation, classification). Decisions number-by-number from `20260507-manual-test-session/decisions.md` (D-series, *Final* unless noted) and `20260507-implement-light-model-for-non-api-subscriptions/decisions.md` (I-series).

### Backbone

- **`ILightModelService` supports the full {single-shot, long-lived} × {thinking, non-thinking} matrix** (D9). Why: isolation and latency/quality are independent axes; different consumers pick different combos.
- **I1 — `Thinking` is a constructor argument, not a setting.** Why: auto-mode derives it per-instance from `approval.auto.effort` (D10); putting it in settings would tempt callers to read from a global.
- **I2 — Build CLI light-model argv in its own helper (`CliLightModelInvocation`), not by extending `ClaudeArgvBuilder`.** Session uses `--permission-prompt-tool`/`--mcp-config <pipe>`/`--allowed-tools`; light-model needs the opposite (`--tools "" --strict-mcp-config --mcp-config '{"mcpServers":{}}' --setting-sources project --disable-slash-commands --no-session-persistence`).
- **I3 — Settings layout nests CLI knobs under `models.light.cli`.** Why: `Id`/timeout shared across SDK and CLI; CLI-only knobs belong under their own key; keeps schema diff additive.
- **I4 — Two `MaxOutputTokens` caps: 128 (thinking off) and 1024 (thinking on).** The 1024 default is a placeholder pending the bench in todo.md item 2. Why: 128 matches a 4-field JSON verdict; with thinking on, model needs room before emitting JSON.
- **I7 — `ManagedCwd` defaults to `%USERPROFILE%/.infinicontext/light-cwd/`** (auto-created). Why: avoids `CLAUDE.md` auto-discovery and `.mcp.json` auto-load.

### Auto-mode

- **D1 — Auto-mode requires `ANTHROPIC_API_KEY` (provisional).** Effectively superseded by D8–D11: OAuth fallback via CLI light-model is now viable at 2.3 s / $0.0007.
- **D4 — Long-lived `claude` light-model child rejected for safety review.** Only reliable isolation is process respawn, which kills the amortization benefit.
- **D5 — Single-shot CLI is the practical OAuth path** for safety review.
- **D8 — Disable thinking + cap output tokens for the CLI light-model.** `CLAUDE_CODE_DISABLE_THINKING=1`, `CLAUDE_CODE_MAX_OUTPUT_TOKENS=128`, tightened "no preamble/postamble" prompt. Why: collapses wall p50 from 9.2 s → 2.3 s and cost $0.0074 → $0.00071 per call.
- **D10 — `approval.auto.effort: "low" | "high"`.** Single user-facing knob maps to both SDK `thinking` parameter and CLI env var. Low is default.
- **D13 — Light-model child disposal happens off the approval hot path** via background `Task.Run`. Why: disposal still cost ~440 ms after D12. Job Object `KILL_ON_JOB_CLOSE` covers worst case.

### `/clear` is not a reset

- **D2 — `/clear` is not a reliable reset primitive over stream-json.** REPL-only slash command; in stream-json mode it's forwarded as plain text. `in_tok` grew 293→3 983 over 9 consecutive `/clear` cycles.
- **D7 — `/clear` brokenness is not version-specific.** D2 holds across 2.1.112 → 2.1.133.

### MAX-stripped config

- **D3 — MAX-stripped config is the canonical OAuth-compatible CLI shape.** Exact recipe: `--tools "" --strict-mcp-config --mcp-config '{}' --setting-sources project --disable-slash-commands --no-session-persistence` plus `ENABLE_CLAUDEAI_MCP_SERVERS=false`, `CLAUDE_CODE_DISABLE_AUTO_MEMORY=1`, `CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS=1`, `CLAUDE_CODE_DISABLE_CLAUDE_MDS=1`, `CLAUDE_CODE_DISABLE_POLICY_SKILLS=1`. Why: gets `input_tokens` floor to ~158 vs 45 K cache_create baseline.
- **D6 — Native binary is the right benchmark target.** All `claude` benchmarks target `~/.local/bin/claude.exe`; bench scripts auto-resolve via `shutil.which("claude")`.

### Long-lived service

- **D11 — Long-lived non-thinking sessions for non-isolation-sensitive workloads** (title/summary generation, classification). Originally with respawn cadence (every 50 turns or 10 min) — see I5 supersession.
- **I5 (SUPERSEDED by I8)** — original: respawn-after-N evaluated at start of each call, never mid-call.
- **I8 (Final)** — long-lived service sends `/clear` between turns instead of using a respawn cadence. `RespawnAfterTurns`/`RespawnAfterMinutes` settings removed; crash-respawn (I6) and prompt-change respawn remain. Why: user accepts cosmetic + TTL-bounded isolation for simple isolation-tolerant tasks; respawn cost is unwanted.
- **I6 — Long-lived crash recovery: respawn once transparently if child has exited between calls; mid-call exit fails the call with a clear error and no auto-retry; next call respawns.** Why: auto-retry hides genuine errors and makes cost/latency budgeting opaque.

### Approval is SDK-preferred when key is present

- **Default to SDK + API key when available; CLI fallback only for OAuth.** SDK ~500 ms / <$0.001; CLI 2.3 s / $0.00071. Source: `docs/research/claude-cli-light-model.md` §9.

## 10. Tasks & sessions management (Stage 2A)

- **MVP anchor.** "User has a fully self-contained durable-task workflow inside InfiniContext — task lifecycle, parallel-session coordination, and context-pressure handoff with no external task-management dependency." Source: 20260507-tasks-and-sessions-management/decisions.md.
- **Artifacts are plain markdown** at `<home_cwd>/.task/<id>-<slug>/*.md`. No MCP-served artifact content; no artifact content in SQLite. Why: agents already know markdown; custom MCP for artifact ops adds tool-call latency.
- **Storage location: `.task/`, not `.claude/tasks/`.** Why: recent Claude Code adds permission-prompt friction for `.claude/`.
- **No `artifacts/` subfolder.** All `.md` files at root of `.task/<id>-<slug>/`. Why: agents conflate "task folder" and "artifacts folder".
- **Mandatory artifacts: `index.md`, `description.md`, `progress.md`.** Optional: `plan.md`, `research.md`, `decisions.md`, `design.md`, plus anything else. Why: `plan.md` ≠ `progress.md` (intent vs log); `description.md` over `context.md` because "context" is overloaded.
- **Index self-heal is non-invasive.** Host prepends a one-line reconcile note to first user message when on-disk files aren't registered; never silently rewrites the index. Why: keeps the agent as the author of its own artifacts.
- **Skills installed at user level (`~/.claude/skills/infinicontext-task/`)** on first launch. Why: Claude Code auto-discovers user-level skills → zero per-session pre-fill cost for rules.
- **Skill invocation: Mechanism 5B (explicit pointer).** First user message includes "Use the `infinicontext-task` skill." Why: empirical — explicit reference fires skill on turn 1 deterministically at 3 turns / $0.09; cwd-only reliance requires Claude to `Bash ls` first (4 turns / $0.22, not guaranteed).
- **Per-session pre-fill is just a pointer** — task id, title, folder path, "Use the `infinicontext-task` skill." Why: rules in the skill; state in `index.md`; agent loads what it needs on demand.
- **Cross-repo: single `home_cwd` per Task.** Agent freely references other repos from there. Multi-repo task storage is **rejected**, not deferred.
- **Parallel-session coordination via MCP lock.** `task.acquire_lock(file)` + `task.release_lock(token)`, TTL 30 s default. Agent contract (in skill): acquire → Read → Edit → release; release on error. Why: Claude has no cross-session exclusive-edit primitive.
- **Lock failure modes are accepted risks for MVP.** Accept (a) crashed agent leaving partial file before TTL expiry, (b) OS-level write truncation on process kill. No write-via-temp-rename, no full-file-`Write` requirement.
- **Soft & hard context thresholds.** Soft 60% polite nudge; hard 85% firm turn. Force-flush guardrail explicitly deferred from MVP — single firm turn only, trusts agent + skill v2.
- **Active-task identification: host-derived from SQLite.** No `.active` marker file. Why: marker files drift; SQLite is already source of truth.
- **Merge tiers: deterministic concat (MVP), light-model, full-model.** Default `/task merge` is full-model session. v1 ships with concat-only; other tiers throw `NotImplementedException`.
- **All merge sources must share `home_cwd`.** Cross-home merge is post-MVP.
- **A3 — `Repl` refuses `/task switch` when `home_cwd` differs case-insensitively from `_hostCwd`.** Why: pointer is relative to `.task/<id>-<slug>/`; switching in wrong cwd dereferences a missing folder. Source: 20260507-discuss-and-apply/decisions.md.
- **Phase 6 — pointer composition lives in Core** (`InfiniContext.Core.Tasks.TaskSessionPointer`); `ClaudeSessionOptions.FirstUserMessage` is intentionally a plain string so Core/Sessions stays Tasks-agnostic. Source: 20260507-tasks-and-sessions-management/decisions.md.
- **Phase 6 — `/task switch` intercepted in `Repl`, not the handler.** Why: switching needs SQLite status flip + session teardown + re-spawn with `FirstUserMessage` — only the outer loop has that authority.
- **Phase 6 — status flips happen in `Program.cs`, not via session lifecycle observers.** `InProgress`/`Idle` transitions in outer loop, immediately before/after `repl.RunAsync()`.
- **Phase 7 — lock messages share the approval pipe via `kind` tag.** One `ApprovalChannel` per session multiplexes `approval` (back-compat default) + `lock.acquire`/`lock.release`. Why: two pipes per session would double lifecycle bookkeeping.
- **Phase 7 — release-on-exit via channel dispose, not `IProcessSupervisor`.** Channel calls `LockRegistry.ReleaseAllOwnedBy(pipeName)` in `DisposeAsync`.
- **Phase 7 — owner id is the channel's pipe name, not Claude's session GUID.** Why: GUID isn't known until `system/init`; pipe name is unique per session up-front. Logs scope locks by pipe name for cross-reference with `approval-channel:` lines.
- **Phase 7 — polling-based contention (5–50 ms `Task.Delay`), not per-file semaphores.** Why: lock has two release paths (explicit and TTL); a semaphore-per-file only fires on explicit release.
- **Phase 8 — `ContextPressureMonitor.OnEvent` invoked synchronously in `ClaudeSession`'s stdout loop after the state machine and session logger, before the channel write.** Why: bounded channel uses `DropOldest`; a second drain would race the renderer.
- **Phase 8 — window resolution prefers claude's `result.modelUsage[<active model>].contextWindow`**, falls back to `ModelContextWindows` settings map; silent if neither. Why: real claude emits `modelUsage` per turn (e.g. `1_000_000` for `claude-opus-4-7[1m]`); trusting self-report keeps thresholds correct as new models ship.
- **Stage labeling: this is Stage 2A.** Original Stage 2 (multi-session console, broadcast, host index, macOS) becomes Stage 2B; 2B is independent UX work and may follow 2A.

## 10b. Multi-instance UI & supervision (Stage 2B)

Stage 2B is the first multi-session deliverable. All decisions below originate in `.claude/tasks/20260512-stage-2b/decisions.md` (D1–D36) plus the Phase 6 / Phase 7 entries below; they govern the work that landed on `main` across commits `17322b9` (Phase 1), `56b1066` (Phase 2), `06fc130` (Phase 3), `96e773b` (Phase 4a), `4414dc8` (Phase 4b), `7723b89` (Phase 5), `6fab2a0` (Phase 6), `72f92ff` (Phase 7 code) + `cda2223` (Phase 7 docs). Phase 8 (CI matrix + exit-criteria smoke) is the only outstanding phase and is **awaiting user sign-off on 5 questions** captured in D36 below before any workflow YAML or smoke-test code lands.

Test baseline at end of Phase 8: **627 passing + 3 `MacOnly`-skipped (630 total) on a Windows runner** (post-format, post-`MultiSessionExitCriteriaTests`). The 3 mac-skipped cases light up for the first time on `macos-latest` via the manual-dispatch `ci-macos.yml` (D37).

### Architecture (Phases 1–2)

- **D1 — `SessionManager` is the host-scoped registry of `SessionContext` bundles.** Replaces the inline outer-loop session construction in `Program.cs`. Each `SessionContext` bundles `ClaudeSession`, `ContextPressureMonitor`, `ThresholdInjector`, `IApprovalSink`, `TranscriptModel`, host-id `Guid`, optional `ActiveTaskId`. Implication: 2A todo §B T3 (extract `HostLoopState`) is a strict prerequisite — landed in Phase 1.
- **D9 — Per-task arbitration via `TaskActivityCoordinator`.** One session per task `InProgress` at a time; the coordinator owns the rule and the `last_active_at_unix_ms` clock injection (closes 2A §B A4). `Refused` path is contract-tested even where the single host can't trigger it.
- **D21 — `SessionManager` does not reach into `TaskActivityCoordinator`.** The outer loop in `Program.cs` orchestrates `Enter → CreateAsync → run → CloseAsync → Leave`. Keeps task-claim concerns and session-lifecycle concerns separate.
- **D2 — One MCP-approval sidecar process per session (no consolidation).** Per-session pipe name doubles as the `LockRegistry` owner id, so locks are reclaimed automatically on channel disposal. Consolidating would force protocol-level session multiplexing.
- **D18 — `SessionManager.CreateAsync` starts the session before returning.** `Repl`/`MultiSessionHost` drop their `StartAsync` calls. Splitting "construct" from "start" creates a dormant-context window the Phase 4 reader can't tolerate.

### Per-session reader & transcript (Phase 4a)

- **D3 — Two-pane Spectre `Layout` + per-session transcript model.** Renderer ticks at ~30 FPS; each `ClaudeSession.Events` channel is drained continuously by a per-session reader regardless of focus. Approval banners render as a strip above the focused transcript.
- **D4 — Channel stays `bounded(1024, DropOldest)`; durability moves to `TranscriptModel`.** State machine is updated *before* the channel write, so dropped fragments cost only rendered text. The transcript model is the durable store.
- **D28 — `TranscriptRenderer` + `SessionTranscriptReader` live in `InfiniContext.Core.Sessions`, not `Cli.Ui`.** `SessionManager` owns the reader's lifecycle; UI in CLI subscribes to `TranscriptModel.FragmentAppended` for echo. The reader runs "regardless of focus" per D3 — that's core infrastructure, not a UI helper.
- **D27 — `MultiSessionHost.RunAsync` does the exit-drain wait *before* unsubscribing.** Naive `Repl` port had the wait in `finally` after unsubscribe; trailing `[turn done:]` fragments appended to the transcript but never echoed. `ReplSmokeTests` caught it.
- **D20 — `TranscriptModel` on-disk overflow deferred.** Bounded in-memory ring only (default 10 000 entries, `DropOldest`). The disk-overflow path needs a consumer to validate design choices against; pair with Phase 4c if/when continuous Spectre `Live` rendering lands.

### Host session index (Phase 3)

- **D5 — Flat-JSON `~/.infinicontext/index.json` with `version=1`.** Schema mirrors the design doc: `session_id`, `cwd`, `model`, `created_at`, `last_active`, `title?`, `last_state?`, `exit_code?`.
- **D22 — Phase 3 writes the index on close only.** Throttled mid-session `last_active` updates from D5 deferred until a Phase 4c+ UI consumer needs them.
- **D23 — A session that never observed `system/init` is *not* persisted.** No claude session id ⇒ no resumable row; persisting one would create a "ghost" entry. Logged at warning when it happens.
- **D24 — Recent-sessions list capped at 1000.** FIFO eviction before persistence. 1000 entries is comfortable headroom for any single-user workload while keeping startup load predictable.
- **D25 — Index path resolves via a static helper, not settings.** `SessionIndex.DefaultPath()` returns `~/.infinicontext/index.json`. `SessionManager.OpenAsync` accepts an override for tests. The path is implementation detail (like the log directory), not a user-facing knob.

### Multi-pane UI (Phase 4a/4b)

- **D17 — Spectre.Console pinned to an exact stable version.** Dependabot/manual upgrades happen as separate PRs.
- **D26 — Spectre.Console / Spectre.Console.Testing pinned at `0.55.2`** in both `InfiniContext.Cli.csproj` and `InfiniContext.Tests.csproj`. Initially considered `0.49.1`; bumped to latest stable.
- **D19 — `Repl` (now `MultiSessionHost`) keeps its `IClaudeSession` ctor parameter, not `SessionContext`** through Phase 4a. The host passes `ctx.Session`. Phase 4b adds optional `SessionManager` ctor param without touching the focused-session arg.
- **D30 — `SlashCommandContext` is rebuilt at each slash dispatch.** With focus rotation, the "current" session is mutable; caching the context at host-ctor time would freeze it. Per-dispatch rebuild is one allocation per command (cheap) and keeps `Session`, `GetLastCostUsd`, `FocusedHostSessionId` fresh.
- **D7 — `Tab` cycles active sessions only; recent rows activated via `r` / `/resume`.** The "no I/O loss while off-screen" exit criterion only meaningfully applies to active sessions. Phase 4b ships `/next` and `/prev` as slash-command substitutes; true `Tab` keybinding requires `Console.ReadKey` input (deferred per D29).
- **D11 — `/n <cwd>` requires an explicit cwd; no default.** Defaulting to launch cwd creates surprise duplicates for a multi-session host. Phase 4b confirms.
- **D12 — `/approval ask|auto|deny|allow` toggles host-wide.** One effective approval mode across all sessions; per-session sinks all listen to the same swap signal. Per-session approval modes are silent-allow-on-one-pane footguns.
- **D8 — Broadcast applies to free-form prompts only; slash commands target the focused session.** Broadcasting `/task switch` would corrupt the task-state machine. Free-form text is the only thing that fans out cleanly.
- **D10 — No per-session settings overrides in Stage 2B.** Per-session model/flag selection is Stage 3 UX territory. `docs/stage2/todo.md` §C "Per-session settings overrides" remains deferred.

### Per-session approvals (Phase 5)

- **D31 — Approval sink is per-session; `PendingApprovalSlot` is the bridge.** Each `SessionContext` owns a `PendingApprovalSlot` (single in-flight request, holds the `TaskCompletionSource<ApprovalResponse>`) plus a `SwitchableApprovalSink` whose inner is rebuilt by `ApprovalSinkFactory.Build(mode, slot, transcript, …)`. `SessionManager` no longer takes a shared `IApprovalSink`; instead it takes a `SessionApprovalSinkFactory` delegate `(slot, transcript) → IApprovalSink` supplied by the host. Rationale: per-session state is what makes the design-doc §9 exit criterion ("approval prompts in one session don't block input or progress in another") reachable; the old shared sink read `Console.In` directly, so two concurrent approvals raced for the same global reader. `MultiSessionHost.InputLoopAsync` now checks `_focused.ApprovalSlot.Current` per line: when non-null, the line resolves the slot (y/yes → Allow; anything else → Deny) instead of being forwarded to claude or dispatched as a slash command.
- **D32 — `SwitchableApprovalSink` moves from CLI to Core.** New home: `InfiniContext.Core.Mcp.SwitchableApprovalSink` (public). `SessionContext.ApprovalSink` is typed as the concrete class — not the interface — so the host can call `Switch()` without casting. The class is 25 lines and has no CLI-specific dependencies; moving it is the lightest touch that lets Core's `SessionContext` expose the per-session switch lever.
- **D33 — W-16 resolved by deleting `ConsoleApprovalSink`.** The 2A todo §A W-16 race (leaked `Task.Run(_input.ReadLine)` after timeout) is resolved by removing the file, not patching it. Phase 5's `SlotApprovalSink` replaces it as the implementation of the Ask path; nothing else referenced `ConsoleApprovalSink`. The 12-case `ConsoleApprovalSinkTests` is deleted alongside it. The slot path doesn't read stdin at all — `MultiSessionHost` owns stdin and routes by slot state. Supersedes Stage 2B D14 (which said W-16 lands inside Phase 5 — it did, by deletion).
- **D34 — Per-request echo routes to `TranscriptModel`, not `Console.Out`.** `ConsoleEchoingApprovalSink` now writes through an `Action<string>` instead of `TextWriter + lock`. In multi-session, the factory wires this with `line => transcript.Append(line + "\n")` so each decision (`[approval] Bash → ALLOW (Nms)`) lands in the originating session's transcript via the existing `FragmentAppended` pipeline. The legacy `(IApprovalSink, TextWriter, object writeLock)` ctor stays for tests. Rationale: a multi-session host that writes approval echoes to a shared `Console.Out` interleaves output between panes and can't be attributed to a specific session.
- **D35 — `ApprovalSinkFactory.Build` gains `announce` flag.** Signature is `(mode, slot, transcript, out effectiveMode, bool announce = true)`. When `announce` is false, the Allow warning, the Auto back-end banner, and the "light-model construct failed" fallback message are suppressed. `Program.cs` passes `announce: !bannersEmittedForCurrentMode` so the FIRST session-create or `/approval` toggle announces; subsequent ones go quiet. Rationale: without gating, every `/n <cwd>` would re-emit the "back-end=CLI (OAuth fallback) …" line — visual spam after the user already saw it at host startup.

### Broadcast (Phase 6, commit `6fab2a0`)

These are Phase-6-specific decisions not large enough for their own D-numbers in the task folder. Logged here so the design lineage is consolidated in one place.

- **`SessionManager.BroadcastAsync` returns `IReadOnlyList<BroadcastSendResult>`, not `Task`.** The Phase-2 fan-out aggregated exceptions and re-threw under `Task.WhenAll`, which loses per-target attribution and forces the caller to inspect an `AggregateException` to figure out which session failed. Phase 6 captures each target's outcome (`Sent | Failed | NotFound`) into a result list keyed by `HostSessionId`, in input order, so the slash-command surface can render a per-row toast and a partial failure still reports which targets succeeded. Per-target exceptions are logged at Warning and absorbed into `Failed(id, ex)`; the fan-out itself only throws when the manager is disposed. Rationale: a UI that wants to render in input order shouldn't have to re-pair an unordered dictionary, and a UX-facing "broadcast" verb should be tolerant of one target being mid-shutdown — the rest still go through.
- **Unknown ids materialize as `NotFound` entries rather than silently skipped.** Phase 2's `BroadcastAsync` filtered unknown ids out before the fan-out; Phase 6 keeps them in the result so the UI can attribute every requested id. Net effect: `/broadcast` lists "K not found" lines if a session closed between the user typing and the dispatch.
- **`/broadcast` rejects single-target broadcasts with a steering message.** `/broadcast <one-prefix> hi` is a UX trap — the user could've just typed at the prompt (focused session) or `/focus`'d first. The two-line steering message ("needs at least two targets — use /n + a regular prompt for a single session") is one defensive branch that costs nothing and prevents the surprise of a "broadcast" that fans to one. Enforced in `BroadcastCommand.cs`, not in `SessionManager.BroadcastAsync` — the manager is a primitive and accepts any non-empty target set; the UX guard belongs at the slash-command boundary.
- **Targets parsed as comma-separated host-id prefixes, with `*` as a sentinel for "all active".** Same prefix-resolution rule as `FocusCommand` (case-insensitive `StartsWith` over `Guid.ToString("D")`; ambiguity is an error). Whitespace inside the comma-separated list is tolerated via `StringSplitOptions.TrimEntries`. Rationale: re-using the prefix-resolution convention from `/focus` means a user who can navigate via `/focus 7a2b` can immediately drive `/broadcast 7a2b,93cf prompt`; introducing a second selector form (numeric indices, name strings, `--target=…` flags) would have been scope creep without a concrete need.
- **No `b` keybinding in Phase 6.** Per D29, real keybindings require `Console.ReadKey` (deferred to optional Phase 4c). The slash-command surface satisfies the design-doc §9 functional acceptance ("broadcast prompt sent to two sessions returns two independent transcripts in their respective panes") without the input-model refactor.
- **`b`-key multi-select picker (Spectre `MultiSelectionPrompt`) deferred.** Plan §6 sketched a "press `b` → checkbox picker → one prompt" flow. That requires `Console.ReadKey` plus a host-state machine for the picker phase. Slash-command form ships first; the picker is only worth building if real-world use shows comma-prefix selection is too clunky for >3 targets.

### macOS supervisor + C7 + T4 (Phase 7, commits `72f92ff` + `cda2223`)

Phase-7-specific decisions, again not large enough for new D-numbers in the task folder but consolidated here for the design lineage.

- **POSIX kill via `[DllImport("libc")] kill(2)`, not `Process.Kill()`.** `Process.Kill()` on .NET 7+ delivers SIGKILL unconditionally; we want SIGTERM first so the child can flush stdout, close pipes, and release locks gracefully, then SIGKILL only for survivors after `GracePeriod` (default 2s). The SIGTERM=15 / SIGKILL=9 constants are part of the stable POSIX ABI across macOS and Linux; hard-coding avoids a runtime dependency on libc constants and one P/Invoke line is cheaper than a managed wrapper. Matches the broader supervisor contract that graceful shutdown is the design-doc §5 default.
- **Reap inside the `PosixSignalRegistration` handler, then `Environment.Exit(128 + signum)`.** _(Corrected 2026-06-12, task 0023 audit C3 — the original Phase-7 design set `ctx.Cancel = true` with the comment "the runtime will still terminate the process once we return." That comment was **wrong**: `Cancel = true` cancels the runtime's default disposition for SIGTERM/SIGHUP/SIGQUIT, so after reaping the host kept running as a zombie with dead sessions, clearable only by `kill -9`.)_ The handler now reaps children synchronously and then terminates the host explicitly via `Environment.Exit`. The audit offered two fixes — "leave `Cancel = false` and let the runtime's default disposition terminate the process" or "`Environment.Exit(128 + signal)` after the reap"; we took the latter because it terminates the host **deterministically** on every POSIX platform regardless of what the main thread is blocked on (the default-disposition path depends on the app cooperatively unwinding, which a host blocked on an infinite stdin read does not). `Environment.Exit` raises `AppDomain.ProcessExit`, which re-enters the reap — but the second pass finds no live survivors and returns silently (idempotent by construction) — then exits with the conventional `128 + signum` code.
- **SIGINT is NOT registered by the supervisor.** _(Corrected 2026-06-12, task 0023 audit C2 — Phase 7 registered SIGINT alongside the terminating signals.)_ The interactive host (`MultiSessionHost`) owns Ctrl+C as the "interrupt the current turn" gesture via `Console.CancelKeyPress` (`e.Cancel = true`). When the supervisor also reaped on SIGINT, a single Ctrl+C destroyed every session's children while the host stayed alive with dead panes — the documented interrupt gesture became a session-killer on macOS/Linux (Windows is unaffected because the Job Object ignores Ctrl+C). The supervisor now registers only the host-terminating signals; a SIGINT that *is* allowed to terminate the host (no console handler) still reaps via the `ProcessExit` hook, so Ctrl+C never orphans children on a real exit but never kills them on an interrupt.
- **`DetachAll` flips `_detached=true` and short-circuits subsequent reaps.** Once the caller signals "I've handed these children off", the supervisor doesn't second-guess. Matches the Windows backend's `DetachAll` semantics, which clears `KILL_ON_JOB_CLOSE`.
- **Signal registration is best-effort per-signal.** Each `PosixSignalRegistration.Create` is wrapped in try/catch; a host that refuses one of SIGTERM/SIGHUP/SIGQUIT (some Linux containers reject SIGQUIT) still gets the rest. Rationale: don't fail supervisor construction over a non-essential signal — `ProcessExit` is still wired and covers clean-shutdown paths.
- **Startup scan for stray `claude`/`node` processes deferred.** Plan §7 mentioned an optional scan that lists pre-existing claude/node children whose parent is `init`/`launchd` and offers to kill them. Not built — the signal handler covers the common cases (terminal close, `kill <host>`; Ctrl+C is owned by the host and cleans up via `ProcessExit` on a real exit — see the C2/C3 corrections above); the scan is mostly useful for recovering from a prior `kill -9` of the host, which is the documented limitation from design-doc §5. Not worth speculative scope without telemetry.
- **`FileSystemNaming` lives in `InfiniContext.Core` root namespace, not under `Tasks/`.** Filename identity is a cross-cutting concern (`Sessions/`, `Mcp/`, future Avalonia file pickers all touch it), so a `Tasks/`-local helper would have invited duplicate copies. A public static class with two members (`Comparison`, `Comparer`) is the smallest surface that doesn't tempt a copy-paste.
- **`FileSystemNaming` gates identity, not ordering.** Sort callers (`TaskFolder.ListArtifactsAsync` display order, `drift.Sort` in `DetectDriftAsync`) intentionally keep `OrdinalIgnoreCase` / `Ordinal` as appropriate — a stable cross-platform display order is friendlier to users than an OS-dependent one. The doc comment on `FileSystemNaming` calls this out explicitly so a reviewer doesn't "fix" the sort sites later.
- **`LooksLikeBuildOutput` segment matching gated alongside artifact identity.** The `obj` / `bin` / `Debug` / `Release` segment checks use `FileSystemNaming.Comparison`. On a case-sensitive FS, a real task home at `~/projects/OBJ-storage/` no longer false-positives as a build output; on Windows, `OBJ\` and `obj\` continue to fold correctly. The convention-named build dirs are lowercase by .NET default, so the typical case is unaffected.
- **Markdown-table header check stays case-insensitive.** `firstCell.Equals("File", OrdinalIgnoreCase)` in `TaskFolder.IndexedFilesAsync` parses what the user/agent typed and should tolerate `| File |` and `| file |` interchangeably. That's a markdown-formatting concern, not an FS-naming one — leaving it untouched is intentional.
- **T4 ships against two `ApprovalChannel`s, not two FakeClaude processes.** Lock messages bypass `claude` entirely — they ride the approval pipe directly between sidecar (Node) and host (C#). Spawning FakeClaude would test process plumbing without adding lock-semantics coverage (and would add minutes of test wall-clock). The unit-under-test for "lock contention" is the registry + channel pair, not the process tree. `LockChannelE2ETests` already exercises the single-channel bytes-on-wire path; the new `MultiSessionLockContentionTests` adds the cross-channel registry-sharing coverage that was missing.
- **`MacOnlyFactAttribute` mirrors `WindowsOnlyFactAttribute`.** xUnit `[Trait("Category", ...)]` was proposed in the task's D6 originally but the repo already had `WindowsOnlyFactAttribute` as the precedent; consistency wins over the trait approach. The CI matrix in Phase 8 will use OS-detection inside the attribute (already implemented) rather than `--filter "Category!=…"` invocations, matching how Windows-only tests already skip on non-Windows runners. No separate `LinuxOnlyFact` — Linux isn't a design-doc §2 target platform, contributors validating locally on Linux can swap to `IsLinux()` ad-hoc.

### Deferrals & validation

- **D13 — Eight-phase ordering.** 1) outer-loop refactor, 2) SessionManager, 3) session index, 4) Spectre UI, 5) per-session approvals, 6) broadcast, 7) macOS supervisor + C7 + T4, 8) CI matrix + exit-criteria smoke. Each phase ends runnable with green tests; each is a separate PR.
- **D14 — W-16 (uncancellable `ConsoleApprovalSink` stdin reader) lands inside Phase 5.** ✅ *Resolved 2026-05-13 via D33* — the offending code is gone rather than patched.
- **D15 — Trusted-cwds banner deferred past Stage 2B.** Orthogonal to multi-session mechanics; concerns *what cwds may be opened*, while 2B concerns *how many sessions run at once*.
- **D16 — macOS code lands unvalidated in Phase 7; Phase 8 introduces CI from scratch.** No Mac workstation, no existing `.github/workflows/` (verified 2026-05-12). Manual real-Mac validation NOT required for Stage 2B sign-off — CI-only validation is acceptable. The "killing the host cleans up child processes — verified on macOS" exit criterion can't be marked done until Phase 8 lands and macOS CI is green.
- **D29 — Phase 4b ships multi-session via slash commands; true `Tab` and continuous Spectre `Live` are deferred.** Adopting `Console.ReadKey` for input would ripple through every test fixture (`StringReader` would need a key-event substitute) and the smoke-test pipeline (`Process.StandardInput.Write` no longer sends what the host reads). The slash-command surface from Phase 4b already satisfies the functional acceptance ("three concurrent sessions, switch focus, resume, broadcast"); the *aesthetic* split-pane TUI plus its prerequisites (`Console.ReadKey`, `AnsiConsole.Live(layout)`, `Ui/SessionListView.cs`, `Ui/SessionView.cs`, `Ui/StatusBar.cs`) bundle into an optional "Phase 4c" and only land if real-world use proves the slash-command UX too rough.

### Phase 8 — CI matrix + exit-criteria smoke (shipped)

- **D36 — Phase 8 CI matrix is being created from scratch; sign-off received on 2026-05-16 (see D37 for resolutions).** No `.github/workflows/` exists today (re-verified 2026-05-14, matching D16's original 2026-05-12 finding). No code or workflow YAML has been written yet. Five choices are gating Phase 8 implementation and are queued for the user:
  1. **`macos-latest` enablement.** Plan §8 and design-doc §9 ("CI green on both `windows-latest` and `macos-latest`") want both. `macos-latest` runners bill at 10× the Windows/Linux rate on private repos ($0.08 vs $0.016/min). Per D16 the user owns the call. Live options: always-on `[windows, macos]` matrix; `windows`-only first and defer macOS; or `windows`-always + a `workflow_dispatch`-gated `ci-macos.yml`.
  2. **Triggers.** Push to `main` + `pull_request` is the default; alternatives are PR-only (cheaper) or push-everywhere (faster signal). Affects how fast macOS minutes accrue under option 1.
  3. **Repo visibility.** If the repo is or trends public, `macos-latest` is free and #1 collapses.
  4. **Workflow scope.** Stated scope is matrix + exit-criteria smoke; adding `dotnet format --verify-no-changes`, coverage upload, or artifact publish is opt-in.
  5. **"Kill host → child cleanup" coverage.** The §9 exit criterion is hard to validate from inside xUnit (the test process *is* the host). Two paths: (a) document as manual proof, lean on existing `JobObjectSupervisorTests` + `PosixSignalSupervisorTests` as proxies for the kill primitive; (b) add an out-of-process integration test that spawns `InfiniContext.Cli`, kills it, and checks for orphans (~100 LOC, historically a CI flake source).

  **Rationale for stopping to ask.** Each option above changes the workflow YAML, the test file's scope, and how the exit-checklist is worded. Writing one set of guesses and re-litigating after the user pushes back would waste a commit cycle. D13's "each phase ends runnable with green tests" still holds — Phase 8 just hasn't started yet because the inputs are user-owned.

  **Once answered, Phase 8 implementation is small.** A single `.github/workflows/ci.yml` (~30–60 lines), one new test file (`tests/InfiniContext.Tests/Cli/MultiSessionExitCriteriaTests.cs` — drives §9 broadcast / focus / approval / resume / drift through `SessionManager` against in-process stubs or `FakeClaude`), and one new doc (`docs/stage2/exit-checklist.md`) mapping every §9 bullet to its test or manual proof. The existing `MacOnlyFactAttribute` / `WindowsOnlyFactAttribute` pattern means CI doesn't need `--filter` flags — the attributes already self-skip per OS.

- **D16 status update.** macOS code from Phase 7 landed on `main` as designed: unvalidated until Phase 8 CI lights up `macos-latest`. Phase 8 shipped `ci-macos.yml` (manual-dispatch via `workflow_dispatch`); the "killing the host cleans up child processes — verified on macOS" exit criterion is automated by [MacOnlyFact]-gated `PosixSignalSupervisorTests` and becomes green on the first successful `ci-macos.yml` dispatch. See `docs/stage2/exit-checklist.md`.

- **D37 — Phase 8 resolutions (sign-off received 2026-05-16; resolves D36's 5 questions).** Phase 8 shipped as the final Stage 2B phase.
  1. **`macos-latest` enablement (D36 q1, q3).** Manual `workflow_dispatch` via separate `ci-macos.yml`. Rationale: private-repo `macos-latest` minutes bill at 10x Windows. Operating procedure documented in `docs/stage2/exit-checklist.md` §9.7.
  2. **Triggers (D36 q2).** Windows `ci.yml` runs on `push` to `main` and every `pull_request`. Standard default — catches main regressions and validates PRs pre-merge.
  3. **Workflow scope (D36 q4).** Matrix build + `MultiSessionExitCriteriaTests` + `dotnet format --verify-no-changes` + coverage (coverlet -> cobertura -> ReportGenerator markdown summary in job step output). Coverage summary uses no third-party SaaS (no token, no PR-comment dependency on a Docker action). Artifact publish deferred — no current downstream consumer.
  4. **Kill-host coverage (D36 q5).** Option (a): manual proof + lean on existing supervisor unit tests. Rationale: `JobObjectSupervisorTests` already exercises the same kernel path as host crash (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` fires regardless of how the handle closes — via `Dispose` or via OS-forced cleanup on termination). `PosixSignalSupervisorTests` proves the graceful POSIX path (SIGTERM via `PosixSignalRegistration`); SIGKILL-of-host is inherently untestable (handler can't run) and the design accepts orphans on hard kill with init/launchd reap backstop. The out-of-process integration test (D36 path b) is filed as a follow-up hardening item in `docs/stage2/todo.md` §A — D36's "historically a CI flake source" outweighed the marginal additional coverage at Phase 8.
  5. **dotnet format integration.** Repo had pre-existing whitespace drift in `FakeClaude/Program.cs`, `State/ToolStackTests.cs`, `Parser/StreamJsonParser.cs`, `Sessions/TranscriptRenderer.cs`, `Logging/SecretScrubbingEnricher.cs` (mostly object-initializer line splits). Fixed in-place alongside Phase 8 so `--verify-no-changes` is green from CI run #1. Going forward, `dotnet format` runs locally before commit are recommended.
  6. **Outcome.** Tests: 627 passing + 3 `MacOnly`-skipped on Windows. Phase 8 closes Stage 2B against design-doc §9 with the noted exception that macOS-side §9.6 / §9.7 require a manual `ci-macos.yml` dispatch to declare green.

## 11. CLI & REPL UX (help, slash-commands)

All from `20260509-improve-cli-repl-help/decisions.md`.

- **D3 — `/help [<topic>]`.** `/help` alone shows summary table; `/help <topic>` shows full details.
- **D4 — Banner no longer lists slash-commands inline.** Replaced with "Type /help inside the REPL".
- **D5 — Slash-commands self-describe via centralized registry** (`ISlashCommand` + `SlashCommandRegistry` built once at startup). Why: adding a command automatically updates help; prevents drift.
- **D6 — Unknown slash-commands fall through to Claude verbatim.** Why: Claude has its own slash-commands (`/clear`, `/compact`).
- **D7 — `--help` uses a parallel `CliFlagCatalog`; `RunArgs.TryParse` stays a switch.** Drift-test asserts every `case` label has a matching catalog entry. Why: only ~7 flags.
- **D9 — `/help <unknown-topic>` prints summary + "no such topic" line.** No hard error.
- **D10 — No `//<cmd>` escape for forwarding recognized commands to Claude.** Trivial to add later if a real need emerges.
- **D12 — Rename new slash-command wrapper to `TaskSlashCommand`.** Avoids collision with `src/InfiniContext.Cli/Tasks/TaskCommand.cs` (parser).

## 12. Testing discipline

- **No mocks of Claude's wire protocol.** Use `FakeClaude` (a real spawned binary) or replay fixtures. Source: `CLAUDE.md`.
- **Test framework: xUnit + FluentAssertions.** Source: `infinicontext-poc-initial-design.md` §7.5.
- **Solution standardized on xunit.v3 (3.2.2) in VSTest mode (2026-06-12).** The Stage 3 headless GUI suite (Avalonia.Headless.XUnit 12.x) requires xunit v3; rather than carry a v2/v3 harness split, `InfiniContext.Tests` migrated to `xunit.v3` (+ `OutputType=Exe`), keeping `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio` so `ci.yml`'s trx/coverage/filter surface is unchanged. v3's analyzers forced two sweeps: source-info ctors on the OS-conditional fact attributes (xUnit3003) and `TestContext.Current.CancellationToken` at ~574 async call sites (xUnit1051). Source: task 0020 `plan-xunit-v3-migration.md`.
- **Test pyramid is shallow.** Replay tests against recorded fixtures are highest leverage; ≥80% coverage on parser+state machine, lower elsewhere. Source: `infinicontext-initial-design.md` §7.5.
- **Pure-logic CLI types are linked into the test assembly** via `<Compile Include="..\..\src\...">` (e.g. `RunArgs`, `SlotApprovalSink`, `MultiSessionHost`) so they can be unit-tested without spawning the binary. Smoke tests still spawn the real exe. Source: `CLAUDE.md`. (Phase 5 retired the legacy `ConsoleApprovalSink` per D33; `SlotApprovalSink` is the linked Ask-path sink now.)
- **`ImplicitUsings` + `Nullable` enabled everywhere.** Cross-platform from day one even though Stage 1 ships Windows-only. Source: `CLAUDE.md`.
- **`WindowsOnlyFactAttribute`, no `Xunit.SkippableFact`.** Project-local attribute that sets `Skip` based on `OperatingSystem.IsWindows()`. Why: one file, no extra dep. Source: 20260506-poc/decisions.md.
- **Fixtures are committed test data, not casual outputs.** Recorded against `claude` 2.1.112 on Windows via `tools/InfiniContext.Recorder`. Bump `TestedAgainstVersion` alongside any re-record. Source: `tests/fixtures/README.md`.
- **`unknown-event.jsonl` is hand-edited.** Invented top-level type, invented content-block type, unknown fields on known events — explicit parser-tolerance fixture. Source: `tests/fixtures/README.md`.
- **FakeClaude scripts ≠ recorder scripts.** `tests/scripts/` drives `FakeClaude`; `tests/fixtures/scripts/` drives the real recorder. Source: `tests/scripts/README.md`.
- **Two-layer GUI test architecture (2026-06-12).** View-layer bugs (bindings, focus, key handling) get headless in-process tests (`Gui.HeadlessTests`, Avalonia.Headless.XUnit pinned to the GUI's Avalonia version, production theme loaded via the `Styles/AppStyles.axaml` extraction); real-window/OS-level behavior gets opt-in UIA E2E tests (`Gui.UiaTests` over the `Gui.UiaDriver` FlaUI.UIA3 page objects, gated on `ICX_UIA_TESTS=1`, dispatched via `ci-uia.yml`). FlaUI chosen over the Avalonia docs' Appium/WinAppDriver route (WinAppDriver unmaintained, needs a server + Developer Mode). Rule: write every GUI test at the lowest layer that can observe the bug; `AutomationId`s are a public contract recorded in `docs/gui-automation-ids.md`. Source: task 0020 `design.md`.

## 13. UI / stages / roadmap

- **Stage 2 UI: Spectre.Console.** Identical layout on Windows and macOS terminals. Source: `infinicontext-initial-design.md` §6.1.
- **Stage 3 UI: Avalonia 11.x.** WPF (Windows-only), WinUI 3 (weak cross-platform), MAUI (mobile-first) all rejected. Source: 20260506-poc/decisions.md, `infinicontext-initial-design.md` §6.1.
- **Approval-prompt MCP server: minimal stdio, no MCP framework dep.** ~150 lines C#; tab/session-scoped, never modal. Source: `infinicontext-initial-design.md` §4.6.
- **MVVM flavour (CommunityToolkit.Mvvm vs ReactiveUI) and Markdown library decisions deferred to Stage 3 spike.** Source: `infinicontext-initial-design.md` §8.

---

## Process / methodology decisions (cross-task)

- **Light model is not exposed as a tool to the main `claude` agent.** Deferred (status `[-]`). No concrete use case yet; exposing the safety judge as a tool would let the main agent probe/manipulate it. Source: 20260506-stage1-new-features/decisions.md.
- **Caching on title/summarize system prompts not yet activated.** Prompts below Haiku 4.5's cache minimum; safety prompt has caching pre-wired and activates automatically once it grows. Source: 20260506-stage1-new-features/decisions.md.
- **`settings.test.json` test overlay — rejected.** Current model (`Defaults` + inline record construction + `BaseDirectory` + opt-out flags) is sufficient. Source: 20260506-stage1-new-features/decisions.md.
