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

## 2026-05-17 (review-gate impl session тАФ step 5: ReviewerPrompts)

User said "continue" after the step-4 commit. Shipped step 5: `ReviewerPrompts.cs` тАФ six compile-time prompt templates (five reviewer roles + coordinator), slot-fill helper, slot-discovery helper.

### Code changes (uncommitted at flush time)

Core:

- `src/InfiniContext.Core/Review/ReviewerPrompts.cs` (new) тАФ public static class. Exposes:
  - `enum ReviewerRole { Completeness, Architecture, CodeLogic, Tests, Security, Coordinator }`.
  - `SystemFor(role)` тАФ system prompt (role definition + JSON output schema, no slots). Each reviewer's prompt names the role, lists what to look for + what NOT to look at (other reviewers handle those), and ends with the common output contract (schema + severity definitions + "output ONLY JSON, no fences"). Coordinator prompt is its own thing тАФ describes dedup/re-score/disposition rules, requires `rejection_reason` for auto_reject, explicitly states severity-is-orthogonal-to-disposition.
  - `UserTemplateFor(role)` тАФ user-payload template with `{slot_name}` markers. The orchestrator fills via `Fill`.
  - `Fill(template, slots)` тАФ substitutes `{slot_name}` markers; throws on missing slot referenced in template; silently ignores extra slot keys so the orchestrator can pass a uniform packet.
  - `SlotsReferencedBy(template)` тАФ returns slot keys in document order; used by tests + (eventually) the orchestrator to verify it provides everything a template needs.
  - `SlotKeys.{TaskTitle, TaskId, DescriptionMd, ProgressMd, IndexMd, ClaudeMd, DecisionsMd, DiffSummary, FileExcerpts, TestExcerpts, TestResults, CsprojDiffs, ReviewerOutputs, ScopeDescription}` тАФ canonical slot names.

Tests (`tests/InfiniContext.Tests/Review/ReviewerPromptsTests.cs`, new): 50 tests, all green.

- Every role returns non-empty SystemFor and UserTemplateFor.
- Every reviewer system prompt names the three severity levels, mentions the JSON schema fields (severity, confidence, suggested_fix, findings), instructs JSON-only output (regex `Output ONLY.*JSON`), and names its role.
- Coordinator system mentions all three disposition values, requires `rejection_reason`, surfaces "orthogonal" wording so the agent doesn't default to "critical always needs user".
- Each reviewer's user template references the slots that role needs (completeness: description/progress/index/diff/excerpts; architecture: claude_md/decisions/excerpts; tests: test_excerpts/test_results; security: csproj_diffs; coordinator: reviewer_outputs).
- Every user template references task_id/task_title/scope_description (the common metadata).
- Fill substitutes single slot; substitutes every occurrence of repeated slot; leaves no-slot text unchanged; throws on missing slot with the slot name in the error message; ignores extra keys.
- SlotsReferencedBy preserves document order.
- Edge cases: SystemFor throws on unknown role; Fill throws on null template.

### Design choices recorded

- **JSON-only output contract is centralised** as `CommonOutputContract` (private const) shared across all five reviewer system prompts. The coordinator's schema is different (adds disposition fields) so it has its own.
- **Coordinator gets its own dispositional schema** spelled out in the system prompt, not derived from the reviewer schema. Avoids hoping the agent infers the extra fields.
- **`rejection_reason` requirement is surfaced as "REQUIRED"** in caps in the coordinator prompt тАФ agents tend to skip optional-looking fields.
- **Severity-orthogonal language** uses the exact word "orthogonal" so a test can pin it. The prompt says "ANY severity can be auto_reject тАФ the gate is your confidence, not the reviewer's severity choice." Mirror of the user's clarification.
- **Unparseable reviewer output handling**: coordinator prompt says "surface that as a needs_user finding (severity=warning) rather than dropping it silently". This is the contract the orchestrator depends on in step 6 when a reviewer's output fails `FindingJson.TryParseReviewerOutput`.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` тАФ green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` тАФ green.
- `dotnet test --no-build --filter Review|Tasks` тАФ **418/418 passing**. No regressions.
- `dotnet format` тАФ green on both Core and Tests.

### What's done vs what's left (vs. the 12-step plan)

Done: 1 (FindingModel), 2 (Migration_002), 3 (ScopeResolver), 4 (ReviewerAgentService), 5 (ReviewerPrompts).

Not done (in priority order): 6 (ReviewService тАФ the orchestrator; largest step), 7 (AutoApplyExecutor + IBuildTestRunner), 8 (ReportRenderer), 9 (CLI), 10 (Settings), 11 (Skill bump 3->4), 12 (Dogfood).

### Files touched this session

- `src/InfiniContext.Core/Review/ReviewerPrompts.cs` (new)
- `tests/InfiniContext.Tests/Review/ReviewerPromptsTests.cs` (new)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 2 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 5 (ReviewerPrompts).`

### Handoff to next session

Resume at step 6 (`ReviewService` тАФ the orchestrator). This is the largest step. It wires together: ScopeResolver (step 3), IReviewerAgentService (step 4), ReviewerPrompts (step 5), and FindingJson + FindingValidator (step 1). Then it calls the AutoApplyExecutor (step 7 тАФ not yet built; can stub) and writes the report via the ReportRenderer (step 8 тАФ not yet built).

API sketch for step 6:

```csharp
public sealed class ReviewService
{
    public ReviewService(
        TaskService tasks,
        ScopeResolver scope,
        IReviewerAgentService reviewer,           // single channel тАФ orchestrator handles N concurrent invocations
        IAutoApplyExecutor autoApply,              // null-safe: when null, every auto_apply demoted to needs_user
        Func<DateTimeOffset>? clock = null);

    public Task<ReviewReport> RunReviewAsync(int taskId, ReviewOptions options, CancellationToken ct);
}

public sealed record ReviewOptions(
    IReadOnlyList<string>? ScopeOverridePaths = null,
    bool AutoApplyEnabled = true,
    double ConfidenceFloor = 0.9,
    int AutoApplyMaxLinesPerFinding = 50,
    int AutoApplyMaxFilesPerFinding = 3);
```

Flow:
1. Load `TaskRecord` via `TaskService.GetAsync`. Resolve task folder. Read description.md, progress.md, index.md.
2. Run `ScopeResolver.ResolveAsync` with task's home_cwd and BaseCommitSha.
3. Build the slot dictionary (PromptSlots in spirit). Render five reviewer user payloads via `ReviewerPrompts.Fill`.
4. `Task.WhenAll` over five `_reviewer.RunOneShotAsync(SystemFor(role), userPayload, ct)`. Parse each output via `FindingJson.TryParseReviewerOutput`; on parse failure, synthesize a single needs_user warning carrying the raw text.
5. Build the coordinator user payload (concatenate five reviewer JSON outputs). Run coordinator. Parse via `FindingJson.TryParseConsolidatedReport`. Validate via `FindingValidator.ValidateAll`; on validation failure, demote every auto_* to needs_user with a `"coordinator output failed validation: <msg>"` note.
6. Hand `auto_apply` findings to `IAutoApplyExecutor.ApplyAsync` (step 7). Get back which actually applied vs. were demoted (build/test failure, source diverged).
7. Build `ReviewReport` partitioning consolidated findings into Applied / Rejected / Awaiting.
8. Return. (Step 8's `ReportRenderer` will turn it into the markdown artifact; step 9's CLI handler invokes that.)

End-to-end test pattern (FakeReviewerAgentService): set up a queue of canned (role, jsonText) responses; service dispatches each call to the next queued response by inspecting the system prompt. Tests verify the partitioning, the unparseable-reviewer fallback, the coordinator-validation-failure demotion path, and that `auto_apply` reaches the executor only when validation passes.

The auto-apply executor (step 7) can be stubbed for step 6 tests via a fake `IAutoApplyExecutor` that just echoes the input back as "all applied" or "all demoted" depending on the test's intent.

## 2026-05-17 (review-gate impl session тАФ step 6: ReviewService orchestrator)

User said "continue" after the step-5 commit. Shipped step 6 тАФ the largest step. `ReviewService` is the orchestrator that wires together steps 1, 3, 4, and 5 plus the step-7 executor seam.

### Design choices made

- **Orchestrator takes a pre-built `ReviewRequest`**, not a task id. Data gathering (load task record, run ScopeResolver, read CLAUDE.md / decisions.md) is the CLI handler's job (step 9). This keeps the orchestrator independent of `TaskService` + `ScopeResolver` and makes it unit-testable without scaffolding the whole task subsystem.
- **`IAutoApplyExecutor` is a seam, defined here but implemented in step 7.** Orchestrator depends only on the interface; step 6 ships testable today.
- **Unparseable reviewer output is synthesized on the orchestrator side**, not delegated to the coordinator. The coordinator prompt still tells the agent to do it (per step 5's prompt), but on the host side we'd rather deterministically synthesize than hope the agent remembers. Synthetic findings are `Severity.Warning` + `Disposition.NeedsUser` with the raw text truncated to 800 chars in the summary.
- **Coordinator validation failure demotes all `auto_*` findings to `NeedsUser`** with a note. Validation runs via `FindingValidator.ValidateAll`; first error message gets embedded in the demotion note.
- **Reviewer failures don't kill the run.** Each reviewer call is wrapped тАФ a thrown exception or a parse failure becomes one synthetic finding; the coordinator still runs on the surviving reviewers. If *all* five fail, coordinator is skipped (no input to consolidate) and the report ships with five synthetic findings.
- **`AutoApplyResult.Demoted` findings are written by the executor with their disposition already flipped to `NeedsUser`** тАФ orchestrator just routes them to the awaiting bucket. Keeps the orchestrator from having to re-write findings the executor already adjusted.

### Code changes (uncommitted at flush time)

Core (`src/InfiniContext.Core/Review/`):

- `IAutoApplyExecutor.cs` (new) тАФ interface (`ApplyAsync(repoRoot, findings, ct) -> AutoApplyResult`) + `AutoApplyResult` record (`Applied`, `Demoted`, `BuildTestGatePassed`, `GateReversionReason`). Step 7 will implement.
- `ReviewRequest.cs` (new) тАФ `ReviewRequest` (TaskId, TaskTitle, RepoRoot, Scope, DescriptionMd, ProgressMd, IndexMd, ClaudeMd, DecisionsMd) + `ReviewOptions` (AutoApplyEnabled).
- `ReviewService.cs` (new) тАФ the orchestrator. ~350 lines. Public `RunReviewAsync(request, options, ct) -> ReviewReport`. Internal helpers: `RunSingleReviewerAsync`, `RunCoordinatorAsync`, `BuildSlots`, `SplitExcerpts` (path-based routing into file_excerpts / test_excerpts / csproj_diffs slots), `BuildDiffSummary`, `SerializeReviewerOutputs`, `SynthesizeReviewerFailureFinding`, `DemoteAllToNeedsUser`, `DemoteAutoToNeedsUser`, `WithDemotion`. Internal records `ReviewerCallResult` + `CoordinatorCallResult` to thread per-call status through the dispatch.

Tests (`tests/InfiniContext.Tests/Review/ReviewServiceTests.cs`, new): 18 tests, all green. Coverage:

- Argument validation: null request / null options / non-positive TaskId / blank TaskTitle / blank RepoRoot.
- Happy paths: NeedsUser findings тЖТ Awaiting bucket; AutoApply findings тЖТ executor called тЖТ Applied bucket; AutoReject findings тЖТ Rejected bucket.
- Auto-apply gating: `AutoApplyEnabled=false` demotes all auto-apply to Awaiting with note `auto-apply disabled by review options`; null executor demotes with note `no auto-apply executor configured`.
- Reviewer failures: one reviewer throws тЖТ synthesized warning in Awaiting (other four + coordinator still run); one reviewer emits unparseable JSON тЖТ same synthesis; *all* reviewers throw тЖТ coordinator skipped, five synthetics returned.
- Coordinator failures: unparseable output тЖТ every reviewer finding demoted to NeedsUser with note `coordinator output was unparseable`; output fails validation (e.g. AutoReject without rejection_reason) тЖТ auto_* findings demoted with note `coordinator output failed schema validation`.
- Report metadata: RawReviewerOutputs has all six role keys; ExcludedFiles propagates with reason; executor's BuildTestGatePassed=false + GateReversionReason flows through.

Tests use a `FakeReviewer` that infers role from system prompt content (matching the role-naming words like "Completeness reviewer", "Code & Logic reviewer", "Coordinator" тАФ all introduced in step 5) and a `FakeAutoApplyExecutor` with a configurable Outcome tuple. No on-disk task scaffolding.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` тАФ green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` тАФ green.
- `dotnet test --no-build --filter Review|Tasks` тАФ **436/436 passing**. No regressions.
- `dotnet format` тАФ green on both Core and Tests.

### What's done vs what's left (vs. the 12-step plan)

Done: 1 (FindingModel), 2 (Migration_002), 3 (ScopeResolver), 4 (ReviewerAgentService), 5 (ReviewerPrompts), 6 (ReviewService).

Not done: 7 (AutoApplyExecutor тАФ `IAutoApplyExecutor` interface already in place from step 6), 8 (ReportRenderer), 9 (CLI), 10 (Settings), 11 (Skill bump 3->4), 12 (Dogfood).

### Files touched this session

- `src/InfiniContext.Core/Review/IAutoApplyExecutor.cs` (new)
- `src/InfiniContext.Core/Review/ReviewRequest.cs` (new)
- `src/InfiniContext.Core/Review/ReviewService.cs` (new)
- `tests/InfiniContext.Tests/Review/ReviewServiceTests.cs` (new)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 4 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 6 (ReviewService orchestrator).`

