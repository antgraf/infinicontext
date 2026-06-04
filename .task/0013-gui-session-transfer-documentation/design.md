# Bug A design: `mcp__infinicontext__session_exit`

## Problem

`AutoRotationService` only fires when a session transitions to `Exited` with a
`RotationReason` set. Today the only way a session reaches `Exited` is:

1. Operator-driven close (CLI `/quit`, GUI window close, `DisposeAsync`).
2. Child crash (`claude` segfault, network kill).
3. Host shutdown (kills the child via supervisor).

The hard-nudge protocol expects the agent to *exit* after flushing, but **the
agent has no programmatic way to do that**. The `Bash`, `Edit`, `Write`, `Read`,
`Grep`, `Glob`, `TaskList`, etc. tool surface does not include "close this
session"; in stream-json mode the child only exits when its stdin closes, which
is operator-driven, never agent-driven. So the agent acknowledges the nudge ("Already
flushed. Exiting.") in plain text, the state machine stays `Idle`, no `Exited`
transition, no `AutoRotationProposal`, no successor session.

Smoke evidence: `src/InfiniContext.Gui/bin/Debug/net10.0/logs/gui-20260527-225628.log`
lines 405-421 вЂ” hard nudge injected at 23:00:32, agent replied "Exiting" at
23:00:48 plain-text, session stayed alive until 23:17:59 when operator closed
the app (exit code -1 = Job Object kill, after `AutoRotationService` was already
disposed вЂ” bug B).

## Design

A new MCP tool `mcp__infinicontext__session_exit` that the agent calls after
flushing. The host responds by closing the session's stdin, which causes
`claude` to finish its current turn and exit naturally вЂ” the same EOF path
`DisposeAsync` already uses, minus the `proc.Kill` follow-up.

### Tool surface

| Property        | Value                                                                  |
|-----------------|------------------------------------------------------------------------|
| Tool name       | `session_exit`                                                         |
| Fully qualified | `mcp__infinicontext__session_exit`                                     |
| Wire `kind`     | `session.exit`                                                         |
| Input schema    | `{ "reason": string? }` (optional free-form, logged at Information)    |
| Returns         | `{"ok": true}` unconditionally (no failure mode worth surfacing)       |
| Approval        | **Bypasses approval prompt.** Auto-honored, like `lock.acquire/release` |
| Idempotency     | Repeated calls are no-ops on the host side                             |

### Wire-format additions

In `LockWireFormat` (despite the name вЂ” this is the multiplexed approval pipe's
shared wire enum):

```csharp
public const string SessionExitKind = "session.exit";
```

New records in a new file `src/InfiniContext.Core/Mcp/Sessions/SessionExitMessages.cs`
(parallel to `Tasks/LockMessages.cs`):

```csharp
public sealed record SessionExitRequest(string? Reason)
{
    public JsonObject ToJson() => new()
    {
        [LockWireFormat.KindField] = LockWireFormat.SessionExitKind,
        ["reason"] = Reason,
    };

    public static SessionExitRequest FromJson(JsonObject obj) => new(
        Reason: obj["reason"]?.GetValue<string>());
}

public sealed record SessionExitResponse(bool Ok)
{
    public JsonObject ToJson() => new()
    {
        [LockWireFormat.KindField] = LockWireFormat.SessionExitKind,
        ["ok"] = Ok,
    };
}
```

### Sidecar registration

In `ApprovalSidecarHost.RunAsync`, after the `task_release_lock` registration:

```csharp
var exitSchema = new JsonObject
{
    ["type"] = "object",
    ["properties"] = new JsonObject
    {
        ["reason"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Free-form description of why the session is exiting "
                + "(e.g., 'flushed after hard nudge', 'task complete').",
        },
    },
};

server.RegisterTool(
    ApprovalSidecarContract.SessionExitToolName,
    "Request a graceful exit of this Claude session. The host closes stdin; "
    + "the current turn completes, then the process exits. Auto-rotation, if "
    + "armed by a context-pressure nudge, fires from the resulting Exited transition. "
    + "Returns { ok: true }.",
    exitSchema,
    async (arguments, innerCt) =>
    {
        var reason = arguments["reason"]?.GetValue<string>();
        var req = new SessionExitRequest(reason);
        logger.Information("sidecar: forwarding session.exit reason={Reason}", reason ?? "<none>");
        var responseObj = await ExchangeAsync(pipeWriter, pipeReader, pipeLock, req.ToJson(), innerCt).ConfigureAwait(false);
        if (responseObj is null)
            return ("{\"ok\":false,\"error\":\"host disconnected\"}", true);
        SessionExitResponse response;
        try { response = SessionExitResponse.FromJson(responseObj); }
        catch (Exception ex)
        {
            logger.Warning(ex, "sidecar: malformed session.exit response");
            return ("{\"ok\":false,\"error\":\"malformed host response\"}", true);
        }
        return ($"{{\"ok\":{(response.Ok ? "true" : "false")}}}", false);
    });
```

And in `ApprovalSidecarContract`:

```csharp
public const string SessionExitToolName = "session_exit";
public static readonly string FullyQualifiedSessionExitToolName =
    $"mcp__{McpServerKey}__{SessionExitToolName}";
```

### Host-side wiring

`ApprovalChannel` gains an optional callback in its ctor:

```csharp
public ApprovalChannel(
    string pipeName,
    IApprovalSink sink,
    ILogger? logger = null,
    int disposeTimeoutSeconds = 2,
    LockRegistry? lockRegistry = null,
    TimeSpan? lockTtl = null,
    TimeSpan? lockMaxWait = null,
    Func<SessionExitRequest, CancellationToken, Task>? onSessionExit = null)  // NEW
```

A new branch in the kind switch inside `RunServerAsync`:

```csharp
case LockWireFormat.SessionExitKind:
    responseJson = (await HandleSessionExitAsync(requestObj).ConfigureAwait(false)).ToJson();
    break;
```

with:

```csharp
private async Task<SessionExitResponse> HandleSessionExitAsync(JsonObject obj)
{
    if (_onSessionExit is null)
    {
        _logger.Warning("approval-channel: session.exit received but no handler wired");
        return new SessionExitResponse(false);
    }
    try
    {
        var req = SessionExitRequest.FromJson(obj);
        // Fire-and-forget: the agent's tool result must return BEFORE stdin closes,
        // so we cannot await the close here. The handler closes over the session
        // and posts work to the threadpool.
        _ = Task.Run(() => _onSessionExit(req, _cts.Token));
        return new SessionExitResponse(true);
    }
    catch (Exception ex)
    {
        _logger.Error(ex, "approval-channel: session.exit handler threw");
        return new SessionExitResponse(false);
    }
}
```

### Session-level integration

`ClaudeSession` gains a public method:

```csharp
/// <summary>
/// Closes the child's stdin without killing the process. Claude finishes the
/// current turn (if any), then exits naturally via EOF. The state machine sees
/// the standard <c>Exited</c> transition through the stdout drain loop's
/// finally block вЂ” so <see cref="AutoRotationService"/> fires if a rotation
/// reason was armed.
///
/// Distinct from <see cref="DisposeAsync"/>, which also kills the process and
/// disposes the launcher / supervisor / write lock.
/// </summary>
public async Task RequestGracefulExitAsync(CancellationToken ct = default)
{
    if (_process is null) return;
    if (_process.HasExited) return;

    _logger.Information("graceful-exit: closing stdin pid={Pid}", _process.Id);
    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
    try { _process.StandardInput.Close(); }
    catch (Exception ex) { _logger.Warning(ex, "graceful-exit: stdin close threw"); }
    finally { _writeLock.Release(); }
}
```

The `_writeLock` acquisition serializes against any in-flight `SendUserMessageAsync`
so we don't close the stream while another task is mid-write.

The callback is plumbed through `ApprovalSidecarLauncher.ConfigureAsync` в†’
`ApprovalChannel` ctor. `ClaudeSession.StartAsync` builds the callback before
configuring the launcher:

```csharp
Func<SessionExitRequest, CancellationToken, Task> onExit = async (req, ct) =>
{
    _logger.Information("session.exit requested by agent reason={Reason}", req.Reason ?? "<none>");
    try { await RequestGracefulExitAsync(ct).ConfigureAwait(false); }
    catch (Exception ex) { _logger.Warning(ex, "session.exit: graceful close threw"); }
};

effectiveOptions = await _approvalLauncher.ConfigureAsync(
    /* вЂ¦existing argsвЂ¦ */
    onSessionExit: onExit).ConfigureAwait(false);
```

### Agent-skill update

`InfiniContextTaskSkill.cs:143-144`:

```diff
-        - **Soft (~60%):** finish the current sub-task, write progress, then exit cleanly so a fresh session can resume without losing state.
-        - **Hard (~85%):** flush *now* вЂ” update `progress.md` with where you are and what's left, then exit. Do not start new work or ask clarifying questions.
+        - **Soft (~60%):** finish the current sub-task, write progress, then call `mcp__infinicontext__session_exit` so a fresh session can resume without losing state.
+        - **Hard (~85%):** flush *now* вЂ” update `progress.md` with where you are and what's left, then call `mcp__infinicontext__session_exit`. Do not start new work or ask clarifying questions.
```

Bump `InfiniContextTaskSkill` version constant by 1 вЂ” the skill text changed.

## Sequence diagram

```
Agent              Claude            Sidecar           Host (Channel)         ClaudeSession
  |                  |                  |                    |                      |
  | tool_use         |                  |                    |                      |
  | session_exit --->|                  |                    |                      |
  |                  | tools/call ----->|                    |                      |
  |                  |                  | {kind:exit} ------>|                      |
  |                  |                  |                    | Task.Run(onExit) --->|
  |                  |                  |                    |                      | RequestGracefulExitAsync
  |                  |                  |                    | {ok:true} <----------|
  |                  |                  | <--- {ok:true}     |                      | (acquires _writeLock)
  |                  |  <- {ok:true}    |                    |                      | stdin.Close()
  |  tool_result <-- |                  |                    |                      |
  |  (result/done)   |                  |                    |                      |
  |       в†“          |                  |                    |                      |
  |       Claude reads EOF, finishes turn, exits             |                      |
  |                  |  process exit в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв–є|
  |                                                                                  | stdout drain loop
  |                                                                                  | HandleProcessExit
  |                                                                                  | в†’ state Exited
  |                                                                                  | в†’ AutoRotationService
  |                                                                                  |    OnStateTransition
  |                                                                                  | в†’ proposal raised
```

The fire-and-forget on the host side is the key timing constraint: closing
stdin synchronously inside the handler would mean the `{ok:true}` reply
races against claude's pending stdout read, and claude could die before
seeing the tool result. The `Task.Run` defers the close to the threadpool
so the reply is written first; in practice the close happens within a few
milliseconds of the reply, before claude has rendered the next turn anyway.

## Why this approach over the alternatives

The 2026-05-27 progress entry listed four options:

1. **Operator-driven close-pane affordance.** Rejected вЂ” adds UI surface for what
   should be an autonomous handoff, defeats the multi-session goal.
2. **Host-detected sentinel** (e.g., scan agent text for `[auto-rotate-now]`).
   Rejected вЂ” brittle; agents drift in phrasing, false positives if user mentions
   the sentinel in a prompt, hard to test deterministically.
3. **N-turn timeout after hard nudge.** Rejected вЂ” heuristic, magic number, fights
   the agent rather than collaborating; would also fire on legitimate post-flush
   sub-tasks the agent decides are needed.
4. **MCP `session_exit` tool.** Chosen вЂ” same pattern as the existing `task_*`
   tools (sidecar tool в†’ host callback over the shared pipe), agent has an
   unambiguous "I am done" contract, host has an unambiguous "close stdin" signal.

## Test plan

1. **`SessionExitMessagesTests`** (new file under `tests/InfiniContext.Tests/Mcp/Sessions/`):
   round-trip `SessionExitRequest` / `SessionExitResponse` through `ToJson` / `FromJson`,
   verify `kind` field, verify nullable `reason`.

2. **`ApprovalChannelSessionExitTests`** (extend or sibling of existing channel
   tests under `tests/InfiniContext.Tests/Mcp/`):
   - Wire a callback that records the request; send a `session.exit` JSON line over
     the pipe; assert callback fired with the right reason and the channel wrote
     `{kind:"session.exit","ok":true}` back.
   - With no callback wired (legacy ctor), send `session.exit`; assert response is
     `{ok:false}` and a warning was logged. (Backward compat: a session opened by
     an older host should not crash if a newer skill version emits the tool.)

3. **`ClaudeSessionGracefulExitTests`** (new file under `tests/InfiniContext.Tests/Sessions/`):
   - Use `FakeClaude` with a script that emits a long-running turn, then watch
     for EOF on stdin and exits.
   - Call `RequestGracefulExitAsync`; assert child exits naturally (not via Kill вЂ”
     check exit code is 0, not Win32 STATUS_CONTROL_C_EXIT / Job Object's
     hex `0xC000013A`).
   - State machine reaches `Exited` with `HandleProcessExit(0)`.

4. **End-to-end via `FakeClaude` action script**
   (`tests/scripts/session-exit.actions.json`):
   - Action script: after `first_user_message`, emit a `tool_use` for
     `mcp__infinicontext__session_exit` with `{reason:"test"}`, then a result event,
     then exit-on-stdin-EOF behaviour.
   - Test: spawn a real session manager, configure with auto-approve sink, let
     FakeClaude run; assert the session reaches `Exited`, `AutoRotationService`
     fires *if* `RotationReason` was pre-armed by the test (call
     `ctx.MarkRotationReason(Hard)` before the script triggers `session_exit`).

5. **Existing `AutoRotationServiceTests` need no changes** вЂ” they already cover the
   `Exited + RotationReason` path. The new tool changes *how* `Exited` is reached,
   not *what happens after*.

6. **No new `MainWindowViewModelTests`** вЂ” the GUI VM's auto-rotation handler is
   already covered by the Slice 2 tests; the new flow lands at the same handler
   via the same `AutoRotationProposed` event.

## Out of scope

- **Confirmation prompt** before exit: would defeat the "agent acts autonomously
  after flushing" goal. Operator can `Ctrl+C` to interrupt if the agent calls
  exit prematurely.
- **`reason` propagation to the next session**: a future enhancement could pass
  it into `PrepareSwitchAsync` so the successor's first user message includes
  "(previous session exited because: вЂ¦)". Defer until field evidence shows it's
  useful.
- **Force-exit timeout**: if claude doesn't exit after stdin close, we don't kill.
  The operator can force-close via the existing path. Worst case: a hung session
  shows as `Idle` indefinitely until the host shuts down. Same failure mode as
  today.
- **Sidecar-level idempotency**: a second `session_exit` call on an
  already-closing session returns `{ok:true}` (state machine has already
  transitioned); no need for state checks in the handler.

## Implementation slices

This is a one-PR feature; no need to split. Suggested commit order:

1. Wire format + sidecar contract constants + sidecar tool registration.
2. `ApprovalChannel` callback plumbing + handler.
3. `ClaudeSession.RequestGracefulExitAsync` + `ApprovalSidecarLauncher` wiring.
4. Agent-skill text update + version bump.
5. Tests (1-4 above).

Total estimate: ~250 LOC including tests. No DB migration, no settings schema
change, no MCP protocol version bump (the tool's a leaf addition).

## Open questions

- **`reason` exposed on `AutoRotationProposal`?** Currently the proposal carries
  `Session`, `TaskId`, `Reason` (the `RotationReason` enum, not the agent's
  string), `Status`, `Plan`. A new `string? AgentExitReason` field could surface
  in the GUI's `StatusText`. Low cost, but extends the type. Defer until a
  consumer asks.
- **Should the sidecar log the call at Information or Debug?** Current pattern
  (`lock.acquire`/`lock.release`) is Debug. `session.exit` is rarer and
  load-bearing for the auto-rotation chain, so Information seems right. Matches
  the host-side log in `HandleSessionExitAsync`.
