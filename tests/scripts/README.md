# FakeClaude scripts (Stage 1F)

Adversarial scenario scripts driven by `FakeClaude --script <path>`. Different from
`tests/fixtures/scripts/` (which holds **recorder** scripts that drive real `claude`).

Action grammar (`{"actions": [...]}`):

| `type` | fields | meaning |
|---|---|---|
| `wait-stdin` | `lines` (default 1) | block until N lines have been read from stdin |
| `emit-line` | `json` (object) | write the JSON as one NDJSON line + newline + flush |
| `emit-raw` | `bytes` (string) | write literal bytes (no newline, no transformation) |
| `emit-fixture` | `path`, `delay_ms?` | stream a recorded fixture line-by-line, optionally paced |
| `sleep-ms` | `ms` | pause N milliseconds |
| `exit` | `code?` (default 0) | terminate with given exit code |

Paths in `emit-fixture` are resolved relative to the script file's directory.

`emit-raw` is for pathological cases — embed `\n` inside `bytes` to terminate a malformed
line, or omit it to leave the final line unterminated when the process exits.
