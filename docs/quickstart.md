# InfiniContext quickstart

Five minutes from clone to a multi-session supervisor that doesn't lose work when a context window fills.

## What you get

- **One terminal, many agents.** Spawn N concurrent `claude` sessions across different repos; focus, broadcast, approve from a single prompt.
- **Work that outlives a session.** Each unit of work is a *Task* whose state lives on disk as durable markdown. When the context window fills, a fresh session picks up where the last one stopped — the file system is the working memory, not the LLM's KV cache.
- **Approval gating you can leave on.** `auto` mode forwards each tool call to a cheap Haiku safety review, low-confidence calls fall through to deny or ask. Long unattended runs without a blank check.

## Prerequisites

- .NET 10 SDK (`dotnet --version` → 10.x).
- Anthropic `claude` CLI v2.x on PATH (`claude --version`). InfiniContext spawns it as a child — it doesn't reimplement the model loop.
- An auth method for `claude` itself: either you've run `claude /login` once (OAuth), or `ANTHROPIC_API_KEY` is set. The host's `/task new` title generator and `/approval auto` reviewer use whichever is available — API key takes the SDK path (faster), OAuth takes the CLI fallback.

## Launch

```
dotnet run --project src/InfiniContext.Cli
```

You land at a `> ` prompt. `/help` lists every host-handled command; anything that isn't one is forwarded verbatim to the spawned `claude`, so `/clear` and `/compact` reach the agent unchanged.

```
> /state
[state] Idle | open_tools=0 | drift=unk:0/malformed:0 | active_sessions=1 | session=<unassigned>

> hello, give me a one-line response
Hello! Here's my one-line response.

> /state
[state] Idle | open_tools=0 | drift=unk:0/malformed:0 | active_sessions=1 | session=8f3a…
```

If `/state` flips back to `Idle` after the assistant turn, the pipeline is healthy.

## Tasks: the unit of work that survives a session

A **Task** lives at `<repo>/.task/<id>-<slug>/` as plain markdown, plus a row in the host's SQLite registry under `~/.infinicontext/infinicontext.db`. Many sessions can serve one task; each picks up where the last one left off by reading the durable artifacts.

```
> /task new "investigate why the auth migration takes 6 minutes"
[task] created 0001 "Investigate slow auth migration"
       folder: D:\src\my-project\.task\0001-investigate-slow-auth-migration
```

The folder is scaffolded with three mandatory files:

```
.task/0001-investigate-slow-auth-migration/
├── index.md         ← table of contents; the agent reads this first
├── description.md   ← what you typed at /task new
└── progress.md      ← append-only audit log the agent maintains
```

Switch into the task. This spawns a *fresh* `claude` process rooted at the task's home cwd, and prefixes its first user message with a pointer to the task folder and the `infinicontext-task` skill:

```
> /task switch 1
[task] switching to 0001 "Investigate slow auth migration"
```

The agent (taught by the auto-installed [`infinicontext-task` skill](../src/InfiniContext.Core/Tasks/Skill/InfiniContextTaskSkill.cs)) reads `index.md`, decides what else to load, edits in place, and appends to `progress.md` on every meaningful state change.

## Not losing context, in practice

Three layered defences:

**1. Durable artifacts.** `progress.md` is the working memory. The skill teaches every session: append at every state change, group by `## YYYY-MM-DD` headings. When the window fills, the file knows where the work is.

**2. Context-pressure nudges.** The host watches the running token sum reported in each `result` event against the model's context window. At **60%** it injects a *soft* user turn: *finish the current sub-task, write progress, exit cleanly so I can hand off to a fresh session.* At **85%** it injects a *hard* turn: *flush now, no new work, no clarifying questions.* Each fires at most once per session. Thresholds live in `settings.json` under `tasks.softThresholdPct` / `tasks.hardThresholdPct`.

**3. Clean hand-off via `/task switch`.** Run `/task switch 1` again — minutes or days later. A brand-new `claude` boots, reads `index.md`, then `progress.md`, sees what the previous session was mid-flight on, and continues. Repeat as needed.

Combined: a task can span dozens of sessions across days. Each one ends with `progress.md` up to date. The next begins by reading where the previous one stopped.

## Multi-session: one supervisor, many agents

`/n <cwd>` opens a new pane rooted in a different working directory:

```
> /n D:\src\service-a
[n] opened 7a2b91c8-… at D:\src\service-a

> /n D:\src\service-b
[n] opened 93cf4e10-… at D:\src\service-b

> /list
                    Active sessions
┌──────────┬─────────┬──────────────────┬────────────┬───────┬──────┐
│ Host id  │ Focused │ Cwd              │ Claude id  │ Drift │ Cost │
├──────────┼─────────┼──────────────────┼────────────┼───────┼──────┤
│ 93cf…    │   *     │ D:\src\service-b │ —          │ 0     │ —    │
│ 7a2b…    │         │ D:\src\service-a │ —          │ 0     │ —    │
└──────────┴─────────┴──────────────────┴────────────┴───────┴──────┘
```

Navigate panes:

- `/focus <prefix>` — switch to a session by any unambiguous prefix of its host id (`/focus 7a2b`).
- `/next` / `/prev` — cycle.
- Anything typed at the prompt goes to the focused session. The others keep running off-screen; their transcripts buffer in memory and replay when you `/focus` back.

Fan one prompt out to many sessions:

```
> /broadcast 7a2b,93cf  run the test suite and report failures
[broadcast] sent to 2/2 session(s)

> /broadcast * /clear
[broadcast] sent to 2/2 session(s)
```

`/broadcast *` hits every active session. Single targets are rejected — use a normal prompt for that.

