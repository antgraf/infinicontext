# InfiniContext — Consolidated TODO / Future Work

**Snapshot date:** 2026-05-16 (Stage 2B Phase 8 shipped; Stage 2B complete on Windows; macOS-side CI pending first `ci-macos.yml` dispatch)
**Scope:** every "deferred", "open question", "post-MVP", "for Stage 2/3" or "not yet implemented" item found across `CLAUDE.md`, `README.md`, `docs/`, `tests/`, and every `.claude/tasks/*/` and `.task/*/` folder.

Items already actionable today (with files-to-touch, acceptance criteria and cost estimates) live in `docs/todo.md`. **This file is the broader queue** — design-open questions, stage roadmap items, research follow-ups, and small hardening items that haven't been promoted to the actionable queue yet.

When an item is picked up, **remove it from this file** (or fold it into `docs/todo.md` first, then remove from there when shipped).

---

## A. Bugs & hardening (open queue, not yet in `docs/todo.md`)

- ~~**W-16 — `ConsoleApprovalSink` uncancellable stdin `Task.Run`.**~~ ✅ *Resolved 2026-05-13 in Stage 2B Phase 5 (commit `7723b89`).* `ConsoleApprovalSink` was deleted entirely (D33); the new `SlotApprovalSink` doesn't read stdin — `MultiSessionHost` owns the reader and routes by `PendingApprovalSlot.Current`. The leaked `Task.Run` no longer exists.
- **C-6 — `.cmd` shim spawn under `UseShellExecute=false`.** Documented-fragile across .NET runtime versions; path with `%` in `Path.GetTempPath()` could be misquoted. Not observed. Files: `ClaudeArgvBuilder.ResolveExecutable`. Source: 20260506-review-stage1-poc.
- **C-4 / C-5 — narrow races.** Process-handle race in `Attach` failure path; `mcp-config` temp-file leak window. Safe-by-construction with `await using` but not zero-risk. Source: 20260506-review-stage1-poc.
- **`SessionStateMachine` adversarial test gaps.** `JobObjectSupervisor` double-Dispose; `Attach` of already-exited process; `McpServer` `tools/call` missing `arguments`; notification-style `tools/call` (no `id`). Source: 20260506-review-stage1-poc/context.md.
- **Flakiness watch.** `Script_slow_stream_remains_in_streaming_through_inter_event_gap` (200 ms sleep / 150 ms assert); `SidecarSmokeTests.Sidecar_handshake_then_tools_list…` (5 s read timeout); `Repl_renders_*` (20 s `WaitForExit`). Source: 20260506-review-stage1-poc/context.md.
- **Automated Ctrl+C smoke test never landed** (deferred from Stage 1G → 1H, still open). Cross-platform Ctrl+C synthesis is fiddly. Source: 20260506-poc/decisions.md, 20260506-review-stage1-poc/context.md.
- **`BelowMinimum` does not yet block.** `ClaudeVersionPolicy` logs error but allows spawn; should eventually hard-fail. Source: 20260506-poc/decisions.md.
- **`/task relocate <id> <new-home>`** — move the folder (if present) and update the DB row to fix stale `home_cwd` entries. (The 2026-05-09 manual-testing follow-ups for "validate home_cwd at switch time" and "reject build-output `--home` paths" landed in `TaskService.PrepareSwitchAsync` and `TaskService.CreateAsync` respectively.) Source: `.claude/tasks/20260509-manual-testing-session/research.md`.
- **Smoke tests create real SQLite at `~/.infinicontext/infinicontext.db`.** Easy follow-up: set `INFINICONTEXT__TASKS__DBPATH` to a temp path in the smoke driver. Source: 20260507-tasks-and-sessions-management/progress.md (Phase 5 known caveat).
- **Update `ApprovalSinkFactory.cs:73` warning text** — should be specific about OAuth-vs-API-key gap and link `console.anthropic.com`. *May already be done in Session 4; verify.* Source: 20260507-manual-test-session/findings.md.
- **Out-of-process "kill host —> child cleanup" integration test.** Spawn `InfiniContext.Cli` as a child, `/n` one or two sessions, kill the host, scan for orphan PIDs. Deferred per D37: the existing `JobObjectSupervisorTests` (Windows — same kernel path as host crash) and `PosixSignalSupervisorTests` (POSIX SIGTERM path) cover the kill primitives; the integration gap is the disposal-cascade wiring (does `SessionManager.CreateAsync` actually `Attach()` every spawned child?), which manager-level disposal tests cover indirectly. D36 flagged this kind of test as "historically a CI flake source." Pick this up if a wiring regression slips past unit tests, or as scheduled hardening when CI minutes are cheap. Files: new `tests/InfiniContext.Tests/Cli/KillHostCleanupTests.cs` (~100 LOC). Source: 20260516-phase-8.

