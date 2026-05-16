# Stage 0 — observed wire-format notes

Captured against `claude` **2.1.112** on Windows. These notes correct/extend design doc §4.2.

## Top-level event types observed (default mode)

| `type` | `subtype` | When | Notes |
|---|---|---|---|
| `system` | `hook_response` | At session start (today, due to a `claude` bug) | Local SessionStart hook errored out. We must tolerate this — it's noisy noise, not a fatal. |
| `system` | `init` | Once per spawn | Session metadata: `session_id`, `model`, full tool list, MCP servers, slash commands, plugins, `claude_code_version`, `cwd`, `permissionMode`, `output_style`, `agents`, `skills`, `apiKeySource`, `memory_paths`, `fast_mode_state`. The session_id is the resume key. |
| `system` | `status` | Mid-stream (with `--include-partial-messages`) | Status update; not present in default mode. |
| `assistant` | (none) | One per assistant turn | `message.content` is an array of `text` and `tool_use` blocks. **Not** streamed in default mode — arrives whole. |
| `user` | (none) | One per tool-result delivery (echoed by `--print`) | `message.content[].type == "tool_result"` with `tool_use_id` linking back to the `tool_use` from the matching assistant message. |
| `rate_limit_event` | (none) | After each turn | `rate_limit_info` with `status`, `resetsAt`, `rateLimitType`, `overageStatus`, `isUsingOverage`. **Not in design doc §4.2** — parser tolerance must catch this. |
| `result` | `success` / `error_max_turns` / etc. | Last event before exit | `duration_ms`, `num_turns`, `result` (final assistant text), `stop_reason`, `total_cost_usd`, `usage`, `modelUsage`, `permission_denials`, `terminal_reason`. |
| `stream_event` | (event subtype inside `event.type`) | Only with `--include-partial-messages` | Wraps Anthropic SDK streaming events: `message_start`, `content_block_start`, `content_block_delta` (with `text_delta`/`thinking_delta`/`input_json_delta`), `content_block_stop`, `message_delta`, `message_stop`. |

## Stream protocol corrections (vs design §4.2)

1. **No `text_delta` events at top level by default.** Streaming deltas exist only with `--include-partial-messages`, and arrive nested as `stream_event.event.delta`. Default stream is per-turn batched.
2. **No separate "tool-use" / "tool-result" events.** Tool calls are content blocks inside `assistant.message.content[]`; tool results are content blocks inside an echoed `user.message.content[]`. The parser groups by content-block `type`, not by event `type`.
3. **`rate_limit_event` is real and frequent.** Add to the known set.
4. **Sub-agents' events are top-level too**, with `parent_tool_use_id` set to the parent `Task` tool's `tool_use_id`. The state-machine "tool-use stack" lives on these correlation IDs — NOT on event nesting.
5. **`hook_response` at session start may carry an error** (current `claude` bug). Tolerate.
6. **`thinking` is a content-block type**, not an event type. Lives inside `assistant.message.content[].type == "thinking"`. Render as collapsed tree node.
7. **`system/task_*` events** track the `Task` tool's sub-agent lifecycle: `task_started`, `task_progress`, `task_notification`. Useful for status indicators; not load-bearing for state.

## Content-block types observed inside `message.content[]`

| Block `type` | In which event | Meaning |
|---|---|---|
| `text` | `assistant`, `user` | Plain text |
| `thinking` | `assistant` | Extended thinking; collapse by default in UI |
| `tool_use` | `assistant` | A tool call; carries `id`, `name`, `input` |
| `tool_result` | `user` (echoed) | Result of a tool call; carries `tool_use_id` linking to the assistant's `tool_use` |

## Implications for Stage 1

- **Default mode is enough** for Stage 1. `--include-partial-messages` is a Stage 3 streaming-UX upgrade.
- The host parses `assistant`/`user`/`result` and walks each `message.content[]` array for `text` / `tool_use` / `tool_result` blocks.
- **Turn boundary** = arrival of `result` event. State machine transitions to `Idle` there.
- **`rate_limit_event`** is informational; surface as a status indicator, don't treat as a state transition.

## Useful flags discovered

| Flag | Effect | When to use |
|---|---|---|
| `--include-partial-messages` | Adds `stream_event` records | Stage 3 streaming UI |
| `--include-hook-events` | Adds full hook lifecycle events | Stage 1 debug pane |
| `--replay-user-messages` | Echoes our user-message inputs back on stdout | Useful for the host's UI to confirm prompt-sent |
| `--no-session-persistence` | Don't write to `~/.claude/projects/` | Tests that don't want persisted state |
| `--session-id <uuid>` | Use a specific UUID | Reproducible tests |
| `--max-budget-usd <amount>` | Hard spending cap | Sanity guard during development |
| `--bare` | Skip hooks/LSP/plugins/auto-memory/CLAUDE.md discovery | Cleanest possible session for fixture replay |
| `--permission-mode bypassPermissions` | No approval prompts | Stage 0 fixture recording (we can't yet drive approvals) |

## Open

- **Compaction / api-retry / cancel-mid-stream** fixtures deferred — they require either long-running context fill, induced rate limits, or interrupt support in the recorder.

## Stage 1H — approval-server fixture

`tests/fixtures/single-tool-with-approval.jsonl` was recorded against the Stage 1H ApprovalSidecar
(no `bypassPermissions`). Argv used:

```
claude --print --verbose --input-format stream-json --output-format stream-json
       --permission-prompt-tool mcp__infinicontext__approval_prompt
       --mcp-config <generated.json>
       --strict-mcp-config
       --allowed-tools mcp__infinicontext__approval_prompt
```

Surprising finding: **`--permission-prompt-tool` is not in `claude --help` output for 2.1.112** but
is silently accepted and functional. Our argv enables the flag and it works. The on-the-wire JSONL
sequence is indistinguishable from `single-tool.jsonl` (no extra event types appear) — the entire
JSON-RPC handshake between claude and the sidecar happens out-of-band.

Recorder flag: `--approval auto-allow|auto-deny`. Defaults to off.
