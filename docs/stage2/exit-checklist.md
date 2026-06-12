# Stage 2B exit checklist

Maps each bullet from `docs/infinicontext-initial-design.md` §9 "Stage 2B exit criteria" to its automated test or, where automation is structurally impossible, to its manual-proof procedure.

**Snapshot:** 2026-05-16 — Phase 8 shipped (`.github/workflows/ci.yml` + `ci-macos.yml`, `MultiSessionExitCriteriaTests`, this checklist). All criteria below are green on the Windows CI matrix; criteria 6 (macOS half) and 7 (macOS half) require a manual `workflow_dispatch` of `ci-macos.yml` to declare green.

---

## §9.1 — Three concurrent sessions, each in a different cwd, all streaming visibly

✅ **Automated.** `MultiSessionExitCriteriaTests.Three_concurrent_sessions_each_in_different_cwd_stream_independently` — drives `SessionManager.CreateAsync` three times with distinct cwds, emits one assistant event into each, and asserts every transcript holds its own text with no cross-talk. Runs in &lt; 1 s.

## §9.2 — Switch focus between sessions with no I/O loss (events buffered in their channels while off-screen)

✅ **Automated.** `MultiSessionExitCriteriaTests.Events_to_unfocused_session_are_preserved_in_its_transcript` — emits two events into an "off-screen" session (one that the in-test host is not focused on) and asserts both land in its `TranscriptModel`. The implementation invariant is that `SessionTranscriptReader` runs per-session regardless of focus (Phase 4a D3), and the test exercises that path directly.

**Adjunct coverage** for the host-side focus-switch UX lives in `Cli/Ui/MultiSessionHostTests.RequestFocus_replays_target_session_transcript_and_emits_switch_banner` (`/focus <id>` replays the target's transcript on switch) and `Fragments_to_unfocused_session_do_not_echo` (off-screen fragments don't reach the live console).

## §9.3 — Resume works for any session listed in the host index, including one whose process exited cleanly previously

✅ **Automated.** `MultiSessionExitCriteriaTests.Closed_session_persists_to_index_and_resume_threads_id_through_to_factory` — creates a session, closes it, reopens the manager (round-trips the index through disk), and asserts a subsequent `CreateAsync` with `ResumeSessionId` threads that id through to the session factory unchanged.

**Adjunct coverage:**
- `SessionManagerTests.CloseAsync_persists_index_row_with_claude_session_id_and_state` — index persistence shape.
- `SessionManagerTests.OpenAsync_seeds_RecentSessions_from_index_on_disk` — reopen path.
- `SessionIndexTests.*` — index file format and atomic-write semantics.

## §9.4 — Broadcast prompt sent to two sessions returns two independent transcripts in their respective panes

✅ **Automated.** `MultiSessionExitCriteriaTests.Broadcast_to_two_of_three_sessions_lands_only_in_those_two` — `BroadcastAsync` to 2 of 3 active sessions; asserts only the 2 targets received `SendUserMessageAsync`.

**Adjunct coverage:** `SessionManagerTests.BroadcastAsync_*` (sent, NotFound, Failed outcomes) and `Cli/SlashCommands/BroadcastCommandTests` (parser + prefix-matching + Spectre toast rendering).

## §9.5 — Approval prompts in one session don't block input or progress in another

✅ **Automated.** `MultiSessionExitCriteriaTests.Pending_approval_in_one_session_does_not_block_message_send_in_another` — parks an approval request in session A's `PendingApprovalSlot` (it will never resolve), sends a user message to session B, and races B's send against a 2-second deadline. B's send must win.

**Adjunct coverage:** `Cli/SlotApprovalSinkTests`, `Cli/SwitchableApprovalSinkTests`, `Cli/Ui/MultiSessionApprovalTests` (Phase 5 `PendingApprovalSlot` per-session contract).

## §9.6 — Killing the host cleans up all child processes — verified on Windows (Job Object) and macOS (signal handler / `ProcessExit`)

**Windows: ✅ Automated.** `Supervision/JobObjectSupervisorTests.Dispose_kills_attached_child_within_5s` proves the kernel guarantee. Per the test's XML doc:

> `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` means closing the job handle terminates every attached process. The handle closes both on explicit `JobObjectSupervisor.Dispose` and implicitly when the host process dies (Windows closes all kernel handles on termination), so a clean-dispose test exercises the same kernel path as a host crash.

