# Progress

## 2026-06-03 (commit prepared on a branch -- awaiting operator approval at `git commit`)

Tree re-confirmed against the documented commit-ready state: 13 modified + 4 untracked, no
stashes, was on `main`. No code changed this session.

- **Branched** off `main` -> `task/0013-gui-session-transfer` (harness rule: don't commit on
  the default branch).
- **Staged commit 1 (bug A = `session_exit` MCP tool)** with explicit paths (never `git add -A`,
  so `settings.user.json` stays out -- verified clean): `ApprovalChannel.cs`,
  `ApprovalSidecarContract.cs`, `ApprovalSidecarHost.cs`, `LockMessages.cs`,
  `Mcp/Sessions/SessionExitMessages.cs` (new), `ApprovalSidecarLauncher.cs`, `ClaudeSession.cs`,
  `InfiniContextTaskSkill.cs`, `Mcp/Sessions/SessionExitMessagesTests.cs` (new),
  `Mcp/Sessions/SessionExitChannelTests.cs` (new), `SidecarSmokeTests.cs`,
  `ClaudeSessionGracefulExitTests.cs` (new). 12 files staged.
- **Commit 2 (bug B + docs)** left unstaged: `AutoRotationService.cs`, `SessionHost.cs`,
  `AutoRotationServiceTests.cs` + `.task/0013-*` (index/progress/design).

Asked the operator the next-step question (commit / full-build-first / Slice 3 / re-verify);
no selection returned (same pattern as prior sessions). Took the recommended path's
**reversible** prep only and stopped at the `git commit` boundary, which stays operator-gated.

**Where I stopped / next session:** commit 1 is staged and ready on branch
`task/0013-gui-session-transfer`; on operator approval run the two `git commit`s below
verbatim, then push. Slice 3 still needs a code-fenced first card on a freshly auto-rotated
GUI row + operator's eyes; Slice 4 conditional.

**Drafted commit messages (run verbatim on approval):**

Commit 1 (bug A -- already staged):
```
Task 0013: session_exit MCP tool for agent-driven graceful exit (bug A)

Adds mcp__infinicontext__session_exit so a task agent can close its own
session after flushing to artifacts, instead of only exiting on operator-
driven stdin close. This is the unblocker for GUI auto-rotation.

- LockMessages: SessionExitKind = "session.exit" wire kind
- Mcp/Sessions/SessionExitMessages: request/response + JSON round-trip
- ApprovalSidecarContract/Host: register session_exit tool (optional reason)
- ApprovalChannel: onSessionExit callback; fire-and-forget reply is load-
  bearing so {ok:true} lands before stdin closes
- ApprovalSidecarLauncher: allowlist the tool so it's auto-honored, not
  routed to the approval sink
- ClaudeSession.RequestGracefulExitAsync: closes stdin, no Kill, idempotent
- InfiniContextTaskSkill: soft+hard nudges now call session_exit (Version 6)
- Tests: message round-trip, channel callback, FakeClaude graceful exit,
  sidecar smoke (tools/list 3->4 + end-to-end tools/call)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

Commit 2 (bug B + docs -- stage: AutoRotationService.cs, SessionHost.cs,
AutoRotationServiceTests.cs, .task/0013-* index/progress/design):
```
Task 0013: fix auto-rotation dispose-order race + drain (bug B); docs

- SessionHost: dispose SessionManager before AutoRotationService so the
  Exited transition fires while still subscribed
- AutoRotationService: bounded drain of in-flight ProposeAsync on Dispose
- AutoRotationServiceTests: drain-blocks-until-prepareSwitch-completes
- .task/0013: design.md + index/progress updates (incl. end-to-end smoke)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

## 2026-06-03 (format gate + GUI compile NOW GREEN -- two long-standing "blocked" items cleared)

Cleared two items that had been marked "blocked by GUI/VS bin locks" across ~5 prior
sessions, **without** the operator closing anything. The locks only block *writing* to
`InfiniContext.Gui\bin\...`; they don't block the two checks below.

- **Full-solution format gate PASSES.** `dotnet format InfiniContext.slnx
  --verify-no-changes --no-restore` -> **EXIT=0**. `dotnet format` loads the workspace via
  MSBuildWorkspace (source parse, no `bin/` write), so the running GUIs don't block it.
  This validates **every** change -- bug A's Core files *and* bug B's GUI-side
  `SessionHost.cs` dispose reorder -- against CI's canonical formatting authority for the
  first time. Prior sessions wrongly lumped format in with the blocked build.
- **GUI project COMPILES clean.** `dotnet build src/InfiniContext.Gui/InfiniContext.Gui.csproj
  -c Debug --no-restore -p:OutDir=<temp>` -> **EXIT=0, 0 warnings, 0 errors** (redirected
  output dir dodges the locked `bin/`; default obj reused so no restore needed). This
  retires the **"bug B GUI-side reorder is inspection-validated only"** caveat carried since
  2026-05-28 -- `SessionHost.cs` now provably compiles.

**Net:** the only check still genuinely blocked is a single-shot full
`dotnet build InfiniContext.slnx` (the GUI `bin` exe is locked by 3 running instances --
PIDs 25936/31628/31764 + devenv 29600). That build is now redundant: Core+CLI+Tests build
green via the workaround, the GUI compiles green via alternate OutDir, format is green
across the whole solution, 1060 tests pass, and the end-to-end smoke passed. CI will run
the canonical full build on push regardless.

**Commit-readiness verdict: everything an agent can validate is green.** The commit
decision remains operator-gated (git pauses). No code changed this session.

**Handoff (hard nudge, exiting at 108%):** surfaced the next-step decision to the operator
(commit now / `/task review` / close GUIs for full build / drive Slice 3) -- recommended
**commit now** as two logical commits: (1) bug A = `session_exit` MCP tool (Core/Mcp files +
`Mcp/Sessions/` new tests + `SidecarSmokeTests`); (2) bug B + `.task/0013` docs
(`AutoRotationService.cs`, `SessionHost.cs`, `AutoRotationServiceTests.cs`). No selection
returned before this nudge. Next session: execute whichever the operator picks; if commit,
pause at `git commit` for approval.

## 2026-06-03 (re-verification -- tree still green, commit still operator-gated)

Fresh session re-confirmed commit-readiness; no code changed.

- **Working tree** matches the documented commit-ready state exactly (git status: 13 modified
  + 4 untracked -- bug A files, bug B files, `Mcp/Sessions/` source+tests, graceful-exit test,
  `.task/0013-*` docs). No stashes, no surprises.
- **Tests green** via the documented GUI-lock workaround (build Core -> rebuild CLI for the
  sidecar smoke -> `dotnet build tests/...csproj -p:BuildProjectReferences=false` ->
  `dotnet test --no-build`): **1060 passed, 3 skipped (POSIX-only), 0 failed.**
- **Full-solution build + `dotnet format --verify-no-changes` STILL BLOCKED** by the same
  locks: 3 running GUI (PIDs 24508, 25936, 31628) + VS (29600) hold
  `InfiniContext.Gui\bin\...\Core.dll`. Bug B's GUI-side dispose reorder remains
  inspection-validated only until those close (or CI runs it on push).

**Ball in the operator's court** (presented this session, no selection returned): (1) commit
the two logical commits, letting CI own the format gate; or (2) close GUIs + VS so the full
build + format gate can run locally first; or (3) hold the commit and drive the Slice 3
code-fenced-card repro. Nothing committed.

## 2026-06-03 (MANUAL GUI SMOKE PASSED -- full auto-rotation chain end-to-end)

**The whole feature works.** First complete end-to-end observation of the auto-rotation
chain in the GUI, captured in `src/InfiniContext.Gui/bin/Debug/net10.0/logs/gui-20260603-195235.log`.
This is handoff item #3 ("the real prize") -- DONE. It validates **bug A and bug B together**.

Sequence (all timestamps 19:56, session `c9e6e580` -> new `328f0952`/`3bf65f3a`):

1. `19:56:25.316` `context pressure: soft threshold crossed used=713494 pct=71%` ->
   `injecting SOFT nudge`. (Soft path, not hard -- the graceful path, even better than the
   originally-planned hard-threshold smoke.)
