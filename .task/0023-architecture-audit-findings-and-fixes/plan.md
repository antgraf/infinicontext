# Plan — Architecture audit findings and fixes

Source: `docs/architecture-review-2026-06-11.md` (5 critical C1–C5, 27 major M1–M27).
All findings verified still-present on `main` at task start (2026-06-12) except as noted.

## Decisions taken (operator did not answer the kickoff question; autonomy defaults applied)

- **C1 approach: sentinel lock now.** Cheap path — exclusive sentinel lock file under
  `~/.infinicontext/` held `FileShare.None` at host startup; a second host fails fast with
  "another InfiniContext host is running". Full multi-host (SQLite-backed claims + WAL +
  cross-process locks) is deferred to a later Stage 3 design item, logged in `docs/todo.md`.
- **Sequencing: risk-ordered, tackle all.** Commit in logical batches; add/extend a test at the
  lowest layer that observes each bug.

## Phases

Ordered by risk/leverage and by "isolated & high-confidence first".

### Phase A — cheap, isolated correctness fixes (low blast radius) — DONE 2026-06-12
- [x] M12 — drift histogram: `ConcurrentDictionary`.
- [x] M27 — AutoApplyExecutor backup map keyed with `FileSystemNaming.Comparer`, not `OrdinalIgnoreCase`.
- [x] M24 — `ProcessBuildTestRunner` timeout kill → `entireProcessTree: true` (cross-platform; not D12's child).
- [x] M23 — `ProcessGitRunner`: drain stderr concurrently, tree-kill in finally, attach to supervisor.
- [x] M26 — validate `review.*` settings in `Validate()`; refuse persisted `skipBuildTestGate=true`.
- [x] M18 — atomic file writes via new `AtomicFile` in `IssueService` + `UpdateFileUnderLockAsync`.
- [x] M17 — bidirectional drift detection (`DetectDriftReportAsync`); anchor index parse to canonical header.
- [x] M5  — secret scrubbing: key=value shape pattern + token prefixes; truncate after redaction.

### Phase B — lifecycle seams (Core) — DONE 2026-06-12 (build + format + 119 tests green)
- [x] M6  — `AbortSend()` on write throw/cancel; poison stdin after failed write.
- [x] M7  — don't dispose `_writeLock`; `_disposed`-gate on send paths.
- [x] M8  — re-check `_disposed` inside registration `lock(_gate)`; dispose+throw if set.
- [x] M9  — serialize index persists with a save semaphore; re-snapshot under it.
- [x] M10 — drain `_openToolUses` as `Abandoned` on turn-ending `ResultEvent` + `HandleProcessExit`.
- [x] M11 — `Task FirstMessageDelivery` (+`FirstMessageError`); await before `_writeLock`; surface failure.
- [x] M13 — accept/serve re-listen loop in `ApprovalChannel`; warn on reconnect.
- [x] M25 — per-call `_turnLock` in `LongLivedCliLightModelService` spanning turn + `/clear`.
- [x] C5  — GUI dispose stays UI-thread; `_disposed` guard; try/finally so `_host.DisposeAsync()` always runs.

### Phase C — tasks subsystem lifecycle — code DONE 2026-06-12 (builds; tests pending bin unlock)
- [x] M14 — fork/merge rows created `Idle`; startup sweep `InProgress`→`Idle` (CLI + GUI).
      DEFERRED: `/task idle <id>` manual escape hatch (new slash command) — sweep + M15 already
      address the wedging root cause; escape hatch is a convenience fallback.
- [x] M15 — `SessionManager.SessionClosed` event (D21-respecting) + `ReleaseAllForSessionAsync`;
      CLI subscribes (GUI uses no coordinator → `TryEnterTask=null`).
- [x] M16 — `DeleteIfNotInProgressAsync` (atomic status-guarded delete); delete row first, then folder.

### Phase D — security / trust boundaries
- [ ] C4  — constrain `finding.File` to `ReviewScope.Files`; hard-denylist `.git/`, `.github/workflows/`, build files.
- [ ] M1  — pipe ACL to current user; verify connected client PID is the expected sidecar.
- [ ] M2  — ignore wire owner, pass `_pipeName`; verify releasing channel owns the token.
- [ ] M3  — delimit reviewed tool input as opaque data; injection-trip / shell-or-network tool → human `ask`.
- [ ] M4  — carve `.git/`, untracked/uncommitted, VCS-history-destroying commands out of "non-destructive"; host verifies VCS.

### Phase E — POSIX signals (re-validate on macOS)
- [ ] C2  — host owns SIGINT; supervisor reaps only on SIGTERM/SIGHUP/SIGQUIT/Dispose (or suppression hook).
- [ ] C3  — leave `Cancel=false` (or `Environment.Exit(128+sig)`); correct `docs/stage2/decisions.md`.
- [ ] (after C2/C3) add a host-lifecycle smoke to `ci-macos.yml`; dispatch it.

### Phase F — GUI bounding & shared wiring
- [ ] C1  — startup sentinel lock under `~/.infinicontext/` (single-host enforcement). [see decision above]
- [ ] M19 — `SessionContext` bounded replay buffer + atomic subscribe-with-replay; delete IsReady band-aid.
- [ ] M20 — promote approval-sink + review-stack construction to Core; parameterize UI bits.
- [ ] M21 — cap/collapse `Turns`; delete or bind `TranscriptViewModel`; auto-retire `Exited` sessions; virtualize panels.
- [ ] M22 — per-command CTS (cancel on shutdown + stop affordance); reject overlapping; batch dispatcher writes.

## Notes
- M19/M20 (Core replay surface + promote wiring) are prerequisites flagged in thematic improvements;
  C1 sentinel lock is independent and lands in Phase F with the GUI work since both touch host startup.
- Each finding closed gets a `progress.md` entry; out-of-scope discoveries go to `issues.md`.
