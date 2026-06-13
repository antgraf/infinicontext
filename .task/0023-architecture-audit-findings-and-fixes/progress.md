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

### Handoff to next session (post-Phase-D)
- Phases A–D done & verified; all uncommitted on `main`. Recommend the operator branch + commit
  Phase D before Phase E.
- Remaining: Phase E (POSIX C2/C3 + macOS host-lifecycle smoke), Phase F (C1 sentinel lock, M19–M22
  GUI bounding). See plan.md.