# /task review feature - v1 design

**Date:** 2026-05-17
**Status:** design locked, not yet implemented
**Conversation:** user asked for a multi-agent task-review command. This is the same review-gate feature postponed in `docs/stage2/todo.md` section B ("review-gate / task-completion ceremony"); v1 ships the *review tool*, not a `/task done` gate that enforces it.

## Problem

A finished task today is "done when the user says so". There is no structured pass that asks: does the work satisfy the goals stated in `description.md`? Is the architecture consistent with rest of the project? Are tests real, or trivial? Is there a security regression? A solo agent doing a self-review is biased toward the work it just produced; the user wants an adversarial fleet of specialised reviewers plus a critical consolidation step.

## v1 scope (in)

### Command surface

`/task review [<id>] [--scope <glob>] [--yes]`

- `<id>` defaults to the focused session's bound task. Refuses with a clear message if neither is given nor active.
- `--scope <glob>` overrides the auto-resolved scope (see below).
- `--yes` skips the cost-confirmation prompt (for scripting / re-runs).
- No top-level `/review` alias in v1 - keep the namespace clean; revisit if usage warrants.

Compiled into `SlashCommandRegistry` via `TaskSlashCommand` (mirrors `/task new`, `/task switch`). This is what the user means by "automatically installed at startup" - no skill file, no `~/.claude/` write; the command exists the instant the host launches.

### Scope resolution (what gets reviewed)

Default: the diff attributable to the task.

1. Read `tasks.base_commit_sha` from SQLite (new column, see below). This is the commit `HEAD` was at when `TaskService.CreateAsync` ran.
2. If non-null and reachable in the current repo: scope = `git diff <base_commit_sha>...HEAD --name-only` plus paths mentioned in any `progress.md` "Files touched" entries (union, deduplicated).
3. If null (pre-existing task, or branch surgery wiped the commit): fall back to `git merge-base main HEAD` ... HEAD. Document the fallback explicitly in the report's "Scope" line so the user can see it's lossy.
4. `--scope <glob>` overrides both: expand via `Microsoft.Extensions.FileSystemGlobbing` against the repo root.

Files exceeding a per-file byte cap (default 200 KB - generated code, lock files) are excluded from reviewer packets but listed in the report's "Excluded files" footer.

### Reviewer fleet

Five concurrent reviewers, each receiving a self-contained context packet from the coordinator (not the whole repo). Each emits structured-JSON findings.

| Reviewer | What the coordinator hands it | What it asks |
|---|---|---|
| **Completeness** | `description.md`, `progress.md`, `index.md`, diff summary (paths + +/- line counts), AFTER excerpts of each touched file | Does the solution satisfy the stated goals? What's missing? Any TODO/FIXME left behind? |
| **Architecture** | `CLAUDE.md`, `docs/infinicontext-initial-design.md` "Architecture cheat sheet" section, `docs/stage2/decisions.md` (entire), diff excerpts | Patterns consistent with rest of project? Layering correct? Abstractions where they should/shouldn't be? Violates a recorded D-number decision? |
| **Code & logic** | Full content of each touched file (surrounding context, not just diff lines), test files for cross-reference | Correctness, edge cases, null handling, off-by-one, concurrency, resource leaks, dead code, obvious bugs |
| **Tests** | Every new/modified test file in full, `dotnet test` results (TRX), skip annotations + their `Skip = "<reason>"` strings | Tests actually assert claimed behavior? Trivial / tautological tests? Tests skipped without reason? Coverage of error paths? Mocks where integration is required (cf. CLAUDE.md "No mocks of Claude's wire protocol")? |
| **Security** | Diff excerpts with focus on: secret handling, user-input boundaries, file-path ops, process spawn argv, deserialization, network, new `<PackageReference>` lines from .csproj diffs | Injection, traversal, secret leakage, unsafe deserialization, command injection, supply-chain |

**These five are opinionated and fixed in v1.** User-supplied custom reviewers are postponed.

### Reviewer invocation channel