## B. Stage 2A leftover items (tasks & sessions)

- ~~**T4 — multi-session lock contention E2E test.**~~ ✅ *Resolved 2026-05-14 in Stage 2B Phase 7 (commit `72f92ff`).* `tests/InfiniContext.Tests/Mcp/Tasks/MultiSessionLockContentionTests.cs` covers two `ApprovalChannel`s on distinct named pipes sharing one `LockRegistry` — the takeover path (A holds → B contends → A releases → B wins) and the channel-dispose-releases-locks path. Implemented against `ApprovalChannel` directly rather than FakeClaude processes (lock messages bypass `claude` entirely — bytes-on-wire shape already covered by `LockChannelE2ETests`).
- **T3 — outer-loop refactor into `HostLoopState` / `TaskActivityCoordinator`.** Extract state machine from `Program.cs` outer loop so status flips, `pendingFirstUserMessage` clearing, and skill-install-warning path are unit-testable against synthetic `ReplResult` values. Source: 20260507-discuss-and-apply/progress.md.
- **A4 (full) — `Program.cs:169, 234` status-flip writes** still use `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` directly. Natural injection point is T3's extracted `HostLoopState`. Source: 20260507-discuss-and-apply/progress.md.
- **A6 — `TaskService.MergeAsync` should go through `TaskFolder` ops** (`WriteArtifactAsync`/`ReadArtifactAsync`/`MergeArtifactsAsync`) instead of raw `File.Read/WriteAllTextAsync`. Files: `TaskService.cs:139-165`. Defer until light/full-model merge tiers ship. Source: 20260507-discuss-and-apply/progress.md.
- **C6 — `parent_task_id` index.** Add `CREATE INDEX idx_tasks_parent ON tasks(parent_task_id) WHERE parent_task_id IS NOT NULL;` as a **new `Migration_002_*`** (never edit Migration_001). Defer until lineage queries land. Source: 20260507-discuss-and-apply/progress.md.
- ~~**C7 — case-sensitive filename compare on non-Windows.**~~ ✅ *Resolved 2026-05-14 in Stage 2B Phase 7 (commit `72f92ff`).* `FileSystemNaming.Comparison` / `FileSystemNaming.Comparer` (Windows → `OrdinalIgnoreCase`; non-Windows → `Ordinal`) applied to `TaskFolder.DetectDriftAsync` (indexed-set + index-skip), `TaskService.MergeAsync` (filename collection + index skip), `WriteMergedIndexAsync`, and `LooksLikeBuildOutput` segment matching. Markdown-table header check and display-only sorts intentionally stay case-insensitive (UX concern, not FS-naming).
- **T5 — `SkillInstaller` startup-integration smoke test.** Pre-create v1 `SKILL.md` in temp `HOME`, launch `infinicontext run`, assert file is now v2. Review's own confidence Low. Source: 20260507-discuss-and-apply/progress.md.
- **A8(b) — `protocol_version` field on every lock/approval message** (default 1). Buys nothing today; revisit when older host meets newer sidecar. Source: 20260507-discuss-and-apply/progress.md.
- **C2(b) — non-blocking dispatch in `ApprovalChannel.RunServerAsync`** so long approvals don't serialize lock releases. C2(a)'s cross-rule validator closes the foot-gun; defer the deeper refactor. Source: 20260507-discuss-and-apply/progress.md.
- **Cherry-pick test gaps.** `LockAcquireResponse.FromJson` round-trip on failure shape; `LockRegistry` same-owner re-acquire semantics; `TaskFolder.DetectDriftAsync` on malformed `index.md`; `TaskStore.AllocateIdAsync` concurrent calls. Source: 20260507-discuss-and-apply/progress.md.
- **Light-model and full-model merge tiers** — currently throw `NotImplementedException`; only `DeterministicConcat` ships. Source: 20260507-tasks-and-sessions-management/design.md §2.4, plan.md.
- **`InSync` / `Stale` status states.** MVP ships only `InProgress` and `Idle`. Source: 20260507-tasks-and-sessions-management/plan.md.
- **`/task switch` while another session is running on the same task** should error cleanly. Multi-session orchestration UX is post-MVP. Source: 20260507-tasks-and-sessions-management/plan.md.
- **Task rename / archival UX.** Post-MVP. (`/task delete` shipped in 2A.) Source: 20260507-tasks-and-sessions-management/decisions.md, `docs/cli/howto/task-commands.md`.
- **Hard-threshold force-flush guardrail.** Design sketched in `design.md` §8; MVP omits. If telemetry shows non-compliance, add an `InterruptAsync`-backed cancel path. Source: 20260507-tasks-and-sessions-management/decisions.md (Phase 8).
- **Automatic successor-session spawn after flush** — host fires the next session without user "go". Part of "long-running uninterruptible sessions" workstream. Source: 20260507-tasks-and-sessions-management/decisions.md, design.md §8.3.
- **Host-controlled flush on errors/cancels** — guarantee artifacts consistent even on crash paths. Future session-reliability task. Source: 20260507-tasks-and-sessions-management/decisions.md.
- **Cross-machine ID coordination.** Host-local SQLite means IDs aren't globally unique across machines. Source: 20260507-tasks-and-sessions-management/decisions.md.
- **Light-model bulk-regeneration of `index.md` as explicit user command.** Possible later as user-invoked feature; explicitly NOT automatic. Source: 20260507-tasks-and-sessions-management/design.md §3.4.
- **Slug recovery via `Slugify.For(title)`.** `TaskRecord` doesn't store the slug; recomputed from stored title. Works until rename lands; when it does, swap to folder-discovery scan over `<id:D4>-*`. Source: 20260507-tasks-and-sessions-management/progress.md (Phase 3).
- **Multi-parent lineage.** Schema records only one `parent_task_id`; multi-parent is post-MVP. Captured as class-level note on `MergeAsync`. Source: 20260507-tasks-and-sessions-management/progress.md (Phase 3).
- **Cross-home `/task merge`.** Guard: "All merge sources must share the same home_cwd. Cross-home merge is post-MVP." Source: `TaskService.cs:117-119`, 20260509-manual-testing-session/research.md.
- **Author `docs/cli/howto/commands.md`** — long-form REPL-only reference; link out to `docs/cli/howto/task-commands.md` rather than duplicate. Explicitly deferred to a separate task. Source: 20260509-improve-cli-repl-help/decisions.md D2/D8, progress.md.