2. `19:56:39.542` agent emitted `tool_use mcp__infinicontext__session_exit` with a `reason`
   arg -> **bug A: the agent has a programmatic exit and used it.**
3. `19:56:39.591` sidecar replied `{"ok":true}` as a `tool_result` *before* stdin closed ->
   the load-bearing fire-and-forget in `ApprovalChannel.HandleSessionExit` did its job.
4. `19:56:39.547` `graceful-exit: closing stdin pid=30820`; then `19:56:41.733`
   `state Idle -> Exited (ProcessExit(exitCode=0))` -> **clean EOF exit, code 0, NOT killed.**
   `RequestGracefulExitAsync` closed stdin and let claude exit naturally, exactly as designed.
5. `19:56:41.740` `auto-rotation: task 13 reason="Soft" plan=true` -> **bug B: the
   AutoRotationService was still subscribed at exit time** (dispose-order reorder + drain
   working) so the Exited transition produced a proposal instead of being silently lost.
6. `19:56:41.757` new session spawned with `--allowed-tools ...,mcp__infinicontext__session_exit`
   -> the launcher allowlist addition (the "extra fix not in design.md") is in the argv.
7. `19:56:41.780` `gui-auto-rotate: task 13 reason="Soft" new-session="328f0952..."` -> **new
   row appeared under the same task header with no app restart.** The successor session then
   ran the skill and read index.md + progress.md normally.

**What this closes:** the original 2026-05-27 repro ("agent says Exiting but session stays
alive") is dead -- the agent now exits programmatically and the successor spawns automatically.
Bug B's GUI-side dispose reorder, previously inspection-only, is now observed end-to-end (the
`auto-rotation: ...` log line is exactly the marker prior handoffs said would prove it).

**Caveat -- Slice 3 still not reproduced.** The successor row's first rendered card was plain
prose ("I'll start by loading the task skill..."), no code fences, so the cross-rebind markdown
misrender (Fix 2) still has no repro. The *prerequisite* (a freshly auto-rotated row) is now
met, but observing the glitch needs a code-fenced first card on the new row + the operator's
eyes. Still effectively blocked on a visual data point, not on the chain.

**Remaining (unchanged in priority):** (1) commit -- still operator-gated; (2) full-solution
`dotnet build` + `dotnet format --verify-no-changes` -- still blocked by 3 running GUI + VS
(PIDs as of now: GUI 27168/31656/35228, devenv 29600); CI will run it on push regardless;
(4) Slice 3 -- needs the code-fenced-card smoke above; (5) Slice 4 -- still conditional, no
evidence.

### Handoff to next session (hard nudge -- this session exiting at 122% context)

This session's only production-affecting work was **documentation**: recorded the smoke pass
above + updated `index.md`. No code changed. Working tree is unchanged from the prior
commit-ready state (bug A + bug B + doc edits, all uncommitted).

**Where I stopped:** asked the operator the commit question and got the hard nudge instead.
The pending decision is theirs to make:
- **Commit now** -- two logical commits (bug A; bug B + `.task/0013-*` docs), let CI own the
  full `dotnet format --verify-no-changes` gate (still blocked locally by 3 running GUI + VS).
- **Or** close the GUIs + VS first, run full `dotnet build InfiniContext.slnx` +
  `dotnet format InfiniContext.slnx --verify-no-changes` locally to validate bug B's GUI-side
  dispose reorder against the canonical authority, then commit.

**Next actions, in priority order:** (1) the commit decision above; (2) Slice 3 -- now that
auto-rotation spawns a fresh row, get a code-fenced first card on the new row and check the
markdown misrender (still the only missing repro); (3) Slice 4 -- conditional, no evidence.

## 2026-06-03 (re-verification -- tree confirmed commit-ready)

Re-confirmed the working tree is commit-ready before the commit step.

- **Core + CLI + Tests build green** (GUI-lock workaround: build Core, rebuild CLI so the
  sidecar exe `SidecarSmokeTests` spawns is current, then
  `dotnet build tests/...Tests.csproj -p:BuildProjectReferences=false`).