### Parallel work on one task

`/task switch <id>` claims the task; a second session on the *same* task is refused (`TaskActivityCoordinator` enforces one writer per task). For genuinely parallel work, **fork** the task and run each fork in its own session, then **merge** when they reconverge:

```
> /task fork 1 "Spike: index hypothesis"
[task] forked 0001 -> 0002 "Spike: index hypothesis"

> /n D:\src\my-project       # second pane for the fork
> /task switch 2             # claim the fork in the new pane
```

Lineage (`parent_task_id`) is recorded in the registry, so you can audit the family tree later.

```
> /task merge 1 2
[task] merged 0001, 0002 -> 0003 "Merge of 0001 + 0002"
```

(Merging is currently deterministic-concat only — every artifact present in either source ends up in the merged folder under labelled section headers. Light-model and full-model merge tiers are post-MVP.)

Within a single task, every write under `.task/` must go through the MCP `tasks/lock` channel. The skill explains the acquire/release contract to the agent — you don't manage it by hand.

## Approval modes

```
> /approval                       # show current mode
[approval] mode=auto.

> /approval ask                   # prompt me for every tool call
> /approval auto                  # light-model safety review (the default)
> /approval allow                 # blanket allow — banner reminder, use with care
> /approval deny                  # read-only / "explain only" sessions
```

`auto` is the default and the right pick for unattended runs: every tool call is reviewed by a cheap Haiku call (`models.light.id`), `confidence=low` routes to deny by default (or `ask`, via `approval.auto.onLowConfidence`). Default timeout per request is 60 s → auto-deny if the user doesn't answer.

Approvals are per-session: a prompt in one pane doesn't block another. If two sessions raise approval requests at once, only the focused pane's request is visible — `/focus` to the other and answer there.

## Resuming a closed session

`/list` also shows a *Recent sessions* table built from the host index (`~/.infinicontext/index.json`). To reopen a closed session in a new pane without disturbing what's running:

```
> /resume 8f3a9c1b-…
[resume] opened a1d4… as resume of 8f3a9c1b-…
```

This is non-destructive — existing panes keep running. The resumed session re-enters its original cwd via `claude --resume`.

## Where things live

```
<your repo>/
└── .task/0001-…/                    ← per-task markdown (commit it; tasks travel with the code)
    ├── index.md
    ├── description.md
    ├── progress.md
    └── plan.md, research.md, …      ← optional, agent-created on demand

~/.infinicontext/
├── infinicontext.db                 ← SQLite task registry (host-only)
├── index.json                       ← session history; survives host restarts
└── light-cwd/                       ← isolated cwd for the light model

~/.claude/
├── skills/infinicontext-task/SKILL.md  ← installed once on first host launch
└── projects/…                       ← claude's own jsonl session store (host reads, never writes)

<repo>/logs/<YYYYMMDD-HHmmss>.log    ← per-run Serilog file (gitignored)
```

## Tuning

Defaults are in `settings.json`. Per-developer overrides go in `settings.user.json` (gitignored — never commit it). Knobs you're likely to touch first:

```jsonc
{
  "tasks": {
    "softThresholdPct": 60,   // soft context-pressure nudge fires at this %
    "hardThresholdPct": 85    // hard "flush now" nudge fires at this %
  },
  "approval": {
    "timeoutSeconds": 60,     // per-request auto-deny timeout
    "mode": "Auto",           // ask | deny | allow | auto
    "auto": {
      "onLowConfidence": "Deny",   // or "FallbackToAsk"
      "effort": "Low"              // "Low" (fast, ~2.3 s/call) or "High" (thinking, ~9 s)
    }
  },
  "models": {
    "main":  { "id": "claude-opus-4-7" },
    "light": { "id": "claude-haiku-4-5" }
  }
}
```

Env-var form for one-off overrides: `INFINICONTEXT__TASKS__SOFTTHRESHOLDPCT=70`. Validation hard-fails on bad config, so a typo won't silently do the wrong thing.

## When something goes wrong

- **`[task] subsystem is unavailable (see startup warnings).`** — host startup couldn't open SQLite. Check `logs/<timestamp>.log` for the underlying exception.
- **`[task] error: …` on `/task new`** — usually a credential issue (neither `ANTHROPIC_API_KEY` set nor `claude` OAuth logged in). Other `/task` commands still work.
- **Drift counter rising on `/state`** — `claude` may have upgraded with new event types the parser hasn't seen. It tolerates them, but `/state` lists the top unknown types so you can decide whether to re-record fixtures.
- **`Migration history has a gap / DB ahead of code`** at startup — you downgraded the host. Either upgrade back or delete `~/.infinicontext/infinicontext.db` (loses the registry rows but preserves `.task/` folders, which are the real source of truth for content).
- **Approval prompts disappear without you answering** — they default to 60 s → auto-deny. Bump `approval.timeoutSeconds` or switch the mode.

## Where to read next

- [`docs/cli/howto/task-commands.md`](cli/howto/task-commands.md) — full `/task` sub-grammar with every flag and edge case.
- [`docs/infinicontext-initial-design.md`](infinicontext-initial-design.md) — the architecture and the *why* behind every load-bearing decision (parser tolerance, no terminal embedding, why we don't mirror claude's session store, …).
- [`docs/stage2/decisions.md`](stage2/decisions.md) — running ledger of every constraint that affects future work. Read this before non-trivial changes.
- [`CLAUDE.md`](../CLAUDE.md) — the contract Claude Code itself works under in this repo: cheat sheet of the wire protocol, state machine, multi-session model, tasks subsystem, light-model service.
