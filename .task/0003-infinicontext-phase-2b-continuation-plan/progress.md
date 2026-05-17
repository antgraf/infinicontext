# Progress

## 2026-05-16

- Task created.
- Resolved D36's 5 questions through user dialogue:
  1. macOS via `workflow_dispatch` only (separate `ci-macos.yml`).
  2. Triggers: `push` to `main` + `pull_request`.
  3. Repo visibility: moot once #1 was dispatch-only.
  4. Scope: matrix build + smoke + `dotnet format --verify-no-changes` + coverage; no artifacts.
  5. Kill-host: option (a) тАФ manual proof + lean on existing supervisor unit tests; OOP integration test deferred to `docs/stage2/todo.md` ┬зA.
- Shipped Phase 8 (final Stage 2B phase):
  - `.github/workflows/ci.yml` тАФ Windows, push+PR, restore тЖТ format-verify тЖТ build тЖТ test with coverlet+cobertura тЖТ ReportGenerator markdown summary in job step output.
  - `.github/workflows/ci-macos.yml` тАФ `workflow_dispatch` only, no format gate.
  - `tests/InfiniContext.Tests/Sessions/MultiSessionExitCriteriaTests.cs` тАФ 5 cases, one per design-doc ┬з9.1тАУ┬з9.5 bullet. In-process against `SessionManager` + local `StreamingFakeSession`. Runs in ~200 ms.
  - `docs/stage2/exit-checklist.md` тАФ maps every ┬з9 bullet to its test or manual proof.
  - `docs/stage2/decisions.md` тАФ added D37 recording the Phase 8 resolutions; updated D16 status; refreshed ┬з10b preamble.
  - `docs/stage2/todo.md` тАФ refreshed snapshot date + ┬зC status; Phase 8 marked shipped; added deferred OOP-kill-host integration test to ┬зA; "Still ahead in Stage 2B" emptied.
- Fixed pre-existing whitespace drift via `dotnet format` in 5 files (`Parser/StreamJsonParser.cs`, `Sessions/TranscriptRenderer.cs`, `Logging/SecretScrubbingEnricher.cs`, `tests/FakeClaude/Program.cs`, `tests/.../State/ToolStackTests.cs`) so `--verify-no-changes` is green from CI run #1.
- Test baseline: **627 passing + 3 `MacOnly`-skipped (630 total) on a Windows runner.**

## Handoff state (for the next session)

**Not committed yet.** Working tree at flush time has the following uncommitted changes:

```
 M docs/stage2/decisions.md
 M docs/stage2/todo.md
 M src/InfiniContext.Core/Logging/SecretScrubbingEnricher.cs   (format only)
 M src/InfiniContext.Core/Parser/StreamJsonParser.cs           (format only)
 M src/InfiniContext.Core/Sessions/TranscriptRenderer.cs       (format only)
 M tests/FakeClaude/Program.cs                                 (format only)
 M tests/InfiniContext.Tests/State/ToolStackTests.cs           (format only)
?? .github/
?? .task/
?? docs/stage2/exit-checklist.md
?? tests/InfiniContext.Tests/Sessions/MultiSessionExitCriteriaTests.cs
```

**Pending user decision:** the conversation ended with me asking *"Want me to commit? If yes тАФ bundle as one commit, or split (format pass / docs / CI+smoke)?"* The user did not answer before the context flush. Next session should resume that question. My recommended default: **single commit** titled e.g. `Stage 2B Phase 8: CI matrix + exit-criteria smoke + format pass` because the format pass is a prerequisite for the `--verify-no-changes` gate in the same CI workflow тАФ they ship together cleanly.

**Build/test caveat for the next session.** When this session ran, two `InfiniContext.Cli` processes (PIDs 30040, 9648) plus Visual Studio Insiders held `src/InfiniContext.Cli/bin/Debug/net10.0/InfiniContext.Core.dll` open, which broke full-solution `dotnet build`. Workaround used: `dotnet build src/InfiniContext.Core/InfiniContext.Core.csproj` then `dotnet build tests/.../InfiniContext.Tests.csproj --no-dependencies` then `dotnet build tests/FakeClaude/FakeClaude.csproj`. Full-solution build will work once those processes exit (or the user closes the running Cli instances). The tests themselves pass; this is purely a build-step gotcha.

**Open follow-ups (filed in `docs/stage2/todo.md`):**

- Out-of-process kill-host integration test (┬зA; deferred per D37).
- First `ci-macos.yml` dispatch + manual validation; record run id in `docs/stage2/exit-checklist.md` "macOS validation" line (currently `_pending first workflow_dispatch_`).

**Files to read first** if a new session picks this up: `docs/stage2/decisions.md` ┬з10b (D37 has the full rationale), `docs/stage2/exit-checklist.md` (the ┬з9 mapping), then `git diff --stat` to see scope.

## 2026-05-17

