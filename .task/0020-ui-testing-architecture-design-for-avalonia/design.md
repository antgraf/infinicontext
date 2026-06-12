# UI testing architecture for the Avalonia GUI -- design

Status: **adopted; Phases 0.5/1/2 shipped at POC depth** (2026-06-12). Phase 0.5 (xunit v3
migration), the partial-P1 GUI prep, both test projects + the UiaDriver classlib, and
ci-uia.yml are implemented and green (ci.yml + one ci-uia.yml dispatch); see progress.md
for execution detail and findings (FlaUI needs net10.0-windows; Avalonia 12 owned modals
are child Windows of the owner in UIA). Remaining: P1 remainder, P3, full seed lists,
UiaProbe - queued in docs/todo.md section D. (Rev 2: all test projects standardized on
xunit v3, see plan-xunit-v3-migration.md; file deliberately ASCII-only, see issues.md#0001.)
Deliverable of task 0020. Written to be implementable by another agent without re-doing the
research; every external claim below was verified against the Avalonia docs / NuGet on
2026-06-12.

## 1. Goals and non-goals

**Goals**

1. Automated, CI-runnable regression tests for the GUI *view layer* -- bindings, focus,
   input handling, visual-tree shape -- i.e. the class of bugs (tasks 0016, 0017) that
   ViewModel unit tests structurally cannot catch.
2. Repeatable end-to-end tests against the **real** `InfiniContext.Gui.exe` process over the
   Windows UIA accessibility tree, replacing the throwaway `tmp/icx-smoke` PowerShell scripts
   with a maintained harness.
3. A reusable **ad-hoc automation surface** so an agent investigating a GUI issue can dump
   the automation tree, click, type, and screenshot a live GUI from the command line -- the
   `tmp/icx-smoke/uia.ps1` workflow, productized.

**Non-goals**

- Pixel-level visual regression baselines (Skia frame capture). The headless platform
  supports it (`CaptureRenderedFrame()`); we leave a hook but build no baseline suite now.
- macOS/Linux UI automation. Headless tests are cross-platform by nature; the UIA layer is
  Windows-only by design (UIA *is* Windows). Appium/Mac2 is out of scope.
- Migrating existing ViewModel unit tests. They stay in `tests/InfiniContext.Tests/Gui/`.

## 2. Current state (verified 2026-06-12)

- **GUI**: `src/InfiniContext.Gui` -- Avalonia **12.0.3**, CommunityToolkit.Mvvm 8.4.2,
  Markdown.Avalonia 12.0.0-a3, net10.0. No DI container; `Services/SessionHost` is the
  composition root, started from `App.axaml.cs` after framework init (placeholder-VM ->
  live-VM swap). UI-thread access is already abstracted (`IUiDispatcher`,
  `AvaloniaUiDispatcher`, `AvaloniaUiTimerFactory`).
- **Tests**: `tests/InfiniContext.Tests` -- xunit 2.9.3 + FluentAssertions 7.2.0 today;
  migrating to **xunit.v3** per plan-xunit-v3-migration.md. `Gui/` already holds ViewModel
  tests plus the doubles we will reuse: `FakeClaudeSession` (real `SessionStateMachine`,
  scriptable events), `SessionContextFactory`, `SynchronousUiDispatcher`, `TestUiTimer`.
- **Ad-hoc harness**: `tmp/icx-smoke/` -- PowerShell 5.1 UIA scripts. Proves two things we
  build on: (a) Avalonia 12 exposes a workable UIA tree on Windows (find-by-Name,
  InvokePattern, ValuePattern all work); (b) the GUI launches hermetically with three env
  vars: `CLAUDE_EXE` -> `FakeClaude.exe` (honored by `ClaudeArgvBuilder.ResolveExecutable`,
  src/InfiniContext.Core/Sessions/ClaudeArgvBuilder.cs:85), `FAKE_CLAUDE_SCRIPT` -> action
  script (honored by FakeClaude, tests/FakeClaude/Program.cs:34),
  `INFINICONTEXT__TASKS__DBPATH` -> sandbox SQLite.
- **No `AutomationProperties.AutomationId` anywhere** in `.axaml`; only `x:Name` on ~15
  controls (`SendButton`, `PromptBox`, `SessionTree`, dialog buttons, ...).

## 3. Hard constraints that shape the design

1. **Avalonia.Headless.XUnit 12.x requires xunit v3** (`xunit.v3.extensibility.core >= 3.2.2`,
   verified on NuGet for 12.0.0-12.0.4). The existing test project is xunit v2 (2.9.3)
   today; rather than carrying both harnesses, the whole solution standardizes on v3
   (plan-xunit-v3-migration.md -- a 2026-06-12 audit found near-zero v2-specific API usage,
   making the migration cheap). The headless suite is still a separate project: it needs the
   assembly-level headless bootstrap (next item) and a different dependency surface.
2. The headless platform needs an **assembly-level** `[AvaloniaTestApplication]` attribute --
   one app builder per test assembly. Mixing headless and non-headless suites in one
   assembly is awkward regardless of xunit version.
3. **FluentAssertions 7.2.0 supports xunit v3** (package description: "XUnit2, XUnit3, ..."),
   so all projects keep the house assertion library. Do **not** bump to FluentAssertions
   8.x (license change to paid-commercial).
4. **WinAppDriver is effectively unmaintained** (last stable release 2021, requires Developer
   Mode + a separate server process). The Avalonia docs describe the Appium/WinAppDriver
   route, but for a Windows-only suite **FlaUI.UIA3** (in-process .NET UIA3 wrapper, actively
   maintained) is strictly simpler: no server, no Developer Mode, same UIA tree that
   `uia.ps1` already drives successfully.
5. House test rules apply: tests never load `settings.user.json` or ambient
   `INFINICONTEXT__*` env vars; no mocking of the wire protocol -- the UIA suite uses the
   spawned `FakeClaude.exe`, the headless suite uses the in-process `FakeClaudeSession`
   double (which owns a real `SessionStateMachine` -- consistent with the convention's
   intent; no protocol parsing is simulated).
6. Version alignment: pin `Avalonia.Headless` / `Avalonia.Headless.XUnit` to **exactly the
   GUI's Avalonia version** (12.0.3 today; 12.0.4 exists if the GUI bumps). A mismatch
   produces runtime type-identity errors, not compile errors.

## 4. Target architecture

```
tests/
  InfiniContext.Tests/              (existing)            -- VM/unit/integration; migrates to xunit v3 (plan-xunit-v3-migration.md)
  InfiniContext.Gui.HeadlessTests/  (NEW, xunit v3)       -- in-process view tests, runs in ci.yml
  InfiniContext.Gui.UiaDriver/      (NEW, classlib)       -- FlaUI page objects + app launcher
  InfiniContext.Gui.UiaTests/       (NEW, xunit v3)       -- E2E over real exe, opt-in workflow
tools/
  InfiniContext.UiaProbe/           (NEW, console)        -- ad-hoc CLI over UiaDriver
```

All test projects standardize on **xunit.v3** (latest harness, 3.2.2). The existing
project's migration is specified in plan-xunit-v3-migration.md and lands as its own commit
before Phase 1. The task proposed two projects; this design keeps those two *test* projects
and factors the UIA plumbing into a small classlib so the probe tool and the test suite
share one driver (per the no-duplication house rule: shared code gets promoted, not copied).

### 4.1 Test pyramid -- what goes where

| Layer | Project | Catches | Cost | CI |
|---|---|---|---|---|
| VM unit | InfiniContext.Tests (existing) | command/state logic, CanExecute | ms | every push |
| Headless view | Gui.HeadlessTests | binding wiring, focus, keyboard/mouse handling, visual-tree shape, dialog logic | ~10-100 ms/test | every push |
| UIA E2E | Gui.UiaTests | window activation/foreground, OS focus, accessibility-tree exposure, real-process lifecycle (child cleanup, dead-session UX) | 1-10 s/test | manual dispatch + before GUI releases |
| Ad-hoc | UiaProbe | investigation, repro construction | interactive | never |

Rule for future test authors: **write the test at the lowest layer that can observe the
bug.** Task 0017 (Send gated on keyboard focus) is a *headless* test -- focus and key input
are simulatable in-process. "Window doesn't come to foreground" or "UIA tree missing a
control" are *UIA* tests.

## 5. Project 1: `tests/InfiniContext.Gui.HeadlessTests`

### 5.1 Packages

```xml
<PackageReference Include="Avalonia.Headless.XUnit" Version="12.0.3" />  <!-- == GUI's Avalonia -->
<PackageReference Include="xunit.v3" Version="3.2.2" />
<PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
<PackageReference Include="FluentAssertions" Version="7.2.0" />
<PackageReference Include="coverlet.collector" Version="10.0.1" />
<ProjectReference Include="..\..\src\InfiniContext.Gui\InfiniContext.Gui.csproj" />
```

Plus `<OutputType>Exe</OutputType>` (xunit v3 test projects are executables).
`xunit.runner.visualstudio` 3.x runs v3 assemblies under VSTest, so the existing
solution-wide `dotnet test InfiniContext.slnx` invocation in `ci.yml` picks the new project
up with **zero workflow changes**. The v3-under-VSTest integration itself (trx, coverage,
filters) is proven by the migration plan's verification matrix before this project exists.

### 5.2 Test application bootstrap

The real `App` starts `SessionHost` (spawns processes, touches `~/.infinicontext/`) in
`OnFrameworkInitializationCompleted` -- unacceptable in tests. Two-part fix:

1. **Gate the side effects in `App.axaml.cs`** on
   `ApplicationLifetime is IClassicDesktopStyleApplicationLifetime` (likely already
   true-by-shape; make it explicit). Headless test sessions have no desktop lifetime, so the
   real `App` styles/resources become loadable without the composition root firing.
2. **`TestApp : Application`** in the test project whose `Initialize()` loads the *production*
   styles by reference, so headless tests exercise the real theme/templates:

```csharp
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions()); // UseHeadlessDrawing=true (default): no pixels, fast
}

public class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new StyleInclude(new Uri("avares://InfiniContext.Gui/"))
            { Source = new Uri("avares://InfiniContext.Gui/App.axaml") });
        // If App.axaml is an <Application> (not a Styles document) this include won't load;
        // fall back: extract App's style list into Styles/AppStyles.axaml in the GUI project
        // and include that from both App.axaml and TestApp. Implementer verifies which works.
    }
}
```

Keep default test isolation (`PerTest` -- fresh `Application` + `Dispatcher` per test).
If suite runtime becomes a problem later, revisit `PerAssembly`; do not start there.

### 5.3 Reusing the existing doubles

`FakeClaudeSession`, `SessionContextFactory`, `TestUiTimer` currently live in
`tests/InfiniContext.Tests/Gui/`. Link them into the new project with
`<Compile Include="..\InfiniContext.Tests\Gui\FakeClaudeSession.cs" />` etc. -- this is the
established repo convention for sharing pure-logic sources (CLAUDE.md "Conventions"). Do
**not** link `SynchronousUiDispatcher`: headless tests run on a real Avalonia
`Dispatcher.UIThread`, so VMs get the production `AvaloniaUiDispatcher`. Where async unwinds
are needed, call `Dispatcher.UIThread.RunJobs()`. If a linked double grows headless-only
needs, promote it to a shared file rather than forking (house rule).

The new project needs `InternalsVisibleTo` from `InfiniContext.Gui` (and `Core` if the
doubles require it) -- add `<InternalsVisibleTo Include="InfiniContext.Gui.HeadlessTests" />`
alongside the existing entries.

### 5.4 Canonical test shape

```csharp
[AvaloniaFact]
public async Task Send_button_enables_and_fires_when_prompt_typed()
{
    var (vm, fake) = await TestRig.CreateMainWindowVmAsync();   // FakeClaudeSession-backed
    var window = new MainWindow { DataContext = vm };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    var promptBox = window.FindControl<TextBox>("PromptBox")!;
    promptBox.Focus();
    window.KeyTextInput("hello");
    Dispatcher.UIThread.RunJobs();

    var send = window.FindControl<Button>("SendButton")!;
    send.IsEnabled.Should().BeTrue();
    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    fake.SentPrompts.Should().ContainSingle().Which.Should().Be("hello");
}
```

`TestRig` is a small static helper in the new project that composes
`MainWindowViewModel` over `SessionContextFactory`/`FakeClaudeSession` exactly the way the
existing VM tests do -- but injecting the real dispatcher. The implementer should check
whether `MainWindowViewModel.CreateAsync` has hard dependencies on `SessionHost`; if so,
extract the seam first (constructor that accepts a prebuilt session list -- likely already
close to what the VM tests use).

### 5.5 Seed test list (Phase 1 acceptance)

1. **0017 regression**: window shown -> prompt typed via `KeyTextInput` -> Send enabled ->
   Enter sends; and: click transcript area -> type -> Send still reaches `PromptBox` per the
   0017 fix (focus contract).
2. Prompt empty -> Send disabled (binding-level, not just CanExecute).
3. Approval banner: raise an approval on `FakeClaudeSession`'s slot -> banner visible in
   tree, Approve button fires the slot; timeout path renders denied state.
4. Session switch: two sessions in tree -> select second -> transcript host swaps cached
   view, first session's view not detached-destroyed (0016 regression -- assert no
   `ThemeVariantScope` cascade by checking the cached control instance survives).
