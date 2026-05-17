# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**InfiniContext** is a from-scratch C# host for Claude Code. It spawns `claude` as a child in stream-json mode, parses the NDJSON event stream, drives a session state machine, and renders the conversation in its own UI. There is no first-party Claude Agent SDK for .NET; this repo is the SDK client.

The full architecture and rationale live in **`docs/infinicontext-initial-design.md`**; the current state of Stage 2A/2B decisions and remaining work lives in **`docs/stage2/decisions.md`** and **`docs/stage2/todo.md`**. Read both before non-trivial changes — they encode constraints that aren't visible from the code (parser tolerance contract, why no terminal embedding, why we read Claude's session store rather than mirror it, version-drift strategy, multi-session architecture).

**Stage 1** (single-instance REPL) and **Stage 2A** (tasks-and-sessions management — markdown artifacts under `.task/`, SQLite registry at `~/.infinicontext/`, MCP locks, context-pressure monitor, `/task` REPL commands) are done. **Stage 2B** (multi-session console UI + macOS supervision + CI matrix) is in flight: Phases 1–7 shipped on `main` (multi-session manager, host session index, Spectre-based slash-command UI, per-session approvals, broadcast, POSIX supervisor backend, case-sensitive FS hardening, multi-session lock-contention E2E); Phase 8 (CI matrix on `windows-latest` + `macos-latest` + exit-criteria smoke) is in design. **Stage 3** (Avalonia GUI) is not yet started.

## Build & test

The solution is `InfiniContext.slnx` (the new XML solution format). Target framework is `net10.0`.

```
dotnet build InfiniContext.slnx
dotnet test  InfiniContext.slnx
dotnet run --project src/InfiniContext.Cli                # multi-session console with cwd defaults
dotnet test  --filter FullyQualifiedName~ReplSmokeTests   # one test class
dotnet test  --filter "DisplayName~Repl_renders"           # one test
```

End-to-end smoke tests opt-in via env var and are skipped in CI by default; unit/integration tests run real binaries (`InfiniContext.Cli` ↔ `FakeClaude`) via `Process.Start`, not in-process mocks.

## CI

Two workflows under `.github/workflows/`:

- **`ci.yml`** — runs on every push to `main` and every PR. `windows-latest`. Gates in order: `dotnet restore` → `dotnet format --verify-no-changes --no-restore` → `dotnet build -c Release` → `dotnet test -c Release` with `XPlat Code Coverage` → ReportGenerator markdown summary appended to the job step output. The format gate is the canonical formatting authority — fix locally with `dotnet format InfiniContext.slnx` before pushing.
- **`ci-macos.yml`** — `workflow_dispatch` only (D37). Private-repo `macos-latest` minutes bill at 10× the Windows rate, so this is run manually whenever Phase 7's macOS-specific code (`PosixSignalSupervisor`, `FileSystemNaming`, `[MacOnlyFact]` tests) is touched, or before any release that needs the "killing the host cleans up child processes" exit criterion green on macOS. No format gate here (Windows owns that), no coverage collection.

`gh run list --repo antgraf/infinicontext --limit 5` is the fast way to see recent runs; `gh api repos/antgraf/infinicontext/actions/jobs/<job-id>/logs > out.txt` is the reliable way to fetch a full failed log when `gh run view --log-failed` returns empty.

**CI-only failure modes worth knowing about** (both encountered in CI run 1, both fixed in code):

- **Windows runners set `core.autocrlf=true` globally.** Any C# raw-string literal that's later compared against an `"\n"`-anchored expectation needs to normalize: `(... raw literal ...).Replace("\r\n", "\n")`. CI checkout flips source `\n` → `\r\n`; the literal captures whatever the source file has on disk. `InfiniContextTaskSkill.SkillMarkdown` is the canonical example. Same hazard applies to any future inline expected-output strings.
- **`Spectre.Console.Testing.TestConsole` defaults to 80-column width** and `Text` renderables wrap at the profile width. The `runneradmin` temp-dir prefix (`C:\Users\runneradmin\AppData\Local\Temp\...`) plus a 32-char GUID blows past 80 cols, so any `stdout.Should().Contain(longPath)` assertion fails because the path got wrapped mid-string. Fix: `new TestConsole().Width(1000)` in the test fixture. Tests that only check short fragments are unaffected.

## Repository layout

- `src/InfiniContext.Core/` — parser, state machine, session, MCP approval server + lock channel, settings, logging, process supervision, multi-session manager + session index, tasks subsystem (`Tasks/` — `TaskService`/`TaskFolder`/`TaskStore` + SQLite migrations, `ContextPressureMonitor`, `ThresholdInjector`, `TaskActivityCoordinator`), light-model service (`LightModel/`). All non-UI logic.
- `src/InfiniContext.Cli/` — multi-session console apphost (`MultiSessionHost`). `Program.cs` also handles the `--mcp-approval-server` sidecar mode (same exe, re-spawned by `claude` over stdio).
- `tests/InfiniContext.Tests/` — xUnit + FluentAssertions. Mirrors `Core` layout. `TestPaths.cs` walks up to find `InfiniContext.slnx` so tests are cwd-independent.
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

**Process supervision.** `IProcessSupervisor` has three backends: Windows (`JobObjectSupervisor` with `KILL_ON_JOB_CLOSE` so child `node` processes die with the host), POSIX (`PosixSignalSupervisor` — `PosixSignalRegistration` for SIGINT/SIGTERM/SIGHUP/SIGQUIT + `AppDomain.ProcessExit` + SIGTERM → grace → SIGKILL via `[DllImport("libc")] kill(2)`, used on macOS and Linux), and `NoopSupervisor` (stub). The macOS code shipped in Stage 2B Phase 7 but lands unvalidated until Phase 8 CI lights up `macos-latest` (per D16).

**Logging (Serilog).** Per-run file at `logs/{YYYYMMDD-HHmmss}.log`; the sidecar uses a `sidecar`-prefixed file. Every `tx:`/`rx:` event logged at Debug, truncated to `maxFieldBytes` from settings. `SecretScrubbingEnricher` filters env-var values matching `*_KEY`/`*_TOKEN`/`*_SECRET` at emission time. The log is the agent's primary debugging surface — no GUI to look at.

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
- Cross-platform from day one — Windows is the validated target; Stage 2B Phase 7 added the macOS/Linux POSIX supervisor backend (CI validation pending in Phase 8). No `\` separators, no Windows-only APIs without an abstraction.