Design conversation only - no code changes, no commits. Topic: a way to report bugs/issues found while working on a task.

### Outcome

v1 design for an `issues.md` task artifact + `/issue` REPL commands locked. Full design in `issues-feature-design.md` (sibling file). One-paragraph summary:

- New optional task artifact `issues.md` (markdown only, no SQLite mirror - per user "don't allow de-sync between sources").
- Capture via `/issue "<title>" [--kind <k>] [--where <ref>]` (top-level) AND `/task issue ...` (alias - "both commands" per user).
- List via `/issue list` (user-invoked, no automatic gate).
- Resolve via `/issue resolve <n> [--as <status>] [<note text...>]` - trailing text after `<n>` (and after optional `--as <status>`) is the note. `--note "<text>"` also accepted as a switch.
- Skill instruction will tell the agent to log out-of-scope findings to `issues.md` instead of fixing inline or burying in progress.md.

### Postponed (filed to `docs/stage2/todo.md` section B)

- `/issue promote <n>` (issue -> new task).
- `/task done` / review-gate / task-completion ceremony. User explicitly said this needs a full requirements + design cycle; v1 has no gate.

### Memory saved

`feedback-single-source-of-truth` at `~/.claude/projects/D--src-infinicontext/memory/` - prefer one canonical location for state over markdown+SQLite mirrors. Rationale: de-sync risk trumps query convenience.

### Files touched this session

- `docs/stage2/todo.md` - appended one bullet under section B (Stage 2A leftover items) recording the postponed `/issue promote` + done-gate items.
- `.task/0003-infinicontext-phase-2b-continuation-plan/issues-feature-design.md` - new artifact, full v1 design.
- `.task/0003-infinicontext-phase-2b-continuation-plan/index.md` - added the new artifact row.
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` - this entry.

### Not committed

Working tree carries the Phase 8 changes from 2026-05-16 (still pending the "commit / split?" question from prior session) plus today's `docs/stage2/todo.md` edit and the new `.task/.../issues-feature-design.md` artifact + index/progress updates. The pending commit question from the prior handoff is still open.

### Handoff to next session

When the user is ready to implement v1, the entry point is the **Code touch list** section near the bottom of `issues-feature-design.md`. No code changes were made; implementation is fresh start. The two minor "open questions" listed at the bottom of that file are non-blocking - decide at implementation time.

### Stale "Not committed" sections (above) are obsolete

The 2026-05-16 Phase 8 work and the 2026-05-17 design-doc work both landed on `main`: commits `2311721 Stage 2B Phase 8: ...` and `6211832 Docs: design v1 issues.md feature; ...`. The "Not committed" subsections in those earlier entries are historical at this point and should be ignored when reading this log.

## 2026-05-17 (implementation session)

Shipped v1 of the `issues.md` feature per `issues-feature-design.md`. Built on top of `6211832`; pushed origin/main before starting.

### Code changes (uncommitted at flush time)

Core:

- `src/InfiniContext.Core/Tasks/IssueService.cs` (new) - `AppendAsync` / `ListAsync` / `ResolveAsync`. Backed by markdown only (no SQLite mirror); lock contract via the host's `LockRegistry` so writes serialize against agent-side MCP edits. Local id auto-increments off the highest-seen `## NNNN -` heading.
- `src/InfiniContext.Core/Tasks/IssueMarkdown.cs` (new) - parser/serializer (`Parse`, `KindString`, `StatusString`, `TryParseKind`, `TryParseStatus`, `TryParseHeadingId`, `IsFieldLine`). Permissive: missing optional fields = null; unknown kind/status fall back to safe defaults. Promoted to `public` so the CLI can re-use the enum-string maps.
- `src/InfiniContext.Core/Tasks/ArtifactTemplates.cs` - one-line doc update mentioning `issues.md` as an optional artifact.
- `src/InfiniContext.Core/Tasks/Skill/InfiniContextTaskSkill.cs` - bumped `Version` 2 -> 3, added an "Issue hygiene" section instructing the agent to log out-of-scope findings to `issues.md` instead of fixing inline or burying in `progress.md`.

CLI:

