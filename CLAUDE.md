# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**InfiniContext** is a from-scratch C# host for Claude Code. It spawns `claude` as a child in stream-json mode, parses the NDJSON event stream, drives a session state machine, and renders the conversation in its own UI. There is no first-party Claude Agent SDK for .NET; this repo is the SDK client.

The full architecture and rationale live in **`docs/infinicontext-initial-design.md`**; recorded Stage 2A/2B decisions live in **`docs/stage2/decisions.md`** (exit-criteria mapping in **`docs/stage2/exit-checklist.md`**) and the forward-looking work queue lives in **`docs/todo.md`** (single consolidated list, no per-stage split). Read the decisions doc before non-trivial changes — it encodes constraints that aren't visible from the code (parser tolerance contract, why no terminal embedding, why we read Claude's session store rather than mirror it, version-drift strategy, multi-session architecture).

**Stage 1** (single-instance REPL), **Stage 2A** (tasks-and-sessions management — markdown artifacts under `.task/`, SQLite registry at `~/.infinicontext/`, MCP locks, context-pressure monitor, `/task` REPL commands), and **Stage 2B** (multi-session console UI + macOS supervision + CI matrix) are all done. Stage 2B closed on 2026-05-20 once `ci-macos.yml` ran green on `macos-latest` and the Phase 7 POSIX supervisor / `FileSystemNaming` code was validated end-to-end. **Stage 3** (Avalonia GUI) is in progress: `src/InfiniContext.Gui` ships the multi-session window (tasks tree, transcript cards, approval banner, new-task dialog) and a two-layer UI test architecture exists (see "UI testing" below; design in `.task/0020-*/design.md`). The forward-looking queue carries the remaining Stage 3 work plus residual follow-ups in `docs/todo.md`. <!-- stage-2b-historical: Phases 1–7 (Phases 1-7 + Phase 8 CI matrix history; preserved verbatim above is no longer the live status). -->

## Build & test

The solution is `InfiniContext.slnx` (the new XML solution format). Target framework is `net10.0`.

```
dotnet build InfiniContext.slnx
dotnet test  InfiniContext.slnx
dotnet run --project src/InfiniContext.Cli                # multi-session console with cwd defaults
dotnet test  --filter FullyQualifiedName~ReplSmokeTests   # one test class
dotnet test  --filter "DisplayName~Repl_renders"           # one test
dotnet test  tests/InfiniContext.Gui.HeadlessTests        # headless GUI view tests (in-process, also in ci.yml)
$env:ICX_UIA_TESTS='1'; dotnet test tests/InfiniContext.Gui.UiaTests -c Release   # UIA E2E vs the real exe (opt-in, takes over the desktop)
```

End-to-end smoke tests opt-in via env var and are skipped in CI by default; unit/integration tests run real binaries (`InfiniContext.Cli` ↔ `FakeClaude`) via `Process.Start`, not in-process mocks. On this dev machine a running GUI / open VS often holds Debug bin locks — test with `-c Release` when plain `dotnet test` fails on file copies.

## CI

Three workflows under `.github/workflows/`:

- **`ci.yml`** — runs on every push to `main` and every PR. `windows-latest`. Gates in order: `dotnet restore` → `dotnet format --verify-no-changes --no-restore` → `dotnet build -c Release` → `dotnet test -c Release` with `XPlat Code Coverage` → ReportGenerator markdown summary appended to the job step output. The format gate is the canonical formatting authority — fix locally with `dotnet format InfiniContext.slnx` before pushing.
- **`ci-macos.yml`** — `workflow_dispatch` only (D37). Private-repo `macos-latest` minutes bill at 10× the Windows rate, so this is run manually whenever Phase 7's macOS-specific code (`PosixSignalSupervisor`, `FileSystemNaming`, `[MacOnlyFact]` tests) is touched, or before any release that needs the "killing the host cleans up child processes" exit criterion green on macOS. No format gate here (Windows owns that), no coverage collection.
- **`ci-uia.yml`** — `workflow_dispatch` only. Runs the UIA end-to-end suite (`ICX_UIA_TESTS=1`) against the real `InfiniContext.Gui.exe` on `windows-latest`'s interactive desktop. Dispatch whenever the UIA suite or the automation-id contract (`docs/gui-automation-ids.md`) changes, and before GUI releases. Expected flakier than ci.yml (1024×768 desktop, foreground policing); first run green 2026-06-12. No format gate, no coverage.