5. `NewTaskDialog`: empty description -> Create disabled / `ErrorLabel` visible; valid input
   -> dialog returns the spawn request object.
6. `ConfirmDialog`: Enter/Escape keyboard contract.
7. Transcript render: push assistant text event through `FakeClaudeSession` -> a
   `TurnNodeViewModel`-bound card appears in the visual tree with the text (find via logical
   tree, not pixels). *Verify Markdown.Avalonia initializes under headless; if it requires a
   render loop, mark this test as the canary and document the finding in `progress.md`.*
8. Busy state: in-flight turn -> Send shows queue semantics per 0017 design.

## 6. Project 2: `tests/InfiniContext.Gui.UiaDriver` + `tests/InfiniContext.Gui.UiaTests`

### 6.1 Driver classlib (`UiaDriver`)

Packages: `FlaUI.UIA3` (latest 5.x), net10.0, references nothing from `src/` (it drives the
exe from outside). Contents:

- **`GuiAppHarness`** -- the launcher. Responsibilities:
  - Locate repo root by walking up to `InfiniContext.slnx` (same pattern as
    `tests/InfiniContext.Tests/TestPaths.cs`; link or replicate that one file).
  - Resolve `InfiniContext.Gui.exe` and `FakeClaude.exe` under
    `bin/<Configuration>/net10.0/` (configuration via env `ICX_UIA_CONFIGURATION`,
    default Release).
  - Create a **sandbox dir** per harness instance (temp): scripted FakeClaude action file,
    SQLite db, session index, logs.
  - Launch with a **clean, explicit environment**: `CLAUDE_EXE`, `FAKE_CLAUDE_SCRIPT`,
    `INFINICONTEXT__TASKS__DBPATH=<sandbox>/smoke.db`, session-index override
    (see prerequisite P3 below), and *strip* any inherited `INFINICONTEXT__*` vars.
  - Attach `FlaUI.UIA3.UIA3Automation`, wait for the main window (FlaUI `Retry` with
    explicit timeout), expose it as `MainWindowPage`.
  - `IDisposable`: graceful close (window close -> wait) then hard `Kill(entireProcessTree:
    true)`; on test failure capture screenshot + copy the GUI's `logs/*.log` from the
    sandbox into the test results dir.
