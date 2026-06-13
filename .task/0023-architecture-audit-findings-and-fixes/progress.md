# Progress

## 2026-06-12

- Task created.
- Verified all 32 audit findings still present on `main` (4 parallel Explore sweeps). M24 nuance:
  one agent argued tree-kill matters less on Windows, but the cross-platform concern holds — kept in scope.
- Decisions (operator did not answer kickoff question; autonomy defaults): C1 = cheap sentinel lock now,
  full multi-host deferred to a Stage 3 design item; sequencing = risk-ordered, tackle all. See plan.md.
- **Phase A complete** (cheap isolated correctness fixes), all with regression tests where observable:
  - M12: drift histogram → `ConcurrentDictionary` (was a plain `Dictionary` raced by the parse loop vs `/state`).
  - M24: `ProcessBuildTestRunner` timeout kill → `entireProcessTree: true`.
  - M23: `ProcessGitRunner` now drains stderr concurrently, tree-kills in `finally`, attaches to the
    supervisor (added optional ctor param; wired at both CLI + GUI sites).
  - M27: `AutoApplyExecutor` backup map keyed with `FileSystemNaming.Comparer` (was `OrdinalIgnoreCase`).
  - M26: `review.*` settings validated in `InfiniContextSettingsLoader.Validate()`; persisted
    `skipBuildTestGate=true` now refused (mirrors `approval.mode=Allow`).
  - M18: new `InfiniContext.Core.AtomicFile` (temp + `File.Move(overwrite)`); used by `IssueService`
    (append/resolve) and `TaskCommandHandler.UpdateFileUnderLockAsync`.
  - M17: `TaskFolder` now reports bidirectional drift (`DetectDriftReportAsync` + `DriftReport`); index
    parse anchored to the canonical `| File | Description | Last updated |` header; `ComposeFirstMessage`
    surfaces both directions; `TaskSwitchPlan` gained `MissingFiles`.
  - M5: secret scrubber value-shape regex broadened (key=value shape + ghp_/github_pat_/AKIA/xox prefixes);
    truncation now applies AFTER redaction.
  - Tests added/extended: TaskFolderTests (drift), ScrubberTests (value-shape + truncation), settings
    loader (review.* bounds), new AtomicFileTests. Touched-area tests green (107 passed). Build + format clean.
- **Not yet committed** — working tree only; awaiting an operator checkpoint before committing
  (on `main`; would branch first).

