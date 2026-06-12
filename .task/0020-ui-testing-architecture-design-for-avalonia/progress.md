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

### Handoff to next session

Deliverables complete: design.md (rev 2) + plan-xunit-v3-migration.md. Phase 0.5 (xunit v3 migration) is EXECUTED in the working tree, verified locally, awaiting commit + CI (ci.yml and one ci-macos.yml dispatch) to close acceptance criteria 2-3. Then: Phase 0 (P1-P4) as follow-up task, Phases 1 and 2 in parallel. Note issues.md#0001 (host mojibake bug) -- worth its own host-side fix task.
