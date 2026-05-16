# Stage 0 fixtures

Recorded NDJSON event streams from real `claude` runs, captured by `tools/InfiniContext.Recorder`. See `NOTES.md` for the wire-format observations distilled from these recordings — those notes correct/extend design doc §4.2.

These are the primary regression surface for the parser and state machine (see design doc §7.5). Treat them as committed test data; do not regenerate casually.

## Recorded against `claude` 2.1.112 (Windows)

| Fixture | Scenario | Captured? | Hand-edited? |
|---|---|---|---|
| `simple-prompt.jsonl` | one prompt, one assistant turn, no tools | ✅ | no |
| `single-tool.jsonl` | one Bash tool call (with `--permission-mode bypassPermissions`) | ✅ | no |
| `nested-tool.jsonl` | `Task` tool spawning a sub-agent that uses Bash; exercises `parent_tool_use_id` correlation | ✅ | no |
| `multi-turn.jsonl` | three prompts piped before EOF (claude collapses them — true multi-turn at runtime is Stage 1's job, not the recorder's) | ✅ | no |
| `partial-messages.jsonl` | same content as simple-prompt with `--include-partial-messages`; gives `stream_event`/SSE deltas | ✅ | no |
| `unknown-event.jsonl` | invented top-level type, invented content-block type, unknown fields on known events | ✅ | **yes** |
| `compaction.jsonl` | long enough to trigger context compaction | ⏸️ deferred — needs context fill |
| `api-retry.jsonl` | rate-limit-induced retry events | ⏸️ deferred — hard to induce |
| `cancel-mid-stream.jsonl` | interrupt sent during a streaming response | ⏸️ deferred — recorder needs interrupt support |
| `single-tool-with-approval.jsonl` | tool call going through the Stage 1H MCP approval round-trip (recorder run with `--approval auto-allow`) | ✅ | no |

## When refreshing

Bump `TestedAgainstVersion` in `InfiniContext.Core` config alongside any re-recording. See design doc §7.3.

## Recipe

```
dotnet run --project tools/InfiniContext.Recorder -- \
  --script tests/fixtures/scripts/<name>.json \
  --cwd /tmp/infinicontext-recorder-cwd
```

To record with the Stage 1H approval flow on:
```
dotnet run --project tools/InfiniContext.Recorder -- \
  --script tests/fixtures/scripts/single-tool-with-approval.json \
  --cwd ./tmp \
  --approval auto-allow
```

The recorder writes `tests/fixtures/<scenario>.jsonl` and a sibling `<scenario>.jsonl.stderr.log`.

## Expected traces

State-machine trace files (`<scenario>.expected-trace.txt`) will be produced as part of Stage 1's state-machine tests, not in Stage 0.