- `src/InfiniContext.Cli/Issues/IssueCommand.cs` (new) - pure parser for `/issue ...` with three subcommands (`Add`, `List`, `Resolve`) plus `Help`. Add supports trailing-tokens-as-title, `--kind`, `--where`. Resolve supports `--as <status>`, `--note "<text>"`, and trailing-text-as-note (with `--note` winning when both are present, per the design's "scripted callers" use case).
- `src/InfiniContext.Cli/Issues/IssueCommandHandler.cs` (new) - dispatches to `IssueService`, renders to `TextWriter`. Refuses with `[issue] no active task ...` when the focused session isn't task-bound.
- `src/InfiniContext.Cli/SlashCommands/IssueSlashCommand.cs` (new) - registers `/issue` in `SlashCommandRegistry`.
- `src/InfiniContext.Cli/SlashCommands/TaskSlashCommand.cs` - routes `/task issue ...` to the same `IssueCommandHandler` (cross-namespace alias per the design - "both commands" per user).
- `src/InfiniContext.Cli/SlashCommands/ISlashCommand.cs` - added `ActiveTaskId` and `IssueHandler` as init-only properties on `SlashCommandContext` (not positional ctor params, to keep existing test constructors compiling).
- `src/InfiniContext.Cli/Ui/MultiSessionHost.cs` - threads `IssueCommandHandler?` ctor param, plumbs `_focused.ActiveTaskId` and `_issueHandler` into the context.
- `src/InfiniContext.Cli/Program.cs` - constructs `IssueService` + `IssueCommandHandler` whenever the task subsystem opens successfully; passes the handler into `MultiSessionHost`. Lock registry is shared with the agent-side MCP path so host and agent writes serialize on the same lock.

Tests (all green):

- `tests/InfiniContext.Tests/Tasks/IssueServiceTests.cs` (new) - 11 tests. Round-trip create/list, auto-increment local id even after a manual mid-file delete (max+1, not gap-filling), `Resolve` sets status + inserts `Resolution:` line + accepts each terminal status (theory over `Resolved`/`WontFix`/`Deferred`), rejects `Open` as target, returns null for unknown id or missing file, throws for unknown task.
- `tests/InfiniContext.Tests/Cli/Issues/IssueCommandTests.cs` (new) - 20 tests across capture (quoted/unquoted/multi-word title, flag variants, error cases), list, resolve (id-only / trailing-note / `--as` + trailing-note / `--note` switch / `--note` wins over trailing / `--as deferred` / rejects `--as open` / rejects missing or non-numeric id), bare-form parity for the `/task issue` alias.
- `tests/InfiniContext.Tests/Cli/Issues/IssueCommandHandlerTests.cs` (new) - 2 tests: refuses with the clear message when no active task; help renders without one.
- `tests/InfiniContext.Tests/InfiniContext.Tests.csproj` - linked the three new CLI source files.

### Decisions made during implementation

- **No `IssuesScaffold` constant added to `ArtifactTemplates`** despite the design listing it. The header `# Issues - <title>` is scaffolded inline by `IssueService.AppendAsync` on first write, which keeps the scaffold next to its only caller and avoids a constant that ships unused. Comment in `ArtifactTemplates` updated to mention `issues.md` as optional.
- **`IssueMarkdown` is public** (had to be) so the CLI parser/handler can reuse `KindString`/`StatusString`/`TryParseKind`/`TryParseStatus`. The parser API is small and stable; promoting it doesn't expose anything internal-only.
- **Heading separator is plain `-`** (not em-dash). Matches the design doc literal; the parser's regex enforces that exact shape so round-trips are deterministic.
- **Default capture kind is `Followup`** when `--kind` is omitted (the most generic option).
- **`activeTaskId` is per-call**, not held on the handler. The slash-command surface reads it from `SlashCommandContext.ActiveTaskId` at dispatch time. This means the handler holds no "current task" state and is trivially shareable across `/issue` and `/task issue`.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core/InfiniContext.Core.csproj` - succeeded.
- `dotnet build src/InfiniContext.Cli/InfiniContext.Cli.csproj --no-dependencies -t:Compile` - succeeded. Full-solution build is still blocked locally by the same two running `InfiniContext.Cli.exe` processes (PIDs 2404, 19100) that locked `InfiniContext.Core.dll` in the prior session - the user explicitly left them running.
- `dotnet test tests/InfiniContext.Tests --no-build` - **667 passing + 3 macOS-only skipped (670 total) on Windows.** Baseline was 627 (Phase 8 snapshot); 40 net-new tests, no regressions in pre-existing surfaces.
- `dotnet format InfiniContext.slnx --verify-no-changes` - green.

### Skill version bump implications

`InfiniContextTaskSkill.Version` is now `3`. `SkillInstaller.InstallIfMissingOrOutdatedAsync` will rewrite `~/.claude/skills/infinicontext-task/SKILL.md` on the next host launch (the on-disk v2 will be backed up to `SKILL.md.bak`). Smoke check on first launch: the agent should now mention `issues.md` when it spots an out-of-scope finding.

### Postponed (still in `docs/stage2/todo.md` section B)

- `/issue promote <n>` - issue -> new task.
- `/task done` + review-gate / task-completion ceremony.

No new follow-ups surfaced during implementation; both "open questions" from `issues-feature-design.md` (heading vs collapsible, `Where:` parsing) are non-blocking and stay deferred.

### Not committed yet

The 12 source-tree files listed in "Code changes" above plus the 3 new test files and the .csproj edit are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: issues.md feature (v1)` - the surface ships cohesively (skill bump + service + parser + CLI + tests are interdependent). User to decide split vs single before commit.