### Handoff to next session

Resume at step 7 (`AutoApplyExecutor` тАФ implements `IAutoApplyExecutor`).

API sketch:

```csharp
public sealed class AutoApplyExecutor : IAutoApplyExecutor
{
    public AutoApplyExecutor(IBuildTestRunner gate, ILogger? logger = null);
    public Task<AutoApplyResult> ApplyAsync(string repoRoot, IReadOnlyList<ConsolidatedFinding> findings, CancellationToken ct);
}

public interface IBuildTestRunner
{
    Task<BuildTestOutcome> BuildAsync(string repoRoot, CancellationToken ct);
    Task<BuildTestOutcome> TestAsync(string repoRoot, CancellationToken ct);
}

public sealed record BuildTestOutcome(bool Passed, string? FailureSummary);
```

Apply algorithm:
1. For each finding: Read target file. If `SuggestedFix.OldText` doesn't match current content тЖТ demote to NeedsUser with note `source diverged`. Else save original тЖТ apply (Edit for kind=edit, full overwrite for kind=replace-file).
2. After batch: BuildAsync. If false тЖТ revert every file to its original тЖТ demote all to NeedsUser with note `build broke after auto-apply` тЖТ return.
3. TestAsync. If false тЖТ revert тЖТ demote тЖТ return.
4. Return Applied=findings, Demoted=any-that-couldn't-apply-step-1, BuildTestGatePassed=true.

For tests use a `FakeBuildTestRunner` that returns canned outcomes. Real `ProcessBuildTestRunner` shells out to `dotnet build` / `dotnet test` and parses exit codes (failure summary can be the last N lines of stderr). Production ctor will need wiring in step 9.

## 2026-05-17 (review-gate impl session тАФ step 7: AutoApplyExecutor)