**Not the light-model service.** Per user: "agents must not be light models". Light-model defaults to Haiku-class via `LightModelServiceFactory`; reviewers need the user's full reasoning model.

New service: `IReviewerAgentService` in `src/InfiniContext.Core/Review/`. Reuses `CliLightModelInvocation`'s argv-building approach (`claude --print --verbose --input-format stream-json --output-format stream-json --tools "" --strict-mcp-config --mcp-config '{"mcpServers":{}}' --setting-sources project --disable-slash-commands --no-session-persistence`) and isolated managed-cwd (`~/.infinicontext/review-cwd/`) but with `--model <reviewerModel>` set from settings (default: same as the user's main session model).

Five reviewer invocations run concurrently via `Task.WhenAll`, each with a generous timeout (`review.invocationTimeoutSeconds`, default 600). Each gets a fully-inlined prompt - no tool calls, no MCP, no file I/O on the reviewer side. Coordinator owns context selection.

Disposal: `Process.Kill(entireProcessTree: false)` per D12 (Windows AV scans on tree-kill cause stalls).

### Finding schema

Each reviewer emits one JSON document at the end of its turn:

```json
{
  "reviewer": "completeness | architecture | code | tests | security",
  "findings": [
    {
      "id": "code-001",
      "severity": "critical | warning | suggestion",
      "title": "short title",
      "summary": "1-3 sentences",
      "why": "rationale - why this matters",
      "file": "src/...cs",
      "line_start": 42,
      "line_end": 48,
      "suggested_fix": {
        "kind": "edit | replace-file | manual",
        "old_text": "...",
        "new_text": "..."
      },
      "confidence": 0.0-1.0
    }
  ],
  "notes": "freeform reviewer notes"
}
```

Tolerant deserialization: unknown fields preserved as `JsonElement` (same posture as `StreamJsonParser`). Findings with `kind=manual` cannot auto-apply; coordinator routes them to `needs_user`. `replace-file` requires the full new content.

JSON extraction is fence-aware: look for ` ```json ` fences in the result-event content first; fall back to raw-content parse. If neither parses, the reviewer's entire output becomes a single `needs_user` finding with severity=`warning` and the raw text in `summary` (the user sees we couldn't parse it).

### Coordinator stage

Sixth agent, same channel, same model class. Input: all five reviewer JSON outputs + the original scope context. Tasks:

1. **Deduplicate.** Two reviewers may flag the same line; merge into one canonical finding, listing all sources in `reviewers: [code, security]`.
2. **Re-score severity** when reviewers disagree; record rationale in `consolidator_note`.
3. **Decide disposition per finding.** Severity-orthogonal - any severity may land in any disposition (per user clarification "auto-apply high-confidence critical and warnings").

Disposition rules:

- `auto_apply` when **all** hold:
  - `confidence >= review.confidenceFloor` (default 0.9)
  - `disposition_confidence >= review.confidenceFloor` (coordinator's own confidence in the fix)
  - `suggested_fix.kind in {edit, replace-file}` with non-empty texts
  - Fix touches `<= review.autoApplyMaxLinesPerFinding` (default 50) and `<= review.autoApplyMaxFilesPerFinding` (default 3)
  - The finding is not "delete this test" or "remove this check" without an explicit replacement (test-coverage guard)
- `auto_reject` when coordinator concludes the reviewer is plainly wrong with high confidence. **A non-empty `rejection_reason` is required** - the schema validator rejects the coordinator's output as malformed if any auto_reject lacks one. (Per user requirement: rejected items must carry the exact reason of rejection.)
- `needs_user` otherwise. Default.

Output:

```json
{
  "findings": [
    {
      "<original fields>": "...",
      "reviewers": ["code"],
      "disposition": "auto_apply | auto_reject | needs_user",
      "disposition_confidence": 0.0-1.0,
      "rejection_reason": "required when disposition=auto_reject",
      "consolidator_note": "freeform - why this severity/disposition"
    }
  ],
  "summary": "overall summary across all reviewers"
}
```

### Auto-apply executor (host-side)

Per user: "auto-apply is host side, yes". The executor is host C#, not a delegated agent action.

For each finding with `disposition=auto_apply`:

1. `Read` target file; verify current content matches `suggested_fix.old_text` byte-for-byte.
2. If matches: apply via `Edit` (kind=edit) or `Write` (kind=replace-file).
3. If diverges: demote to `needs_user` with note `"source diverged from suggested_fix.old_text since review ran"`.

After the batch is applied:

4. **Build gate:** `dotnet build InfiniContext.slnx -c Release`. On failure: revert every edit in this batch (we kept the originals), demote all to `needs_user` with note `"build broke after auto-apply"`. Stop. (Settings: `review.skipBuildTestGate` bypasses gates for testing the executor itself; default false.)
5. **Test gate:** `dotnet test InfiniContext.slnx -c Release --no-build`. On failure: same revert + demote with note `"tests broke after auto-apply"`.
6. On both gates green: leave the edits applied. **Never commit.** The user owns commits.

Edits to `.task/` files (would be rare; e.g. coordinator-suggested progress.md cleanup) acquire the lock contract first. Source edits go straight through `Edit`.

### Report artifact

`review-<YYYY-MM-DDTHHmmss>.md` written to the task folder. Filename is timestamped so concurrent re-runs never collide (no lock needed for the initial Write).

```markdown
# Review report - <task title>