## C. Stage 2B (multi-session console + macOS)

Status snapshot **as of Phase 8 shipped (2026-05-16)**: all eight planned phases of the Stage 2B plan have shipped on `main`. `.github/workflows/ci.yml` (Windows, push+PR, with `dotnet format` + coverage) and `.github/workflows/ci-macos.yml` (`workflow_dispatch`) are committed; `tests/InfiniContext.Tests/Sessions/MultiSessionExitCriteriaTests.cs` walks every design-doc §9 exit bullet to a named pass. **macOS code from Phase 7 is automated via [MacOnlyFact]-gated `PosixSignalSupervisorTests` but stays unvalidated until a successful `ci-macos.yml` dispatch per D37.** Test baseline at end of Phase 8: **627 passing + 3 `MacOnly`-skipped (630 total) on a Windows runner.**

### Shipped (commits `17322b9` · `56b1066` · `06fc130` · `96e773b` · `4414dc8` · `7723b89` · `6fab2a0` · `72f92ff` + `cda2223`)

- ✅ **Multi-session manager.** `SessionManager` + `SessionContext` + `SpawnRequest` + `TranscriptModel` (commit `56b1066`). Registry, per-session lifecycle, `BroadcastAsync` fan-out, `DisposeAsync` cascades.
- ✅ **Outer-loop refactor + `TaskActivityCoordinator`** (commit `17322b9`). Closes 2A §B T3 and A4. D9 "one session per task" arbitration in place; contract-tested even where Phase-4b single host can't trigger the refused path.
- ✅ **Persistent host index at `~/.infinicontext/index.json`** (commit `06fc130`). D5 flat-JSON schema; atomic temp+rename; malformed/missing tolerated; recent-sessions capped at 1000 (D24); rows recorded on `CloseAsync`/`DisposeAsync` only (D22).
- ✅ **Spectre.Console adoption** (commit `96e773b`). Spectre.Console 0.55.2 pinned (D26). `Repl.cs` removed; `MultiSessionHost` writes through `IAnsiConsole`. Per-session `SessionTranscriptReader` (D28) populates `TranscriptModel.FragmentAppended`; UI subscribes per focused session.
- ✅ **Multi-session via slash commands** (commit `4414dc8`). `/n <cwd>` (D11), `/list` (Spectre tables for active + recent), `/focus <prefix>`, `/next`, `/prev`, non-destructive `/resume`. Focus rotation in `MultiSessionHost` swaps subscriptions and replays the target transcript. `SlashCommandContext` rebuilt per dispatch (D30).
- ✅ **Schema-drift counter exposed via `/state`.** Surfaces `UnknownEventCount + MalformedLineCount + ActiveCount` plus a top-5 unknown-types histogram when drift > 0. (A persistent status bar is deferred to Phase 4c — see below.)
- ✅ **Per-session approval queueing** (commit `7723b89`). `PendingApprovalSlot` (Core), `SlotApprovalSink` (Cli) replace `ConsoleApprovalSink` (D33 — deleted; W-16 resolved by deletion); `SwitchableApprovalSink` moves Cli → Core (D32); each session's `ApprovalSink` is its own wrapper; `/approval` iterates `sessionManager.Active` and swaps each in lockstep (D12 host-wide). `MultiSessionHost.InputLoopAsync` routes y/N to `_focused.ApprovalSlot.Resolve(...)` when one is pending. Per-request echo lands in the originating session's transcript (D34); banners gate on `announce` so they fire once per mode toggle (D35). Stage 2B D31–D35.
- ✅ **Broadcast UX** (commit `6fab2a0`). `/broadcast <prefixes,…|*> <text>` (alias `/bcast`) fans one prompt out to ≥2 active sessions. `SessionManager.BroadcastAsync` now returns `IReadOnlyList<BroadcastSendResult>` (new `BroadcastSendOutcome` enum: `Sent | Failed | NotFound`) so the slash command can render a per-row toast — partial failures are captured per-target rather than thrown via `AggregateException`. Single-target broadcasts are rejected with a steering message at the slash-command boundary (the manager is a primitive and accepts any non-empty set). Target resolution reuses the prefix-matching convention from `/focus`. Stage 2B Phase 6 decisions block in `decisions.md` §10b.
- ✅ **macOS process-supervisor backend + C7 + T4** (Phase 7, commits `72f92ff` code + `cda2223` docs). New `PosixSignalSupervisor` in `src/InfiniContext.Core/Supervision/Posix/` registers SIGINT/SIGTERM/SIGHUP/SIGQUIT via `PosixSignalRegistration` and hooks `AppDomain.ProcessExit`; on dispose/signal/exit it walks attached PIDs with SIGTERM → grace → SIGKILL via `[DllImport("libc")] kill(2)`. `ProcessSupervisor.CreateForCurrentPlatform` returns it on `IsMacOS()` and `IsLinux()`. `C7` shipped via a new `FileSystemNaming` helper applied to `TaskFolder.DetectDriftAsync`, `TaskService.MergeAsync` / `WriteMergedIndexAsync`, and `LooksLikeBuildOutput`. `T4` shipped as `MultiSessionLockContentionTests` — two channels, one registry, takeover + dispose-releases-locks paths. **macOS code is unvalidated until Phase 8 CI on `macos-latest`** per D16. Tests: 605 passing on Windows runner + 3 `MacOnly`-gated cases that skip on Windows. New `MacOnlyFactAttribute` mirrors the existing `WindowsOnlyFactAttribute` pattern (consistent with the repo, not the original D6 `[Trait]` proposal). Stage 2B Phase 7 decisions block in `decisions.md` §10b.
-  **CI matrix + exit-criteria smoke** (Phase 8, 2026-05-16). `.github/workflows/ci.yml` (Windows, push to `main` + PR; restore -> `dotnet format --verify-no-changes` -> build -> `dotnet test` with coverlet/cobertura/ReportGenerator markdown summary). `.github/workflows/ci-macos.yml` (`workflow_dispatch` only per D37; `[MacOnlyFact]` cases self-skip on Windows and light up here). New `tests/InfiniContext.Tests/Sessions/MultiSessionExitCriteriaTests.cs` walks each design-doc §9 bullet to a named pass (three concurrent sessions, off-screen I/O preserved, index resume round-trip, broadcast fan-out, approval-queue independence). `docs/stage2/exit-checklist.md` maps every §9 bullet to its test or manual proof. Pre-existing whitespace drift fixed alongside via `dotnet format` so `--verify-no-changes` is green from CI run #1. Stage 2B Phase 8 decisions block in `decisions.md` §10b (D37).

