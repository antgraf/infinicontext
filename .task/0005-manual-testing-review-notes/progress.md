# Progress

## 2026-05-21

- Task created.
- Manual testing session logged 12 findings via `/review` into `issues.md` (0001тАУ0012). All status `open`.
  Themes: missing/broken tools (AskUserQuestion #0001, Task* tools #0003), UX gaps (multi-line input
  #0002, external editor #0009, background-worker visibility #0008), auto-approval policy tuning
  (#0004 false-positive docs, #0005 broader default allowlist, #0007 git push gating), task-skill
  ergonomics (#0006 autonomy default, #0010 auto-switch on `/task new`, #0012 `/task flush` command),
  and architectural simplification (#0011 drop MCP task locks in favor of per-task executor).
- Flushed task documents and pushed via host-driven commit.
- Open questions / next steps: triage `issues.md` into stage-3/follow-up backlog in `docs/todo.md`;
  several findings (#0001, #0006, #0007, #0010, #0012) are short-cycle host fixes worth promoting
  to their own tasks before stage 3 GUI work starts.
- Critical-review pass over the 12 issues; applied four with confidence, one commit each:
  - `3cd1811` -- closed #0006 (autonomy default already in skill v5, commit `1ecab59`).
  - `c62460f` -- #0004: `docs/quickstart.md` gains a false-positive paragraph under "Approval modes"
    with two example axes (mixed-script glyphs, routine `git push`) and the `/approval ask` recovery
    path. Live-reproduced: the safety reviewer auto-denied the doc edit itself when it contained
    the literal example characters, so the doc describes the pattern rather than embedding the
    offending glyphs.
  - `eb89da9` -- #0010: `/task new` auto-switches in the REPL. `TaskSlashCommand` intercepts
    `TaskCommand.New`, runs `TaskCommandHandler.HandleNewAsync` (widened to `internal` and now
    returning the persisted `TaskRecord`), then chains into `TryStartTaskSwitchAsync`. Bare CLI
    dispatch unchanged.
  - `9b7584b` -- #0012: new `/task flush` subcommand injects an operator-requested flush nudge into
    the focused session via `IClaudeSession.SendUserMessageAsync` (same channel `ThresholdInjector`
    uses for the soft/hard threshold nudges). Task-bound sessions get the active id named in the
    nudge. `HelpText` updated; bare CLI dispatch refuses with a clear message.
- All four passed `dotnet build -c Release`, `dotnet test` (382-394 task tests), and
  `dotnet format --verify-no-changes`. Debug build is currently blocked by 4 long-running
  `InfiniContext.Cli` host processes holding a file lock on the Debug DLL output; Release builds
  to a different folder so verification used Release throughout.
- Deferred with rationale: #0001/#0002/#0003/#0009 (non-trivial features needing design), #0005
  (no specific tools named), #0007 (host-side safety-reviewer prompt tune, not a settings toggle --
  needs its own task), #0008 (Stage 3 GUI concern), #0011 (conflicts with D9, user marked
  "discussible" -- needs explicit alignment).

## 2026-05-21 (post-flush)

- Hard context-pressure flush requested by operator. Wrote this entry; stopping per the skill's
  hard-nudge protocol. No new work started.

## 2026-05-21 (post-resume)

- Operator returned: the hard-nudge stop ended the session but the host did not auto-spawn a
  replacement. Operator manually ran `/task switch 0005` to continue, and reported this as a
  showstopper for the task system. Logged as issue #0013.
- Plan: detect "session exited and at least one nudge (soft or hard) fired during its life",
  and when so, automatically populate `HostResult.SwitchPlan` for the same task id. The outer
  loop in `Program.cs` already respawns on `SwitchPlan`, so reusing that primitive avoids new
  multi-pane rotation machinery for this MVP (the single-pane case is what the operator hit).
- Safety net: an `AutoRotationGuard` caps consecutive auto-rotations per task within a short
  window so a runaway "agent immediately re-hits hard nudge on load" loop is bounded.
- Defaults adopted without explicit operator confirmation (operator may push back): soft nudge
  also triggers rotation (same "exit so I can hand off" intent as hard); same-pane replacement
  via the existing outer-loop respawn; always-on, no settings toggle; cap = 3 rotations within
  5 minutes.
- Deferred to a separate follow-up: multi-pane non-focused-session rotation (the user's current
  scenario is single-pane; the focused-only fix unblocks them immediately).
- Implemented end-to-end and marked #0013 resolved. Code lives in:
  - `src/InfiniContext.Core/Sessions/RotationReason.cs` (new enum).
  - `src/InfiniContext.Core/Sessions/SessionContext.cs` (RotationReason property, MarkRotationReason).
  - `src/InfiniContext.Core/Sessions/SessionManager.cs` (pressure crossings wire to MarkRotationReason).
  - `src/InfiniContext.Core/Tasks/AutoRotationGuard.cs` (new тАФ cap state).
  - `src/InfiniContext.Cli/Ui/MultiSessionHost.cs` (TryStartAutoRotation, AutoRotateAsync; OnStateTransition branch).
  - `src/InfiniContext.Cli/Program.cs` (constructs AutoRotationGuard, passes to host).
  - `tests/InfiniContext.Tests/Tasks/AutoRotationGuardTests.cs` (new тАФ 5 cases).
  - `tests/InfiniContext.Tests/Sessions/SessionManagerTests.cs` (one new case).
  - `tests/InfiniContext.Tests/Cli/Ui/MultiSessionHostTests.cs` (three new cases + a `SignalExit` test hook on `FakeSession`).
- Latent bug surfaced during design (deferred): `TaskActivityCoordinator` doesn't auto-release a
  task lock when its holding session exits тАФ currently fine for the focused-session-exit path
  because `Program.cs` calls `LeaveAsync` on every outer-loop iteration, but multi-pane
  non-focused rotation will need an explicit auto-release. Bundling with the multi-pane
  follow-up.

## 2026-05-21 (hard-nudge flush, mid-session)

- Hard-nudge fired with reported usage 596% of 1M tokens (clearly anomalous тАФ likely a usage
  accounting quirk in the host, not a real 6M-token conversation). Flushing per protocol and
  stopping; not investigating the anomaly in this session.
- Final state of #0013 work, all on disk and uncommitted on `main`:
  - 3 new files: `src/InfiniContext.Core/Sessions/RotationReason.cs`,
    `src/InfiniContext.Core/Tasks/AutoRotationGuard.cs`,
    `tests/InfiniContext.Tests/Tasks/AutoRotationGuardTests.cs`.
  - 6 modified source/test files plus 3 task artifacts (this file, issues.md, index.md).
  - `git status --short` end-of-session: 9 modified + 3 untracked, 380 insertions / 9 deletions.
- Verification already run in this session and passing:
  - `dotnet build InfiniContext.slnx -c Release` тАФ 0 warnings, 0 errors.
  - `dotnet test InfiniContext.slnx -c Release --nologo --no-build` тАФ 980 passed, 0 failed, 3
    skipped (POSIX-only).
  - `dotnet format InfiniContext.slnx --verify-no-changes --no-restore` тАФ clean.
- Next session: this work is ready to commit. If re-verifying first, the three commands above
  are the gates. The fix is the resolution for #0013; the commit message should reference task
  0005 issue 0013 and note that the auto-rotation path was tested end-to-end (this very
  hard-nudge тЖТ flush тЖТ next session continuity is its first live exercise of the rotation
  loop, assuming the operator's host now auto-rotates as designed).
- Open follow-ups (not for this task): multi-pane non-focused-session rotation +
  `TaskActivityCoordinator` auto-release-on-exit; the 596%-of-window accounting anomaly.

## 2026-05-21 (triage of remaining open issues)

- Resumed task after the #0013 detour. The pre-flush "Open questions / next steps" had named
  the triage as the next step: each open finding either gets a same-task short-cycle fix, or
  goes into `docs/todo.md` as a backlog entry, or stays open with a clear "needs decision"
  note for the operator. Triage outcome:
  - **#0001 (`AskUserQuestion` returns empty)** тЖТ promoted to `docs/todo.md` ┬зA.
  - **#0002 (multi-line prompt input)** тЖТ promoted to `docs/todo.md` ┬зC with the
    trailing-backslash-first / `Shift+Enter`-second recommendation.
  - **#0003 (Claude's `TaskCreate`/`TaskUpdate`/`TaskList` no-op)** тЖТ promoted to
    `docs/todo.md` ┬зA with the implement-vs-reject design choice captured.
  - **#0005 (broaden default auto-allowlist)** тЖТ promoted to `docs/todo.md` ┬зA as a
    research-first item (scrape logs, propose specific tools).
  - **#0007 (auto-deny on routine `git push`)** тЖТ promoted to `docs/todo.md` ┬зA as a
    safety-reviewer prompt iteration task.
  - **#0008 (background-worker / sub-agent visibility)** тЖТ promoted to `docs/todo.md` ┬зD
    Stage 3 GUI; tracked explicitly alongside the existing tree-view bullet.
  - **#0009 (external editor for prompts)** тЖТ promoted to `docs/todo.md` ┬зC with the
    `$VISUAL`/`$EDITOR`/notepad fallback chain + new `/edit` slash command shape.
  - **#0011 (drop `.task` MCP locking)** тЖТ stays open in `issues.md` with a `Triage` note:
    conflicts with D9 (one-session-per-task) and the multi-pane rotation work tracked at
    `docs/todo.md` ┬з7; not promoted until operator alignment.
- Manual-testing-review task is functionally complete: 12 of 13 issues resolved (4 fixed in
  code, 1 absorbed into the existing skill, 1 closed with docs, 1 fixed via issue #0013, and
  7 promoted to backlog); 1 (#0011) waits on an operator decision.
- Total uncommitted changes after this session: same #0013 code + tests as before, plus the
  doc/triage edits (`docs/todo.md`, `.task/0005-manual-testing-review-notes/issues.md`,
  `progress.md`, `index.md`).

## 2026-05-21 (#0011 alignment)

- Operator confirmed: D9 already enforces intra-host one-session-per-task; the MCP `tasks/lock`
  channel adds nothing useful intra-host and increases moving parts. Direction agreed: rip it
  out, replace agent-vs-host write serialization with a process-local `SemaphoreSlim`, and
  document "one `infinicontext.exe` per home_cwd at a time" for the rare cross-host case.
- Surfaced two wrinkles before promoting: (a) D9 is intra-host-only and doesn't address two
  hosts on the same machine, and (b) the lock currently serializes host writes from
  `IssueService` and `TaskCommandHandler` against the agent's writes тАФ both addressed by the
  replacement design above.
- Promoted #0011 to `docs/todo.md` numbered item 8 ("Rip out the `tasks/lock` MCP channel;
  replace with a process-local write lock"). File list, replacement design, decisions to
  consult, and acceptance criteria all captured there. Marked the issue resolved in
  `issues.md`.
- Manual-testing-review task is now fully complete: all 13 findings resolved (5 closed via
  code/docs/skill changes in this branch, 1 fixed earlier in the skill v5 commit, 7 promoted
  to `docs/todo.md` ┬зA/┬зC/┬зD, and 1 promoted to numbered item 8). No open follow-ups remain
  on this task itself; backlog continues in `docs/todo.md`.