**Date:** <ISO 8601>
**Task:** NNNN - <title>
**Reviewers:** completeness, architecture, code, tests, security
**Coordinator:** v1
**Scope:** git diff <base_sha>..HEAD (N files, +A/-B lines)
**Excluded files:** <list of files over byte cap, or "none">
**Findings:** total=N (applied=A, rejected=R, awaiting=W)
**Build/test gate after auto-apply:** green | reverted (<reason>)

## Applied (auto-applied by host)

### [critical] <title>
- Reviewers: code, security
- Location: `src/Foo/Bar.cs:42-48`
- Why: <summary>
- Fix applied: <inline diff or "see git diff after this report">
- Reviewer confidence: 0.94
- Coordinator confidence: 0.91

(... per applied finding ...)

## Rejected (consolidator dismissed)

### [warning] <title>
- Reviewers: architecture
- Location: `src/Foo/Bar.cs:101`
- What the reviewer flagged: <summary>
- **Rejection reason:** <consolidator's exact verbatim reason>
- Reviewer confidence: 0.78
- Coordinator confidence in rejection: 0.93

(... per rejected finding, every entry has an explicit rejection reason ...)

## Awaiting decision

### [critical] <title>
- Reviewers: security
- Location: `src/Foo/Bar.cs:55-60`
- Why: <summary>
- Suggested fix: <text, or "manual">
- Coordinator note: <text>
- Reviewer confidence: 0.81
- Disposition reason: confidence below auto-apply floor (0.9)

(... per awaiting finding ...)

## Raw reviewer outputs

<details>
<summary>completeness</summary>

```json
<verbatim>
```
</details>

(... per reviewer, kept for audit ...)
```

The report is rendered from a `ReviewReport` POCO; the markdown is a serialization, not the source of truth (the JSON it serialised from is also kept inside the `<details>` blocks for re-processing).

### Cost guard

On `/task review` without `--yes`:

```
[task review] will spawn 6 sub-Claude agents (5 reviewers + 1 coordinator)
              targeting <N> files (~<M> lines, ~<K> tokens estimated).
              Model: <reviewerModel>. This is real money.
              Continue? [y/N]:
