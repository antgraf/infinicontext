# Progress

## 2026-06-12

- Task created.
- Researched Avalonia testing docs (testing overview, headless platform, headless-xunit, Appium page) and surveyed the GUI/test codebase. Key findings driving the design:
  - Avalonia.Headless.XUnit 12.x requires **xunit v3**; existing tests are xunit 2.9.3 -> headless suite must be a separate project (confirms the two-project proposal).
  - FluentAssertions 7.2.0 supports xunit v3; stay on 7.x (8.x license change).
  - WinAppDriver is unmaintained -> chose **FlaUI.UIA3** over the docs' Appium route for the UIA suite.
  - Hermetic GUI launch already proven by tmp/icx-smoke: `CLAUDE_EXE` + `FAKE_CLAUDE_SCRIPT` + `INFINICONTEXT__TASKS__DBPATH`; gap: session index path (`~/.infinicontext/index.json`) is not overridable -> prerequisite P3.
  - No AutomationIds in any .axaml -> prerequisite P1.
- Wrote **design.md** (the task deliverable): two test projects + `Gui.UiaDriver` classlib + `UiaProbe` ad-hoc console tool; Phase 0 prerequisites P1-P4; seed test lists; `ci-uia.yml` (workflow_dispatch, mirrors ci-macos pattern); risks.
- Operator asked whether the v2/v3 xunit split could be consolidated on v3. Audited `InfiniContext.Tests` for v2-specific APIs: near-zero usage (0 ITestOutputHelper, 0 IAsyncLifetime, 0 MemberData/ClassData, 5 raw Assert calls, 3 ctor-Skip custom attributes, 1 collection fixture). Conclusion: consolidation is cheap.
- Operator decided: **migrate all tests to xunit v3** (latest harness). Wrote **plan-xunit-v3-migration.md** (xunit.v3 3.2.2, VSTest mode kept for ci.yml parity, OutputType Exe, baseline-count acceptance gate, coverage-collection flagged as least-proven integration). Updated design.md to rev 2: `UiaTests` born on v3, Phase 0.5 = migration, v3-under-VSTest risk now owned by the migration plan.
- **Encoding corruption found and worked around** (issues.md#0001): design.md / plan / progress.md were mojibake-corrupted on disk between turns (UTF-8 read as CP1251 and re-saved by some host-side process; non-ASCII chars like em-dash destroyed). All task artifacts rewritten **pure ASCII** as a defensive workaround. Future sessions: keep `.task/` artifacts ASCII-only until 0001 is fixed in the host.

- **Phase 0.5 executed: xunit v3 migration done in the working tree** (operator asked to run the migration in-session rather than as a separate task).
  - Baseline (xunit 2.9.3, Release): 1109 total / 1106 passed / 3 skipped.
  - csproj: xunit 2.9.3 -> xunit.v3 3.2.2, added OutputType=Exe; Test.Sdk 17.14.1 + runner.visualstudio 3.1.5 unchanged (VSTest mode kept per plan).
  - Plan's "near-zero stragglers" audit held for compile (0 errors first build) but UNDERCOUNTED analyzer fallout: v3 analyzers raised xUnit3003 (3 custom fact attributes -> added CallerFilePath/CallerLineNumber ctors passing to base) and xUnit1051 at 574 unique call sites in 42 test files (pass TestContext.Current.CancellationToken).
  - dotnet format analyzers could NOT batch-apply xUnit1051: "Code fix UseCancellationTokenFixer doesn't support Fix All in Solution" (it claims "Formatted N files" while writing nothing -- trap for future use). Fixed instead via 7 parallel agents over a deduped warning-site worklist; all 574 sites fixed, rebuild = 0 warnings 0 errors.
  - Verification matrix all green: full suite counts identical to baseline (1109/1106/3); both CLAUDE.md filter invocations work (ReplSmokeTests=5, Repl_renders=1); trx + XPlat coverage produce non-empty cobertura (~2MB) -- the plan's least-proven integration is CONFIRMED working; dotnet format --verify-no-changes clean. No new smoke-test flakiness across 3 full-suite runs (no xunit.runner.json needed).
  - Environment note, not a migration issue: plain Debug `dotnet test --filter ...` was blocked by running InfiniContext.Gui processes + VS holding Debug bin locks; filters were validated with `-c Release --no-build`.
  - decisions.md: added one bullet to docs/stage2/decisions.md section 12 (Testing discipline).
  - Remaining for acceptance: commit (awaiting operator), green ci.yml run, one green ci-macos.yml dispatch (MacOnlyFact skip pattern must show executed-on-mac / skipped-on-win).

- Migration committed and pushed on branch task/0013-gui-session-transfer: 1c303f2 (tests/ + decisions.md line), 982d77a (task 0020 artifacts). CI still pending: ci.yml has no manual trigger (PR or push-to-main only; branch is 18 commits ahead of main, so a PR carries tasks 0013-0021, operator's call); ci-macos.yml dispatch was blocked by the session permission hook (auto-deny on `gh workflow run`) -- operator must dispatch it once, ref task/0013-gui-session-transfer (or main after merge).

- **CI acceptance closed (both remaining items green).** Migration commit 1c303f2 reached main (ancestor of cf3df48). ci.yml run 27438597399 on main @ cf3df48: success (2026-06-12 19:37 UTC). ci-macos.yml dispatch run 27441695512, headSha exactly cf3df48: success, job "build + test (macos-latest)" green (2026-06-12 20:38 UTC, operator-dispatched). Residual caveat: the MacOnlyFact executed-on-mac / skipped-on-win log pattern could not be eyeballed from this session -- the permission hook auto-denies gh log fetches -- but the green full-suite run at the exact migration-bearing commit satisfies the criterion as written. Operator can spot-check the run log in the browser if they want the skip-pattern confirmation on record.

- **Phases 1 + 2 POC implemented** (operator: "Implement test projects as designed. Add a couple
  of simple tests just as POC."). Three new projects, all green locally:
  - GUI prep (partial P1 + the design 5.2 fallback): App.axaml IS an Application document, so
    the predicted StyleInclude fallback was needed -- style list extracted to
    src/InfiniContext.Gui/Styles/AppStyles.axaml, included from both App.axaml and the headless
    TestApp. AutomationIds added for the POC control set plus LogFilePathLabel (footer log path;
    doubles as the UIA harness's "live VM has replaced the placeholder" readiness beacon).
    Id table started at docs/gui-automation-ids.md (public test contract; P1 remainder listed
    there). InternalsVisibleTo for the new headless assembly added to Gui AND Core (the linked
    SessionContextFactory drives internal SessionContext members).
  - tests/InfiniContext.Gui.HeadlessTests (xunit.v3 3.2.2 + Avalonia.Headless.XUnit 12.0.3
    pinned == GUI Avalonia): TestApp/TestAppBuilder bootstrap, TestRig, linked doubles from
    InfiniContext.Tests (FakeClaudeSession/SessionContextFactory/TestUiTimer; NOT
    SynchronousUiDispatcher -- real Dispatcher.UIThread). 2 POC tests: realize MainWindow under
    production styles + Send/prompt gating; KeyTextInput -> VM -> Ctrl+Enter -> FakeClaudeSession
    delivery (the 0017 focus-contract surface). Both passed first run. Runs inside plain
    `dotnet test InfiniContext.slnx`, so ci.yml picks it up with zero workflow edits.
  - tests/InfiniContext.Gui.UiaDriver (FlaUI.UIA3 5.0.0) + tests/InfiniContext.Gui.UiaTests:
    GuiAppHarness (hermetic icx-smoke env contract + INFINICONTEXT__* stripping + temp sandbox
    cwd + pinned 960x600 window), MainWindowPage/NewTaskDialogPage page objects, DumpTree.
    UiaFact skips unless ICX_UIA_TESTS=1 (Windows-only). 2 POC tests green locally: automation-id
    contract on launch; New-task dialog open/cancel. NO session spawn in UIA tests until P3
    (session-index override) lands -- documented on the harness.
  - **Findings worth keeping:** (a) FlaUI 5.x needs TFM net10.0-windows, plain net10.0 silently
    falls back to .NET Framework assets (NU1701). (b) Avalonia 12 exposes an owned modal as a
    child Window of its OWNER in the UIA tree, not as a desktop top-level -- desktop-scoped
    searches time out; FindNewTaskWindow comment records it. (c) The headless KeyPressQwerty
    Ctrl+Enter -> TextBox KeyBinding -> AsyncRelayCommand chain works as designed.
  - ci-uia.yml added (workflow_dispatch only, windows-latest, ICX_UIA_TESTS=1, trx artifact
    upload; mirrors the ci-macos pattern per design 6.4). slnx updated with all 3 projects.
  - Verification: full suite 1110 total / 1107 passed / 3 skipped (count grew with main since
    the migration baseline); UIA suite reports 2 skipped in a plain run (gate works);
    `dotnet format --verify-no-changes` clean after one format pass.
  - Deferred (unchanged from design): P1 remainder, P3, UiaProbe tool (Phase 3), seed lists
    5.5/6.3 beyond the POC pairs. Logged issues.md#0002: mojibake already committed in
    NewTaskDialog.axaml.cs:92 doc comment.
  - Remaining: commit (operator), and one green ci-uia.yml dispatch (operator must trigger;
    the session permission hook auto-denies `gh workflow run`).

- **Mojibake sweep (issues.md#0002) executed and resolved.** 29 files across src/tests/docs/
  .github restored to intended Unicode via PowerShell byte-exact writes; .task/ history left
  as-is on purpose (quoted bug examples + append-only logs). Details in issues.md#0002
  resolution. Verified: build clean, 1110/1107/3 suite green, format gate clean. Committed
  and pushed together with the Phase 1+2 POC work (operator instruction).

### Handoff to next session

Design + migration acceptance COMPLETE (see above); Phases 1 and 2 now have working POC
skeletons in the working tree (3 new projects, 4 POC tests, ci-uia.yml), verified locally,
awaiting commit. Next: (1) operator commits + dispatches ci-uia.yml once; (2) grow the seed
test lists (design 5.5 / 6.3) on the new skeletons; (3) P3 session-index override before any
UIA test spawns sessions; (4) P1 remainder per docs/gui-automation-ids.md; (5) UiaProbe
(Phase 3). Note issues.md#0001 (host mojibake, resolved/diagnosed) and #0002 (mojibake baked
into committed source -- cheap repo-wide sweep candidate).