- **Page objects** -- `MainWindowPage`, `NewTaskDialogPage`, `ConfirmDialogPage`. Each wraps
  `FindFirstDescendant(cf => cf.ByAutomationId("SendButton"))` + FlaUI patterns
  (`Invoke()`, `Patterns.Value.Pattern.SetValue(...)`), with retry-based waits. **No raw
  UIA lookups in tests** -- tests speak page-object verbs (`page.TypePrompt("hi")`,
  `page.Send()`, `page.WaitForTurnContaining("ack")`).
- **Diagnostics** -- `DumpTree()` (the `uia.ps1` tree dump, returning indented
  ControlType/Name/AutomationId text) and `Screenshot(path)` (FlaUI capture). Used by both
  failure handlers and the probe tool.

### 6.2 Test project (`UiaTests`)

xunit **v3** (3.2.2) + FluentAssertions 7.2.0, matching the solution-wide harness after the
plan-xunit-v3-migration.md migration lands. References `UiaDriver`. One xunit **collection
fixture** owning a `GuiAppHarness` per collection; tests that mutate global GUI state get
their own harness (slow path, used sparingly).

**Gating:** UIA tests must not run in `ci.yml` or on dev machines that run
`dotnet test InfiniContext.slnx` casually -- they need a desktop and steal mouse/keyboard
focus. Follow the existing smoke-test convention: skip unless env var set. Concretely, a
`UiaFact` attribute: `[UiaFact]` -> `Skip` unless `ICX_UIA_TESTS=1`. This keeps the project
in the solution (build + format gates apply) while making execution opt-in. (xunit v3 also
offers dynamic skips -- `Skip.When` -- which the implementer may use instead of the
ctor-Skip pattern; either satisfies the contract.)

