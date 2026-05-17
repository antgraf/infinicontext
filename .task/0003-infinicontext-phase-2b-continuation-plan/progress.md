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

## 2026-05-17 (review-gate design session)

Design conversation only - no code changes, no commits. Topic: a `/task review` command that spawns multiple agents to review the current task's solution, consolidates their findings, auto-applies high-confidence fixes, and saves a report.

### Outcome

v1 design for the `/task review` multi-agent review command locked. Full design in `review-feature-design.md` (sibling file). One-paragraph summary:

- `/task review [<id>] [--scope <glob>] [--yes]`. Compiled into `SlashCommandRegistry` (the "installed at startup" mechanism).
- Five concurrent reviewer agents (completeness, architecture, code & logic, tests, security), each receiving a coordinator-prepared context packet via a new full-model channel (NOT light-model, per user requirement) - parallel pipeline to `CliLightModelInvocation`, isolated managed-cwd, no tools, no MCP.
- Sixth coordinator agent deduplicates, re-scores, and assigns a disposition: `auto_apply` | `auto_reject` | `needs_user`. Disposition is severity-orthogonal - any severity (critical / warning / suggestion) can auto-apply at high confidence, and any severity can be auto-rejected with an explicit `rejection_reason` (required field).
- Host-side auto-apply executor: source-divergence check via `old_text` match, batch apply, build gate (`dotnet build -c Release`), test gate (`dotnet test`), revert-on-failure + demote-to-user.
- Report artifact `review-<timestamp>.md` in the task folder with three sections (Applied / Rejected / Awaiting decision), every Rejected entry carrying the consolidator's verbatim rejection reason. Index row added; progress entry appended.
- Cost guard: `[y/N]` prompt by default (5+ full-model invocations is real money); `--yes` to skip.
- New `tasks.base_commit_sha` SQLite column (Migration_002) so the diff range is reproducible; falls back to `merge-base main HEAD` when null.
- Skill bump 3 -> 4: agent learns the command exists and that it should suggest running it before declaring a task "done".

### User clarifications captured in the design