- **Phase B complete** (lifecycle seams), build + format clean, 119 lifecycle tests green + new ToolStack cases:
  - M6: `SessionStateMachine.AbortSend()` rolls back a failed/cancelled send; `ClaudeSession` poisons stdin
    on write failure (new `SendUserMessageCoreAsync`, `PoisonStdin`).
  - M7: `_writeLock` no longer disposed (would strand WaitAsync waiters); send paths fail-fast on `_disposed`.
  - M8: `SessionManager.CreateAsync` re-checks `_disposed` under the registration lock; disposes+throws if set.
  - M9: index persistence serialized through a `_saveLock` (mutate+snapshot+save as a unit).
  - M10: `_openToolUses` drained as `Abandoned` on turn-ending `ResultEvent` + `HandleProcessExit`
    (new `ToolStackChangeKind.Abandoned`; SessionLogger renders `~`).
  - M11: `IClaudeSession.FirstMessageDelivery` (+`FirstMessageError`, default interface impls so fakes
    don't break); public send awaits it so a prompt can't race ahead of the task pointer.
  - M13: `ApprovalChannel` accept/serve re-listen loop so a respawned sidecar reconnects.
  - M25: `LongLivedCliLightModelService` per-call `_turnLock` spanning real turn + `/clear`.
  - C5: GUI `MainWindowViewModel.DisposeAsync` stays UI-thread (no ConfigureAwait(false)), `_disposed`
    re-entrancy guard, try/finally so `_host.DisposeAsync()` always runs.
- **Phase C code complete + builds** (tasks subsystem lifecycle); Tasks tests pending bin-lock release
  (a background full-suite run is holding Release bins):
  - M16: `TaskStore.DeleteIfNotInProgressAsync` (atomic status-guarded delete) + `DeleteOutcome`;
    `TaskService.DeleteAsync` now deletes the DB row FIRST, then the folder.
  - M14: fork/merge rows created `Idle`; `TaskStore.ResetInProgressToIdleAsync` startup sweep wired into
    both CLI (`Program.cs`) and GUI (`SessionHost`). **Deferred:** `/task idle <id>` manual escape hatch
    (needs a new slash command) — root-cause wedging already addressed by sweep + M15; see plan.md.
  - M15: `TaskActivityCoordinator.ReleaseAllForSessionAsync`; new `SessionManager.SessionClosed` event
    (fired in CloseAsync + DisposeAsync, D21-respecting); CLI subscribes to release a closing session's
    claims (GUI sets `TryEnterTask=null`, so it doesn't use the coordinator — CLI-only concern).

### Handoff to next session
- Phases A+B done & tested; Phase C code lands & builds (run Tasks tests once bins unlock).
- Remaining: Phase D (security: C4, M1–M4), Phase E (POSIX C2/C3 + macOS smoke), Phase F (C1 sentinel,
  M19–M22 GUI). See plan.md.
- Still uncommitted on `main` — recommend the operator commit the completed phases (branch first).

## 2026-06-12 (later)

- Fixed an M11 regression: `Send_before_StartAsync_throws_InvalidOperationException` hung forever
  because the public send awaited `FirstMessageDelivery` before the not-started check, and that gate
  is only ever completed by `StartAsync`. `SendUserMessageAsync` now fails fast on `_process is null`
  before awaiting the gate.
- Full verification on the complete working tree: `dotnet format --verify-no-changes` clean, full
  `dotnet test -c Release` green (1129 passed / 3 opt-in skips + headless GUI suite). This closes
  the pending Phase C Tasks-tests check from the earlier bin-lock.
- Phases A-C committed to `main` and pushed (operator instruction).

## 2026-06-12 (Phase D — security)

- **C4 DONE** (constrain auto-apply writes): `IAutoApplyExecutor.ApplyAsync` gained an optional
  `allowedRelativePaths` param; `ReviewService` now passes `request.Scope.Files` so a fix touching a
  file outside the reviewed set is demoted to NeedsUser, not written. Added an *unconditional*
  denylist (`AutoApplyExecutor.DenylistReason`, internal+tested) that refuses `.git/`,
  `.github/workflows/`, and build-system files (`.csproj/.props/.targets/.slnx/.sln/.fsproj/.vbproj`)
  even when no scope is supplied — these would otherwise be executed by the executor's own
  post-apply `dotnet build`. Tests: 9 new cases + `FakeAutoApplyExecutor` signature bump; 81
  Review-suite tests green, Core builds clean.
- **M1 DONE** (pipe peer auth): `ApprovalChannel` now (a) creates the Windows pipe with a
  current-user-only ACL via `NamedPipeServerStreamAcl.Create` (best-effort; falls back to default on
  failure), and (b) authenticates the connected peer — resolves the client PID via
  `GetNamedPipeClientProcessId` and refuses a connection whose image is confidently NOT the expected
  sidecar exe. Enforcement is Windows-only; non-Windows / unresolvable cases allow (UDS perms scope
  Unix). New ctor param `expectedClientExecutable` (defaults to `Environment.ProcessPath`); the
  launcher passes the resolved `sidecarExe`. Updated SidecarSmokeTests (real spawned sidecar = CLI
  exe) to pass it. New `[WindowsOnlyFact]` rejection test.
- **M2 DONE** (lock ownership): `ApprovalChannel` ignores the wire-supplied owner and always uses
  its pipe name as the lock owner (so dispose-time `ReleaseAllOwnedBy` reclaims everything), and
  releases now go through a new owner-checked `LockRegistry.Release(token, expectedOwner)` overload —
  a connection cannot release another session's lock even with the token. In-process trusted callers
  keep the unchecked `Release(token)`. New registry + cross-channel E2E regression tests.
- **M3 DONE** (injection hardening): extracted the safety-review prompt body to a single shared
  `SafetyReviewPrompt.Body` (was copy-pasted across the CLI + SDK services; each now appends only its
  format tail). Body explicitly frames tool input as UNTRUSTED DATA — injection-shaped text is a
  signal to flag, never an instruction, and must never flip the verdict to allow. `AutoApprovalSink`
  now escalates an `allow` that still flags `prompt_injection` to the human fallback (self-
  contradictory → don't trust). **Deferred sub-point:** "always route shell/network-capable tools to
  ask" — that disables auto-approval for Bash entirely (a product/UX call for the operator); the
  framing + axis escalation already close the actual bypass. See issues.md.
- **M4 DONE** (destructive carve-out): `SafetyReviewRequest` gained a host-verified
  `UnderVersionControl` fact; `AutoApprovalSink.IsUnderVersionControl` walks up from cwd for a `.git`
  dir/file and forwards it (both light-model services emit `under_version_control`). The prompt no
  longer *asserts* VCS — it conditions the in-cwd reversibility exemption on the fact and carves out
  `.git/` writes, history/uncommitted-work-destroying commands (`reset --hard`, `clean -fdx`,
  force-push, rebase/filter-branch, removing `.git`), and overwriting uncommitted files.
- **Phase D complete + verified:** full Core suite green — 1156 passed / 3 POSIX-skips / 0 failed;
  `dotnet format --verify-no-changes` clean. New tests for C4/M1/M2/M3/M4.
- Pre-existing flake observed (NOT mine): `ApprovalSinkFactoryTests.Auto_mode_default_factory_*`
  fails intermittently under parallelism (toggles `ANTHROPIC_API_KEY`); passes in isolation. Logged
  to issues.md#0001.

- **Phase D committed + pushed** to `main` as `40c0e2b` ("Task 0023: architecture audit fixes,
  phase D (security/trust boundaries)", 22 files, +750/−93) per operator "push" instruction.

### Handoff to next session (post-Phase-D)
- Phases A–D done, verified, committed & pushed (`main` @ `40c0e2b`). Working tree clean.
- Remaining: Phase E (POSIX C2/C3 + macOS host-lifecycle smoke), Phase F (C1 sentinel lock, M19–M22
  GUI bounding). See plan.md.
- Phase E entry points: `src/InfiniContext.Core/Supervision/Posix/PosixSignalSupervisor.cs`
  (SIGINT-as-reap-trigger = C2; `Cancel=true` zombie-host = C3), `src/InfiniContext.Cli/Ui/
  MultiSessionHost.cs:112-125` (host's `Console.CancelKeyPress` ownership of Ctrl+C), and
  `docs/stage2/decisions.md` §10b Phase 7 (the incorrect "runtime still terminates" claim to correct).

## 2026-06-12 (Phase E — POSIX signals)

- **C2 DONE** (host owns SIGINT): `PosixSignalSupervisor` no longer registers `PosixSignal.SIGINT`.
  Reap triggers are now SIGTERM/SIGHUP/SIGQUIT + `AppDomain.ProcessExit` + `Dispose`. Rationale: the
  interactive host owns Ctrl+C via `Console.CancelKeyPress` (`e.Cancel = true`) as "interrupt the
  turn"; the old dual-registration reaped every session's children on that gesture while the host
  stayed alive (Windows unaffected — Job Object ignores Ctrl+C, so the platforms diverged). A
  SIGINT that *does* terminate the host (no console handler) still reaps via the ProcessExit hook.
- **C3 DONE** (no zombie host): `OnSignal` now reaps children synchronously then
  `Environment.Exit(128 + signum)` — chose the audit's deterministic option over "leave Cancel=false
  and rely on the runtime's default disposition" because a host blocked on an infinite stdin read
  doesn't cooperatively unwind, so the default-disposition path is not reliably terminal. The old
  `ctx.Cancel = true` cancelled termination outright → zombie host clearable only by `kill -9`.
- **Docs corrected:** `docs/stage2/decisions.md` §"macOS supervisor + C7 + T4 (Phase 7)" — the
  `ctx.Cancel = true` bullet rewritten with a dated C3 correction + new C2 bullet; the best-effort
  signal list and the startup-scan bullet updated to drop SIGINT from the supervisor's handlers.
- **Host-lifecycle smoke (audit item 7) DONE:** new `tests/SupervisorHostProbe` minimal real host
  wires the production `ProcessSupervisor.CreateForCurrentPlatform()` exactly as the CLI does,
  attaches a `/bin/sh -c "sleep 600"` child, mirrors the host's `Console.CancelKeyPress` ownership,
  prints `CHILD_PID`/`READY`, and blocks. Two `[MacOnlyFact]` tests in `PosixSignalSupervisorTests`
  drive it with real signals: `HostLifecycle_SIGINT_leaves_host_and_child_alive` (C2 regression — old
  code would have reaped the child within the 2s grace) and `HostLifecycle_SIGTERM_terminates_host_and_child`
  (C3 regression — old code left the host a zombie). Added the probe to `InfiniContext.slnx` + the
  test csproj (`ReferenceOutputAssembly=false`) + `SupervisorHostProbeRunner` locator (mirrors
  `FakeClaudeRunner`). `ci-macos.yml` header updated to describe the two test layers.
- **Verified (Windows):** full solution builds clean in Release; `dotnet format --verify-no-changes`
  clean; Supervision suite green (11 passed / 5 MacOnly-skipped, incl. the 2 new). The macOS smoke
  itself can only run via `ci-macos.yml` dispatch (10×-billed) — **not yet dispatched**.

- **Committed + pushed** Phase E to `main` as `fd9aae5` (operator "push" instruction).
- **Dispatched `ci-macos.yml`** (run 27452811545) — **FAILED at build**, but NOT the supervisor code:
  the full-slnx `dotnet build` hit NETSDK1073 on `InfiniContext.Gui.UiaDriver` (net10.0-windows +
  WindowsForms, can't build on macOS). Latent breakage from task 0020 adding the UIA projects to the
  slnx; earlier macOS runs passed because the slnx predated them. Logged issues.md#0003.
  **Fix:** scoped `ci-macos.yml` to `tests/InfiniContext.Tests/InfiniContext.Tests.csproj` (builds
  Core/Cli/Gui/FakeClaude/SupervisorHostProbe transitively; UIA is Windows-only via `ci-uia.yml`).
  Re-committing + re-dispatching.

- **ci-macos.yml fix committed + pushed** as `bb135a4`; re-dispatched (run 27452915377) — **GREEN**.
  Test step: 1147 passed / 0 failed / 8 skipped (the skips are the Windows-only tests, e.g.
  `JobObjectSupervisorTests`). The `[MacOnlyFact]` `PosixSignalSupervisorTests` — including both
  `HostLifecycle_SIGINT_*` and `HostLifecycle_SIGTERM_*` — ran (absent from the skip list; passed
  count rose from 1096 → 1147 vs the pre-UIA slnx run) and passed. **C2/C3 validated on real macOS.**

### Handoff to next session (post-Phase-E)
- Phases A–E DONE, verified on Windows, **and Phase E validated on macOS** (ci-macos green).
  `main` @ `bb135a4`, working tree clean.
- Remaining: Phase F (C1 startup sentinel lock; M19–M22 GUI bounding). See plan.md.