User said "continue" after the step-6 commit. Shipped step 7: `AutoApplyExecutor` (implements the `IAutoApplyExecutor` seam from step 6) + `IBuildTestRunner` abstraction (so tests don't shell out to dotnet).

### Code changes (uncommitted at flush time)

Core (`src/InfiniContext.Core/Review/`):

- `IBuildTestRunner.cs` (new) тАФ interface (`BuildAsync` / `TestAsync` тЖТ `BuildTestOutcome`) + `BuildTestOutcome` record. Production wiring (a `ProcessBuildTestRunner` shelling out to `dotnet build` / `dotnet test`) is a step-9 concern; this step ships the abstraction and a fake-based test suite.
- `AutoApplyExecutor.cs` (new) тАФ concrete `IAutoApplyExecutor`. Per-finding apply:
  - Path safety: target file must resolve under `repoRoot` (with `Path.DirectorySeparatorChar` boundary check so `repoRoot-sibling` paths don't sneak through).
  - **Edit** kind: file must exist; `OldText` must appear *exactly once* (zero or multiple тЖТ demote "source diverged"); writes the spliced result.
  - **ReplaceFile** kind: writes the new content; creates parent directories; backs up the original (or `null` for "didn't exist before").
  - **Manual** kind or missing fields: demote with a specific note (`"fix not auto-applicable: kind=Manual"` / `"SuggestedFix is null"` / etc.).
  - Per-file backups recorded in a `Dictionary<string, byte[]?>` keyed on the absolute path; `null` value means "didn't exist before, revert = delete".
- After the batch: `BuildAsync` first; on failure, revert every backed-up file and demote every Applied finding with note `"build broke after auto-apply: <summary>"`, return early. On build green, `TestAsync`; on failure, same revert + demote with `"tests broke after auto-apply: <summary>"`. On both green, return the originally-Applied bucket + any per-finding demotions from step 1.

Tests (`tests/InfiniContext.Tests/Review/AutoApplyExecutorTests.cs`, new): 19 tests, all green. Coverage:

- Argument validation: missing repo root throws `DirectoryNotFoundException`; blank repo root throws `ArgumentException`; null runner throws at ctor.
- No-findings shortcut: skips the gate entirely (no Build/Test calls).
- Edit: applies when OldText matches once; demotes "source diverged" when not found; demotes "ambiguous" when found multiple times (and leaves the file untouched); demotes "does not exist" for missing target.
- Path safety: `"../escape.cs"` demotes "outside repo root".
- ReplaceFile: overwrites existing file; creates new file when target missing.
- Manual / null fix demotions with specific notes.
- Build gate: failure reverts the file and demotes; `TestAsync` not called when build fails; `GateReversionReason` carries the failure summary.
- Test gate: failure reverts the file and demotes.
- ReplaceFile that created a new file: revert deletes the created file (doesn't leave it behind).
- Multi-finding interactions: two findings on different files both apply; two findings on the same file with overlapping OldText тАФ the second demotes "source diverged" after the first's edit changed the source; gate failure reverts every touched file.

Tests use a temp-dir `RepoFixture` (IDisposable) and a `FakeRunner` with `BuildCalls` / `TestCalls` counters + configurable outcomes.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` тАФ green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` тАФ green.
- `dotnet test --no-build --filter Review|Tasks` тАФ **455/455 passing**. No regressions.
- `dotnet format` тАФ applied one whitespace fix and is now green.

### What's done vs what's left (vs. the 12-step plan)

Done: 1 (FindingModel), 2 (Migration_002), 3 (ScopeResolver), 4 (ReviewerAgentService), 5 (ReviewerPrompts), 6 (ReviewService), 7 (AutoApplyExecutor).

Not done: 8 (ReportRenderer), 9 (CLI: TaskCommand.Review + handler + Program.cs wiring), 10 (Settings), 11 (Skill bump 3->4), 12 (Dogfood).

### Files touched this session

- `src/InfiniContext.Core/Review/IBuildTestRunner.cs` (new)
- `src/InfiniContext.Core/Review/AutoApplyExecutor.cs` (new)
- `tests/InfiniContext.Tests/Review/AutoApplyExecutorTests.cs` (new)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 3 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 7 (AutoApplyExecutor).`

### Handoff to next session

Resume at step 8 (`ReportRenderer.cs`) тАФ turn `ReviewReport` into the markdown artifact (`review-<timestamp>.md`). The design's "Report artifact" section in `review-feature-design.md` has the exact markdown structure (three sections тАФ Applied / Rejected / Awaiting тАФ each grouped by severity, every Rejected entry showing the verbatim `RejectionReason`, plus a `<details>`-wrapped Raw reviewer outputs section). Golden test in step 8.

Then step 9 (CLI) wires it together: `/task review` parses; the handler loads task records, runs `ScopeResolver`, reads `CLAUDE.md` + `decisions.md` from disk, builds the `ReviewRequest`, calls `ReviewService.RunReviewAsync`, renders via `ReportRenderer`, writes the artifact under the lock contract, updates `index.md` + appends to `progress.md`. The handler also needs to build a `ProcessBuildTestRunner` (production `IBuildTestRunner`) тАФ that's the only step-7-adjacent piece that step 9 still has to build.

## 2026-05-17 (review-gate impl session - step 8: ReportRenderer)

User said "continue" after the step-7 commit. Shipped step 8: `ReportRenderer` - pure function that turns a `ReviewReport` into the markdown body of `review-<timestamp>.md`.

### Code changes (uncommitted at flush time)

Core (`src/InfiniContext.Core/Review/`):

- `ReportRenderer.cs` (new) - `Render(ReviewReport) -> string`. Pure function; CLI handler writes the result under the lock contract. Sections in order: header (date, task, reviewers, scope, excluded files, findings count, build/test gate) -> Applied -> Rejected -> Awaiting -> Raw reviewer outputs.
  - Each bucket orders findings critical -> warning -> suggestion within itself.
  - Applied entries show `Fix applied (kind)` block with Before/After code fences.
  - **Rejected entries surface `RejectionReason` verbatim** prefixed with `**Rejection reason:**`. If a reason is somehow missing (validator should have caught it), renders an explicit "schema violation" marker as a clear flag.
  - Awaiting entries show `Suggested fix` block (or `_none_` when null); manual fixes show a "manual - host cannot auto-apply" marker.
  - Locations render as `` `path:line` `` for single-line, `` `path:start-end` `` for ranges; falls back to `_file not specified_` when File is null.
  - Raw reviewer outputs render in `<details>`/`<summary>` blocks with the role name as the summary, in stable alphabetic order. Empty raw text becomes `(no output)`.
  - Empty buckets render `_None._` so the section is never blank.

Tests (`tests/InfiniContext.Tests/Review/ReportRendererTests.cs`, new): 26 tests, all green. Coverage: null report throws; header fields (task id padding, findings count, gate status, excluded files); all three buckets always present; severity ordering verified within a bucket; Applied/Rejected/Awaiting per-bucket field rendering; location single-line vs range vs missing; rejection reason verbatim + missing-reason flag; manual-fix marker (asserted via two ASCII anchors around the em-dash to avoid tool-vs-source encoding fights); raw outputs in `<details>` blocks.

### One messy aside

The em-dash literal in the renderer caused several rounds of editing pain - the host's Edit tool wouldn't reliably match em-dash bytes that had been written to disk and rendered with mojibake in the Read view. Workaround: the test asserts the two ASCII anchors flanking the em-dash rather than the literal. Renderer source keeps the em-dash; tests don't try to round-trip it. Going forward, I'll prefer ASCII in code that the host edit tools touch unless the non-ASCII is load-bearing.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build --filter Review|Tasks` - **481/481 passing**. No regressions.
- `dotnet format` - green on both Core and Tests.

### What's done vs what's left (vs. the 12-step plan)

Done: 1 (FindingModel), 2 (Migration_002), 3 (ScopeResolver), 4 (ReviewerAgentService), 5 (ReviewerPrompts), 6 (ReviewService), 7 (AutoApplyExecutor), 8 (ReportRenderer).

Not done: 9 (CLI: TaskCommand.Review parser + handler + Program.cs wiring + ProcessBuildTestRunner), 10 (Settings: `review` section + schema + binding), 11 (Skill bump 3 -> 4), 12 (Dogfood).

### Files touched this session

- `src/InfiniContext.Core/Review/ReportRenderer.cs` (new)
- `tests/InfiniContext.Tests/Review/ReportRendererTests.cs` (new)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 2 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 8 (ReportRenderer).`

### Handoff to next session

Resume at step 9 (CLI). This step ties everything together. Touch list:

- `src/InfiniContext.Cli/Tasks/TaskCommand.cs` - add `sealed record Review(int? Id, string? Scope, bool SkipConfirm)` + parser branch for `review` subcommand handling `<id?>`, `--scope <glob>`, `--yes`.
- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` - `HandleReviewAsync` dispatch. Steps: resolve task id (focused session's if omitted); print cost-guard `[y/N]` unless `--yes`; load task record via `TaskService.GetAsync`; run `ScopeResolver` (need to construct it with `ProcessGitRunner` + glob expansion for `--scope`); read description.md/progress.md/index.md from the task folder; read CLAUDE.md / decisions.md from the repo (best-effort, empty string when missing); build `ReviewRequest`; call `ReviewService.RunReviewAsync`; render via `ReportRenderer`; write `review-<timestamp>.md` to the task folder; acquire lock + update index.md + append progress.md entry.
- `src/InfiniContext.Core/Review/ProcessBuildTestRunner.cs` (new) - production `IBuildTestRunner`. Shells out to `dotnet build` / `dotnet test` from the repo root; captures the last N lines of stderr for the failure summary.
- `src/InfiniContext.Cli/Program.cs` - construct `ReviewService` + `AutoApplyExecutor` + `ProcessBuildTestRunner` + `CliReviewerAgentService` (from `ReviewerAgentOptions` built from settings); thread `ReviewService` into `TaskCommandHandler`.

The CLI handler is the largest piece. Plan to ship glob expansion via a small helper (`Microsoft.Extensions.FileSystemGlobbing` isn't currently a dep - either add the package or write a minimal `*`/`**` matcher).

After step 9 lands: step 10 (settings) is small (~50 lines to add a `ReviewSettings` record + schema entries + reading in Program.cs). Step 11 (skill bump) is trivial. Step 12 (dogfood) is a behavioral pass - run `/task review` on task 0003 itself and iterate on the prompts.

## 2026-05-17 (review-gate impl session - step 9a: CLI parser + ProcessBuildTestRunner)

User said "go next" after the step-8 commit. Step 9 (CLI wiring) is the largest remaining step; splitting into two slices for focused commits.

**Slice 9a (this commit):** `TaskCommand.Review` parser + parser tests + `ProcessBuildTestRunner` (the production `IBuildTestRunner`) + tests.

**Slice 9b (next session):** `TaskCommandHandler.HandleReviewAsync` + `Program.cs` wiring + handler tests. That's the bulk of the data-gathering glue.

### Code changes (uncommitted at flush time)

CLI:

- `src/InfiniContext.Cli/Tasks/TaskCommand.cs` - added `Review(int? Id, string? Scope, bool SkipConfirm)` discriminated record + `"review"` switch arm + `ParseReview` method. Accepts flags before or after the id (mirrors the `/task new` / `/task delete` before-or-after convention). Recognises `--yes` / `-y` and `--scope <glob>`; rejects unknown options, duplicate ids, and `--scope` without a value.

Core:

- `src/InfiniContext.Core/Review/ProcessBuildTestRunner.cs` (new) - production `IBuildTestRunner`. Shells out to `dotnet build` / `dotnet test` from the repo root via `Process.Start`. On non-zero exit, captures last 20 lines (or 2K chars, whichever smaller) of stderr (falling back to stdout) into `BuildTestOutcome.FailureSummary`. Linked CancellationTokenSource gives each invocation its own timeout budget; default 10 min for build, 15 min for test (long, because a tight budget on a slow CI host would false-positive the gate and revert good edits).
- `ProcessBuildTestRunnerOptions` record - `Solution` (default `InfiniContext.slnx`), `Configuration` (default `Release`), `BuildTimeoutSeconds`, `TestTimeoutSeconds`, `ExtraBuildArgs`, `ExtraTestArgs`.

Tests:

- `tests/InfiniContext.Tests/Cli/Tasks/TaskCommandTests.cs` - appended 11 review-parser tests: bare `/task review` defaults to all-nulls; explicit id; zero-padded id; `--yes` and `-y`; `--scope <glob>`; mixed flag-and-id order; rejects unknown option / missing scope value / two positional ids / non-numeric id.
- `tests/InfiniContext.Tests/Review/ProcessBuildTestRunnerTests.cs` (new) - 4 tests for option defaults, record `with` overrides, and argument validation (blank repo root throws on both Build and Test). No real `dotnet` spawn here - that's covered by step 12's dogfood run.

### One build-system note

`Task<string>.AsTask()` doesn't exist (the method I wrote was for ValueTask). Compiler caught it; fixed to drop the `.AsTask()` calls тАФ `ReadToEndAsync(CancellationToken)` already returns `Task<string>` directly.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet build src/InfiniContext.Cli -t:Compile` - green (full Cli build still blocked by the running CLI processes that lock Core.dll; using `-t:Compile` bypasses the copy step that fails).
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build --filter Review|Tasks|Cli.Tasks` - **496/496 passing**. No regressions.
- `dotnet format` - green on Core, Cli, and Tests.

### What's done vs what's left (vs. the 12-step plan)

Done: 1, 2, 3, 4, 5, 6, 7, 8, **9a** (parser + ProcessBuildTestRunner).

Not done: 9b (`TaskCommandHandler.HandleReviewAsync` + `Program.cs` wiring + handler tests), 10 (Settings), 11 (Skill bump 3 -> 4), 12 (Dogfood).

### Files touched this session

- `src/InfiniContext.Cli/Tasks/TaskCommand.cs` (added Review variant + parser branch + ParseReview)
- `src/InfiniContext.Core/Review/ProcessBuildTestRunner.cs` (new)
- `tests/InfiniContext.Tests/Cli/Tasks/TaskCommandTests.cs` (appended 11 tests)
- `tests/InfiniContext.Tests/Review/ProcessBuildTestRunnerTests.cs` (new)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 4 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 9a (CLI parser + ProcessBuildTestRunner).`

### Handoff to next session

Resume at **slice 9b**: the handler + wiring. Key work:

1. `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` - add a `HandleReviewAsync` branch. Steps the handler must do:
   - Resolve the task id (CLI arg if present; else focused session's `ActiveTaskId`; else refuse with a clear message).
   - Cost-guard prompt `[y/N]` unless `--yes`. Use whatever console-prompt helper the existing handler uses for similar cases (see how `/task delete` confirms, if it does).
   - Load `TaskRecord` via `TaskService.GetAsync`.
   - Build the `TaskFolder` from the record; read `description.md`, `progress.md`, `index.md` (best-effort - empty string when missing).
   - Read `CLAUDE.md` from `record.HomeCwd` (best-effort).
   - Read `docs/stage2/decisions.md` (or any single `docs/**/decisions.md` if path is uncertain) - best-effort.
   - Glob expansion for `--scope`: a minimal `*`/`**` matcher (or add `Microsoft.Extensions.FileSystemGlobbing` to `InfiniContext.Cli.csproj`).
   - Construct `ProcessGitRunner` + `ScopeResolver(byteCap = settings.review.perFileByteCap, default 200KB)`.
   - Run `ScopeResolver.ResolveAsync(homeCwd, record.BaseCommitSha, globResolvedPaths, progressMdPath)`.
   - Build `ReviewRequest`, call `_reviewService.RunReviewAsync(req, options)`.
   - Render via `ReportRenderer.Render(report)`.
   - Write `review-<YYYY-MM-DDTHHmmss>.md` to the task folder.
   - Acquire lock on `index.md` -> append row -> release. Acquire lock on `progress.md` -> append entry -> release.
   - Print a console summary: applied/rejected/awaiting counts + report file path.

2. `src/InfiniContext.Cli/Program.cs` - construct `CliReviewerAgentService`, `AutoApplyExecutor` (with `ProcessBuildTestRunner`), `ReviewService`. Thread `ReviewService` into `TaskCommandHandler` ctor.

3. Handler tests: confirmation prompt declines -> no service call; --yes skips prompt; refuses with clear message when no active task and no id given; refuses with clear message when not in a git repo (or task home_cwd doesn't exist).

This will be a sizeable commit тАФ possibly 200-300 lines of handler code plus tests. Should fit in one session if focused.

## 2026-05-17 (review-gate impl session - step 9b: handler + Program.cs wiring)

User said "go next" after the step-9a commit. Shipped slice 9b: `TaskCommandHandler.HandleReviewAsync` + `Program.cs` wiring + handler tests.

### Code changes (uncommitted at flush time)

CLI:

- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` - ctor extended with optional `ReviewService?`, `ScopeResolver?`, `TextReader? input`, `Func<DateTimeOffset>? clock`. Defaults to null/UtcNow so existing call sites still compile. New public `HandleReviewAsync(TaskCommand.Review cmd, int? activeTaskId, CancellationToken ct)` method does the whole review flow: refuse if subsystem null; resolve id from `cmd.Id ?? activeTaskId`; load `TaskRecord`; refuse on not-found or missing home_cwd; cost-guard prompt (with input reader) unless `--yes`; read description.md / progress.md / index.md / CLAUDE.md / decisions.md best-effort; resolve scope via `ScopeResolver`; build `ReviewRequest`; call `ReviewService.RunReviewAsync`; render via `ReportRenderer`; write `review-<ISO-timestamp>.md` to the task folder; print applied/rejected/awaiting summary.
- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` - HelpText updated with the `/task review` line. `HandleAsync`'s switch routes `TaskCommand.Review` to `HandleReviewAsync(cmd, activeTaskId: null, ct)` so non-slash-command callers still work.
- `src/InfiniContext.Cli/SlashCommands/TaskSlashCommand.cs` - intercepts `TaskCommand.Review` after parsing (mirrors Switch interception) and dispatches to `ctx.TaskHandler.HandleReviewAsync(reviewCmd, ctx.ActiveTaskId, ct)`. This is the only way the active task id flows into the review path.
- `src/InfiniContext.Cli/Program.cs` - new wiring block right where TaskCommandHandler is constructed: `CliReviewerAgentService` with `ReviewerAgentOptions.Model = settings.Models.Main.Id`; `AutoApplyExecutor` with a `ProcessBuildTestRunner` (defaults to `InfiniContext.slnx`, `Release`); `ReviewService` against the agent + executor; `ScopeResolver` with `ProcessGitRunner`. All threaded into `TaskCommandHandler` via the new optional ctor params, plus `input: Console.In` for the cost-guard prompt.

Tests (`tests/InfiniContext.Tests/Cli/Tasks/TaskCommandHandlerReviewTests.cs`, new): 12 tests, all green. Coverage: subsystem-null refusal; missing-id-and-active refusal; not-found refusal; missing home_cwd refusal; activeTaskId-fallback path (verifies 6-call sequence: 5 reviewers + coordinator); no-input-reader-without-yes refusal; `n` cancellation; empty-line cancellation; `y` proceed; `--yes` skips prompt; report artifact written to task folder; console summary with counts.

Tests use a real `TaskService` against in-memory `TaskStore` + temp filesystem (task folder really exists on disk for the handler to write into), a real `ReviewService` against a `FakeReviewer` (role inferred from system prompt content), and a `FakeGitRunner` for `ScopeResolver`. No real `claude` spawn.

### v1 follow-ups deferred and noted

- **Glob expansion for `--scope`**: v1 splits on `;` and treats each token as a literal relative path. Adding `Microsoft.Extensions.FileSystemGlobbing` (or a small inline `*`/`**` matcher) is a follow-up.
- **Lock-contract update of index.md + progress.md**: after writing the timestamped report, v1 doesn't auto-add an index row or progress entry. User has to do it manually if they want the artifact tracked.
- **`/task done` gate**: still postponed (todo.md section B). This step ships the *tool*; the *gate* is separate.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet build src/InfiniContext.Cli -t:Compile` - green (full build still blocked by running CLI processes locking Core.dll, same as prior sessions).
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build --filter Review|Tasks|Cli` - **661/661 passing**. No regressions.
- `dotnet format` - green on Cli and Tests (Core untouched in this slice).

### What's done vs what's left (vs. the 12-step plan)

Done: 1, 2, 3, 4, 5, 6, 7, 8, 9 (both 9a and 9b).

Not done: 10 (Settings: dedicated `review` section in InfiniContextSettings + JSON schema entries), 11 (Skill bump 3 -> 4 with a "Task review" subsection), 12 (Dogfood).

### Files touched this session

- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` (ctor extended; new HandleReviewAsync method; HelpText updated; ReadIfExistsAsync + ReadDecisionsAsync helpers)
- `src/InfiniContext.Cli/SlashCommands/TaskSlashCommand.cs` (intercept TaskCommand.Review)
- `src/InfiniContext.Cli/Program.cs` (Review subsystem wiring)
- `tests/InfiniContext.Tests/Cli/Tasks/TaskCommandHandlerReviewTests.cs` (new)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 4 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 9b (handler + Program.cs wiring).`

### Handoff to next session

Resume at step 10 (Settings). Small step (~50 lines):

1. `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` - add a `ReviewSettings` record under a new `Review` property on the root settings type. Fields: `ReviewerModel` (string?, null = inherit from `Models.Main.Id`), `CoordinatorModel` (string?, null = same as reviewer), `ConfidenceFloor` (double, default 0.9), `AutoApplyMaxLinesPerFinding` (int, 50), `AutoApplyMaxFilesPerFinding` (int, 3), `PerFileByteCap` (int, 204800), `InvocationTimeoutSeconds` (int, 600), `SkipBuildTestGate` (bool, false, tests-only).
2. `settings.schema.json` - mirror the new section so editor autocomplete works.
3. `settings.json` - add the section with defaults.
4. `src/InfiniContext.Cli/Program.cs` - thread settings into `ReviewerAgentOptions`, `ScopeResolver` (byteCap), and into `ReviewService` (when it grows options).

Then step 11 (Skill bump 3 -> 4): edit `InfiniContextTaskSkill.cs` to add a "Task review" subsection (one paragraph) and bump `Version` 3 -> 4. The skill installer will pick up the version diff on next host launch.

Then step 12 (Dogfood): run `/task review` on task 0003 itself in a real session. Iterate on the prompts based on observed findings. This is a behavioral pass, not code.

## 2026-05-17 (review-gate impl session - step 10: Settings)

User said "go next" after the step-9b commit. Shipped step 10: `ReviewSettings` record + settings.json/schema entries + threaded through Program.cs.

### Code changes (uncommitted at flush time)

Core:

- `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` - added `Review` property on the root `InfiniContextSettings` record (default `new ReviewSettings()`) and a new `ReviewSettings` record at the bottom of the file with all 8 design-spec fields: `ReviewerModel` (string?, null inherits Models.Main.Id), `CoordinatorModel` (string?, null = same as Reviewer), `ConfidenceFloor` (0.9), `AutoApplyMaxLinesPerFinding` (50), `AutoApplyMaxFilesPerFinding` (3), `PerFileByteCap` (204_800), `InvocationTimeoutSeconds` (600), `SkipBuildTestGate` (false, tests-only).
- Naming: `ReviewSettings` rather than `ReviewOptions` to avoid clash with `InfiniContext.Core.Review.ReviewOptions` (the orchestrator's per-run knobs). Documented in the type's summary.

Config:

- `settings.json` - new `review` section with the design's default values.
- `settings.schema.json` - new `$defs.review` entry + property on the root; describes each field with bounds (confidenceFloor 0..1; minimums on the integer guards).

CLI:

- `src/InfiniContext.Cli/Program.cs` - the review-wiring block now reads from `settings.Review`:
  - `reviewerModelId = settings.Review.ReviewerModel ?? settings.Models.Main.Id`
  - `ReviewerAgentOptions.InvocationTimeoutSeconds = settings.Review.InvocationTimeoutSeconds`
  - `ScopeResolver(byteCap: settings.Review.PerFileByteCap)`
- Settings not yet wired through: `CoordinatorModel` (orchestrator currently uses the reviewer agent for the coordinator role too - need to refactor ReviewService to accept a separate coordinator agent), `ConfidenceFloor` and the auto-apply size guards (currently the coordinator's prompt enforces them; defensive host-side checks are a v1 follow-up), `SkipBuildTestGate` (AutoApplyExecutor doesn't support this knob today). These all defer to v1 follow-ups.

Tests:

- `tests/InfiniContext.Tests/Configuration/InfiniContextSettingsLoaderTests.cs` - added 3 tests under a "ReviewSettings" section: defaults match the design; binds from a JSON file with every field overridden; omitted section keeps compile-time defaults.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet build src/InfiniContext.Cli -t:Compile` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite, no filter) - **867/870 passing** (3 macOS-only `[MacOnlyFact]` tests skipped on Windows as expected). No regressions.
- `dotnet format` - green on Core, Cli, and Tests.

### What's done vs what's left (vs. the 12-step plan)

Done: 1, 2, 3, 4, 5, 6, 7, 8, 9 (a+b), 10.

Not done:
- 11 (Skill bump 3 -> 4 with a "Task review" subsection in `InfiniContextTaskSkill.SkillMarkdown`).
- 12 (Dogfood: run `/task review` on task 0003; behavioral pass, not code).

V1 follow-ups (post-12, filed here):

- Wire `CoordinatorModel` end-to-end (requires splitting ReviewService's single `IReviewerAgentService` into reviewer + coordinator channels).
- Wire `ConfidenceFloor` + `AutoApplyMax*` as host-side defensive checks in AutoApplyExecutor (today only the coordinator's prompt enforces them).
- Wire `SkipBuildTestGate` as a knob on AutoApplyExecutor (skip both Build and Test gates when true).
- Glob expansion for `--scope` (currently splits on `;` literally).
- Lock-contract update of index.md + progress.md after writing the report.

### Files touched this session

- `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` (new Review property + ReviewSettings record)
- `settings.json` (new review section)
- `settings.schema.json` (new review $defs + property)
- `src/InfiniContext.Cli/Program.cs` (settings threaded into ReviewerAgentOptions + ScopeResolver byte cap)
- `tests/InfiniContext.Tests/Configuration/InfiniContextSettingsLoaderTests.cs` (3 new tests)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 5 source/test/config files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 10 (Settings).`

### Handoff to next session

Resume at **step 11** (Skill bump). Trivial:

1. `src/InfiniContext.Core/Tasks/Skill/InfiniContextTaskSkill.cs` - in `SkillMarkdown`, add a brief "Task review" subsection (one paragraph) telling the agent that `/task review` exists and that it should suggest running it before declaring a task done.
2. Bump `Version` 3 -> 4 (same file, top of the class).
3. The skill installer (`SkillInstaller.InstallIfMissingOrOutdatedAsync`) will detect the version diff on next host launch and rewrite `~/.claude/skills/infinicontext-task/SKILL.md`.

Then step 12 (Dogfood) - real-session pass with `/task review 0003 --yes` to see how the prompts behave on actual content. Likely outcomes: some false positives ("missing tests" when tests exist under a slightly unusual path; "TODO not addressed" when the TODO is in unrelated code), and some prompt tightening. Update `ReviewerPrompts.cs` strings + re-run. After 2-3 iterations, this surface is shippable.

## 2026-05-17 (review-gate impl session - step 11: Skill bump 3 to 4)

User said "go next" after the step-10 commit. Shipped step 11: skill version bump + "Task review" subsection in `InfiniContextTaskSkill.SkillMarkdown`. Trivial single-file change.

### Code changes (uncommitted at flush time)

Core:

- `src/InfiniContext.Core/Tasks/Skill/InfiniContextTaskSkill.cs`:
  - `Version` 3 -> 4.
  - Updated the type doc-comment to mention "v4 adds the 'Task review' section".
  - Added a new `## Task review` section in `SkillMarkdown`, inserted between "Issue hygiene" and "Host context-pressure nudges". One paragraph explaining what `/task review` does, plus three bullets:
    - "Don't run it yourself" (the user invokes; it costs real money - 6 spawned agents).
    - "Suggest it when the task feels done" (before saying "this looks complete", recommend the user run review).
    - "Acting on a report" - guidance for handling Awaiting / Applied / Rejected entries when the user hands the agent a `review-*.md`.

### Skill installer behavior on next host launch

- `SkillInstaller.InstallIfMissingOrOutdatedAsync` parses the version marker (`<!-- infinicontext-task-skill-version: N -->`) at the bottom of the on-disk SKILL.md.
- Existing on-disk v3 -> installer detects 3 < 4 -> rewrites `~/.claude/skills/infinicontext-task/SKILL.md` (the prior content is backed up as `SKILL.md.bak`).
- Next time the agent inspects a task-bearing cwd or the host's first user message names this skill, it'll see the new "Task review" subsection.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet test --no-build --filter SkillInstaller|InfiniContextTaskSkill` - 12/12 passing.
- `dotnet test --no-build` (full suite) - **867/870 passing** (3 macOS-only skips). No regressions.
- `dotnet format` - green.

### What's done vs what's left (vs. the 12-step plan)

Done: 1, 2, 3, 4, 5, 6, 7, 8, 9 (a+b), 10, 11.

Not done:
- 12 (Dogfood: run `/task review` on task 0003 itself; iterate on prompts).

### Files touched this session

- `src/InfiniContext.Core/Tasks/Skill/InfiniContextTaskSkill.cs` (Version 3 -> 4 + new "Task review" section)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 1 source file plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review step 11 (skill bump v3 -> v4).`

### Handoff to next session

Resume at step 12 (Dogfood). Behavioral pass, not code unless prompt-tightening surfaces issues:

1. Start a host session bound to task 0003 via `/task switch 0003`.
2. Run `/task review --yes`.
3. Wait for the 6 agents to complete. Inspect the resulting `review-<timestamp>.md` in `.task/0003-infinicontext-phase-2b-continuation-plan/`.
4. Look for false positives, weak findings, missing categories. Likely candidates: "tests missing" when tests exist under a non-standard path; "TODO unaddressed" when the TODO is in code outside the task's scope; over-reliance on the diff (a finding's location may not match the changed lines).
5. Tighten `ReviewerPrompts.cs` strings as needed (the v1 prompts were best-guess). Re-run.
6. After 2-3 iterations, the surface is shippable: commit a "Step 12: prompt tightening from dogfood" commit per iteration, then declare the review-gate feature v1 complete and update `docs/stage2/todo.md` section B accordingly.

If during dogfood the v1-deferred items become visibly painful (glob expansion, lock-contract update of index.md, separate coordinator model), promote them out of "follow-up" and into immediate post-12 work. Otherwise they stay deferred.

## 2026-05-17 (review-gate impl session - v1 follow-up: lock-contract update of index.md + progress.md)

User pushed the previous 10 commits and asked for a v1 follow-up. Shipped the lock-contract update so the review report is auto-discoverable in the task's `index.md` and a short summary lands in `progress.md` - clears the v1 follow-up I'd filed "lock-contract update of index.md + progress.md after writing report".

### Code changes (uncommitted at flush time)

CLI:

- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs`:
  - New optional ctor param `LockRegistry? lockRegistry`. Stored as `_lockRegistry`; per-instance `_lockOwnerId` = `"host-review-<8-hex>"`.
  - `HandleReviewAsync` now, after writing `review-<timestamp>.md`, calls two new helpers: append a row to `index.md` and append an entry to `progress.md`. Both go through `UpdateFileUnderLockAsync`.
  - `UpdateFileUnderLockAsync(path, transform, ct)`: when `_lockRegistry` is set, acquires (30s TTL, 5s max wait), reads, applies transform, writes, releases. When the registry is null (tests), writes directly. Lock-acquire timeout surfaces a soft warning to the user but isn't fatal - the review report itself is already on disk.
  - `AppendIndexRow(existing, reportName, report, today)` static internal helper: appends `| <name> | Review report (applied=A rejected=R awaiting=W) | <date> |` to the existing markdown. Handles missing trailing newline.
  - `AppendProgressEntry(existing, reportName, report, today)` static internal helper: appends a fresh `## YYYY-MM-DD (review)` heading + a bullet summarizing applied/rejected/awaiting counts and the build/test gate status. Always opens a fresh heading rather than parsing for an existing one for today; keeps writes additive.

Program:

- `src/InfiniContext.Cli/Program.cs`:
  - Moved `var lockRegistry = new LockRegistry(logger: logging.Logger);` up to right after `taskStore` is opened (was constructed later, after the task subsystem block). This is the host-scope singleton; both the review path and the issue path share it.
  - Threaded `lockRegistry` into the `TaskCommandHandler` ctor.
  - Removed the duplicate construction below the task block.

Tests:

- `tests/InfiniContext.Tests/Cli/Tasks/TaskCommandHandlerReviewTests.cs`:
  - `Fixture.OpenAsync` extended with `bool withLockRegistry = false` knob; constructs a real `LockRegistry` when requested.
  - 7 new tests:
    - `Appends_row_to_index_md_after_review_run` - end-to-end through the handler (with `withLockRegistry: true`); verifies `| review-` row + the summary substring appear and that existing content is preserved.
    - `Appends_entry_to_progress_md_after_review_run` - end-to-end; verifies `## YYYY-MM-DD (review)` heading + the summary bullet appear.
    - `Works_without_lock_registry_too` - the `_lockRegistry is null` fallback path; review still completes and index.md still gets the row.
    - `AppendIndexRow_adds_row_with_summary_and_date` - pure unit test on the static helper.
    - `AppendIndexRow_handles_existing_without_trailing_newline` - boundary case.
    - `AppendProgressEntry_inserts_h2_heading_and_summary_bullet` - pure unit test.
    - `AppendProgressEntry_surfaces_gate_reversion_reason` - branch when build/test gate failed.
  - New `MakeReport` + `MakeBlankFinding` helpers in the test class so we can construct `ReviewReport` instances with the right shape.

### Build/test snapshot

- `dotnet build src/InfiniContext.Cli -t:Compile` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite) - **874/877 passing** (3 macOS-only skips). No regressions.
- `dotnet format` - green on Cli and Tests.

### Status

12-step plan: 1-11 done in code; 12 (dogfood) is the user-driven behavioral pass.

V1 follow-ups status:

- [x] **Lock-contract update of index.md + progress.md** - shipped this session.
- [ ] Glob expansion for `--scope` (currently splits on `;` literally).
- [ ] Wire `CoordinatorModel` end-to-end (needs ReviewService refactor to split reviewer/coordinator channels).
- [ ] Wire `ConfidenceFloor` + `AutoApplyMax*` as host-side defensive checks in AutoApplyExecutor.
- [ ] Wire `SkipBuildTestGate` as a knob on AutoApplyExecutor.

### Files touched this session

- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` (new lock-aware update helpers + index/progress writes)
- `src/InfiniContext.Cli/Program.cs` (moved LockRegistry up; threaded into TaskCommandHandler)
- `tests/InfiniContext.Tests/Cli/Tasks/TaskCommandHandlerReviewTests.cs` (7 new tests + Fixture knob + MakeReport helper)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 3 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review - lock-contract update of index.md + progress.md.`

## 2026-05-17 (review-gate impl session - unlock auto-apply: CRLF + baseline-green gate)

User asked "why I don't see any auto-applied items?" after running. I diagnosed two practical blockers I'd surfaced in my own /review pass earlier this session:

1. **CRLF/LF mismatch on Windows checkouts.** `TryApplySingleAsync` did `current.IndexOf(fix.OldText, StringComparison.Ordinal)`. Windows files often have CRLF on disk (core.autocrlf=true); LLM-emitted `suggested_fix.old_text` is LF-only. Every multi-line Edit was demoting with "source diverged".
2. **No baseline-gate check.** Post-apply build/test gate ran unconditionally. If the repo's baseline build or tests were red BEFORE the review (common during in-progress work), the post-apply gate would also be red and revert every edit blaming our changes. Net effect: applied=0 nearly always.

### Code changes (uncommitted at flush time)

Core:

- `src/InfiniContext.Core/Review/AutoApplyExecutor.cs`:
  - New baseline-gate check at the top of `ApplyAsync`. When `findings.Count > 0`, runs `BuildAsync` then `TestAsync` BEFORE applying anything. Either failing demotes every finding with a clear `"baseline build was red"` / `"baseline tests were red"` reason (including the failure summary from the runner) and returns immediately with `BuildTestGatePassed: false`. Pays the cost of two extra gate runs but only when there's something to auto-apply.
  - New `NormalizeLineEndingsToFile(current, oldText, newText)` internal static. Heuristic: if the file contains any `\r\n`, treat it as CRLF and upgrade lone `\n` in `OldText`/`NewText`; otherwise downgrade any `\r\n` to `\n`. Two-step replace avoids `\r\r\n` artefacts. Mixed-line-ending files in a single .cs file lose; documented as acceptable v1 trade-off.
  - `TryApplySingleAsync` Edit branch now calls `NormalizeLineEndingsToFile` and uses the adjusted texts for both the match and the write. Behavior is byte-identical when file and texts already agree (the common case).
  - Also added: `LooksAlreadyCrlf(string)` helper that counts paired `\n`/`\r\n` so we don't double-convert.

Tests:

- `tests/InfiniContext.Tests/Review/AutoApplyExecutorTests.cs`:
  - **FakeRunner refactor**: added `BuildOutcomes` and `TestOutcomes` init-only arrays. When set, each call dequeues by index (first call gets index 0, etc.); calls beyond the sequence return the last entry. When null, the existing single-outcome semantics apply. This lets tests express "baseline green, post-apply red" semantics that the new baseline check requires.
  - **Updated existing tests** to use the sequence pattern: `ApplyAsync_reverts_when_build_gate_fails` and `ApplyAsync_reverts_when_test_gate_fails` both now declare `[green, red]` so the baseline check passes and the post-apply check fails (preserving original test intent). Same for `ApplyAsync_reverts_a_replace_file_that_created_a_new_file_by_deleting_it` and `ApplyAsync_reverts_every_touched_file_when_gate_fails_after_multiple_applies`.
  - **New tests for the baseline check** (3): refuses when baseline build red (with the failure summary in the demotion note); refuses when baseline tests red; skips baseline check entirely when findings list is empty (no spurious cost when there's nothing to apply).
  - **New tests for CRLF normalization** (6): edit applies when file has CRLF and OldText has LF (the Windows common case); edit applies when file has LF and OldText has CRLF (inverse); multi-line NewText with LF preserves the file's CRLF after write; three pure unit tests on `NormalizeLineEndingsToFile` covering all three branches.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite) - **883/886 passing** (3 macOS-only skips). 9 new tests in AutoApplyExecutorTests; no regressions.
- `dotnet format` - green on Core and Tests.

### Practical effect

These two fixes are what unlocks auto-applied items in actual review runs:

- Reviewers that emit LF-only `suggested_fix.old_text` (i.e. all of them) will now match against CRLF source files on Windows.
- The user gets a clear refusal up front when their working tree's baseline is red, rather than a misleading "tests broke after auto-apply" demotion that blamed our edits for pre-existing failures.

Remaining v1 follow-ups (not addressed this session):

- Cancellation-safe revert (issue #2 from the /review pass): RevertAsync should use `CancellationToken.None` once committed to reverting.
- Schema-advertised settings that silently no-op (issue #3): `coordinatorModel`, `confidenceFloor`, `autoApplyMax*`, `skipBuildTestGate`.
- Report scope-resolver secret-file exclusion (issue #4).
- Same-day `## YYYY-MM-DD (review)` heading collapse in progress.md.
- Empty-scope short-circuit (issue #7).
- Glob expansion for `--scope`.
- `CoordinatorModel` channel split.

### Files touched this session

- `src/InfiniContext.Core/Review/AutoApplyExecutor.cs` (baseline gate + CRLF normalization)
- `tests/InfiniContext.Tests/Review/AutoApplyExecutorTests.cs` (FakeRunner sequence + 9 new tests + 4 updated tests)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 2 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review - unblock auto-apply (CRLF + baseline gate).`

## 2026-05-17 (review-gate impl session - cancellation-safe revert)

User said "continue" after the CRLF + baseline-gate commit. Picked the next v1 follow-up from my own /review pass: Critical #2 - cancellation-safe revert.

### The bug

`RevertAsync` honored the caller's CancellationToken on every `File.WriteAllBytesAsync(path, original, ct)`. When the user hit Ctrl-C (or any other path canceled the CT) AFTER the executor had committed to reverting, the first `WriteAllBytesAsync` would fail-fast with OperationCanceledException; the try/catch swallowed it; the foreach moved to the next file; the next `WriteAllBytesAsync` also fail-fasted; ... net result: zero files were restored even though the executor's promise was to revert all of them on gate failure. The working tree silently kept its mid-flight edits.

### Fix

- `RevertAsync` no longer takes a CancellationToken parameter. Internally it passes `CancellationToken.None` to `File.WriteAllBytesAsync`, so canceled caller tokens don't propagate to the revert writes.
- Per-file IO failures (file locked, disk full, etc.) are still tolerated via the existing try/catch - one wedged file doesn't abort the rest of the rollback.
- Both call sites in `ApplyAsync` updated to `RevertAsync(backups)` (no ct).
- Comment in `RevertAsync` records the rationale: "once we've committed to reverting, do NOT honor caller cancellation. A partial revert leaves the working tree in a mixed state worse than the gate failure itself."

### Test that proves the fix

`ApplyAsync_completes_full_revert_even_when_token_cancels_during_gate` (new) sets up three files, three findings, and a `CancellingRunner` whose second `TestAsync` call cancels the CT before returning red. Without the fix: file `src/A.cs` is left at `AAA-edited` because the first revert write fail-fasts (verified by temporarily reverting the fix - test failed with `AAA-edited` instead of `AAA-original`). With the fix: all three files restored to their originals; result demoted with `BuildTestGatePassed=false`; Demoted count = 3.

The test is in the same `AutoApplyExecutorTests` file alongside the `CancellingRunner` helper. The test was empirically verified to fail without the fix and pass with it, with a force-rebuild + targeted rerun.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite) - **884/887 passing** (3 macOS-only skips). 1 new test in AutoApplyExecutorTests (now 29 in that class); no regressions.
- `dotnet format` - green on Core and Tests.

### Remaining v1 follow-ups (in priority order)

- Schema-advertised settings that silently no-op: `coordinatorModel`, `confidenceFloor`, `autoApplyMax*`, `skipBuildTestGate`.
- Secret-file exclusion in `ScopeResolver` (defense against leaking `.env` / `*.key` / `settings.user.json` into reviewer prompts and the on-disk report).
- Same-day `## YYYY-MM-DD (review)` heading collapse in `progress.md` (skill says "Group entries under day headings", multiple runs/day currently violate).
- Empty-scope short-circuit (don't spawn 6 agents when there are 0 files to review).
- Glob expansion for `--scope` (currently splits on `;` literally).
- `CoordinatorModel` channel split (ReviewService refactor).

### Files touched this session

- `src/InfiniContext.Core/Review/AutoApplyExecutor.cs` (RevertAsync drops ct param; uses CancellationToken.None internally; both call sites updated)
- `tests/InfiniContext.Tests/Review/AutoApplyExecutorTests.cs` (new test + CancellingRunner helper)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 2 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review - cancellation-safe revert.`

## 2026-05-17 (review-gate impl session - wire AutoApply settings end-to-end)

User said "continue". Picked the next follow-up: stop the silently-no-op `ReviewSettings.AutoApply*` and `SkipBuildTestGate` fields. These were advertised in the schema and `settings.json` but ignored by the executor at runtime - a user setting `confidenceFloor: 0.99` would get false confidence that they were tightening the gate.

### What's wired now

`AutoApplyExecutor` accepts a new `AutoApplyOptions` record. Settings тЖТ options translation lives in `Program.cs`:

- `ConfidenceFloor` (default 0.9): each `ConsolidatedFinding`'s `Confidence` AND the coordinator's `DispositionConfidence` must both meet or exceed this. Below-floor findings demote to NeedsUser with a specific reason citing the actual value vs the floor.
- `MaxLinesPerFinding` (default 50): combined line count of `OldText` + `NewText` is bounded. Demote with `"fix touches X lines, exceeds MaxLinesPerFinding Y"`.
- `MaxFilesPerFinding` (default 3): currently a no-op at runtime since v1 fix kinds (Edit, ReplaceFile) only touch one file each; reserved for a future multi-file fix kind. Ctor still validates it's positive.
- `SkipBuildTestGate` (default false): when true, both the baseline gate AND the post-apply build+test gate are bypassed. The fix applies; `BuildAsync`/`TestAsync` are never called. Used by tests that exercise the apply path without spawning `dotnet`. Production should never set this.

`coordinatorModel` from `ReviewSettings` is **still no-op** - wiring it requires a `ReviewService` refactor to split the single `IReviewerAgentService` into reviewer + coordinator channels. Filed as remaining v1 follow-up.

### Code changes (uncommitted at flush time)

Core:

- `src/InfiniContext.Core/Review/IAutoApplyExecutor.cs` - new `AutoApplyOptions` record placed next to the interface (its consumer). Public ctor params validated by `AutoApplyExecutor`.
- `src/InfiniContext.Core/Review/AutoApplyExecutor.cs`:
  - Ctor: new optional `AutoApplyOptions? options = null` parameter; validates `ConfidenceFloor` in [0,1] and positive `MaxLines*`/`MaxFilesPerFinding`.
  - `ApplyAsync`: baseline-gate check now also guarded by `!_options.SkipBuildTestGate`; after the apply phase, if `SkipBuildTestGate` is true the post-apply gate path is skipped (return as-is with `BuildTestGatePassed=true`).
  - `TryApplySingleAsync`: new defensive checks BEFORE writing - reviewer confidence below floor тЖТ demote; coordinator disposition_confidence below floor тЖТ demote; combined `CountLines(OldText) + CountLines(NewText)` > `MaxLinesPerFinding` тЖТ demote. Each demotion carries a specific note with the actual value vs the floor/limit.
  - `CountLines(string?)`: helper. Null/empty тЖТ 0; otherwise `1 + count('\n')`. CRLF counted as one break (does not double-count via `\r`).

CLI:

- `src/InfiniContext.Cli/Program.cs`: when constructing `AutoApplyExecutor`, builds an `AutoApplyOptions` from `settings.Review` fields (`ConfidenceFloor`, `AutoApplyMaxLinesPerFinding`, `AutoApplyMaxFilesPerFinding`, `SkipBuildTestGate`).

Tests:

- `tests/InfiniContext.Tests/Review/AutoApplyExecutorTests.cs`:
  - 4 tests on the confidence + size gates: reviewer-confidence-below-floor demotes; coordinator-disposition-confidence-below-floor demotes; multi-line OldText/NewText exceeding `MaxLinesPerFinding` demotes; boundary case (confidence == floor) applies.
  - 1 test on `SkipBuildTestGate=true`: runner returns red for build AND test, but the executor never calls into it, so the fix still applies. Asserts `BuildCalls == 0`, `TestCalls == 0`.
  - 5 new ctor-validation tests: out-of-range `ConfidenceFloor` (both bounds), non-positive `MaxLinesPerFinding`, non-positive `MaxFilesPerFinding`.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet build src/InfiniContext.Cli -t:Compile` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite) - **894/897 passing** (3 macOS-only skips). 10 new tests in AutoApplyExecutorTests (now 39 in that class); no regressions.
- `dotnet format` - green on Core, Cli, and Tests.

### Remaining v1 follow-ups

- Secret-file exclusion in `ScopeResolver` (defense against leaking `.env` / `*.key` / `settings.user.json` into reviewer prompts and the on-disk report).
- Same-day `## YYYY-MM-DD (review)` heading collapse in `progress.md`.
- Empty-scope short-circuit (don't spawn 6 agents when there are 0 files to review).
- Glob expansion for `--scope`.
- `CoordinatorModel` channel split (ReviewService refactor).

### Files touched this session

- `src/InfiniContext.Core/Review/IAutoApplyExecutor.cs` (new AutoApplyOptions record)
- `src/InfiniContext.Core/Review/AutoApplyExecutor.cs` (ctor accepts options; baseline gate / post-apply gate / per-finding checks honor options)
- `src/InfiniContext.Cli/Program.cs` (settings -> options translation)
- `tests/InfiniContext.Tests/Review/AutoApplyExecutorTests.cs` (10 new tests)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 4 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review - wire AutoApply settings end-to-end.`

## 2026-05-17 (review-gate impl session - secret-file exclusion in ScopeResolver)

User said "continue". Picked the next follow-up: defense against secret-bearing files leaking into reviewer prompts and the on-disk report. This was Critical-adjacent in the /review pass (issue #4) - the report's `<details>` blocks dump raw reviewer outputs verbatim, so any secret pulled into a reviewer's prompt lands on disk and travels with the artifact.

### What's guarded

`ScopeResolver.LooksLikeSecret(relativePath)` flags paths whose FILENAME (not full path - so `config/prod/.env` is also caught) matches a conservative list:

- **Exact filenames:** `.env`, `settings.user.json`, `id_rsa`, `id_dsa`, `id_ecdsa`, `id_ed25519`.
- **Filename prefixes:** `.env.` (catches `.env.local`, `.env.production`, etc.).
- **Extensions:** `.key`, `.pem`, `.pfx`, `.p12`, `.secret`, `.secrets`.

The check happens in `ReadFilesAsync` AFTER the empty-path guard and BEFORE `Path.GetFullPath` / `File.ReadAllTextAsync`. Matching paths go to `ExcludedFiles` with reason `"matches secret-file pattern (not loaded into reviewer prompts)"`. The file's content is never read from disk - even if it exists and is under the byte cap.

### Path coverage

- **Diff-based scope:** `git diff <base>..HEAD` could surface a `.env` if the user accidentally tracked one; resolver excludes.
- **progress.md "Files touched":** parsed via `ParseFilesTouched`, then unioned with the diff result. Goes through `ReadFilesAsync`, excluded the same way.
- **`--scope` override:** the CLI flag CAN'T bypass the guard. A user explicitly typing `--scope .env` still hits `ReadFilesAsync` and the secret file is excluded with the reason. Explicit test verifies this.

The exclusion does NOT depend on git's `.gitignore` - we don't shell out to `git check-ignore`. Heuristic-only for v1.

### Code changes (uncommitted at flush time)

Core:

- `src/InfiniContext.Core/Review/ScopeResolver.cs`:
  - New public static `LooksLikeSecret(relativePath)` - filename-based heuristic returning true for any path matching the secret-name / secret-prefix / secret-extension lists.
  - Three private static arrays: `SecretFilenames`, `SecretFilenamePrefixes`, `SecretExtensions`. Conservative defaults.
  - `ReadFilesAsync`: new early-exit branch right after the empty-path check. When `LooksLikeSecret(trimmed)` is true, the path goes straight to `ExcludedFiles` with the secret-pattern reason and the loop continues without reading.

Tests:

- `tests/InfiniContext.Tests/Review/ScopeResolverTests.cs`:
  - 12 theory inlines for `LooksLikeSecret` positives: `.env` in various positions, `.env.production`, `.env.local`, `*.key`, `*.pem`, `*.pfx`, `*.secret`, `*.secrets`, `settings.user.json`, `id_rsa`, `id_ed25519`.
  - 7 theory inlines for `LooksLikeSecret` negatives: `src/Foo.cs`, `key.cs` (extension only matches when the file's extension is exactly `.key`), `env.txt` (no leading dot), `settings.json` (only user-flavoured variant matches), `README.md`, `src/keyboard.cs`, empty string.
  - `ResolveAsync_excludes_secret_files_from_the_diff_result`: 3-file diff (one normal, two secrets); only the normal file appears in `Files`; both secrets in `ExcludedFiles` with the right reason.
  - `ResolveAsync_excludes_secrets_even_when_passed_via_scope_override`: user-supplied `--scope .env` is refused; demonstrates the guard isn't bypassable.
  - `ResolveAsync_secret_file_content_is_never_read_from_disk`: writes a `.env` with a sentinel value, runs the resolver, asserts the sentinel doesn't appear anywhere in `Files` content (defensive тАФ the file is excluded so the assertion is on `Files` being empty, but the sentinel check guards against any future refactor that might accidentally read the content).

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite) - **916/919 passing** (3 macOS-only skips). 22 new tests in ScopeResolverTests (now 38 in that class); no regressions.
- `dotnet format` - green on Core and Tests.

### What's not in v1 (deferred follow-ups within secret-exclusion)

- Configurable patterns via `ReviewSettings.ExcludedFilePatterns`. v1 hardcodes a conservative list; users can't add `*.test-credential` or `**/secrets/*.json` yet. Add when the list of false-positives / missed-positives surfaces in dogfood.
- `.gitignore` integration. The host has no `git check-ignore` plumbing; the secret guard is independent. If a project's `.gitignore` adds new secret shapes, the resolver won't know.

### Remaining v1 follow-ups (top of list)

- Same-day `## YYYY-MM-DD (review)` heading collapse in `progress.md`.
- Empty-scope short-circuit (don't spawn 6 agents when 0 files in scope).
- Glob expansion for `--scope`.
- `CoordinatorModel` channel split (ReviewService refactor; biggest of these).

### Files touched this session

- `src/InfiniContext.Core/Review/ScopeResolver.cs` (LooksLikeSecret + guard in ReadFilesAsync)
- `tests/InfiniContext.Tests/Review/ScopeResolverTests.cs` (22 new tests)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 2 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review - secret-file exclusion in ScopeResolver.`

## 2026-05-17 (review-gate impl session - same-day heading collapse in progress.md)

User pushed and said "continue". Picked the next follow-up: collapse multiple `/task review` runs on the same day under one `## YYYY-MM-DD (review)` heading instead of opening a new heading per run. The previous behavior violated the skill's "Group entries under day headings" guidance for high-frequency review use.

### Behavior change

`AppendProgressEntry` now:

1. Searches for an existing `## YYYY-MM-DD (review)` heading (exact-match on the trimmed line so user-written same-day headings like `## 2026-05-17 (impl session)` don't collide with the auto-emitted one).
2. If found: appends the new bullet at the end of that section (just before the next `## ` or `# ` heading, or EOF). Trailing blank lines inside the section get squeezed so the bullet sits cleanly.
3. If not found: opens a fresh `## YYYY-MM-DD (review)` heading at the end of the file (the prior behavior).

Also fixed: line-ending preservation. The function now detects the file's existing convention (CRLF vs LF) and uses the same separator for any appended content - no mixing CRLF/LF in a single file. Previously `StringBuilder.AppendLine` used `Environment.NewLine` unconditionally; on Windows that wrote CRLF into LF files.

### Code changes (uncommitted at flush time)

CLI:

- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs`:
  - `AppendProgressEntry` rewritten: split-on-newline тЖТ search-for-heading тЖТ insert-bullet-at-section-end (or append-fresh-heading at EOF). Uses `lines.Split(newline)` and `string.Join(newline, lines)` to preserve CRLF/LF.
  - Doc-comment updated to describe the collapse + line-ending behavior.

Tests:

- `tests/InfiniContext.Tests/Cli/Tasks/TaskCommandHandlerReviewTests.cs`:
  - `AppendProgressEntry_collapses_under_existing_same_day_heading`: two runs same day тЖТ one heading, both bullets under it.
  - `AppendProgressEntry_inserts_bullet_before_next_heading_when_section_is_not_last`: today's heading exists with a later-dated heading after it; new bullet lands inside today's section, not at EOF, not in the later section.
  - `AppendProgressEntry_does_not_reuse_a_review_heading_from_a_different_date`: yesterday's review heading + today's run тЖТ both headings exist, no collision.
  - `AppendProgressEntry_does_not_collide_with_user_written_same_day_session_headings`: user-written `## 2026-05-17 (impl session)` stays untouched; the auto path opens a separate `## 2026-05-17 (review)`.
  - `AppendProgressEntry_preserves_crlf_line_endings`: feed a CRLF file in, assert no lone-LF appears in the output.

### Build/test snapshot

- `dotnet build src/InfiniContext.Cli -t:Compile` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite) - **921/924 passing** (3 macOS-only skips). 5 new tests; no regressions.
- `dotnet format` - green on Cli and Tests.

### Remaining v1 follow-ups

- Empty-scope short-circuit (don't spawn 6 agents when 0 files in scope).
- Glob expansion for `--scope`.
- `CoordinatorModel` channel split (ReviewService refactor; biggest of these).

### Files touched this session

- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` (AppendProgressEntry rewritten)
- `tests/InfiniContext.Tests/Cli/Tasks/TaskCommandHandlerReviewTests.cs` (5 new tests)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 2 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review - same-day progress.md heading collapse.`

## 2026-05-17 (review-gate impl session - empty-scope short-circuit)

User pushed and said "continue". Next follow-up: when scope resolution yields 0 files, refuse the review with a clear message - don't spawn 6 agents to review nothing.

### Behavior change

In `TaskCommandHandler.HandleReviewAsync`, the ordering is now:

1. Load task record, validate home_cwd.
2. **Resolve scope.** (Moved up from after the cost prompt.)
3. If `scope.Files.Count == 0`: print a clear message + the list of excluded files + a hint, return. No cost prompt, no agents.
4. Cost prompt (unless `--yes`).
5. Gather inputs (description.md, progress.md, CLAUDE.md, etc.).
6. Spawn 5 reviewers + 1 coordinator.

The cost prompt now also includes the resolved file count (`Scope: N files.`) so the user has the right context to decide.

### Hint wording

- Without `--scope`: "Pass --scope <path[;path]> to override, or check that the task's base_commit_sha is correct." (Helps users who hit zero-files because base_commit_sha equals HEAD or merge-base wasn't usable.)
- With `--scope`: "Every --scope path was excluded or unreadable." (User already supplied paths; pointing them to --scope wouldn't help.)
- The scope's `FallbackNote` (when base_commit_sha was null and merge-base was used) is also surfaced regardless of zero/non-zero.

### Code changes (uncommitted at flush time)

CLI:

- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs`:
  - `HandleReviewAsync` reordered. Scope resolution moves up before the cost prompt. New `if (scope.Files.Count == 0)` block emits the message-and-return path. Scope's FallbackNote is now surfaced as a `note:` line regardless of zero/non-zero.
  - Cost prompt message updated to include `Scope: N files.`

Tests:

- `tests/InfiniContext.Tests/Cli/Tasks/TaskCommandHandlerReviewTests.cs`:
  - **Fixture refactor.** `Fixture.OpenAsync(emptyDiff: false)` now defaults to seeding `src/Foo.cs` on disk AND configuring the FakeGitRunner to report it in `DiffResult`. This means the 11 existing tests that depend on reviewers spawning continue to work without any per-test changes. New tests opt into empty-scope behavior with `emptyDiff: true`.
  - `FakeGitRunner` gained a settable `DiffResult` property.
  - 3 new tests:
    - `Refuses_with_clear_message_when_scope_has_zero_files`: empty diff тЖТ "nothing to review (0 files in scope)" + the "Pass --scope" hint + no agents spawned.
    - `Empty_scope_short_circuit_runs_before_the_cost_prompt`: even without `--yes`, the empty-scope path doesn't trigger the `[y/N]` prompt - users shouldn't confirm a spawn that will never happen.
    - `Empty_scope_with_user_supplied_scope_shows_a_scope_specific_message`: when the user explicitly passed `--scope ghost.cs`, the hint reflects "Every --scope path was excluded or unreadable" rather than pointing at the `--scope` flag they already used.

### Build/test snapshot

- `dotnet build src/InfiniContext.Cli -t:Compile` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite) - **924/927 passing** (3 macOS-only skips). 3 new tests; no regressions.
- `dotnet format` - green on Cli and Tests.

### Remaining v1 follow-ups

- Glob expansion for `--scope` (currently splits on `;` and treats each as a literal path).
- `CoordinatorModel` channel split (ReviewService refactor; the biggest remaining piece).

### Files touched this session

- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` (reordered scope resolution before cost prompt; new empty-scope short-circuit block)
- `tests/InfiniContext.Tests/Cli/Tasks/TaskCommandHandlerReviewTests.cs` (Fixture defaults seed a non-empty scope; FakeGitRunner DiffResult settable; 3 new tests)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 2 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review - empty-scope short-circuit.`

## 2026-05-17 (new top-level `/flush` slash command)

User requested a new `/flush` command to "forcely flush all task data ... to finish some actions or prepare to split the task". Shipped as the user-triggered analogue of the context-pressure hard-threshold injector.

### Design

`/flush` is a top-level slash command. It injects a synthetic user turn into the focused session telling the agent to:

1. Update `progress.md` with where you are and what's left (lock-aware).
2. Update `index.md` if any new artifacts exist (lock-aware).
3. Acknowledge with `[flushed]` and stop.

The wording mirrors the language the `infinicontext-task` skill already uses for the hard-threshold nudge ("flush now ... update progress.md with where you are and what's left, then exit"), so the agent recognises the intent without needing skill changes.

The session stays open after the agent flushes. The user can then `/quit` (closes the host) or `/task switch` to a fresh session against the same task - the "prepare to split" workflow.

`/flush` refuses when the focused session has no bound task. There's no `.task/` folder to flush to, so the command would be meaningless. Mirrors `/issue`'s refusal pattern.

### Code changes (uncommitted at flush time)

CLI:

- `src/InfiniContext.Cli/SlashCommands/FlushSlashCommand.cs` (new) - implements `ISlashCommand`. Takes no arguments. Checks `ctx.ActiveTaskId`, refuses if null. Sends the canned nudge via `ctx.Session.SendUserMessageAsync` and prints `[flush] requested for task NNNN; the agent will acknowledge with "[flushed]" when done.`
- `FlushNudgeTemplate` is exposed as `internal const string` so tests can pin the invariants (mentions `progress.md`, `index.md`, lock-aware, `[flushed]`, "do not start new work").
- `src/InfiniContext.Cli/Ui/MultiSessionHost.cs` - registered `new FlushSlashCommand()` in the slash-command registry list (right after `IssueSlashCommand`).

Tests:

- `tests/InfiniContext.Tests/Cli/SlashCommands/FlushSlashCommandTests.cs` (new) - 6 tests, stand-alone (no `SessionManager` needed since `/flush` works against `ctx.Session` directly):
  - Refuses when `ActiveTaskId` is null.
  - Sends nudge with the task id substituted (verifies the body contains `0042`, `progress.md`, `index.md`, `[flushed]`; verifies the console message includes "task 0042").
  - Pads the task id to 4 digits (id=7 -> "0007" in both nudge and console).
  - Refuses when arguments are provided ("[flush] this command takes no arguments").
  - Surfaces dispatch failure cleanly when `SendUserMessageAsync` throws ("[flush] dispatch failed: pipe closed").
  - `FlushNudgeTemplate` invariants test: pins the template's references to the load-bearing strings so a future edit can't silently drift the nudge wording.

Test seam uses a minimal `FakeSession` modeled on the one in `BroadcastCommandTests.cs` (records sent messages, has `SendThrows` for the failure path).

Test csproj:

- `tests/InfiniContext.Tests/InfiniContext.Tests.csproj` - added `<Compile Include="..\..\src\InfiniContext.Cli\SlashCommands\FlushSlashCommand.cs" Link="..." />` so the test assembly can instantiate the `internal sealed class FlushSlashCommand` directly (same pattern every other `SlashCommand` test file uses).

### Build/test snapshot

- `dotnet build src/InfiniContext.Cli -t:Compile` - green.
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite) - **930/933 passing** (3 macOS-only skips). 6 new tests; no regressions.
- `dotnet format` - green on Cli and Tests.

### What's intentionally NOT in v1

- **`--end` flag** to auto-close the session after the agent flushes. The host has no per-session-close action today (only `RequestExit` which closes the whole host). Adding one is a separate piece of plumbing; deferred. Users who want to split a task can manually `/quit` after `[flushed]`, or use `/task switch <id>` if `RequestSwitch` is plumbed for their context.
- **Skill update** documenting the `/flush` command. The agent already knows what to do (matches the existing hard-threshold language); no skill bump needed.

### Remaining `/task review` v1 follow-ups

- Glob expansion for `--scope`.
- `CoordinatorModel` channel split.

### Files touched this session

- `src/InfiniContext.Cli/SlashCommands/FlushSlashCommand.cs` (new)
- `src/InfiniContext.Cli/Ui/MultiSessionHost.cs` (register the command)
- `tests/InfiniContext.Tests/Cli/SlashCommands/FlushSlashCommandTests.cs` (new)
- `tests/InfiniContext.Tests/InfiniContext.Tests.csproj` (link the new source file into the test assembly)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 4 source/test files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Add /flush slash command for user-triggered task-data flush.`

## 2026-05-17 (status-check session)

Fresh session bound to task 0003. User asked "what's next?", got the summary, then immediately hit a soft context-pressure handoff (~73%) and asked for a flush before any work started.

### What happened

- Read `index.md`, the full `progress.md` (1488 lines, in three chunks), and the relevant slice of `docs/stage2/todo.md` to summarize remaining work.
- Reported back the queue: two `/task review` v1 follow-ups (glob expansion for `--scope`; CoordinatorModel channel split via a ReviewService refactor), plus step 12 (dogfood `/task review 0003 --yes`), plus the orthogonal `ci-macos.yml` workflow_dispatch.
- Recommended glob expansion as the next pick: small, self-contained, user-visible. CoordinatorModel split is bigger and is easier to scope correctly *after* dogfood reveals whether a separate coordinator model is wanted in practice.

### No code changes

Working tree clean at session start; clean at exit. The prior "Not committed yet" subsections in this file are historical тАФ `git log` shows everything through `1b8425f Add /flush slash command ...` is on `main`.

### Handoff to next session

User left the next pick open. Standing recommendation: **glob expansion for `--scope`**. The change is local to `TaskCommandHandler.HandleReviewAsync` (today it splits `cmd.Scope` on `;` and feeds literal tokens to `ScopeResolver`) plus a small glob helper. Two viable shapes:

- Add `Microsoft.Extensions.FileSystemGlobbing` (a `Matcher` over the repo root, ~well-tested, one new dep on `InfiniContext.Cli.csproj`); or
- Write a minimal in-line `*` / `**` matcher (~30 LOC + tests, no new dep).

Behind that, in priority order: dogfood (step 12), then CoordinatorModel split.

### Why exiting now

Soft context-pressure threshold (~73%). Most of the budget went to reading the 1488-line `progress.md`. A fresh session can pick up glob expansion with only the tail of `progress.md` + `TaskCommandHandler.HandleReviewAsync` in context.

## 2026-05-17 (review-gate impl session - glob expansion for `--scope`)

User said "move next" after the soft handoff. Pushed through and shipped the next v1 follow-up: glob expansion for `/task review --scope` so users can pass patterns like `--scope "src/**/*.cs"` instead of literal paths.

### Design choice

Used `Microsoft.Extensions.FileSystemGlobbing` 9.0.0 (added as a package ref to `InfiniContext.Cli.csproj`). Rationale: same 9.0.0 line as the other `Microsoft.Extensions.*` packages already in `InfiniContext.Core`; well-tested `Matcher` API handles `*` / `**` semantics correctly; avoids hand-rolling a glob matcher that could go wrong in subtle ways. The matcher's case sensitivity is set per-OS to mirror `ScopeResolver.PathComparer` (Windows: OrdinalIgnoreCase; others: Ordinal).

Tokens with no `*` pass through unchanged - preserves the v1 literal-path behaviour and lets `ScopeResolver` surface "file does not exist" for typos. `?` is NOT a wildcard in `M.E.FileSystemGlobbing.Matcher` (verified by a test that produced an empty match against `src/?.cs`); the helper treats it as a literal character. Doc-comment notes this explicitly so a future reader doesn't think `?` is supported.

### Code changes (uncommitted at flush time)

CLI:

- `src/InfiniContext.Cli/InfiniContext.Cli.csproj` - new `Microsoft.Extensions.FileSystemGlobbing` 9.0.0 package ref.
- `src/InfiniContext.Cli/Tasks/ScopeGlobExpander.cs` (new) - `internal static` helper. Single public method `Expand(string? scopeArg, string repoRoot) -> IReadOnlyList<string>`. Splits on `;`, routes each token through either pass-through (no `*`) or `Matcher` (has `*`). Dedup via per-OS string comparer. Returns relative paths with forward-slash separators.
- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` - `HandleReviewAsync` now calls `ScopeGlobExpander.Expand(cmd.Scope, record.HomeCwd)` instead of `cmd.Scope.Split(';', ...)`. Doc-comment updated to describe the new semantics.

Tests:

- `tests/InfiniContext.Tests/InfiniContext.Tests.csproj` - added the mirror `Microsoft.Extensions.FileSystemGlobbing` package ref (because `ScopeGlobExpander.cs` is link-compiled into the test assembly) + a `<Compile Include="..\..\src\InfiniContext.Cli\Tasks\ScopeGlobExpander.cs">` line.
- `tests/InfiniContext.Tests/Cli/Tasks/ScopeGlobExpanderTests.cs` (new) - 13 tests, all green. Coverage: null/blank input returns empty; literal path passes through; multi-token literal preserves order; backslash normalisation; single-star matches one directory only; double-star matches recursively; mixed literal + glob; wildcard with no matches returns empty for that token; wildcard with no matches doesn't drop sibling tokens; glob/literal overlap dedup; two-glob overlap dedup; empty tokens skipped; blank root throws when scope is non-empty.

Tests use a `IDisposable` `_root` fixture pattern (one temp dir per test, cleaned up best-effort in `Dispose`) so file-system state doesn't leak between cases.

### Build/test snapshot

- `dotnet build src/InfiniContext.Core` - green.
- `dotnet build src/InfiniContext.Cli -t:Compile` - green (full CLI build still blocked by running CLI processes locking Core.dll - persistent local gotcha, doesn't affect CI).
- `dotnet build tests/InfiniContext.Tests --no-dependencies` - green.
- `dotnet test --no-build` (full suite) - **943/946 passing** (3 macOS-only skips). 13 new tests in `ScopeGlobExpanderTests`; no regressions on the 930-test baseline from the `/flush` commit.
- `dotnet format InfiniContext.slnx --verify-no-changes` - green.

### One build-system gotcha worth recording

First attempt at the `.csproj` comment used `--scope` literally, which XML rejects (a `--` is illegal inside an XML comment per the spec). Fixed by rewriting to "the /task review scope glob expansion." Lesson: avoid double-dashes in any XML-comment text, even when describing CLI flags.

### What's NOT in v1 (deferred)

- **Per-token "matched nothing" diagnostic.** When `--scope "src/A.cs;ghost/**"` matches A.cs but `ghost/**` matches zero files, the user gets just `["src/A.cs"]` in `ResolveAsync` with no feedback about `ghost/**`. The empty-scope short-circuit catches the all-zero case clearly; partial-zero is silent. Add if dogfood shows users tripping on it.
- **Negation patterns** (`!exclude/**`). `Matcher.AddExclude` supports them but the expander doesn't expose the syntax yet. Add if needed.

### Remaining v1 follow-ups

- **`CoordinatorModel` channel split** - `ReviewSettings.CoordinatorModel` silently no-ops; needs `ReviewService` refactor to split reviewer + coordinator agents. Biggest remaining piece.
- **Step 12 (Dogfood)** - run `/task review 0003 --yes` in a real session; iterate on `ReviewerPrompts.cs` if false-positives surface.

### Files touched this session

- `src/InfiniContext.Cli/InfiniContext.Cli.csproj` (new package ref)
- `src/InfiniContext.Cli/Tasks/ScopeGlobExpander.cs` (new)
- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` (call site swap; doc-comment update)
- `tests/InfiniContext.Tests/InfiniContext.Tests.csproj` (new package ref + linked compile)
- `tests/InfiniContext.Tests/Cli/Tasks/ScopeGlobExpanderTests.cs` (new)
- `.task/0003-infinicontext-phase-2b-continuation-plan/progress.md` (this entry)

### Not committed yet

The 5 source/test/config files plus progress.md are uncommitted. **Recommended commit shape: single commit** titled e.g. `Stage 2B follow-up: /task review - glob expansion for --scope.`

### Handoff to next session

Two remaining items, in priority order:

1. **Dogfood (step 12)** - the cheap win. Spin up a session bound to task 0003, run `/task review 0003 --yes`, inspect the resulting `review-<timestamp>.md`. Iterate on the prompts in `ReviewerPrompts.cs` if false-positives surface. Likely 2-3 prompt-tightening commits before the surface is shippable.
2. **`CoordinatorModel` channel split** - bigger refactor. `ReviewService` currently takes a single `IReviewerAgentService` and uses it for both reviewers and the coordinator. To honor `ReviewSettings.CoordinatorModel`, split the constructor to accept reviewer + coordinator services (or one service plus a coordinator model id override that the service threads through). Tests in `ReviewServiceTests` would need updating to inject both seams.

Dogfood first - real findings are cheaper feedback than guessing at the prompts; CoordinatorModel can wait until after dogfood confirms whether a separate coordinator model is actually wanted.