- **`dotnet test ...Tests.csproj --no-build`: 1060 passed, 3 skipped (POSIX-only), 0 failed.**
- **Full-solution `dotnet build InfiniContext.slnx` STILL BLOCKED** by running GUI processes
  (PIDs 27168, 27740) + VS Insiders (29600) holding `InfiniContext.Gui\bin\...\Core.dll`.
  So bug B's GUI-side dispose reorder remains inspection-validated only; the full build +
  `dotnet format --verify-no-changes` canonical gate needs the GUIs + VS closed (or will run
  in CI on push). Manual GUI smoke (handoff item #3) is blocked on the same close.

**Ball is in the operator's court:** close the two GUI processes + VS to unblock the full
gate and the smoke, or approve the commit to defer the format gate to CI.

## 2026-06-03 (bug A implemented -- `session_exit` MCP tool)

Implemented bug A per `design.md` (the actual unblocker for GUI auto-rotation). All 5
slices landed; build + full test suite + format gates green (within the GUI-lock limit).

### What landed (production)

- **Slice 1 -- wire + contract + sidecar tool.**
  - `LockMessages.cs`: added `LockWireFormat.SessionExitKind = "session.exit"`.
  - New file `Mcp/Sessions/SessionExitMessages.cs` (namespace `InfiniContext.Core.Mcp.Sessions`):
    `SessionExitRequest(string? Reason)` + `SessionExitResponse(bool Ok)` with `ToJson`/`FromJson`.
  - `ApprovalSidecarContract.cs`: `SessionExitToolName = "session_exit"` +
    `FullyQualifiedSessionExitToolName`.
  - `ApprovalSidecarHost.RunAsync`: registers the `session_exit` tool (optional `reason`
    arg) after `task_release_lock`. Returns `{ok:true}`; surfaces `ok:false` as a tool
    error (isError=true) when the host has no handler.
- **Slice 2 -- channel.** `ApprovalChannel` gained `onSessionExit` ctor param + field, a
  `case LockWireFormat.SessionExitKind` branch, and `HandleSessionExit` (synchronous --
  the fire-and-forget `Task.Run(onSessionExit)` is the load-bearing bit so `{ok:true}`
  is written to the pipe before stdin closes). No-handler path logs a warning + returns
  `{ok:false}`.
- **Slice 3 -- ClaudeSession + launcher + allowlist.**
  - `ApprovalSidecarLauncher.ConfigureAsync` gained `onSessionExit` param, passes it to the
    `ApprovalChannel` ctor.
  - **Extra fix not in design.md (caught during impl):** the launcher must also *allowlist*
    `FullyQualifiedSessionExitToolName` (gated on `onSessionExit is not null`), exactly like
    the lock tools -- otherwise claude would route `session_exit` through the approval-prompt
    tool instead of auto-honoring it. Without this the feature would silently route to the
    sink.
  - `ClaudeSession`: new public `RequestGracefulExitAsync(ct)` -- acquires `_writeLock`,
    `_process.StandardInput.Close()`, no Kill; idempotent (no-op if exited). Private
    `OnSessionExitRequestedAsync` is the callback wired into `ConfigureAsync(onSessionExit:)`.
- **Slice 4 -- skill.** `InfiniContextTaskSkill.cs:143-144`: both soft + hard nudge now say
  "then call `mcp__infinicontext__session_exit`". `Version` bumped 5 to 6.

### Tests (Slice 5)

- `Mcp/Sessions/SessionExitMessagesTests.cs` -- round-trip (reason, null reason, missing
  field, response ok true/false, default-false).
- `Mcp/Sessions/SessionExitChannelTests.cs` -- callback fires with reason + `{ok:true}` over
  a raw pipe; no-handler -> `{ok:false}`; sink never called.
- `Sessions/ClaudeSessionGracefulExitTests.cs` -- real FakeClaude child blocked on stdin;
  `RequestGracefulExitAsync` closes stdin -> child exits **0** (EOF, not killed) -> `Exited`;
  idempotent second call.
- `Mcp/SidecarSmokeTests.cs` -- **updated** the `tools/list` assertion 3 -> 4 (added
  `session_exit`); **added** `Sidecar_session_exit_forwards_to_channel_callback_and_returns_ok`
  driving the real spawned sidecar via `tools/call session_exit` -> channel callback fires +
  `{ok:true}`. This is the genuine end-to-end; design.md's "FakeClaude action script"
  variant (test 4) is **not feasible** -- FakeClaude has no MCP client so it can't originate
  the `tools/call`; emitting a `tool_use` line on its stdout would not reach the sidecar.

### Build / verification

- GUI exe lock recurred (PIDs 27032, 32440 + VS hold `InfiniContext.Gui\bin\...\Core.dll`).
  Workaround per CLAUDE.md: built Core, then **rebuilt `InfiniContext.Cli`** (the sidecar exe
  `SidecarSmokeTests` spawns -- stale CLI = old tool list, which failed the count assertion
  until rebuilt), then `dotnet build tests/...Tests.csproj -p:BuildProjectReferences=false`.
- `dotnet test ...Tests.csproj --no-build`: **1060 passed, 3 skipped (POSIX-only), 0 failed.**
- `dotnet format --verify-no-changes` on Core + Tests projects: exit 0 each. Full-solution
  format gate (CI's canonical authority) still needs the GUIs closed; CI will run it on push.

### Handoff to next session

**Where I stopped:** bug A fully implemented + tested. Working tree now also carries the
bug B (dispose order + drain) changes from prior sessions, still uncommitted.

**What's left, in priority order:**

1. **Commit.** Two logical commits (operator approval needed -- git pauses):
   - bug A: `LockMessages.cs`, `SessionExitMessages.cs` (new), `ApprovalSidecarContract.cs`,
     `ApprovalSidecarHost.cs`, `ApprovalChannel.cs`, `ApprovalSidecarLauncher.cs`,
     `ClaudeSession.cs`, `InfiniContextTaskSkill.cs` + the 3 new test files + `SidecarSmokeTests.cs`.
   - bug B (pre-existing in tree): `AutoRotationService.cs`, `SessionHost.cs`,
     `AutoRotationServiceTests.cs`.
   - Plus `.task/0013-*` doc updates.
2. **Operator: close GUIs + VS lock, run full `dotnet build InfiniContext.slnx` +
   `dotnet format InfiniContext.slnx --verify-no-changes`** to validate the GUI-side bug B
   reorder (still only validated by inspection) and the canonical format gate.
3. **Manual GUI smoke -- the real prize.** With bug A in, re-launch GUI, task-bind a session,
   trip a hard threshold, and confirm the agent calls `session_exit` -> session exits -> a new
   row spawns under the same task header without an app restart. This is the first time the
   full auto-rotation chain (nudge -> flush -> session_exit -> Exited -> AutoRotationProposal ->
   new row) can actually be observed end-to-end. Watch for `[INF] session.exit requested by
   agent` and `[INF] graceful-exit: closing stdin` log lines.
4. **Slice 3 (Fix 2 markdown renderer).** Now unblocked once #3 produces a fresh auto-rotated
   row -- that's the misrender repro Slice 3 was waiting on.
5. **Slice 4 (Fix 3 task exclusivity).** Conditional, no new evidence.

**Cautions:**

- The fire-and-forget in `ApprovalChannel.HandleSessionExit` is load-bearing -- see comment +
  design.md "Sequence diagram". Don't await `onSessionExit` synchronously (races the reply
  against the stdin close).
- The allowlist addition in `ApprovalSidecarLauncher` is what makes `session_exit` auto-honored
  rather than routed to the approval sink. Removing it silently breaks the feature.
- `SidecarSmokeTests` spawn the **real CLI exe** -- after any sidecar-contract change you must
  rebuild `InfiniContext.Cli` or the tests run against a stale binary (the failure looks like a
  bogus tool-count / isError mismatch, not a contract bug).

## 2026-05-28 (bug A design вЂ” `session_exit` MCP tool)

**Hard nudge fired at end of this session at 359% of 1M tokens** вЂ” flushed
and exited cleanly. The design.md + handoff below are the complete state.
This is also another live data point for bug A (the irony: I cannot
`session_exit` myself; operator must close).

### What landed

Wrote `design.md` capturing the design for `mcp__infinicontext__session_exit`,
the agent-driven exit path that's the actual unblocker for the GUI
auto-rotation feature (the 2026-05-27 "agent says Exiting but session stays
alive" repro). No production code changed this session вЂ” design only.

**Design summary** (full text in `design.md`):

- **New tool** `session_exit` registered in `ApprovalSidecarHost.RunAsync`
  with a 1-field optional schema (`reason: string?`). Returns `{ok:true}`.
- **New wire kind** `session.exit` multiplexed onto the existing approval
  pipe alongside `approval`, `lock.acquire`, `lock.release`.
- **Host-side**: `ApprovalChannel` gains an optional
  `Func<SessionExitRequest, CancellationToken, Task>? onSessionExit`
  callback. New `HandleSessionExitAsync` does `Task.Run(onSessionExit)` then
  immediately replies `{ok:true}` вЂ” fire-and-forget is **load-bearing** so
  the agent's tool result lands before stdin closes.
- **Session-side**: `ClaudeSession.RequestGracefulExitAsync()` acquires
  `_writeLock`, closes stdin, returns. No `Kill`. Claude exits naturally on
  EOF в†’ existing stdout-loop finally в†’ `HandleProcessExit` в†’ `Exited` в†’
  `AutoRotationService.OnStateTransition` (no changes needed there).
- **Skill update**: `InfiniContextTaskSkill.cs:143-144` replaces "then exit"
  with "then call `mcp__infinicontext__session_exit`" in both soft and hard
  nudge instructions; bump skill `Version` constant.

**Why fire-and-forget on the host**: the agent's tool_use в†’ tools/call в†’
sidecar в†’ channel в†’ callback chain MUST return `{ok:true}` to claude before
stdin closes, or claude may die before reading the tool result. The
`Task.Run` defers the close to the threadpool so the reply is written first.

**Test plan in design.md** covers 4 layers: wire-format round-trip,
ApprovalChannel branch (with + without callback), ClaudeSession graceful
close via FakeClaude, end-to-end via FakeClaude action script. Existing
`AutoRotationServiceTests` need no changes.

**Alternatives rejected** (with rationale in design.md В§"Why this approach
over the alternatives"): operator-driven close button, host-detected
sentinel, N-turn timeout.

### Handoff to next session

**Where I stopped:** design.md written, index.md updated. No production
code touched. The 4 modified files from the prior session
(`AutoRotationService.cs`, `SessionHost.cs`, `AutoRotationServiceTests.cs`,
docs) remain uncommitted in the working tree.

**What's left, in priority order:**

1. **Implement bug A per design.md.** Five-commit slice order suggested in
   design.md В§"Implementation slices":
   1. Wire format + sidecar contract constants + tool registration.
   2. `ApprovalChannel` callback plumbing + handler.
   3. `ClaudeSession.RequestGracefulExitAsync` + launcher wiring.
   4. Skill text + version bump.
   5. Tests (4 categories from design.md В§"Test plan").
   Total estimate ~250 LOC including tests. Could land as one PR.
2. **Full-solution rebuild + manual GUI smoke for bug B + bug A.** Operator
   closes the 2 running GUIs (still alive вЂ” PIDs 11336, 29244 as of this
   session) and runs `dotnet build InfiniContext.slnx`, then re-launches
   GUI + trips a hard threshold and watches for *both*:
   - `[INF] auto-rotation: task 13 reason=Hard plan=...` log line (bug B
     fix observable);
   - new session row appearing under the same task header without an app
     restart (full bug A + B working).
3. **Commit the Slice 1+2 + bug B + drain test work.** As outlined in the
   prior handoff (suggested message: `Task 0013 Slice 1+2 bug B fix:
   dispose order + bounded drain in AutoRotationService`).
4. **Slice 3 (Fix 2 markdown renderer).** Still blocked behind bug A
   end-to-end validation; the misrender on cross-rebind only shows up
   when a *new* row actually spawns from an auto-rotation.
5. **Slice 4 (Fix 3 task exclusivity).** Conditional. No new evidence.

**Cautions for the next session:**

- The fire-and-forget on the `session.exit` handler is load-bearing.
  Awaiting `onSessionExit` synchronously would race the tool reply against
  the stdin close. Don't "tighten" it without re-reading design.md В§"Sequence
  diagram".
- The `_writeLock` acquisition in `RequestGracefulExitAsync` serializes
  against `SendUserMessageAsync`. If a test simulates a turn-injection
  happening concurrent with a `session_exit` call, expect ~ms-level
  ordering вЂ” the close lands when the in-flight write finishes.
- Skill text changes require bumping the `Version` constant or the host's
  drift-detect logs will warn. Check the constant name in
  `InfiniContextTaskSkill.cs` before bumping.
- `LockWireFormat` is named for locks but is the shared wire enum for the
  approval pipe. Adding `SessionExitKind` there is correct; renaming the
  class is out of scope for this task (and would touch every test that
  references it).
- The "tool name vs fully-qualified name" pattern in
  `ApprovalSidecarContract` is: short `session_exit` on the sidecar's
  `RegisterTool` call, fully-qualified `mcp__infinicontext__session_exit`
  in skill text and any place the agent might surface it.

## 2026-05-28 (validation + follow-up #1b)

### Validation of prior session's dispose-order + drain edits

The 2026-05-27 working-tree edits in `SessionHost.cs` (dispose order reversed) and
`AutoRotationService.cs` (`_pending` list + bounded drain in `Dispose`) were
**unbuilt and untested** at the start of this session. Status now:

- **Build (partial).** `dotnet build tests/InfiniContext.Tests/InfiniContext.Tests.csproj
  -p:BuildProjectReferences=false` green. The full-solution build is still blocked
  by two running `InfiniContext.Gui` instances (PIDs 28704 + 22860) locking
  `bin/Debug/net10.0/InfiniContext.Gui.exe` (same hazard as the prior handoff;
  operator hasn't closed them yet). Core builds clean on its own; the test
  assembly links against the cached `InfiniContext.Gui.dll` so the
  `SessionHost.cs` dispose-order line edit is **NOT** linked into the running
  tests вЂ” it has been read by `dotnet format` (clean) but never executed.
  Operator should `dotnet build InfiniContext.slnx` after closing the GUIs to
  validate that part.
- **Tests (53 rotation-relevant cases).** All green:
  - `AutoRotationServiceTests` (8 prior + 1 new) вЂ” pass.
  - `MultiSessionHostTests`, `SessionManagerTests`, `MainWindowViewModelTests`,
    `AutoRotationGuardTests` вЂ” pass via the cached GUI dll, so legacy paths still
    behave.
- **Format gate.** `dotnet format InfiniContext.slnx --verify-no-changes`
  exit=0.

### New test: `Dispose_drains_in_flight_ProposeAsync_until_prepareSwitch_completes`

Resolves follow-up #1b from the 2026-05-27 handoff. Added to
`tests/InfiniContext.Tests/Sessions/AutoRotationServiceTests.cs` after the
existing `Dispose_detaches_handlers_and_suppresses_later_proposals`. Shape:

1. Construct service with `prepareSwitch = async () => await prepareGate.Task`
   (a `TaskCompletionSource<TaskSwitchPlan?>` the test owns).
2. Trigger the qualifying transition (`MarkRotationReason(Hard)` +
   `HandleProcessExit(0)`). After `HandleProcessExit` returns,
   `OnStateTransition` has synchronously enqueued the in-flight `ProposeAsync`
   Task into `_pending` (the `await prepareGate.Task` suspends before
   `_pending.Add` returns).
3. Call `Dispose` on `Task.Run`. After 200 ms, assert `disposeTask.IsCompleted
   == false` вЂ” the bounded drain must be holding.
4. `prepareGate.SetResult(plan)` вЂ” ProposeAsync completes, drain returns.
5. Assert `disposeTask` completes within 2 s and the `AutoRotationProposed`
   event fired with `Approved + plan`.

No timeout-ceiling test was added вЂ” that would consume 5 s of real time per
run. Code-inspection / `DisposeDrainTimeout` constant is sufficient.

### Handoff to next session

**Where I stopped:** Slice 1 + Slice 2 + bug B fix (dispose order + drain) all
validated as far as the GUI lock allows. New drain test landed. Format gate
clean. Working tree still has 4 modified files; nothing committed.

**What's left, in priority order:**

1. **Full-solution rebuild + manual GUI smoke for bug B.** Operator closes
   the running GUIs, runs `dotnet build InfiniContext.slnx`, re-launches the
   GUI, and trips a hard threshold (or sets `softThresholdPct: 2` in
   `settings.user.json` then closes the app while a session is mid-turn).
   Success criterion: at least one `[INF] auto-rotation: task 13 reason=Hard
   plan=...` log line in `src/InfiniContext.Gui/bin/Debug/net10.0/logs/gui-*.log`
   near the `state Idle -> Exited` marker. The drain test proves the
   AutoRotationService side; the SessionHost.cs reorder is only validated end-to-end
   by this smoke run.
2. **Design bug A (no agent-driven exit).** Still the actual unblocker for the
   GUI auto-rotation feature. Four options listed in the 2026-05-27 entry; the
   `mcp__infinicontext__session_exit` MCP tool is probably the right design.
   Out of scope for this task's original framing but it dominates everything
   downstream of bug B.
3. **Commit the Slice 1+2 + bug B + drain test work.** Suggested message
   shape (each on its own commit OR bundled):
   - `Task 0013 Slice 1+2 bug B fix: dispose order + bounded drain in AutoRotationService`
     touching `AutoRotationService.cs`, `SessionHost.cs`, +
     `AutoRotationServiceTests.cs` (new drain test).
   - `.task/0013-*` doc updates as a separate commit or folded in.
4. **Slice 3 (Fix 2 markdown renderer).** Still blocked behind bug A in the
   sense that you can't observe the cross-rebind misrender until a fresh row
   actually spawns. Three options remain: SelectableTextBlock swap,
   Avalonia.Controls.Markdown 12.1.1, or defer.
5. **Slice 4 (Fix 3 task exclusivity).** Conditional. No new evidence this
   session.

**Cautions for the next session:**

- The drain test relies on `OnStateTransition` running synchronously inside
  `HandleProcessExit(0)` and the synchronous prefix of `ProposeAsync`
  completing before `_pending.Add` returns. Both hold today (the await of
  `_prepareSwitch` is the first suspension point, after the `_pending.Add`
  closure has finished). If either changes, the test will flake.
- The `Task.Delay(200)` in the drain test is a "still blocked?" probe, not a
  worst-case bound. If it ever flakes, raise to 500 ms before suspecting a
  real regression.
- The GUI exe lock pattern recurs across sessions вЂ” any test of a
  GUI-touching file requires the operator to close the GUIs first. Capture
  that as a one-line CLAUDE.md addendum if it bites a third time.

## 2026-05-28

- Task created.
- Read SPEC.md / INVESTIGATION.md / FIX-DESIGN.md under
  `_bmad-output/specs/spec-gui-task-session-handoff/`. The investigation + fix design are
  already done; this task implements them.
- Surveyed the anchor code in `main` (commit 004261b + recent Phase 3B commits): confirmed
  `MultiSessionHost.TryStartAutoRotation` is the policy to promote; `SessionManager` has
  no `SessionCreated` event today (needs one for Fix 1's service to subscribe);
  `SessionContext.RotationReason` is already public; `MainWindowViewModel.OnSessionClosed`
  has no rotation handling; `Views/MainWindow.axaml:287,298` uses
  `md:MarkdownScrollViewer` (Markdown.Avalonia 12.0.0-a2) for turn cards.
- Wrote `plan.md` capturing anchor file:line refs, dependency order, and per-slice
  acceptance criteria. No production code changed.

### Slice 1 production code landed (later 2026-05-28 session)

- `src/InfiniContext.Core/Sessions/AutoRotationService.cs` -- new file. Records
  `AutoRotationStatus { Approved, Refused, Failed }` and `AutoRotationProposal`
  (Session, TaskId, Reason, Status, Plan, RefusedCap, RefusedWindow, Failure).
  Subscribes to `SessionManager.SessionCreated` and to each ctx's
  `Session.State.Transitioned`; on `Exited` with `RotationReason != None` AND
  `ActiveTaskId != null` calls `guard.TryRegister`, then `prepareSwitch`, raises
  `AutoRotationProposed`. Unsubscribes per-ctx on first qualifying transition so a
  flapping state machine cannot double-fire. `IDisposable`.
- `src/InfiniContext.Core/Sessions/SessionManager.cs` -- added
  `event Action<SessionContext>? SessionCreated` raised at the end of `CreateAsync`
  (after reader is started, with try/catch around the invoke). No other change.
- `src/InfiniContext.Cli/Ui/MultiSessionHost.cs` -- added optional
  `AutoRotationService? autoRotationService` ctor param + `_autoRotationService` field
  + `_onAutoRotationProposal` handler. In `RunAsync`, subscribes the handler before the
  input loop and unsubscribes in the finally. `OnStateTransition` now branches: if a
  service is wired and `WillServicePropose()` returns true (focused has `RotationReason`
  + `ActiveTaskId`), suppress `_exitSignal` and let the async proposal arrive; otherwise
  fall back to the legacy `TryStartAutoRotation` path so existing
  `MultiSessionHostTests` keep passing without changes. New `OnAutoRotationProposal`
  writes the same console strings the legacy path did: cap-refused, failed
  (`failure.Message`), task not found, and the `hard-nudge`/`soft-nudge`/`nudge`
  "flush detected" line followed by `RequestSwitch(plan)`. Filter:
  only acts on `proposal.Session.Id == _focused.Id` -- single-focus CLI policy;
  GUI's adapter (Slice 2) will have the opposite policy.
  **Em-dash gotcha:** the literal long-dash character got mojibake'd through the
  Edit tool to `\xd1\x82\xd0\x90\xd0\xa4` bytes; fixed via Python rewrite to use
  `тАФ` (matching the legacy line at MultiSessionHost.cs:533). Any future edit
  that introduces non-ASCII characters in this file should prefer `\u####` escapes.
- `src/InfiniContext.Cli/Program.cs` -- after `SessionManager.OpenAsync` and before
  the while-loop, constructs
  `using var autoRotationService = taskService is null ? null :
    new AutoRotationService(sessionManager, autoRotationGuard,
      (id, ct) => taskService.PrepareSwitchAsync(id, ct), logging.Logger);`
  and passes it into the `MultiSessionHost` ctor as `autoRotationService:
  autoRotationService`. The legacy `autoRotationGuard:` param is still passed (the
  service uses the same guard instance; the host's legacy `TryStartAutoRotation` is
  only reachable when no service is wired, which in production never happens once
  `taskService` is non-null -- matching the legacy preconditions).

### Slice 1 tests landed + mojibake fix (next session)

- **Mojibake repair.** `AutoRotationService.cs:58` carried the same em-dash mojibake
  noted in the prior handoff (cp866-flavored bytes `\xe2\x95\xa4\xd0\x92\xe2\x95\xa8\xd0\xa0\xe2\x95\xa8\xd0\xb4`).
  Replaced with the proper U+2014 em-dash bytes `\xe2\x80\x94` via Python rewrite
  (the Edit tool's roundtrip is what created the mojibake in the first place).
  MultiSessionHost.cs uses the literal em-dash in doc comments and `тАФ` in C# string
  literals -- match that convention going forward.
- **`tests/InfiniContext.Tests/Sessions/AutoRotationServiceTests.cs`** -- 8 cases:
  Hard approved + plan, Soft approved, RotationReason.None no-fire, no ActiveTaskId
  no-fire, guard cap Refused (with `RefusedCap`/`RefusedWindow` populated, prepareSwitch
  not invoked), prepareSwitch throws -> Failed with same exception instance, service
  constructed *after* session still observes exit (covers the `Active` snapshot path
  in the ctor), Dispose detaches handlers. `FakeSession` was copied inline (not hoisted
  to a shared helper -- that's a separate follow-up if anything else needs it).
  Pattern: `SessionManager.CreateAsync` via `QueueingFactory`, then
  `ctx.MarkRotationReason(...)`, then `fake.State.BeginSend(); fake.State.HandleProcessExit(0);`.
  Async proposals signal via `TaskCompletionSource<AutoRotationProposal>`; no-fire
  cases rely on `Task.Yield()` to flush continuations before asserting.
- **Build + tests verified.** Stale CLI processes (PIDs 27324, 26060 -- different
  from the 26104/9636 in the prior handoff but same root cause) still lock
  `src/InfiniContext.Cli/bin/Debug/net10.0/InfiniContext.Core.dll`. Workaround:
  `dotnet build tests/InfiniContext.Tests/InfiniContext.Tests.csproj -p:BuildProjectReferences=false`
  after Core has been built individually. Test results:
  - `AutoRotationServiceTests` (8 cases) -> all pass.
  - `MultiSessionHostTests` (15 cases, including the 4 rotation tests) -> all pass via
    the legacy `TryStartAutoRotation` fallback (host constructed without
    `autoRotationService:` in those tests).
  - `SessionManagerTests` (15 cases) -> all pass; the new `SessionCreated` event has
    no subscribers in those tests so the empty-invocation-list path is exercised.
  - `Tasks.AutoRotationGuardTests` (5) -> green; the guard contract hasn't shifted.
  No format-gate run (the `dotnet format` check needs the full solution build, which
  requires the Cli copy to succeed -- defer to whenever the Cli processes are freed).

### Handoff to next session

**Where I stopped:** Slice 1 is complete end-to-end (production code + tests + build
+ green tests). Slice 2/3/4 untouched.

**What's left:**

1. **Full solution build / format gate.** Once the stale Cli processes are freed,
   run `dotnet build InfiniContext.slnx` + `dotnet format InfiniContext.slnx
   --verify-no-changes`. The new test file follows the established conventions
   (no `\r\n` literals, 4-space indent, `using` declarations in the same order as
   neighbors) so the format gate should pass clean, but CI is the canonical authority.
2. **Slice 2 (Fix 1 GUI wiring)** -- `SessionHost` constructs `AutoRotationService`
   and re-exposes the event. `MainWindowViewModel` subscribes and spawns the new row
   using the proposal's `FirstUserMessage` (reuse the tail of `SpawnSessionForTaskAsync`
   after `PrepareSwitchAsync` -- extract a private helper). On Refused/Failed, update
   `StatusText` rather than triggering exit (no CLI-style RequestSwitch in GUI).
   GUI filter is the *opposite* of the CLI: GUI handles proposals for *any* session
   (all rows visible simultaneously); CLI ignored proposals for non-focused sessions.
3. **Slice 3 (Fix 2)** -- Diagnostic + renderer or binding fix. Defer until 1+2 are
   in place; the pragmatic shortcut is to replace the two `md:MarkdownScrollViewer`
   instances at `MainWindow.axaml:287,298` with `SelectableTextBlock TextWrapping="Wrap"`
   (since the codebase's own style comment at lines 34-43 already flags 12.0.0-a2 as
   alpha-quality).
4. **Slice 4 (conditional Fix 3)** -- Task exclusivity in GUI. Only if reproduction
   shows two concurrent sessions on the same task corrupt each other's reads.

**Cautions for the next session:**

- Don't break `MultiSessionHostTests.Hard_nudge_then_session_exit_auto_rotates_via_SwitchPlan`
  (and its soft / baseline / cap siblings). They run via the legacy fallback path
  because they construct `MultiSessionHost` without an `autoRotationService:` arg.
  The fallback is preserved in `OnStateTransition` exactly so these tests pass
  unchanged.
- The CRLF + Spectre.Console-width + smoke-test-drain pitfalls in `CLAUDE.md` apply
  to any new tests.
- `tests/InfiniContext.Tests/Cli/Ui/MultiSessionHostTests.cs:351` (`FakeSession`) is
  the reusable transport stub. `AutoRotationServiceTests` already has its own inline
  copy; if a third consumer arrives, hoist `FakeSession` to a shared
  `tests/InfiniContext.Tests/_Shared/FakeSession.cs` rather than triple-copying.
- The em-dash in source files is brittle through the Edit tool from this host
  environment. Convention in the codebase: actual U+2014 bytes in XML doc comments
  (e.g. `MultiSessionHost.cs:22`) and `\u2014` escapes in C# string literals
  (`MultiSessionHost.cs:533`). If mojibake patterns like `тАФ`
  or `╤В╨...` appear, fix via Python byte rewrite,
  not via Edit (the roundtrip is what created them in the first place).

**Open question** (not blocking): Whether `MainWindowViewModel` should subscribe to
the `SessionHost.AutoRotationProposed` event or whether `SessionHost` should call
`CreateSessionAsync` directly and just notify the VM via the existing `SessionCreated`
event. FIX-DESIGN.md "Fix 1 - Files / types to change" #3 leans toward the VM owning
the spawn so it can also set focus / status text; keep that split.

## 2026-05-27 (manual GUI smoke session вЂ” hard nudge, no rotation)

### What was tested

Operator ran the GUI app with `tasks.softThresholdPct` lowered to 2%. Two runs in
`src/InfiniContext.Gui/bin/Debug/net10.0/logs/`:

- `gui-20260527-225628.log` вЂ” Run 1, 22:56:28 в†’ 23:17:59. The run where the threshold
  crossed and the auto-rotation pipeline was exercised.
- `gui-20260527-231856.log` вЂ” Run 2, the session currently writing this entry.

### Findings (all from Run 1 unless noted)

**The auto-rotation never fired.** `AutoRotationService` produced zero log lines
(`grep -i "auto-rotat"` on the GUI log returns only file-content echoes from agent
tool_use payloads, not actual logger output from `AutoRotationService.cs:131` or
`:148`). Three layered causes, in increasing severity:

1. **Soft was suppressed; only HARD fired (this is by design).**
   - Line 402 of the log: `context pressure: HARD threshold crossed used=1588148
     window=1000000 pct=159 %` at 23:00:32.487.
   - `ContextPressureMonitor.HandleResult` at `Tasks/ContextPressureMonitor.cs:119-127`:
     when the first observed `ResultEvent` is already past the hard fraction, the
     monitor fires `HardCrossed` and *also* sets `_softCrossed = true` as a
     no-flapping guard. Soft is intentionally skipped in the jumped-past-hard case.
   - Net: the operator's `softThresholdPct=2` was eligible, but the very first
     measured turn already burned >hard% of the 1M window. Soft path remained
     unobservable in this run.
   - Implication: to validate soft separately, configure soft well below hard and
     trip it on a shallow first turn (e.g., soft=2, hard=99, and stop before the
     window fills). Or test without `claude-opus-4-7[1m]` so the window is smaller
     and easier to half-fill.

2. **Agent acknowledged the hard nudge but did not (could not?) exit the session.**
   - The HARD nudge was injected as a synthetic user turn at log line 405:
     `tx: {"type":"user", ... "content":"Context is critical (159% of 1000000
     tokens). Flush all relevant progress to artifacts now. Do not start any new
     work or ask clarifying questions."}` вЂ” exactly the text from
     `ThresholdInjector.OnHardAsync` at `Tasks/ThresholdInjector.cs:55`.
   - Agent's reply at line 421: `"Already flushed. progress.md ... Exiting."` вЂ”
     plain text, not a tool call.
   - Session state went `Sending в†’ Streaming в†’ Idle` and **stayed Idle**. Two
     more operator prompts ("list remaining tasks / followups" at 23:14, "report
     context usage" at 23:15) were served normally.
   - **Design gap (= new bug A):** the agent has no programmatic exit path. Claude
     Code's tool surface (`Bash`, `Edit`, `Write`, `Read`, `Grep`, `Glob`,
     `TaskList`, `TaskCreate`, etc., plus the InfiniContext MCP tools and skills)
     does not include "close this session." In stream-json mode the child only
     exits when stdin closes вЂ” which is operator-driven, never agent-driven. The
     skill text at `InfiniContextTaskSkill.cs:144` instructs the agent to "exit"
     after a hard nudge, but the agent has no way to actually do that. So the
     `Exited` transition never came from the agent.

3. **When the session finally exited via app shutdown, the rotation handler
   couldn't fire because the service was already disposed (= new bug B,
   shutdown-order race in Slice 2 code).**
   - Log line 23:17:59.386: `state Idle -> Exited (ProcessExit(exitCode=-1))`.
     Exit code `-1` = killed by the Job Object, which fires on GUI app close
     (operator closed the window). Supervisor disposed 26 ms later.
   - `SessionHost.DisposeAsync` at `src/InfiniContext.Gui/Services/SessionHost.cs:329-335`
     runs in this order:
       1. `_autoRotationService?.Dispose()` вЂ” unsubscribes **all** per-ctx
          `Transitioned` handlers (`AutoRotationService.cs:181-197`).
       2. `_sessionManager.DisposeAsync()` вЂ” kills the child processes, which
          eventually raises `Idle в†’ Exited`.
     By the time the `Exited` transition fires, `AutoRotationService` is no
     longer a subscriber в†’ `OnStateTransition` is never invoked в†’ no proposal,
     no log line, no rotation. The Slice 1+2 tests don't catch this because they
     use `fake.State.HandleProcessExit(0)` *before* tearing down the manager;
     production tears down the manager and the service in the same teardown.
   - The fix is to reverse the order (dispose `SessionManager` first so exits
     propagate, *then* dispose `AutoRotationService`). Or have the service await
     in-flight `ProposeAsync` tasks on `Dispose` so any late-arriving exit still
     gets to log at least the cap/refused/failed path. The simple reorder is the
     2-line change and likely sufficient for app-shutdown handoff (subscribers
     in the VM may already be gone by then, but at minimum we'd get the
     `auto-rotation: task X reason=Hard plan=...` log line that's missing today).

### Settings note

`settings.user.json` in the checked-in working tree contains only `$schema` вЂ” the
soft=2% override was either reverted after the run or applied via env var
(`INFINICONTEXT__TASKS__SOFTTHRESHOLDPCT=2`) or by editing then reverting. The
defaults still say `softThresholdPct: 60, hardThresholdPct: 85`. Confirmation
that soft fired (or didn't) is independent of the override вЂ” finding #1 shows
the monitor would suppress soft regardless when hard is already breached on
turn 1.

### What this means for Slice 3 (Fix 2 markdown renderer)

The hard nudge response from this session is the manual-smoke evidence Slice 3
was waiting on, but it's *partial*: the agent's response card in the GUI was
plain text, not enough to reproduce the markdown misrender. The Slice 3
diagnostic decision (renderer bug vs binding bug) is **still blocked** вЂ” neither
the SelectableTextBlock swap nor the Avalonia.Controls.Markdown 12.1.1 port can
be justified from this run alone. A focused Slice 3 smoke (load a session, get
the agent to emit a code-fenced reply, then trigger a context-pressure event
and watch whether the *new* row's first card mis-renders) is still required.

### Handoff to next session

**Where I stopped:** smoke evidence captured in this entry. No production code
changed in this session. Slice 1+2 still committed at `ebb42a4`. Working tree
still has the uncommitted progress.md + index.md edits from the prior
recon session, plus this new entry.

**What's left, in priority order:**

1. ~~**Fix bug B (dispose order in `SessionHost.cs:334-335`).**~~ **DONE** in
   this same session вЂ” reversed the two lines so `SessionManager.DisposeAsync`
   awaits first, then `_autoRotationService?.Dispose()` runs. Comment updated
   to spell out the rationale and reference this entry. *Not yet built /
   smoked / committed* вЂ” operator should `dotnet build InfiniContext.slnx`
   then re-run the GUI app and trip the hard threshold again. Success
   criterion: at least one `[INF] auto-rotation: task 13 reason=Hard
   plan=...` log line appears in the GUI log near the `state Idle -> Exited`
   marker. (Subscribers in `MainWindowViewModel` may still be gone by the
   time the proposal fires during shutdown вЂ” that's a separate UX question;
   the log line alone validates the dispose-order fix.)
   - **Caveat noted while editing:** `AutoRotationService.Dispose` does
     **not** await pending `ProposeAsync` calls вЂ” the `_ = ProposeAsync(...)`
     in `OnStateTransition` is fire-and-forget. So even with the reversed
     order, `ProposeAsync` may still race against `taskStore.DisposeAsync`
     (which runs after the service dispose). Worst case is an
     `ObjectDisposedException` caught by the existing `catch (Exception ex)`
     at `AutoRotationService.cs:160-172` в†’ logged as `auto-rotation: failed
     to build plan for task X` with `Status=Failed`. Still strictly better
     than today's silent loss. A follow-up to truly drain pending proposals
     on dispose (CountdownEvent / Task list) is captured below.
   - Tests: `MainWindowViewModelTests` covers the proposal-arrival path but
     not the shutdown race. Add a dispose-order test on `SessionHost` or
     `AutoRotationService` if the seam is testable (probably needs a
     `FakeSessionManager` that fires `Transitioned` on dispose).
   - ~~**Follow-up #1a (cleaner B fix):** add proposal-draining to
     `AutoRotationService`.~~ **DONE** in this same session:
     - Added `List<Task> _pending` field sharing the existing `_gate`
       lock with `_handlers` (one lock acquire on the add side, atomic
       snapshot of both lists on the dispose side).
     - `OnStateTransition` now re-checks `_disposed` *inside* `_gate` and
       adds the started `ProposeAsync` task to `_pending`. The
       disposed-check happens under the same lock that `Dispose` uses to
       snapshot, so the add/drain is race-free: either the task is in the
       snapshot, or the add was skipped because Dispose already started.
     - `Dispose` snapshots `_handlers` and `_pending` atomically, sets
       `_disposed=1` under the lock, unsubscribes per-ctx handlers, then
       `Task.WaitAll(pending, DisposeDrainTimeout=5s)` to bound the wait.
       `AggregateException` swallowed -- per-task failures are already
       logged inside `ProposeAsync`'s catch.
     - **Why the dispose-order reorder in `SessionHost.cs` is STILL
       needed:** the drain only helps if `Exited` fires *while the
       service is still subscribed*. With the prior order (service
       disposed first), the handler is unsubscribed before the manager
       kills the child, so the Exited transition has no listener: no
       `OnStateTransition` runs, no `ProposeAsync` is started, and
       there is nothing for the drain to wait on. The reorder makes the
       transition fire while subscribed; the drain makes the in-flight
       proposal complete before downstream (`taskStore`, `supervisor`,
       `logging`) is torn down.
     - **Subscriber callback hazard noted in code:** `ProposeAsync`'s
       synchronous prefix can `Raise` a Refused proposal inline while
       holding `_gate`. If a subscriber ever acquires a lock that
       `_gate` transitively guards or re-enters the service, that is a
       deadlock. Today MVVM marshals through the dispatcher (releases
       the calling thread first) and CLI does not acquire any lock;
       tests use `TaskCompletionSource.SetResult` which is non-blocking.
       Future subscribers must respect this -- comment captured at
       `AutoRotationService.cs:137-140`.
     - **No new unit test added.** Existing `Dispose detaches handlers`
       test continues to pass (handlers still detached). New drain
       behaviour is only observable via a hung `_prepareSwitch`. A
       targeted test would inject a `TaskCompletionSource`-gated
       `prepareSwitch` and assert `Dispose` blocks until the TCS is set
       (with the 5 s ceiling). Captured as **follow-up #1b** so the
       next session can add it without re-doing the analysis.

2. **Design bug A (no agent-driven exit).** This is the actual unblocker for
   the GUI auto-rotation feature, and it's bigger than this task originally
   scoped. Options to evaluate:
   - **Operator-driven close-pane affordance.** Add a "close this session"
     button to each row in `MainWindow.axaml`. The operator manually closes
     the pane after seeing the agent's "Exiting" text. AutoRotation fires
     off the clean exit. Lowest-magic but requires UI design.
   - **Host-detected sentinel.** The agent emits a specific marker (e.g.,
     "[auto-rotate-now]" or a `TaskCreate` of a "session-done" task or a
     specific tool call), the host parses for it and closes stdin to claude.
     Requires either a new MCP tool ("session_exit") or a string-matching
     rule (brittle).
   - **Hard-nudge в†’ host force-exits after N turns of "no progress."** If
     after the hard nudge the session continues to receive operator prompts
     for >N turns without exiting, the host force-closes the session. Brittle
     in different ways but doesn't require an agent contract.
   - **MCP exit tool.** The cleanest: register `mcp__infinicontext__session_exit`
     alongside the existing task lock tools. Agent calls it after flushing;
     the host closes stdin in response.
   - The MCP-exit-tool option is probably the right design but it's a bigger
     diff (new sidecar handler, new tool, new agent docs, new tests).

3. **Slice 3 (Fix 2 markdown renderer).** Still needs the dedicated smoke
   described above. May be blocked behind A вЂ” until auto-rotation actually
   spawns a new row, you can't observe a misrendered cross-rebind card.

4. **Slice 4 (Fix 3 task exclusivity).** Still conditional. No new evidence
   from this run.

**Cautions for the next session:**

- Don't conflate finding #1 (soft suppressed when hard fires first) with a bug.
  It's the documented "no flapping" semantic at `ContextPressureMonitor.cs:122`.
  The soft path needs separate validation with thresholds chosen so that the
  first measured turn lands between them.
- Run 2 (the current session writing this entry) is task-bound at task 13 and
  has been in service for ~30+ turns; context pressure should fire here too
  on the next ResultEvent. If you see the same "agent says Exiting but session
  stays alive" pattern in this log, that's a *second* repro of bug A and worth
  capturing explicitly.
- The dispose-order bug (B) is what makes B observability hardest: even after
  you fix the order, in-process tests of the dispose race may still flake on
  Windows because the Job Object teardown is faster than a managed-thread
  unsubscribe race. Prefer a unit-level seam (`AutoRotationService` watching a
  fake `SessionManager`) over an integration test of the GUI shutdown path.

## 2026-05-27 (later session)

### Slice 3 reconnaissance, blocked on operator decision

Started Slice 3 (Fix 2 markdown renderer). Surveyed `MainWindow.axaml`, the `InfiniContext.Gui.csproj` package landscape, and how `Headline` / `UserPrompt` are actually populated. Two of plan.md's assumptions don't hold:

1. **No whistyun-stable Markdown.Avalonia for Avalonia 12.x exists yet.** `dotnet package search Markdown.Avalonia` (2026-05-27) shows whistyun's latest stable is **11.0.3** (Avalonia 11-only, per the csproj comment at line 38-40). The 12.x stable versions on NuGet are unofficial forks (`ClassIsland.Markdown.Avalonia` 12.0.0, `Markdown.Avalonia.Saiu` 12.0.0). The official avaloniaui org publishes `Avalonia.Controls.Markdown` 12.1.1 - a different package with a different XAML namespace and API surface.
2. **`Headline` carries full assistant replies, not "short prose."** `SessionRowViewModel.AppendAssistantContent` at line 527 assigns `turn.Headline = text.Text` from `AssistantEvent.Content` -> i.e. the assistant's entire text block, which routinely contains code fences, lists, headers. Plan.md Slice 3's "headlines are mostly short prose" rationale for the `SelectableTextBlock` shortcut is wrong in practice; that swap would cause assistant code blocks to render as literal backticks. `UserPrompt` IS mostly plain text and would survive the swap fine.

The decision is now larger than plan.md anticipated. Three options, all reversible:
- **SelectableTextBlock swap** - smallest diff but loses markdown UX for assistant text.
- **Switch to Avalonia.Controls.Markdown 12.1.1** - preserves UX, but it's an API port (different namespace/control names) and adds a new dep to vet.
- **Defer Slice 3 entirely** - run manual GUI smoke first (plan.md's intended ordering, agent cannot do this) to decide 2a vs 2b on actual evidence.

**No production code changed in this session.** Lock acquired on `progress.md`, this entry appended, lock released.

### Handoff to next session (Slice 3, blocked)

**Where I stopped:** package landscape researched, no edits made.

**What's left for Slice 3:** operator picks one of the three options above. If `SelectableTextBlock`: swap both `md:MarkdownScrollViewer` instances at `MainWindow.axaml:287,298`, drop the now-dead `Window.Styles` block at lines 34-43, remove the `xmlns:md` import at line 5, and drop the `Markdown.Avalonia` PackageReference in `.csproj:42`. If `Avalonia.Controls.Markdown 12.1.1`: vet the new package (license/maintenance), swap namespace + control names, verify text wrapping defaults, run the full test suite, then manual GUI smoke. If defer: leave Slice 3 untouched until the operator can reproduce the misrender locally.

**Caution:** Slice 1+2 are fully green - don't lose that. Format gate passes today; any axaml/csproj edit should re-run `dotnet format InfiniContext.slnx --verify-no-changes` after.

## 2026-05-27

### Slice 2 landed (Fix 1 GUI wiring)

- **Resolved the open question above** in favour of "VM owns the spawn." `SessionHost`
  exposes `event Action<AutoRotationProposal>? AutoRotationProposed` as a thin re-publish
  of the underlying `AutoRotationService` event (the field uses `add`/`remove` accessors
  that no-op when the service is null, so the GUI degrades gracefully without
  `TaskService`). `MainWindowViewModel` subscribes; spawn / focus / status-text happens
  in the VM.
- **Pre-flight checks.** Full solution build green, format gate (`dotnet format
  --verify-no-changes`) clean, and the 31 rotation-relevant tests (AutoRotationServiceTests
  + MultiSessionHostTests + SessionManagerTests) pass on `main` before any Slice 2 code
  touched. The "stale Cli processes lock dll" hazard noted in the prior handoff did not
  reproduce this session.
- **`SessionHost.cs`** -- constructs `AutoRotationService` in `StartAsync` with the same
  defaults the CLI uses at `Program.cs:284` (3 rotations / 5-minute window). Disposes it
  before tearing down the `SessionManager`. Field is nullable because the service
  requires a non-null `TaskService.PrepareSwitchAsync` delegate; mirrors the CLI's
  `taskService is null ? null : new AutoRotationService(...)` gate.
- **`MainWindowViewModel.cs`** -- subscribes to `host.AutoRotationProposed` in the
  host-attached ctor, unsubscribes in `DisposeAsync`. New handler
  `OnAutoRotationProposed(AutoRotationProposal)` marshals through `_dispatcher` (the
  service fires on the state-machine thread, same as `EventObserved`) and dispatches by
  status:
  - **Refused** -> `StatusText = "Auto-rotation refused: cap of {N} within {M} min hit on
    task {NNNN}. Spawn manually from the tree."`. No tree mutation.
  - **Failed** -> `StatusText = "Auto-rotation failed for task {NNNN}: {message}"`. No
    tree mutation.
  - **Approved + plan==null** -> `StatusText = "Auto-rotation: task {NNNN} not found ..."`.
  - **Approved + plan** -> calls `_host.CreateSessionAsync(cwd, taskId, plan.FirstUserMessage)`,
    then `AttachRowForContext` (see below). Status text mirrors the CLI's
    "hard-nudge / soft-nudge / nudge flush detected тАФ auto-rotated to task NNNN \"Title\"
    (shortId). drift: ...; warming up..." pattern.
- **`AttachRowForContext(SessionContext, string?)`** -- extracted from
  `SpawnSessionForTaskAsync`'s tail (originally MainWindowViewModel.cs:462-480). Creates
  the `SessionRowViewModel`, hangs it under its `ResolveParentNode(ctx.ActiveTaskId)`,
  focuses it, sets `SelectedNode`, and renders the seed prompt via `row.BeginUserTurn`
  + `ctx.Transcript.Append`. Both spawn paths (manual + auto-rotation) now go through
  this helper so the row appears identically.
- **GUI vs CLI filter contrast.** The CLI's `OnAutoRotationProposal` filters
  `proposal.Session.Id != _focused.Id` (single-focus policy); the GUI's handler has the
  opposite: every row is visible simultaneously, so every qualifying proposal spawns a
  successor regardless of which row the operator is currently reading. This is what the
  earlier handoff anticipated and matches FIX-DESIGN.md's GUI filter section.
- **`MainWindowViewModelTests.cs`** -- 4 new cases covering Refused / Failed /
  Approved-but-task-not-found / dispatcher-marshal-when-off-thread. Approved-with-plan
  is deferred to manual GUI smoke per `plan.md`'s "skip the VM test and rely on the Core
  service tests + manual GUI smoke" escape hatch (testing it requires either a real
  SessionHost or a refactor to inject the CreateSessionAsync callback, neither of which
  pays for itself when the Core service has full coverage already).
- **`InfiniContext.Gui.csproj`** -- added `<InternalsVisibleTo Include="InfiniContext.Tests" />`
  matching the Core project's pattern at `InfiniContext.Core.csproj:22`. The
  `OnAutoRotationProposed` handler is `internal` (event subscriber, not part of the
  public VM surface) and the test asserts against it directly.
- **Build + tests verified.**
  - `dotnet build InfiniContext.slnx` -> green.
  - `dotnet format InfiniContext.slnx --verify-no-changes` -> clean (EXIT=0).
  - `dotnet test InfiniContext.slnx --no-build` -> 1049 passed, 3 skipped (POSIX-only on
    Windows), 0 failed. The full suite (not just rotation tests) ran green.

### Handoff to next session

**Where I stopped:** Slice 1 + Slice 2 complete end-to-end. Slice 3 (Fix 2 markdown
renderer) and Slice 4 (conditional Fix 3) untouched.

**What's left:**

1. **Slice 3 (Fix 2)** -- markdown rendering bug across rebinds. Per
   `_bmad-output/specs/spec-gui-task-session-handoff/FIX-DESIGN.md`, the diagnostic
   call (general renderer bug vs binding-specific) requires running the GUI app
   end-to-end and exercising the auto-rotation path now that it's wired (slice 1+2 land
   the prerequisite). The pragmatic shortcut from `plan.md` Slice 3 is still on the
   table: replace `md:MarkdownScrollViewer` at `Views/MainWindow.axaml:287,298` with
   `SelectableTextBlock TextWrapping="Wrap"` (Markdown.Avalonia 12.0.0-a2 is the only
   suspect named alpha-quality in the codebase's own comments at axaml:34-43). If the
   operator wants markdown preserved, pin to a stable Markdown.Avalonia 12.x once
   whistyun ships one; `SelectableTextBlock` is the smaller reversible change.
2. **Slice 4 (Fix 3, conditional)** -- only if reproduction shows two concurrent sessions
   on the same task corrupt each other's task-doc reads. Code change is minimal: thread
   `TaskActivityCoordinator` into `SessionManager.CreateAsync` so both hosts inherit the
   guard. Skip until evidence demands it.
3. **Suggested next: a manual GUI smoke**. Spin the GUI (`dotnet run --project
   src/InfiniContext.Gui`), open a task-bound session, hammer it until a soft / hard
   nudge fires (or trip the threshold by lowering it in `settings.user.json`), and watch
   the new row appear under the same task header. That's the only path testing the
   Approved-with-plan branch of `OnAutoRotationProposed`. Then ask the operator
   whether they observed the Fix 2 rendering glitch on the new row -- the data point that
   decides 2a (general renderer bug -> swap to SelectableTextBlock or bump Markdown.Avalonia)
   vs 2b (binding-specific -> deeper investigation).

**Cautions for the next session:**

- The auto-rotation event handler is `internal`, not `private` -- the
  `InternalsVisibleTo` shim in `InfiniContext.Gui.csproj` is what lets
  `MainWindowViewModelTests` invoke it directly. Don't promote it back to `private`
  without first refactoring the tests.
- `AttachRowForContext` is now shared between `SpawnSessionForTaskAsync` and the
  auto-rotation path. Any future change to row-attachment ordering (focus, expand, seed
  prompt) MUST be made in the helper -- splitting the two paths would re-introduce the
  drift we just removed.
- The GUI's filter policy is "spawn for any proposal, regardless of focus." If a future
  refactor adds focus-aware filtering, double-check it doesn't accidentally suppress
  auto-rotation for a non-focused pane -- that's the CLI's policy, not the GUI's.
- The `AutoRotationProposed` event re-publish on `SessionHost` is intentionally
  null-safe via `add`/`remove` accessors. If the task subsystem is unavailable
  (`taskService is null` -> `_autoRotationService is null`), subscribing is a no-op
  rather than throwing. The GUI degrades to "manual /task switch only" in that branch
  with no crash surface.
- CLAUDE.md's CRLF + Spectre.Console-width + smoke-test-drain pitfalls still apply to
  any new tests touching CLI-shaped paths. The new VM tests don't touch any of those.