1. **Auto-apply ceiling.** Severity is NOT a floor for auto-apply - high-confidence `critical` and `warning` findings auto-apply, not just `suggestion`. Gate is confidence + concrete fix + size guards, not severity.
2. **Critical may be auto-rejected** if the reviewer is plainly wrong, but every rejection (regardless of severity) MUST carry an explicit `rejection_reason` in the report. Schema validator enforces this.
3. **Reviewers are NOT light-model.** Use full reasoning model (default: same as user's main session model). Coordinator passes them reduced/targeted context packets - reviewers don't need tools to read files themselves.
4. **Auto-apply is host-side.** Not delegated to a sub-agent.

### Postponed (filed below; still in `docs/stage2/todo.md` section B for the broader feature)

- `--reviewer <name>` single-reviewer re-run.
- Custom reviewer prompts beyond the five.
- Cross-task review.
- Auto-commit (never - user owns commits).
- The `/task done` gate that *enforces* the review (refuses to mark done if any `critical` lands in "Awaiting decision"). This design ships the *tool*; the *gate* is still a separate command, still postponed.
- `review.parallelism` setting for serialised invocation (cost predictability) - add if users complain.

### Files touched this session

- `.task/0003-infinicontext-phase-2b-continuation-plan/review-feature-design.md` - new artifact, full v1 design.
- `.task/0003-infinicontext-phase-2b-continuation-plan/index.md` - added the new artifact row.
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` - this entry.

### Handoff to next session

When the user is ready to implement v1, the entry point is the **Code touch list** section in `review-feature-design.md`. No code changes were made; implementation is fresh start.

Recommended implementation order:

1. `FindingModel.cs` + `FindingModelTests.cs` (POCOs + schema validation are the contract everything else hangs off).
2. `Migration_002_AddBaseCommitSha` + `TaskStore` / `TaskService.CreateAsync` capture.
3. `ScopeResolver` + `ScopeResolverTests` (deterministic, no model calls - cheap to iterate on).
4. `IReviewerAgentService` + `CliReviewerAgentService` (mirror `CliLightModelInvocation` with the model knob).
5. `ReviewerPrompts` v1 strings - expect to iterate against real runs.
6. `ReviewService` + fake-reviewer end-to-end test.
7. `AutoApplyExecutor` + `FakeBuildTestRunner`.
8. `ReportRenderer` + golden test.
9. CLI: `TaskCommand.Review` parser, `TaskCommandHandler.HandleReviewAsync`, wiring in `Program.cs`.
10. Settings entries + schema.
11. Skill bump 3 -> 4 (last step - skill change implies install on next host launch).
12. Dogfood: `/task review` on task 0003 itself. Tighten reviewer prompts based on findings.

The four "open questions" listed at the bottom of `review-feature-design.md` are non-blocking; decide at implementation time.

## 2026-05-17 (review-gate impl session тАФ foundational slice)

User said "implement" after the soft context-pressure handoff. Did the foundational slice of the design (steps 1-2 of the 12-step plan) and flushed before context burned out. Steps 3-12 hand off cleanly to a fresh session тАФ every contract the rest of the subsystem hangs off (the JSON schema, the SQLite column for the diff base) is now in code with tests.

### Code changes (uncommitted at flush time)

Core:

- `src/InfiniContext.Core/Review/FindingModel.cs` (new) тАФ POCOs for the whole review subsystem contract: `Severity` (`Critical | Warning | Suggestion`), `Disposition` (`AutoApply | AutoReject | NeedsUser`), `FixKind` (`Edit | ReplaceFile | Manual`), `SuggestedFix`, `Finding`, `ReviewerOutput`, `ConsolidatedFinding`, `ConsolidatedReport`, `ReviewReport`. Plus `FindingJson` helpers (shared `JsonSerializerOptions` with snake_case enum policy + fence-aware `ExtractJson` for tolerant agent-output parsing) and `FindingValidator` (the schema invariants тАФ `RejectionReason` required on `AutoReject`, concrete fix + file required on `AutoApply`, etc.).
- `src/InfiniContext.Core/Tasks/Migrations/Migration_002_AddBaseCommitSha.cs` (new) тАФ `ALTER TABLE tasks ADD COLUMN base_commit_sha TEXT NULL;`.
- `src/InfiniContext.Core/Tasks/Migrations/MigrationRunner.cs` тАФ added `Migration_002_AddBaseCommitSha` to `BuiltIn`.
- `src/InfiniContext.Core/Tasks/TaskRecord.cs` тАФ added optional `string? BaseCommitSha = null` ctor parameter (defaults to null so existing call sites still compile).
- `src/InfiniContext.Core/Tasks/TaskStore.cs` тАФ `CreateAsync` writes `base_commit_sha`; `GetAsync` / `ListAsync` SELECT it; `ReadRecord` populates it.
- `src/InfiniContext.Core/Tasks/GitHead.cs` (new) тАФ `TryResolveAsync(workingDirectory)` shells out to `git rev-parse HEAD` with a 5s budget, validates the 40-hex-char shape, returns null on any failure. Best-effort by design тАФ review falls back to `merge-base main HEAD` when the column is null.
- `src/InfiniContext.Core/Tasks/TaskService.cs` тАФ `CreateAsync` calls `GitHead.TryResolveAsync(homeCwd)` before persisting the row; the SHA flows into `TaskRecord.BaseCommitSha`.

Tests:

- `tests/InfiniContext.Tests/Review/FindingModelTests.cs` (new) тАФ 19 tests. JSON round-trip, snake_case enum serialization both directions, optional-field tolerance, fence extraction (`\`\`\`json` fence / bare `\`\`\`` fence / first-brace-to-last-brace fallback / null on garbage), `TryParseReviewerOutput` happy + sad paths, validator coverage of every schema invariant (rejection reason required for `AutoReject`, fix required for `AutoApply`, manual fix disallowed for `AutoApply`, empty old/new text rejected, file required, confidence range), explicit assertions that severity is orthogonal to disposition (critical can `AutoApply` and `AutoReject`), `ValidateAll` reports one error per offending finding.
- `tests/InfiniContext.Tests/Tasks/MigrationRunnerTests.cs` тАФ updated three existing tests that hardcoded migration count (now `MigrationRunner.BuiltIn.Count`), added `Migration_002_adds_base_commit_sha_column_to_tasks` that verifies the column via `PRAGMA table_info(tasks)`.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core/InfiniContext.Core.csproj` тАФ succeeded.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` тАФ succeeded. (Full-solution build is still blocked locally by running `InfiniContext.Cli.exe` processes locking `InfiniContext.Core.dll` тАФ same gotcha as prior sessions.)
- `dotnet test --no-build --filter Review|Tasks` тАФ **108/108 passing**. No regressions on existing Tasks-namespace tests.
- `dotnet format InfiniContext.Core` тАФ green.
- `dotnet format tests/InfiniContext.Tests` тАФ applied one whitespace fix and is now green.