### Still ahead in Stage 2B

- — *empty.* Phase 8 (CI matrix + exit-criteria smoke) shipped 2026-05-16; see D37 in `decisions.md` §10b for the resolved decisions and `docs/stage2/exit-checklist.md` for the design-doc §9 mapping. Stage 2B is complete on Windows; the macOS-side §9.6 / §9.7 bullets light up green on the first successful `ci-macos.yml` `workflow_dispatch`.

### Deferred from Stage 2B (still tracked here)

- **Phase 4c — real Spectre `Layout` + `Live` continuous repaint** (per D29). Requires `Console.ReadKey`-based input loop (prerequisite for true `Tab`/`Shift+Tab` keybindings). Also bundles `Ui/SessionListView.cs`, `Ui/SessionView.cs`, `Ui/StatusBar.cs`, persistent schema-drift counter in status bar. Adopting `Console.ReadKey` would ripple through `StringReader`-based test fixtures and the `Process.StandardInput.Write` smoke-test pipeline; only worth landing if the slash-command UX (`/focus`/`/next`/`/prev`, `/list`) proves too rough in real use.
- **`TranscriptModel` on-disk overflow** (per D20). Currently a bounded in-memory ring (10 000 entries, `DropOldest`); D4 envisions disk-backed overflow per `~/.infinicontext/transcripts/<session_id>.log`. Pair with Phase 4c when there's a continuous-render consumer to validate the design against.
- **Throttled mid-session `last_active` updates to the session index** (per D22). Phase 3 writes only on close. The throttled `last_active` from D5 lands with Phase 4c if/when a UI consumer needs "recently used" sorting of *live* sessions.
- **True `Tab` / `Shift+Tab` keybinding for focus rotation** (per D7/D29). `/next` and `/prev` slash commands are the Phase 4b substitute.
- **"Trusted cwds" first-time-discovery banner** — explicitly deferred past Stage 2B per D15. Orthogonal to multi-session mechanics; will become its own task after 2B ships.
- **Per-session settings overrides** — Stage 3 UX territory per D10.
- **N1 — `r` key shortcut for `/resume <highlighted row>`.** Same `Console.ReadKey` dependency as `Tab`; lands with Phase 4c if at all.
- **`b` key + Spectre `MultiSelectionPrompt` picker for `/broadcast`.** Plan §6 sketched a "press `b` → checkbox-pick → one prompt" flow as the broadcast UI; Phase 6 ships only the slash-command surface. Same `Console.ReadKey` dependency as `Tab`/`r`; lands with Phase 4c iff comma-prefix selection feels clunky in real use for ≥4 targets.
- **Real-spawn broadcast fan-out test.** Phase 6 tests drive `BroadcastAsync` against the in-memory `RecordingSession` and `FakeSession`. The end-to-end version (two FakeClaude processes via `SessionManager.CreateAsync`, one `/broadcast` line, both transcripts converge) is best paired with Phase 7's T4 (multi-session lock-contention E2E) — same test scaffold (two real children sharing one host) and the design-doc §9 exit-criteria smoke battery (Phase 8) will exercise it anyway.