```

The token estimate is rough: char-count / 3.5. Good enough for a sanity check; not a billing prediction.

### Settings additions

Under a new `review` section in `settings.json` (and schema):

```jsonc
{
  "review": {
    "reviewerModel": "claude-sonnet-4-6",        // null => inherit from host session config
    "coordinatorModel": null,                     // null => same as reviewerModel
    "confidenceFloor": 0.9,                       // auto-apply gate
    "autoApplyMaxLinesPerFinding": 50,
    "autoApplyMaxFilesPerFinding": 3,
    "perFileByteCap": 204800,                     // 200 KB
    "invocationTimeoutSeconds": 600,              // per reviewer / coordinator
    "skipBuildTestGate": false                    // tests-only
  }
}
```

CLI flags (`--scope`, `--yes`) take precedence over settings as usual.

### Lock contract

The report file's timestamped name avoids collisions, so no lock on the initial Write. But:

- `index.md` lock acquired/released to append the report row.
- `progress.md` lock acquired/released to append the per-run entry.
- Edits to `.task/` files via auto-apply (rare) go through the lock contract before each Edit.

### Skill update

Bump `InfiniContextTaskSkill.Version` 3 -> 4. Add a brief "Task review" subsection to `SkillMarkdown` telling the agent:

- `/task review` exists; the user may invoke it at any point during or at the end of a task.
- When the agent is about to declare a task "done", suggest the user run `/task review` first.
- Agent does NOT spawn the review itself - host owns the orchestration.

## v1 scope (out / postponed)

- **`--reviewer <name>`** to re-run a single reviewer. Defer.
- **Custom reviewer prompts** beyond the five. Defer.
- **Cross-task review** (changes spanning multiple tasks). Defer.
- **Auto-commit after auto-apply.** Never - user owns commits.
- **`/task done` gate that enforces review-must-be-clean.** Still postponed (`docs/stage2/todo.md` section B). This design ships the *tool*; the *gate* is a separate command that runs the tool and refuses to mark done if anything `critical` lands in "Awaiting decision".
- **Re-using prior review reports.** v1 always runs all five reviewers; no caching.

## Code touch list (when implementation starts)

Core (new namespace `InfiniContext.Core.Review`):

- `src/InfiniContext.Core/Review/IReviewerAgentService.cs` + `CliReviewerAgentService.cs` - full-model invocation channel (parallel to `CliLightModelInvocation`).
- `src/InfiniContext.Core/Review/ReviewService.cs` - orchestrator. `RunReviewAsync(int taskId, ReviewOptions, CancellationToken) -> ReviewReport`. Owns scope resolution -> packet building -> reviewer dispatch -> coordinator -> auto-apply -> report write.
- `src/InfiniContext.Core/Review/ReviewerPrompts.cs` - five compile-time reviewer prompt templates + the coordinator prompt. `{slot}` markers filled by the orchestrator.
- `src/InfiniContext.Core/Review/FindingModel.cs` - POCOs (`Finding`, `Disposition`, `Severity`, `SuggestedFix`, `ReviewReport`, `ReviewerOutput`). Tolerant JSON.
- `src/InfiniContext.Core/Review/ScopeResolver.cs` - git-diff scope resolution, file enumeration, byte-cap exclusion, "Files touched" parsing from `progress.md`.
- `src/InfiniContext.Core/Review/AutoApplyExecutor.cs` - applies findings, runs build/test via `IBuildTestRunner` abstraction, reverts on failure.
- `src/InfiniContext.Core/Review/ReportRenderer.cs` - `ReviewReport` -> markdown (the structure shown above).
- `src/InfiniContext.Core/Tasks/Migrations/Migration_002_AddBaseCommitSha.cs` (new) - adds `base_commit_sha TEXT NULL` to `tasks`. Never edit `Migration_001`.
- `src/InfiniContext.Core/Tasks/TaskStore.cs` - read/write `BaseCommitSha`.
- `src/InfiniContext.Core/Tasks/TaskService.cs` - on `CreateAsync`, capture `git rev-parse HEAD` (best-effort - if not a git repo, leave NULL).

CLI:

- `src/InfiniContext.Cli/Tasks/TaskCommand.cs` - new discriminated record `Review(int? Id, string? Scope, bool SkipConfirm)`. Parser supports `<id?>`, `--scope <glob>`, `--yes`.
- `src/InfiniContext.Cli/Tasks/TaskCommandHandler.cs` - `HandleReviewAsync` branch: resolves task id from active session if omitted, prints cost-guard line, reads y/n via the existing console prompt helper, invokes `ReviewService`, prints summary (counts + report path), returns.
- `src/InfiniContext.Cli/Program.cs` - construct `ReviewService` + `CliReviewerAgentService`; thread `ReviewService` into `TaskCommandHandler`'s ctor.
- `src/InfiniContext.Core/Tasks/Skill/InfiniContextTaskSkill.cs` - bump Version 3 -> 4, add the "Task review" subsection.

Settings:

- `settings.json` - add `review` section with defaults shown above.
- `settings.schema.json` - schema entries.
- `src/InfiniContext.Core/Configuration/InfiniContextSettings.cs` - `ReviewSettings` record + binding.

Tests (`tests/InfiniContext.Tests/Review/`):

- `ScopeResolverTests.cs` - diff range resolution; fallback to merge-base when BaseCommitSha is null; --scope glob overrides; byte-cap exclusion; "Files touched" parsing.
- `FindingModelTests.cs` - tolerant deserialization, schema validation (rejection_reason required for auto_reject; suggested_fix presence for auto_apply).
- `AutoApplyExecutorTests.cs` - applies edit, detects source-diverged, reverts on build failure, demotes on test failure. Uses `FakeBuildTestRunner` (no real `dotnet` invocation).
- `ReviewServiceTests.cs` - end-to-end with a fake `IReviewerAgentService` that returns canned reviewer JSON; checks report file contents, applied/rejected/awaiting partitioning, index/progress updates, that rejected entries all carry rejection reasons.
- `ReportRendererTests.cs` - markdown rendering golden file.
- `Cli/TaskCommandTests.cs` - `/task review` arg parsing (id default, --scope, --yes).
- `Cli/TaskCommandHandlerTests.cs` - confirmation prompt declines => no agents spawned; --yes skips prompt; refuses with clear message when no active task; refuses with clear message when not in a git repo.

## Open questions for the implementing session

1. **Reviewer prompt iteration.** v1 prompts are best-guess; expect 2-3 iterations against real tasks before findings are actually useful. Plan: ship v1 prompts, dogfood by running `/task review` on task 0003 itself, tighten prompts against observed false-positives/false-negatives. Counts as part of v1 acceptance, not a follow-up.
2. **JSON output reliability.** `claude --print` without tools sometimes wraps JSON in prose. The fence-aware extractor handles common cases; truly bad output becomes a single `needs_user` finding so the user sees we couldn't parse. Iterate on prompt strictness ("Output ONLY the JSON document, no preamble") if false-fallbacks happen.
3. **BaseCommitSha for pre-existing tasks.** Task 0003 was created before this migration; `BaseCommitSha` will be NULL. The `merge-base main HEAD` fallback handles it, but for tasks whose work has already merged into `main`, the diff range may be empty - the review reports "nothing to review." Document the workaround (`--scope` override) in the report's footer when this happens.
4. **Concurrent reviewer cost.** Five parallel `claude --print` invocations against Sonnet/Opus is genuinely expensive. If users routinely complain, add `review.parallelism` (default 5, can be lowered to serialize for cost predictability) as a follow-up. Not v1.

## References

- Conversation date: 2026-05-17
- User clarifications: (1) auto-apply applies to *any* severity at high confidence; (2) auto_reject is allowed for any severity, including critical, but must carry an explicit rejection reason; (3) reviewers are NOT light-model (use full reasoning model), coordinator passes them a reduced context packet; (4) auto-apply is host-side.
- Postponed item: `docs/stage2/todo.md` section B "review-gate / task-completion ceremony" - this design ships the *tool*; the *gate* (a `/task done` that refuses unless review is clean) remains separate.
- Sibling design doc (style reference): `issues-feature-design.md`.
- Light-model channel reused for argv shape: `src/InfiniContext.Core/LightModel/CliLightModelInvocation.cs` (D12 - kill-tree gotcha applies to reviewer disposal too).
- Memory: [[feedback-single-source-of-truth]] (report content lives only in the markdown artifact + the in-memory POCO; no SQLite mirror of findings).