### What's done vs what's left (vs. the 12-step plan in the design)

Done:

1. тЬЕ `FindingModel.cs` + `FindingModelTests.cs` (the JSON contract everything else hangs off).
2. тЬЕ `Migration_002` + `TaskStore` / `TaskService.CreateAsync` capture (with `GitHead`).

Not done (hand off to next session, in priority order):

3. `ScopeResolver` + `ScopeResolverTests` тАФ git-diff range resolution (`base_commit_sha` -> `git diff base..HEAD`), fallback to `merge-base main HEAD`, "Files touched" parsing from progress.md, byte-cap exclusion, `--scope` glob override.
4. `IReviewerAgentService` + `CliReviewerAgentService` тАФ mirror `CliLightModelInvocation` with model knob from settings. The argv shape is documented in CLAUDE.md "Light-model service" тАФ same flags except the model.
5. `ReviewerPrompts.cs` тАФ five compile-time prompt strings + the coordinator prompt. Expect 2-3 rounds of iteration after dogfooding.
6. `ReviewService.cs` тАФ orchestrator. Owns scope resolution -> packet building -> reviewer dispatch (Task.WhenAll) -> coordinator -> auto-apply -> report write. End-to-end test with a fake `IReviewerAgentService` that returns canned JSON.
7. `AutoApplyExecutor.cs` + `IBuildTestRunner` abstraction тАФ applies edits, source-divergence check via `old_text` match, build+test gate with revert-on-failure. `FakeBuildTestRunner` for tests (no real `dotnet` invocation).
8. `ReportRenderer.cs` тАФ `ReviewReport` -> markdown (the structure shown in the design's "Report artifact" section). Golden test.
9. CLI: `TaskCommand.Review(int? Id, string? Scope, bool SkipConfirm)` parser, `TaskCommandHandler.HandleReviewAsync`, wiring in `Program.cs`.
10. Settings: `review` section in `settings.json` + `settings.schema.json` + `ReviewSettings` record + binding.
11. Skill bump: `InfiniContextTaskSkill.Version` 3 -> 4, add "Task review" subsection.
12. Dogfood: run `/task review` on task 0003 itself; tighten prompts.

### Files touched this session

- `src/InfiniContext.Core/Review/FindingModel.cs` (new)
- `src/InfiniContext.Core/Tasks/Migrations/Migration_002_AddBaseCommitSha.cs` (new)
- `src/InfiniContext.Core/Tasks/Migrations/MigrationRunner.cs` (BuiltIn list)
- `src/InfiniContext.Core/Tasks/TaskRecord.cs` (added BaseCommitSha param)
- `src/InfiniContext.Core/Tasks/TaskStore.cs` (read/write base_commit_sha)
- `src/InfiniContext.Core/Tasks/GitHead.cs` (new)
- `src/InfiniContext.Core/Tasks/TaskService.cs` (capture HEAD on CreateAsync)
- `tests/InfiniContext.Tests/Review/FindingModelTests.cs` (new)
- `tests/InfiniContext.Tests/Tasks/MigrationRunnerTests.cs` (3 updates + 1 new test)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 9 source/test files above are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review foundational slice (FindingModel + Migration_002)` тАФ the surface is cohesive (schema contract + diff-base capture are the two contracts everything in steps 3-12 builds on).

### Handoff to next session

Open `review-feature-design.md` "Code touch list" and resume at step 3 (`ScopeResolver`). The POCOs in `FindingModel.cs` and the `BaseCommitSha` column are the stable contracts the rest of the subsystem hangs off тАФ no need to revisit them.

Open notes for next session:

- Existing `TaskServiceTests` may incidentally invoke `GitHead.TryResolveAsync` against test temp dirs since `CreateAsync` is unconditional about the call. In those tests it returns null and BaseCommitSha stays null тАФ observed via the 108-test pass above. No test breakage; just be aware if you add a TaskService test that asserts on full-record equality.
- The skill version stays at `3` for now тАФ bump to `4` only when step 11 lands (the on-disk skill should match the code surface).

## 2026-05-17 (review-gate impl session тАФ step 3: ScopeResolver)

User said "go next" after the foundational-slice commit. Shipped step 3 of the 12-step plan: `ScopeResolver` + tests. Pure-logic component; no agent invocation, no settings churn тАФ clean stand-alone unit.

### Code changes (uncommitted at flush time)

Core (`src/InfiniContext.Core/Review/`):

- `IGitRunner.cs` (new) тАФ thin abstraction with three methods: `ResolveHeadAsync`, `ResolveMergeBaseAsync`, `DiffNameOnlyAsync`. All return null/empty on failure (no exceptions). Lets tests swap in a fake.
- `ProcessGitRunner.cs` (new) тАФ production `IGitRunner`. Shells out to `git` with a 10s budget per call, validates 40-hex-char SHAs, swallows all failure modes to null (no git on PATH, not a repo, unreachable ref, timeout).
- `ScopeResolver.cs` (new) тАФ resolves the review file set. Order: (1) `--scope` override paths take precedence; (2) else `git diff <baseCommitSha>..HEAD --name-only`, unioned with `progress.md` "Files touched" entries (dedup, platform-sensitive path comparer); (3) if `baseCommitSha` is null, fall back to `merge-base main HEAD` and surface the fallback in `ReviewScope.FallbackNote`. Files that don't exist, escape the repo root, or exceed the byte cap (default 200KB) go to `ExcludedFiles` with the reason. Returns `ReviewScope { Description, Files: List<ReviewFile>, ExcludedFiles, FallbackNote }`. Static `ParseFilesTouched(markdownText)` is exposed for tests тАФ finds backtick-quoted paths under any heading containing "files touched" (case-insensitive), with a path-shape filter (must contain `/` or `\`, no spaces).

Tests (`tests/InfiniContext.Tests/Review/ScopeResolverTests.cs`, new): 16 tests, all green. Coverage:

- ResolveAsync: scope-override wins (no git calls); baseCommitSha used when set; merge-base fallback when null + fallback note populated; no-base-resolvable case reports clearly; diff тИк progress.md union; dedup when both contain the same path; byte-cap exclusion; missing-file exclusion; outside-repo-root exclusion; blank repo root throws; non-positive byte cap throws.
- ParseFilesTouched: finds paths under heading; ignores paths outside the section; filters non-path backticks; case-insensitive heading match; empty input returns empty.

Tests use a local `RepoFixture` (temp dir, IDisposable) and a `FakeGitRunner` (canned `Head`/`MergeBase`/`DiffResult`, records `DiffCalls` + `LastDiffBaseRef`). No git invocation in tests.

### What's done vs what's left (vs. the 12-step plan)

Done:

1. тЬЕ FindingModel + tests (prior session).
2. тЬЕ Migration_002 + base_commit_sha capture (prior session).
3. тЬЕ ScopeResolver + tests (this session).

Not done (hand off, in priority order):

4. `IReviewerAgentService` + `CliReviewerAgentService` тАФ mirror `CliLightModelInvocation` argv shape with model knob from settings. The Light-model channel argv is documented in `CLAUDE.md` "Light-model service"; reuse everything except switch to the user's main model.
5. `ReviewerPrompts.cs` тАФ five compile-time reviewer prompts + the coordinator prompt. Expect 2-3 rounds of iteration after dogfooding.
6. `ReviewService.cs` тАФ orchestrator (scope -> packets -> Task.WhenAll reviewers -> coordinator -> auto-apply -> report). End-to-end test with a fake `IReviewerAgentService`.
7. `AutoApplyExecutor.cs` + `IBuildTestRunner` abstraction (so tests don't shell out to dotnet). `FakeBuildTestRunner` for unit tests.
8. `ReportRenderer.cs` + golden test.
9. CLI: `TaskCommand.Review(int? Id, string? Scope, bool SkipConfirm)` parser, `TaskCommandHandler.HandleReviewAsync`, wiring in `Program.cs`. Glob expansion for `--scope` happens here (caller of `ScopeResolver`).
10. Settings: `review` section + schema + binding.
11. Skill bump 3 -> 4.
12. Dogfood.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` тАФ green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` тАФ green. (CLI lock workaround still applies.)
- `dotnet test --no-build --filter Review|Tasks` тАФ **350/350 passing**. No regressions.
- `dotnet format` тАФ green on both Core and Tests.

### Files touched this session

- `src/InfiniContext.Core/Review/IGitRunner.cs` (new)
- `src/InfiniContext.Core/Review/ProcessGitRunner.cs` (new)
- `src/InfiniContext.Core/Review/ScopeResolver.cs` (new)
- `tests/InfiniContext.Tests/Review/ScopeResolverTests.cs` (new)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 4 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 3 (ScopeResolver).`

### Handoff to next session

Resume at step 4 (`IReviewerAgentService` + `CliReviewerAgentService`). Reference: `src/InfiniContext.Core/LightModel/CliLightModelInvocation.cs` is the argv-shape template тАФ same flags except swap the model. The full-model channel needs a `--model` knob driven by `settings.review.reviewerModel` (defaults to user's main model when null).

## 2026-05-17 (review-gate impl session тАФ step 4: ReviewerAgentService)

User said "continue" after the step-3 commit. Shipped step 4: `IReviewerAgentService` + `CliReviewerAgentService` + `ReviewerAgentOptions`. The full-model invocation channel reviewers use.

### Design decision (recorded in code)

Reused `CliLightModelInvocation` / `CliLightModelChild` as the wire layer rather than duplicating the spawn loop. The user's "agents must not be light models" requirement is satisfied at the *model* level (the `--model` arg carries the user's main reasoning model, not Haiku-class); the spawn argv recipe (`--print --tools "" --strict-mcp-config --mcp-config {} --setting-sources project --disable-slash-commands --no-session-persistence`) is identical between the two channels and there's no value in forking the plumbing. The architectural distinction lives in *what model* gets passed, not in *which spawn machinery*. Comment in `CliReviewerAgentService.cs` records the rationale.

### Code changes (uncommitted at flush time)

Core (`src/InfiniContext.Core/Review/`):

- `IReviewerAgentService.cs` (new) тАФ public interface, single method `RunOneShotAsync(systemPrompt, userPayload, ct) -> Task<string>`. Returns the assistant's full response text; caller uses `FindingJson.ExtractJson` for the JSON payload.
- `ReviewerAgentOptions.cs` (new) тАФ public record. Model (default `claude-sonnet-4-6`), ClaudeExecutable?, ManagedCwd? (defaults to `%USERPROFILE%/.infinicontext/review-cwd/` тАФ separate from the light-cwd), MaxOutputTokens (default 32_768 тАФ reviewers emit JSON finding lists, not single-token verdicts), ThinkingEnabled (default true), InvocationTimeoutSeconds (default 600), ExtraEnv? (test-only).
- `CliReviewerAgentService.cs` (new) тАФ concrete impl. Ctor validates Model/MaxOutputTokens/InvocationTimeoutSeconds. `RunOneShotAsync` builds a `LightModelOptions`-shaped config via `internal static BuildLightModelOptions(ReviewerAgentOptions)`, links the caller's ct with a per-invocation timeout cts, and delegates the spawn + turn drive to `CliLightModelChild.Spawn` (same-assembly internal access). Single-shot тАФ every call spawns a fresh process and disposes it; no long-lived sharing.

Tests (`tests/InfiniContext.Tests/Review/CliReviewerAgentServiceTests.cs`, new): 18 tests, all green.

- Ctor: rejects null options, blank model, non-positive MaxOutputTokens, non-positive InvocationTimeoutSeconds.
- RunOneShotAsync: rejects blank systemPrompt + blank userPayload.
- BuildLightModelOptions: propagates Model id; forces `Mode=SingleShot`; mirrors MaxOutputTokens to BOTH `MaxOutputTokens` AND `ThinkingMaxOutputTokens` (the light-model split doesn't apply to reviewers); propagates ClaudeExecutable, ManagedCwd (set), ExtraEnv (by reference). When ManagedCwd is null, resolves the default `.infinicontext/review-cwd` path (distinct from `.infinicontext/light-cwd`, so the two channels don't share a cwd).
- Recipe argv: passing reviewer model id through `CliLightModelInvocation.BuildArgv` produces an argv with `--model <reviewerModel>` and all the recipe isolation flags.

No end-to-end "real spawn" test in step 4 тАФ that surfaces naturally in step 6 (`ReviewService` with a fake `IReviewerAgentService`) and in step 12 (dogfooding).

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` тАФ green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` тАФ green.
- `dotnet test --no-build --filter Review|Tasks` тАФ **368/368 passing**. No regressions.
- `dotnet format` тАФ green on both Core and Tests.

### What's done vs what's left (vs. the 12-step plan)

Done:

1. тЬЕ FindingModel + tests.
2. тЬЕ Migration_002 + base_commit_sha capture.
3. тЬЕ ScopeResolver + tests.
4. тЬЕ IReviewerAgentService + CliReviewerAgentService + tests.

Not done: 5 (ReviewerPrompts), 6 (ReviewService), 7 (AutoApplyExecutor), 8 (ReportRenderer), 9 (CLI: TaskCommand.Review + handler), 10 (Settings), 11 (Skill bump 3->4), 12 (Dogfood).

### Files touched this session

- `src/InfiniContext.Core/Review/IReviewerAgentService.cs` (new)
- `src/InfiniContext.Core/Review/ReviewerAgentOptions.cs` (new)
- `src/InfiniContext.Core/Review/CliReviewerAgentService.cs` (new)
- `tests/InfiniContext.Tests/Review/CliReviewerAgentServiceTests.cs` (new)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 4 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 4 (ReviewerAgentService).`

### Handoff to next session

Resume at step 5 (`ReviewerPrompts.cs`). Six compile-time strings:

- One per reviewer role (completeness, architecture, code, tests, security) тАФ each describes the role, the JSON schema the reviewer must emit (cross-reference `FindingModel.cs` for field names), and the slot markers the orchestrator fills (e.g. `{description_md}`, `{progress_md}`, `{diff_summary}`, `{file_excerpts}`).
- One for the coordinator тАФ describes dedup, severity re-scoring, disposition rules (auto_apply / auto_reject / needs_user with the confidence + fix-presence gates from the design), and the requirement that `rejection_reason` be non-empty for `auto_reject`.

Then step 6 (`ReviewService`) is the orchestrator that wires steps 3, 4, and 5 together. The `IReviewerAgentService` interface that landed here is exactly the seam step 6 will use a fake for in its end-to-end tests.