There is no separate "host-died" code path on Windows — the Job Object guarantees the cleanup happens regardless of how the host terminates.

**macOS (graceful host shutdown): ✅ Automated, gated on `[MacOnlyFact]`.** `Supervision/PosixSignalSupervisorTests.Dispose_kills_attached_child_within_grace_plus_buffer` exercises SIGTERM → grace → SIGKILL via `kill(2)` against a real `/bin/sh` child. Lights up on `macos-latest` only — triggered manually via `ci-macos.yml` (Stage 2B D37: macOS CI is `workflow_dispatch`-only on the private repo for cost reasons).

**macOS (SIGKILL-of-host): ✗ Untestable — accepted gap.** A host that receives SIGKILL cannot run its `PosixSignalRegistration` handler — that's the OS contract for SIGKILL. The InfiniContext design accepts this (see `docs/infinicontext-initial-design.md:266` — orphaned-process scan on next host startup is the documented backstop). No automated test covers this case; mitigation is the launch-time orphan scan.

**Manual proof checklist (run before declaring §9.6 green on macOS):**
1. Trigger `ci-macos.yml` via `workflow_dispatch`. All `[MacOnlyFact]` `PosixSignalSupervisorTests` cases must pass.
2. Optional — local repro on a Mac: `dotnet run --project src/InfiniContext.Cli`, spawn one session with `/n .`, then in another terminal `kill -TERM <pid-of-InfiniContext.Cli>`. Confirm no orphaned `claude` / `node` processes via `ps -ef | grep -E 'claude|node'`.

**Future hardening:** an out-of-process integration test (spawn `InfiniContext.Cli`, kill it, scan for orphan PIDs) is filed in `docs/stage2/todo.md` §A. Deferred per D37 — D36's "historically a CI flake source" risk outweighs the small additional regression coverage; the manager-level disposal-cascade tests cover the wiring gap adequately.

## §9.7 — CI green on both `windows-latest` and `macos-latest` in the test matrix

**Windows: ✅** `.github/workflows/ci.yml` runs on every `push` to `main` and every `pull_request`. Pipeline: restore → `dotnet format --verify-no-changes` → build → test (with cobertura coverage collection) → coverage summary in job step output.

**macOS: ✅ pending first manual dispatch.** `.github/workflows/ci-macos.yml` runs on `workflow_dispatch` only — see D37 for the rationale (private-repo `macos-latest` minutes bill at 10× Windows; macOS coverage is needed pre-release but not on every commit). Pipeline: restore → build → test. `[MacOnlyFact]` cases self-skip on Windows and light up here.

**Operating procedure:** before any release that needs to declare Stage 2B exit-green, manually dispatch `ci-macos.yml` and confirm it passes. Also dispatch it after any change to Phase 7 macOS-specific code (`PosixSignalSupervisor`, `FileSystemNaming`, `[MacOnlyFact]` tests) since those have no Windows-side validation.

---

## Phase 7 macOS code validation (D16 follow-through)

The following code shipped on `main` in commit `72f92ff` unvalidated against a real macOS runner per D16. The first green `ci-macos.yml` run is its first real-world exercise:

- `src/InfiniContext.Core/Supervision/Posix/PosixSignalSupervisor.cs` — SIGINT/SIGTERM/SIGHUP/SIGQUIT registration via `PosixSignalRegistration`, `AppDomain.ProcessExit` hook, SIGTERM → grace → SIGKILL reap via `kill(2)`.
- `src/InfiniContext.Core/FileSystemNaming.cs` — case-sensitive comparison on non-Windows. Applied in `TaskFolder.DetectDriftAsync`, `TaskService.MergeAsync`, `WriteMergedIndexAsync`, `LooksLikeBuildOutput`.
- `[MacOnlyFactAttribute]` infrastructure in tests — mirrors `WindowsOnlyFactAttribute`.

After the first green `ci-macos.yml`, append a "macOS validation: passed on YYYY-MM-DD via run #N" line below.

**macOS validation:** passed on 2026-05-20 via the first `ci-macos.yml` `workflow_dispatch`. All `[MacOnlyFact]` `PosixSignalSupervisorTests` cases ran green on `macos-latest`; the Phase 7 POSIX supervisor (`PosixSignalSupervisor`), case-sensitive filesystem hardening (`FileSystemNaming`), and the multi-session lock-contention E2E test (`MultiSessionLockContentionTests`) are now validated end-to-end. Stage 2B is closed.
