# issues.md feature - v1 design

**Date:** 2026-05-17
**Status:** design locked, not yet implemented
**Conversation:** user asked for a way to report bugs/issues found while working on a task without polluting `progress.md` or fixing them inline.

## Problem

While working on a task, the agent or user often surfaces out-of-scope findings: a bug in an adjacent module, a code smell, a follow-up question, a wrong assumption in another file. Today these either (a) get fixed inline (scope creep), (b) get buried in `progress.md` mixed with audit-log entries, or (c) get forgotten. No structured surface for "noted, not in scope right now."

## v1 scope (in)

### Artifact: `issues.md`

Optional task artifact, one per task, append-only, flat structure. No code registration needed - `TaskFolder.ListArtifactsAsync` already enumerates `*.md` and the drift detector flags any file missing from `index.md`.

Format:

```markdown
# Issues - <task title>

## 0001 - <short title>
- **Logged:** <ISO 8601> by session <id-prefix>
- **Kind:** bug | smell | followup | question
- **Where:** path/to/file.cs:line (optional)
- **Status:** open | resolved | wont-fix | deferred
- **Body:** 2-10 lines

## 0002 - ...
```

Local IDs (`0001`, `0002`) are per-task and stable so `progress.md` / `decisions.md` can cross-reference them ("see issues.md#0003"). Default status is `open`.

**State lives only in `issues.md`** - no SQLite mirror. User preference: avoid de-sync between sources (see memory `feedback-single-source-of-truth`).

### Capture

Two surfaces:

1. **Agent side** - skill instruction: when the agent finds an out-of-scope bug, smell, or follow-up while doing in-scope work, append a numbered entry to `issues.md` under the lock contract instead of fixing inline or burying in `progress.md`.

2. **User side** - REPL command:
   - `/issue "<title>" [--kind <k>] [--where <ref>]` (top-level, canonical)
   - `/task issue "<title>" [--kind <k>] [--where <ref>]` (alias, mirrors the `/task <verb>` namespace)
   - Targets the focused session's bound task; refuses if no active task.
   - Acquires the lock, appends the entry with auto-incremented local ID, releases.

### Listing

`/issue list` - prints the issues table for the focused session's task. No filter args in v1; list shows all entries with status. **User-invoked only.** No automatic gate at any other point.

### Resolution

`/issue resolve <n> [--as <status>] [<note text...>]`

- `<n>` = local ID (the `0003` from the heading).
- `--as <status>` = optional; defaults to `resolved`. Accepted: `resolved | wont-fix | deferred`.
- Trailing text after `<n>` (and after optional `--as <status>`) is treated as the note. `--note "<text>"` also accepted for scripted/multi-line use.
- Examples:
  - `/issue resolve 3` -> resolved, no note
  - `/issue resolve 3 fixed in commit abc123` -> resolved, note inline
  - `/issue resolve 3 --as wont-fix not worth the churn` -> wont-fix + note

Edits the entry's `Status:` line and appends a `- **Resolution:** <date> <note>` line under it.

## v1 scope (out / postponed)

These are recorded in `docs/stage2/todo.md`:

- **`/issue promote <n>`** - spin an issue into a new task (seed `description.md` from issue body, mark source as `filed-as-task <new-id>`, record `parent_task_id` lineage). Postponed; not blocking v1 since user can manually invoke `/task new` then `/issue resolve <n> filed as task NNNN` until promotion lands.
- **`/task done` + any review gate** - automatic "review issues before closing task" enforcement. Needs a full requirements + design cycle for the broader task-completion ceremony (no `done` command exists today; SQLite status is `InProgress | Idle` with no terminal state). v1 has no gate; user runs `/issue list` on demand.

## Code touch list (when implementation starts)

- `src/InfiniContext.Core/Tasks/ArtifactTemplates.cs` - add `IssuesScaffold` constant (file header only) and mention `issues.md` in the optional-artifacts comment.
- `src/InfiniContext.Core/Tasks/InfiniContextTaskSkill.cs` - add "Issue hygiene" section to `SkillMarkdown` instructing the agent when/how to log to `issues.md`. Bump skill version to 3.
- `src/InfiniContext.Core/Tasks/TaskService.cs` (or a new `IssueService.cs` if cleaner) - `AppendIssueAsync`, `ResolveIssueAsync`, `ReadIssuesAsync`. All go through the existing `ApprovalChannel` lock contract.
- Parser: small markdown reader for the issue-entry format (heading + key-value lines). Lives next to the service.
- `src/InfiniContext.Cli/Commands/IssueCommand.cs` + `IssueCommandHandler.cs` - mirror `TaskCommand` / `TaskCommandHandler`. Register top-level `/issue` and route `/task issue ...` to the same handler.
- Tests: parser round-trip, ID auto-increment, resolve with all three syntax variants (no note, trailing note, `--as` + trailing note), refusal when no active task.
- **No SQLite migration.** No DB changes.

## Open questions for the implementing session

None blocking. Two minor calls to make at implementation time:

1. **Issue-entry heading vs collapsible.** v1 uses `## 0001 - <title>` as a flat heading. If `issues.md` grows large (>50 entries), consider a TOC at the top auto-regenerated on each append. Defer until observed.
2. **`Where:` parsing.** `--where src/foo.cs:42` is freeform text in v1 - no validation that the path exists. Validation could come later if the field becomes load-bearing for navigation.

## References

- Conversation date: 2026-05-17
- Memory: [[feedback-single-source-of-truth]]
- Postponed items: `docs/stage2/todo.md` section B (`/issue promote`, done-gate)