`gh run list --repo antgraf/infinicontext --limit 5` is the fast way to see recent runs; `gh api repos/antgraf/infinicontext/actions/jobs/<job-id>/logs > out.txt` is the reliable way to fetch a full failed log when `gh run view --log-failed` returns empty.

**CI-only failure modes worth knowing about** (all encountered in early CI runs, all fixed in code):

- **Windows runners set `core.autocrlf=true` globally.** Any C# raw-string literal that's later compared against an `"\n"`-anchored expectation needs to normalize: `(... raw literal ...).Replace("\r\n", "\n")`. CI checkout flips source `\n` → `\r\n`; the literal captures whatever the source file has on disk. `InfiniContextTaskSkill.SkillMarkdown` is the canonical example. Same hazard applies to any future inline expected-output strings.
- **`Spectre.Console.Testing.TestConsole` defaults to 80-column width** and `Text` renderables wrap at the profile width. The `runneradmin` temp-dir prefix (`C:\Users\runneradmin\AppData\Local\Temp\...`) plus a 32-char GUID blows past 80 cols, so any `stdout.Should().Contain(longPath)` assertion fails because the path got wrapped mid-string. Fix: `new TestConsole().Width(1000)` in the test fixture. Tests that only check short fragments are unaffected.
- **Smoke tests racing the `SessionTranscriptReader` drain.** When the focused session's child process exits quickly (FakeClaude replay finishes in ~50 ms), `IClaudeSession.WaitForExitAsync` returns before the per-session reader has rendered every event still in the channel. If the host unsubscribes `FragmentAppended` at that moment, the final `ResultEvent` fragment (the `[turn done:]` marker) is appended to the in-memory transcript but never echoes to stdout, and `Contains("[turn done:")` assertions fail. The host's shutdown drain therefore waits for `SessionManager.DrainReaderAsync` (which awaits the reader's `RunCompletion` task, bounded by `session.exitDrainWaitSeconds`) *after* the process-exit wait. Any new event a future test asserts on must flow through the reader; if you add tests that look for trailing markers on a fast-exiting session, that drain is what guarantees they're observable.
- **macOS Unix-domain-socket path cap (104 chars).** .NET maps a named pipe on Unix to a UDS at `$TMPDIR/CoreFxPipe_<name>`, and macOS's `sun_path` is 104 chars max. macOS `$TMPDIR` is `/var/folders/<2>/<28>/T/` (~50 chars) + `CoreFxPipe_` (11), leaving roughly 43 chars for the pipe name. The 32-hex-char `Guid.NewGuid():N` suffix blew past that. `ApprovalChannel.GeneratePipeName` truncates to 12 hex chars (48 bits — far more than needed for per-host uniqueness) so the total fits with headroom. If you add another pipe name, do the same arithmetic before picking a length.
- **`Path.GetFullPath` is direction-dependent on cross-shaped path inputs.** On Unix, `Path.GetFullPath("C:\\src\\repo")` does not recognise `C:` as a drive root — it treats the whole string as a *relative* path and prepends the runner's cwd. On a CI test host that cwd is `…/bin/Release/net10.0/…`, so any heuristic that splits the full path on `/` and inspects segments will see synthetic `bin`/`Release` segments and false-positive. The `TaskService.LooksLikeBuildOutput` test cases that pass Windows-shaped paths therefore live under `[WindowsOnlyTheory]` (see `tests/InfiniContext.Tests/Supervision/WindowsOnlyTheoryAttribute.cs` — sibling of the existing `WindowsOnlyFact`); the platform-portable cases stay in a plain `[Theory]`. Apply the same split to any future cross-platform test that feeds Windows-style strings into `Path.GetFullPath`.

## Repository layout

- `src/InfiniContext.Core/` — parser, state machine, session, MCP approval server + lock channel, settings, logging, process supervision, multi-session manager + session index, tasks subsystem (`Tasks/` — `TaskService`/`TaskFolder`/`TaskStore` + SQLite migrations, `ContextPressureMonitor`, `ThresholdInjector`, `TaskActivityCoordinator`), light-model service (`LightModel/`). All non-UI logic.
- `src/InfiniContext.Cli/` — multi-session console apphost (`MultiSessionHost`). `Program.cs` also handles the `--mcp-approval-server` sidecar mode (same exe, re-spawned by `claude` over stdio).
- `tests/InfiniContext.Tests/` — xUnit (v3) + FluentAssertions. Mirrors `Core` layout. `TestPaths.cs` walks up to find `InfiniContext.slnx` so tests are cwd-independent.
- `tests/InfiniContext.Gui.HeadlessTests/` — in-process GUI view tests on the Avalonia headless platform (real XAML + production theme, simulated keyboard/mouse). Runs in `ci.yml` like any other test project.
- `tests/InfiniContext.Gui.UiaDriver/` — classlib shared by the UIA suite and future probe tooling: `GuiAppHarness` (hermetic real-exe launcher) + page objects over the Windows UIA tree (FlaUI.UIA3).
- `tests/InfiniContext.Gui.UiaTests/` — opt-in (`ICX_UIA_TESTS=1`) end-to-end tests against the real `InfiniContext.Gui.exe`. Windows-only by design.
- `tests/FakeClaude/` — scripted stand-in for the real `claude` binary; `--replay <fixture>` streams an NDJSON fixture, `--script <path>` runs adversarial JSON action scripts. Used as a transport substitute in integration tests.
- `tests/fixtures/` — committed NDJSON recordings from real `claude` 2.1.112; the primary regression surface for the parser. **Do not regenerate casually** — see `tests/fixtures/README.md` and `NOTES.md`.
- `tests/scripts/` — FakeClaude action scripts (different from `tests/fixtures/scripts/`, which are recorder scripts).
- `tools/InfiniContext.Recorder/` — Stage 0 capture tool. Spawns real `claude`, writes raw NDJSON to a fixture path. Re-record fixtures only via this tool.
- `logs/` — gitignored; per-run Serilog files (`YYYYMMDD-HHmmss.log`).

## Architecture cheat sheet

**Wire protocol.** `claude --print --verbose --input-format stream-json --output-format stream-json`. Flag order matters: `--verbose` must precede `--output-format`. The argv is centralized in `ClaudeArgvBuilder` — change it there, not at call sites. Default mode is **per-turn batched**, not streamed: assistant content arrives as one `assistant` event per turn with `message.content[]`. True deltas only appear under `stream_event` when `--include-partial-messages` is set.

**Parser tolerance is load-bearing** (`StreamJsonParser`). Unknown event types become synthetic `unknown_event` records with raw JSON preserved; unknown fields on known events are kept in an `Extra` `JsonElement`. A per-session drift counter is the real signal that a `claude` upgrade broke something — not the version check. Never use closed POCOs that drop unknown data.

**Session state.** Top-level state machine (`SessionStateMachine`) plus a tool-use correlation map keyed off `tool_use.id`; sub-agent events (Task tool) correlate via `parent_tool_use_id`. `awaiting-approval` is an independent flag, not a state.

**Multi-session.** A host-scoped `SessionManager` (Core) owns N `SessionContext` bundles, each holding a `ClaudeSession` + `ContextPressureMonitor` + `ThresholdInjector` + `SwitchableApprovalSink` + `PendingApprovalSlot` + `TranscriptModel`. `MultiSessionHost` (CLI) drives the UI via slash commands — `/n <cwd>` to spawn, `/focus <prefix>` / `/next` / `/prev` to rotate focus, `/broadcast` to fan a prompt out to many sessions. A flat-JSON session index at `~/.infinicontext/index.json` survives host restarts. See `docs/stage2/decisions.md` §10b for the design lineage.

**Tasks (structured compaction).** This is the host's answer to claude's free-form `/compact` (one opaque in-session summary). Each task is a defined artifact set the agent edits in place across sessions: `.task/<id>-<slug>/` with mandatory `index.md` (table-of-contents), `description.md` (problem statement), `progress.md` (append-only audit log), plus optional `plan.md` / `decisions.md` / `research.md` / `design.md`. The agent reads via `Read` and writes via `Edit`/`Write` — no MCP-served content, no DB-stored content. SQLite at `~/.infinicontext/infinicontext.db` (via `Microsoft.Data.Sqlite`, migrations under `Tasks/Migrations/` — never edit `Migration_001`, add a new one) holds only identity, lineage, status, timestamps. `TaskService` is the read/write surface; `TaskFolder` enforces on-disk shape and detects drift; `TaskActivityCoordinator` enforces one-session-per-task (D9). `ContextPressureMonitor` tallies `assistant`/`result` token sums (widened to `long` per C1) and signals `ThresholdInjector` to inject a steering message before auto-compact wipes context — the nudge tells the agent to flush to the artifacts. Cross-session task exclusivity uses an MCP `tasks/lock` channel (`LockRegistry` + `ApprovalChannel`) over the approval sidecar's pipe — file-touching task ops must go through it. DB-ahead-of-code = refuse to start.

**Persistence.** The host **does not mirror Claude's session store**. Read from `~/.claude/projects/<encoded-cwd>/<session_id>.jsonl`; write only a host-only index and metadata under `~/.infinicontext/`. Path resolution uses `Environment.GetFolderPath(SpecialFolder.UserProfile)` + `Path.Combine` exclusively — no string concat with separators.

**Tool approvals.** No TTY in stream-json mode. The host launches a sidecar MCP server (the same CLI exe re-spawned with `--mcp-approval-server <pipe>`) which `claude` connects to via `--permission-prompt-tool`. Approvals flow back through a per-session `PendingApprovalSlot` to the focused pane in `MultiSessionHost`. 60s default timeout → auto-deny; configurable in `settings.json`. **The sidecar's stdout is reserved for JSON-RPC** — see the early short-circuit in `Program.cs` before any other startup. Four approval modes (`ask | deny | allow | auto`, `auto` default); `auto` consults the light-model service for safety review and routes `confidence=low` to deny-or-ask per settings.

**Light-model service.** `ILightModelService` is a separate cheap channel for one-shot AI tasks (auto-approval safety review, title generation). Four-corner matrix: {single-shot, long-lived} × {thinking, non-thinking}, selected per consumer via `LightModelServiceFactory`. CLI-backed: re-spawns `claude --print` with the *opposite* argv from a real session (`--tools "" --strict-mcp-config --mcp-config '{"mcpServers":{}}' --setting-sources project --disable-slash-commands --no-session-persistence`) — built in `CliLightModelInvocation`, not `ClaudeArgvBuilder`. `ManagedCwd` defaults to `~/.infinicontext/light-cwd/` to avoid `CLAUDE.md` auto-discovery and `.mcp.json` auto-load polluting the prompt. Long-lived disposal must `Process.Kill(entireProcessTree: false)` per D12 (Windows AV scans on tree-kill caused ~16 s stalls).

**Process supervision.** `IProcessSupervisor` has three backends: Windows (`JobObjectSupervisor` with `KILL_ON_JOB_CLOSE` so child `node` processes die with the host), POSIX (`PosixSignalSupervisor` — `PosixSignalRegistration` for SIGINT/SIGTERM/SIGHUP/SIGQUIT + `AppDomain.ProcessExit` + SIGTERM → grace → SIGKILL via `[DllImport("libc")] kill(2)`, used on macOS and Linux), and `NoopSupervisor` (stub). The macOS code shipped in Stage 2B Phase 7 and was validated on `macos-latest` via the first `ci-macos.yml` `workflow_dispatch` on 2026-05-20 (per D16 / D37).

**Logging (Serilog).** Per-run file at `logs/{YYYYMMDD-HHmmss}.log`; the sidecar uses a `sidecar`-prefixed file. Every `tx:`/`rx:` event logged at Debug, truncated to `maxFieldBytes` from settings. `SecretScrubbingEnricher` filters env-var values matching `*_KEY`/`*_TOKEN`/`*_SECRET` at emission time. The log is the agent's primary debugging surface — no GUI to look at.

## UI testing (GUI)

Four layers exist for GUI coverage (architecture: `.task/0020-ui-testing-architecture-design-for-avalonia/design.md`). **When you change GUI code, add or extend a test at the lowest layer that can observe the bug** — view-model logic stays in `tests/InfiniContext.Tests/Gui/`; binding/focus/keyboard/visual-tree behavior goes in `Gui.HeadlessTests`; window activation, OS focus, UIA-tree exposure, or real-process lifecycle goes in `Gui.UiaTests`. A GUI bug fix without a regression test at the right layer is incomplete (tasks 0016/0017 were exactly the class of bug VM tests cannot catch).

| Layer | Project | Use for | Runs |
|---|---|---|---|
| VM unit | `InfiniContext.Tests/Gui/` | command/state logic, CanExecute | every push |
| Headless view | `Gui.HeadlessTests` | bindings, focus contract, key/mouse handling, dialog logic, visual-tree shape | every push (ci.yml) |
| UIA E2E | `Gui.UiaTests` | real window activation, accessibility tree, process lifecycle | `ICX_UIA_TESTS=1` locally + `ci-uia.yml` dispatch |
| Ad-hoc | `tmp/icx-smoke` scripts (until the planned `UiaProbe` tool lands) | investigation / repro | manual |

Rules that keep the layers working:

- **AutomationIds are a public test contract** (`docs/gui-automation-ids.md`). Every new interactive control gets `AutomationProperties.AutomationId` equal to its `Name`, plus a row in that doc. Renaming an id breaks both suites — update page objects (`Gui.UiaDriver`) and the doc together.
- **App-wide styles go in `src/InfiniContext.Gui/Styles/AppStyles.axaml`, not `App.axaml`** — the headless `TestApp` loads that file by reference so view tests run under the production theme. Styles added to `App.axaml` directly are invisible to the headless suite.
- **Pin `Avalonia.Headless.XUnit` to the GUI's exact Avalonia version.** A mismatch produces runtime type-identity errors, not compile errors. Bump them together or not at all.
- **UIA projects target `net10.0-windows`** (FlaUI 5.x ships modern assets for `-windows` TFMs only; plain `net10.0` silently falls back to .NET Framework assets). UIA tests must not spawn sessions until the session-index path override (design prerequisite P3) lands — a spawned session would pollute the operator's real `~/.infinicontext/index.json`.
- **Avalonia 12 exposes owned modals as child `Window`s of their owner in the UIA tree**, not desktop top-levels — scope dialog searches to the owner window (see `MainWindowPage.FindNewTaskWindow`).
- The headless harness's "host is ready" beacon is the footer `LogFilePathLabel` (empty under the placeholder VM, a path once the live VM lands). UIA tests must `WaitForHostReady()` before clicking command-backed controls.

## Settings

Three layers, loaded in order: `settings.json` (committed defaults) → `settings.user.json` (gitignored per-user overrides) → `INFINICONTEXT__SECTION__KEY` env vars. Auto-discovered in the working directory; `--settings <path>` bypasses discovery and loads exactly that file. Schema lives in `settings.schema.json` for editor autocomplete. Validation hard-fails on bad config. CLI args (`--model`, `--include-partial-messages`, etc.) take precedence over settings.

**`settings.user.json` must never be staged or committed.** It is per-developer machine state and is listed in `.gitignore`. If you ever see it under "Changes to be committed" or in a diff, unstage and exclude it — never `git add settings.user.json`, never `git add -A`/`git add .` in a way that sweeps it in. The committed file is `settings.json`; the user file exists only to override locally.

**Tests must never load `settings.user.json` or pick up `INFINICONTEXT__*` env vars from the dev machine.** The rule:
- Default path: tests construct fakes against `InfiniContextSettings.Defaults` and don't call `InfiniContextSettingsLoader.Load()` at all. Every consumer (`ClaudeSession`, `MultiSessionHost`, `SlotApprovalSink`, `ApprovalChannel`, `ApprovalSidecarHost`, `SessionLogger`, `InfiniContextLogger`) accepts settings as an optional ctor param that defaults to the in-code defaults — match that pattern.
- Escape hatch: a test that genuinely exercises the loader (validation bounds, layering precedence) must pass `LoadOptions { BaseDirectory = <test-temp-dir>, IncludeUserOverrides = false, IncludeEnvironmentVariables = false }`. Any of those omitted is a bug.

## Versioning policy

`ClaudeVersionPolicy` enforces `MinClaudeVersion` (hard fail below) and warns on mismatch with `TestedAgainstVersion`. Both live in `settings.json` under `versioning`. Bump `TestedAgainstVersion` whenever fixtures are re-recorded. The drift counter, not the version check, is the load-bearing safety net.

## Conventions

- **No mocks of Claude's wire protocol.** Use `FakeClaude` (a real spawned binary) or replay fixtures. The point is to exercise the same code paths as the real `claude`.
- Test files are linked into the test assembly via `<Compile Include="..\..\src\...">` for pure-logic CLI types (`RunArgs`, `SlotApprovalSink`, `MultiSessionHost`, etc.) so they can be unit-tested without spawning the binary. Smoke tests still spawn the real exe.
- `ImplicitUsings` and `Nullable` are enabled everywhere. `Core` also has `AllowUnsafeBlocks` for Job Object P/Invoke.
- Cross-platform from day one — Windows and macOS are both validated targets as of Stage 2B close (2026-05-20). The POSIX supervisor backend covers macOS and Linux; Linux runs the same code path as macOS but has no dedicated CI surface today. No `\` separators, no Windows-only APIs without an abstraction.