### 6.3 Seed test list (Phase 2 acceptance)

1. Launch -> main window appears in UIA tree with expected AutomationIds (tree contract
   test -- this is the canary for Avalonia automation-peer regressions).
2. The full `step1..step6` icx-smoke flow as one scenario: new-task dialog -> pick model ->
   create -> session spawns -> prompt sent -> FakeClaude ack visible in transcript.
3. 0017-style: foreground/focus after dialog close -- Send works without manually clicking
   the prompt box (the class of bug headless can't see: real window activation).
4. Dead session (`step7`): kill the FakeClaude child -> GUI shows dead-session state, host
   doesn't crash; relaunch path works.
5. Approval flow E2E: FakeClaude script triggers a tool approval -> banner in UIA tree ->
   Invoke Approve -> turn completes. (Exercises the real sidecar pipe end-to-end.)

### 6.4 CI: `ci-uia.yml`

Mirror the `ci-macos.yml` pattern: **`workflow_dispatch` only** at first. `windows-latest`
runners do provide an interactive desktop and FlaUI suites are known to run on them, but
expect flakiness from 1024x768 default resolution and foreground-window policing -- budget
for it (set window size explicitly in the harness; never depend on screen coordinates;
upload screenshots + UIA tree dumps as artifacts on failure). Steps: restore -> build
Release -> `ICX_UIA_TESTS=1 dotnet test --filter FullyQualifiedName~UiaTests`. No coverage,
no format gate (Windows `ci.yml` owns both). Promote to PR-triggered-on-GUI-paths only
after a few weeks of green manual runs.

### 6.5 Ad-hoc probe (`tools/InfiniContext.UiaProbe`)

Thin console exe over `UiaDriver` replacing `tmp/icx-smoke/uia.ps1` verbs:

```
dotnet run --project tools/InfiniContext.UiaProbe -- launch [--script <fake-script.json>]
... -- dump [--pid <pid>]            # automation tree
... -- click --id SendButton
... -- settext --id PromptBox --text "hello"
... -- invoke --id CreateButton
... -- screenshot --out shot.png
... -- kill
```

`launch` persists pid + sandbox path to `%TEMP%/icx-probe/state.json` so subsequent verbs
attach without re-specifying. This is the agent-facing investigation surface: every verb is
one Bash/PowerShell call, no UIA COM knowledge needed. `tmp/icx-smoke/` stays untouched
until the probe reaches verb parity, then gets deleted in a follow-up task.

## 7. Prerequisite code changes (Phase 0)

- **P1 -- AutomationIds.** Add `AutomationProperties.AutomationId` to every interactive
  control in `MainWindow.axaml`, `NewTaskDialog.axaml`, `ConfirmDialog.axaml`. Convention:
  value equals existing `x:Name` where present (`SendButton`, `PromptBox`, ...); add ids for
  unnamed interactive elements (approval banner buttons, model combo items' container, task
  tree item template -- for tree items bind `AutomationProperties.Name` to the VM title so
  UIA can address rows by text). Record the full id table in a new
  `docs/gui-automation-ids.md`; the ids are a public test contract -- renaming one is a
  breaking change to both test suites.
- **P2 -- `App.axaml.cs` lifetime gate.** Side-effectful startup (SessionHost, index load)
  runs only under `IClassicDesktopStyleApplicationLifetime` (see 5.2). Pure refactor, no
  behavior change for the real app.
- **P3 -- Session-index path override.** `SessionIndex` resolves `~/.infinicontext/index.json`
  unconditionally (src/InfiniContext.Core/Sessions/SessionIndex.cs:97-101); even the current
  icx-smoke launch pollutes the real index. Add a `sessions.indexPath` setting (default
  unchanged, schema + validation per existing settings conventions) so the UIA harness can
  point it into the sandbox via `INFINICONTEXT__SESSIONS__INDEXPATH`. Audit for any other
  unconditional `~/.infinicontext` writes the GUI performs at startup (light-cwd is lazy;
  review-cwd is lazy) and route them through settings the same way if hit.
- **P4 -- Avalonia version decision.** Either stay on 12.0.3 everywhere (Headless.XUnit
  12.0.3 exists) or bump GUI+tests to 12.0.4 together. Never split.

## 8. Phased implementation plan

| Phase | Scope | Done when |
|---|---|---|
| 0 | P1-P4 prerequisites | build green, `dotnet format` clean, ids documented, existing tests pass |
| 0.5 | xunit v3 migration of `InfiniContext.Tests` per plan-xunit-v3-migration.md (parallel to Phase 0; must land before Phase 1) | migration plan's acceptance criteria met: identical test counts, green ci.yml with coverage, green ci-macos dispatch |
| 1 | `Gui.HeadlessTests` project + TestApp bootstrap + TestRig + seed tests (5.5) | all seed tests green locally and in `ci.yml` (no workflow edits needed); Markdown.Avalonia headless finding recorded |
| 2 | `UiaDriver` + `UiaTests` + seed tests (6.3) + `ci-uia.yml` | `ICX_UIA_TESTS=1` run green locally; one green `workflow_dispatch` run |
| 3 | `UiaProbe` tool; verb parity with `uia.ps1`; delete `tmp/icx-smoke` | an agent can repro a 0017-class issue using only probe verbs |

Phases 1 and 2 are independent after Phases 0/0.5 and can be parallel tasks.

## 9. Risks and open questions

- **xunit v3 under VSTest** (trx logger, coverlet coverage, `--filter`) -- expected to work
  via `xunit.runner.visualstudio` 3.x, but unproven in this repo. This risk is now owned by
  the Phase 0.5 migration plan, whose verification matrix proves the full CI surface before
  any new project depends on it. (Coverage collection is the least-proven piece -- see the
  migration plan's step 4.)
- **Markdown.Avalonia (12.0.0-a3) under headless** -- prerelease; may assume a real render
  loop. Seed test 7 is the canary; if it fails, transcript-content assertions move to the
  ViewModel layer and the headless suite asserts only card presence.
- **GitHub-hosted runner flakiness for UIA** -- mitigations in 6.4; the suite stays
  manual-dispatch until proven stable, so it cannot block PRs.
- **AutomationId coverage drift** -- new controls added without ids silently weaken the UIA
  contract. Mitigation: UIA seed test 1 asserts the full expected-id set; adding a control
  without updating the contract test is caught at the next dispatch run.
- **Headless focus fidelity** -- headless `Focus()`/`KeyTextInput` approximate but don't
  replicate Win32 activation. That's why 0017's *foreground* variant lives in the UIA suite
  (6.3 #3) even though its *focus-contract* variant lives headless (5.5 #1).
