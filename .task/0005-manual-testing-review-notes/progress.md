# Progress

## 2026-05-21

- Task created.
- Manual testing session logged 12 findings via `/review` into `issues.md` (0001тАУ0012). All status `open`.
  Themes: missing/broken tools (AskUserQuestion #0001, Task* tools #0003), UX gaps (multi-line input
  #0002, external editor #0009, background-worker visibility #0008), auto-approval policy tuning
  (#0004 false-positive docs, #0005 broader default allowlist, #0007 git push gating), task-skill
  ergonomics (#0006 autonomy default, #0010 auto-switch on `/task new`, #0012 `/task flush` command),
  and architectural simplification (#0011 drop MCP task locks in favor of per-task executor).
- Flushed task documents and pushed via host-driven commit.
- Open questions / next steps: triage `issues.md` into stage-3/follow-up backlog in `docs/todo.md`;
  several findings (#0001, #0006, #0007, #0010, #0012) are short-cycle host fixes worth promoting
  to their own tasks before stage 3 GUI work starts.
- Critical-review pass over the 12 issues; applied four with confidence, one commit each:
  - `3cd1811` -- closed #0006 (autonomy default already in skill v5, commit `1ecab59`).
  - `c62460f` -- #0004: `docs/quickstart.md` gains a false-positive paragraph under "Approval modes"
    with two example axes (mixed-script glyphs, routine `git push`) and the `/approval ask` recovery
    path. Live-reproduced: the safety reviewer auto-denied the doc edit itself when it contained
    the literal example characters, so the doc describes the pattern rather than embedding the
    offending glyphs.
  - `eb89da9` -- #0010: `/task new` auto-switches in the REPL. `TaskSlashCommand` intercepts
    `TaskCommand.New`, runs `TaskCommandHandler.HandleNewAsync` (widened to `internal` and now
    returning the persisted `TaskRecord`), then chains into `TryStartTaskSwitchAsync`. Bare CLI
    dispatch unchanged.
  - `9b7584b` -- #0012: new `/task flush` subcommand injects an operator-requested flush nudge into
    the focused session via `IClaudeSession.SendUserMessageAsync` (same channel `ThresholdInjector`
    uses for the soft/hard threshold nudges). Task-bound sessions get the active id named in the
    nudge. `HelpText` updated; bare CLI dispatch refuses with a clear message.
- All four passed `dotnet build -c Release`, `dotnet test` (382-394 task tests), and
  `dotnet format --verify-no-changes`. Debug build is currently blocked by 4 long-running
  `InfiniContext.Cli` host processes holding a file lock on the Debug DLL output; Release builds
  to a different folder so verification used Release throughout.
- Deferred with rationale: #0001/#0002/#0003/#0009 (non-trivial features needing design), #0005
  (no specific tools named), #0007 (host-side safety-reviewer prompt tune, not a settings toggle --
  needs its own task), #0008 (Stage 3 GUI concern), #0011 (conflicts with D9, user marked
  "discussible" -- needs explicit alignment).

## 2026-05-21 (post-flush)

- Hard context-pressure flush requested by operator. Wrote this entry; stopping per the skill's
  hard-nudge protocol. No new work started.