### Stage 1 / 2A hangovers that Stage 2B is *not* tackling

- **C-1 deeper restructure (stdin/stdout deadlock).** Current `DropOldest` mitigation suffices through Phase 4b. Stage 2 should consider a dedicated stdin-writer task driven by a queue if multi-session contention surfaces deadlocks. Files: `ClaudeSession.cs` (write path). Source: 20260506-poc/decisions.md §Pending, 20260506-review-stage1-poc/context.md.

## D. Stage 3 (Avalonia GUI)

- **Avalonia 11.x app on Win + Mac.** Begins with a written design proposal; supersedes `infinicontext-initial-design.md` §9 Stage 3. Estimated 4–8 weeks.
- **MVVM flavour decision: CommunityToolkit.Mvvm vs ReactiveUI.** Deferred to Stage 3 spike. Source: `infinicontext-initial-design.md` §8 #5.
- **Markdown library decision: Markdown.Avalonia vs alternative.** Source: `infinicontext-initial-design.md` §8 #6.
- **Collapsible tree view per session.** Each turn = root; assistant text/thinking/tool calls/sub-agents = children. Markdown rendering with syntax-highlighted code blocks. Source: `infinicontext-initial-design.md` §4.7, §9.
- **Persistence escalation decision.** Flat JSON vs SQLite for non-task host state — stay flat unless query patterns force the upgrade. (Task registry is already SQLite per Stage 2A.) Source: `infinicontext-initial-design.md` §9 Stage 3.
- **Settings UX scope.** Model per session, allowed-tools selection, env overrides — table editor or per-spawn dialog. Source: `infinicontext-initial-design.md` §9 Stage 3.
- **Lazy-loading large tool results.** `VirtualizingStackPanel` + on-expand `Read` from Claude's jsonl. Source: `infinicontext-initial-design.md` §6.2, §9.
- **Host-side slash commands.** `/clear`, `/cost`, `/model`, `/resume <id>` — not a terminal. Source: `infinicontext-initial-design.md` §9 Stage 3.
- **Aggregate status strip.** Counts (idle/working/awaiting-approval/schema-drift); full dashboard v2 if it adds value. Source: `infinicontext-initial-design.md` §9 Stage 3.
- **Decoded `stream_event` inner discriminator.** Modeled as `JsonElement` for now; decode only when Stage 3 needs it. Files: `Events/ClaudeEvent.cs`. Source: 20260506-poc/stage-1-plan.md.
- **Hand-rolled parser converters revisited.** Parser uses `JsonNode` round-trip; hand-rolled can come back if Stage 3 needs typed access to deeply nested fields (e.g. `assistant.message.content[i].input`). Source: 20260506-poc/decisions.md.
- **Tree-view UI.** Source: 20260507-tasks-and-sessions-management/decisions.md, design.md §6.
- **Auto-start mode.** UI checkbox firing off agents without confirmation — footgun to design carefully. Source: 20260507-tasks-and-sessions-management/decisions.md.

## E. Stage 4 (release engineering)

- **macOS code-signing / notarization.** Flagged as a Stage 4 problem. Source: `infinicontext-initial-design.md` §6.1.

## F. Light-model research follow-ups

- **`MAX_OUTPUT_TOKENS` ceiling for thinking-on mode.** Quick bench at 512 / 1024 / 2048 to find the smallest cap that doesn't truncate verdicts (~$0.22 of API tokens). Settings has a 1024 placeholder. Already promoted to `docs/todo.md` item 2. Source: 20260507-manual-test-session/decisions.md D9 sub-question.
- **Two-tier escalation (`effort=low` → re-call with `effort=high` on `confidence=low`).** **Deferred — requires user buy-in.** Already promoted to `docs/todo.md` item 3. Source: 20260507-manual-test-session/decisions.md D10 sub-question.
- **Per-tool effort overrides** (`approval.auto.effort.bash: "high"`, …). **Deferred — explicitly post-v1.** Already promoted to `docs/todo.md` item 4. Source: 20260507-manual-test-session/decisions.md D10 sub-question.
- **Long-lived crash-recovery retry policy.** **Open sub-question** — needs user discussion before code. Already promoted to `docs/todo.md` item 5. Source: 20260507-manual-test-session/decisions.md D9 sub-question.
- **Investigate single-shot wall-clock variance.** Native p95 of 17 s on uncapped variant still rough; check if nothink+cap closed the tail enough. Source: 20260507-manual-test-session/findings.md §7.
- **How does long-lived child interact with rate limits across the rest of InfiniContext?** Source: 20260507-manual-test-session/findings.md §7.
- **Long-lived process supervision details.** Crash restart, drain stdin/stdout cleanly on host shutdown, integrate with `IProcessSupervisor` Job Object on Windows. Source: 20260507-manual-test-session/findings.md §7.
- **Track GH `anthropics/claude-code#38022`.** If Anthropic ships `--bare --auth` or `--no-context` with OAuth support, revisit immediately. Source: 20260507-manual-test-session/findings.md §7, `docs/research/claude-cli-light-model.md` Appendix B.
- **First-party consumer for `LongLivedCliLightModelService`.** Service exists per user directive to support all four corners but has no consumer yet; wire as features land. Source: 20260507-implement-light-model-for-non-api-subscriptions/context.md.
- **Larger-scale labelled eval before adversarial use.** Small synthetic probe is a sanity check, not a quality benchmark. Source: `docs/research/claude-cli-light-model.md` §5.3 / §8.
- **N=100 follow-up pass.** N=10 enough for 4× shift, not for tight CIs. Source: `docs/research/claude-cli-light-model.md` §8.
- **Identify the actual `/clear` reset trigger.** Hypothesized 5-min cache TTL but never directly tested by timing the turns. Source: `docs/research/claude-cli-light-model.md` §8.
- **Re-bench on macOS / Linux.** All measurements Windows 11 only. Source: `docs/research/claude-cli-light-model.md` §8.
- **Re-bench Sonnet / Opus.** Only Haiku 4.5 measured. Source: `docs/research/claude-cli-light-model.md` §8.
- **Strip the cosmetic ` ```json ` fence wrapper.** Tighten the prompt further. Source: `docs/research/claude-cli-light-model.md` §5.3.
- **Watch for upstream Anthropic changes.** `CLAUDE_CODE_DISABLE_THINKING`, `--no-session-persistence`, etc. are not all documented stable contracts. Apply MinClaudeVersion/TestedAgainstVersion discipline. Source: `docs/research/claude-cli-light-model.md` §8.

## G. Fixture & testing follow-ups

- **Re-record fixtures against `claude` 2.1.133** + bump `versioning.testedAgainstVersion`. Already promoted to `docs/todo.md` item 7. Source: `CLAUDE.md`, `tests/fixtures/README.md`.
- **Manual smoke test of `/task new` end-to-end under OAuth.** Unit/integration suite passes but failing command not re-run in live session post-fix. Source: 20260507-manual-test-session/progress.md session 4.
- **Record `compaction.jsonl`.** Deferred — needs context fill. Source: `tests/fixtures/README.md`.
- **Record `api-retry.jsonl`.** Deferred — hard to induce (rate-limit). Source: `tests/fixtures/README.md`.
- **Record `cancel-mid-stream.jsonl`.** Deferred — recorder needs interrupt support. Files: `tools/InfiniContext.Recorder/`. Source: `tests/fixtures/README.md`.
- **Produce state-machine `<scenario>.expected-trace.txt` files** as part of state-machine tests. Source: `tests/fixtures/README.md`.
- **Lock recordings in `tests/fixtures/`** — confirm exact NDJSON event schema vs current SDK source. Source: `infinicontext-initial-design.md` §4.2 / §8 #2.
- **Confirm 60 s approval-MCP timeout.** Pick another if real users find it too short. Source: `infinicontext-initial-design.md` §8 #4, 20260506-poc/decisions.md §Pending.

## H. Already-actionable items from `docs/todo.md` (canonical queue)

These are listed verbatim in `docs/todo.md` with full files/decisions/acceptance/cost detail — included here only for cross-reference.

1. **`AutoApprovalSink` → `IAsyncDisposable`** — close the long-lived approval leak when `approval.auto.mode = "LongLived"`. ~30–60 min.
2. **`MAX_OUTPUT_TOKENS` ceiling for thinking-mode bench.** ~$0.22 + 10 min.
3. **Two-tier escalation (low → high on confidence=low).** **Deferred — requires user buy-in.** ~1–2 h after user confirms.
4. **Per-tool effort overrides.** **Deferred — explicitly post-v1, requires user buy-in.** ~2–3 h.
5. **Long-lived crash-recovery retry policy.** **Open sub-question.** ~1 h once policy decided.
6. **Re-record fixtures vs `claude` 2.1.133** + bump `TestedAgainstVersion`. ~2–4 h.

(2A shipped on 2026-05-12: `/task delete <id> [--remove-folder]` — see `docs/cli/howto/task-commands.md`; stale `home_cwd` validation at `/task switch`; build-output rejection at `/task new` via `LooksLikeBuildOutput` guard in `TaskService.CreateAsync`.)

---

## Cross-cutting context

- **Memory files** at `~/.claude/projects/C--src-infinicontext-poc/memory/`:
  - `feedback_avoid_kill_process_tree_windows.md` — relevant for any new `Process.Kill` call (D12 in decisions.md §9).
  - `feedback_cli_flags_before_freeform.md` — relevant for any new slash command with a long free-form text arg.
- **Decision records** at `.claude/tasks/20260507-manual-test-session/decisions.md` (D1–D13) and `.claude/tasks/20260507-implement-light-model-for-non-api-subscriptions/decisions.md` (I1–I8) — light-model architecture. New decisions should append.
- **Research doc** at `docs/research/claude-cli-light-model.md` — canonical, externally-publishable. New bench data goes into `docs/research/claude-cli-light-model/`.
- **Build/test commands** are in `CLAUDE.md` at the repo root.
