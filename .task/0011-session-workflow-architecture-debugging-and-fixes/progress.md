# Progress

## 2026-05-22

- Task created.
- **Root-cause analysis complete.** The "thinking..." card never resolves because of two
  bugs in `SessionRowViewModel`, both rooted in the same architectural confusion: the
  `_pendingTurn` field conflates "last turn I created" with "in-flight turn awaiting a
  ResultEvent."

### Reproduction trace (task-bound GUI session)

1. `SpawnSessionForTaskAsync` builds the pointer text and calls
   `_host.CreateSessionAsync` which spawns claude with `FirstUserMessage` set.
   `ClaudeSession.StartAsync` fires-and-forgets `SendFirstUserMessageAsync`, which
   transitions the state machine Idle -> Sending.
2. `MainWindowViewModel:316-320` then calls `row.BeginUserTurn(firstUserMessage)`.
   This creates `turn1` with `UserPrompt = "Working on task..."`, default
   `Headline = "thinking..."`, `IsInProgress = true`. `_pendingTurn = turn1`.
3. `SystemInitEvent` arrives. `ApplyEvent`'s init arm runs and **closes turn1
   prematurely** (`SessionRowViewModel.cs:303-324`):
   - `SyntheticHeadlines.Contains("thinking...")` is true, so the headline is blanked
     to `""`.
   - `IsInProgress = false`.
   - `if (string.IsNullOrEmpty(initTurn.Headline)) _pendingTurn = null;` -> pointer
     dropped.
   The in-source comment ("Keep `_pendingTurn` live when there's a real seed prompt")
   states the intended behaviour, but the code does the opposite because the synthetic
   check matches the ctor-default `"thinking..."` -- which is exactly what a real seed
   prompt also looks like before claude responds.
4. First `AssistantEvent` for the pointer (thinking + tool_use blocks) arrives.
   `EnsurePendingTurn("(claude-initiated)")` creates `turn2` -- a synthetic
   "claude-initiated" card -- because `_pendingTurn` was dropped at step 3. The user
   sees this as a NEW card, distinct from their pointer prompt.
5. Operator types and clicks Send while claude is still mid-response.
   `SendToSessionAsync` calls `target.BeginUserTurn(text)` which **overwrites
   `_pendingTurn` from turn2 to turn3** (the operator's new turn). turn2 is now
   orphaned: nothing will ever close it. Its `Headline` stays `"thinking..."` and
   `IsInProgress` stays `true`. This is the visible "stuck thinking" card.
6. Claude finishes the pointer turn -> `ResultEvent` arrives. `ClosePendingTurnWith`
   closes `_pendingTurn` = turn3 (operator's turn!), filling it with the pointer's
   `Result` text and footer summary -- the operator's card now displays the WRONG
   reply.
7. Claude starts processing the operator's buffered prompt -> next `AssistantEvent`
   finds `_pendingTurn` null again and creates yet another `(claude-initiated)` turn
   (turn4) for that work.

### Root causes

**RC1: `SystemInitEvent` closes the seed-prompt turn.**
`SessionRowViewModel.cs:315-320`. The synthetic-headline check matches the default
ctor headline, so a real operator-pointer turn gets blanked and closed before claude
has even started responding.

**RC2: `BeginUserTurn` unconditionally overwrites `_pendingTurn`.**
`SessionRowViewModel.cs:221-237`. When the operator sends while a turn is in flight
(documented design per `IsAgentTurnInFlight` comment block), the new turn steals the
in-flight slot. There is no queue.

**RC3 (architectural):** `_pendingTurn` is a single scalar that tries to encode
"current in-flight turn." That works for synchronous claude (send -> response ->
done), but the host explicitly supports operator queueing via stdin buffering. The
state machine returns to Idle between turns, but the *next* `BeginSend` is a no-op if
the channel is still draining the previous turn, so state transitions alone don't
disambiguate which `ResultEvent` belongs to which user message. The fix is a turn
queue keyed on send order.

**RC4 (state-machine subordinate):** When `SendUserMessageAsync` runs while the state
machine is already Streaming, `BeginSend()` is a no-op (`SessionStateMachine.cs:51`).
The buffered prompt eventually gets processed but `MaybeStartStreaming` only
transitions `Sending -> Streaming`, so a buffered second send produces no
state-machine signal of "new turn starting." This is fine for the CLI (which renders
a flat fragment stream) but leaves the GUI without a clean per-turn boundary marker
out of the state machine.

### Fix plan

1. **Replace `_pendingTurn` with `_inFlightTurn` + `Queue<TurnNodeViewModel>` for
   queued turns.** Turn boundaries are determined by send order, not by `_pendingTurn`
   assignment.
   - `BeginUserTurn(text)`: always create a new turn, always add to `Turns`.
     If `_inFlightTurn is null` -> become the in-flight turn. Else -> enqueue.
   - `ResultEvent`: close `_inFlightTurn`; dequeue next as the new `_inFlightTurn`.
   - `AssistantEvent`/`UserEvent`: attach to `_inFlightTurn`. If null (synthetic
     "(claude-initiated)" case), create a new claude-initiated turn and set as
     in-flight.
2. **`SystemInitEvent` no longer closes a turn with a real seed prompt.** Branch on
   `HasOperatorMessage`:
   - Real seed prompt: append (session ready) / system/init to children; keep
     `IsInProgress=true` and Headline="thinking..." until claude's ResultEvent closes
     it.
   - Synthetic placeholder turn (free session, no pointer): blank headline,
     IsInProgress=false, clear in-flight (current behaviour).
3. **Tests** in `tests/InfiniContext.Tests/Gui/SessionRowViewModelTests.cs` covering
   the canonical sequence: seed-prompt + system/init + assistant(thinking+tool_use) +
   user(tool_result) + result -> exactly ONE closed turn with the seed prompt and a
   real headline; and the queued-send scenario: send2 while send1 in flight ->
   send2's turn shows queue indicator, both turns close in order with correct
   headlines.
4. Optional follow-up (out of scope here but logged): expose `AssistantEvent.MessageId`
   from the parser so the GUI can additionally validate by message.id grouping.

### Implementation (landed 2026-05-22)

- `TurnNodeViewModel`: added `IsQueued` observable property (default false) for XAML
  to render a "queued" badge once the polish slice lands. View binding is a follow-up.
- `SessionRowViewModel`: `_pendingTurn` replaced by `_inFlightTurn` plus
  `_queuedTurns` (`Queue<TurnNodeViewModel>`). `BeginUserTurn` now enqueues when an
  in-flight turn exists. `CloseInFlightTurnWith` (renamed from `ClosePendingTurnWith`)
  promotes the head of the queue.
- `ApplyEvent`'s `SystemInitEvent` arm now branches on `HasOperatorMessage`: a real
  seed-prompt turn stays in-flight with its `thinking...` headline; only synthetic
  "(session start)" cards get closed at init time.
- `OnStateTransition`'s exit branch flushes the queue, marks each turn `IsInProgress
  = false`, clears the queued badge, and appends a "(not sent - session exited
  before turn started)" system child so operators can see exactly which buffered
  turns never ran.

### Test surface

Seven new tests in `tests/InfiniContext.Tests/Gui/SessionRowViewModelTests.cs`:
- `Seed_prompt_turn_survives_system_init_without_closing` (regression for RC1)
- `Free_session_init_event_opens_synthetic_card_and_closes_it` (companion: synthetic
  cards still close)
- `Seed_prompt_full_lifecycle_one_card_one_close` (end-to-end seed -> init ->
  assistant text -> result -> one closed turn)
- `Operator_send_during_inflight_turn_is_queued` (regression for RC2)
- `Result_event_promotes_next_queued_turn_and_clears_isqueued`
- `Assistant_event_during_queued_state_attaches_to_inflight_not_queued`
- `Process_exit_flushes_queue_and_marks_each_not_sent`

Full suite: 1014 passed, 0 failed, 3 skipped (the existing macOS-only POSIX
supervisor tests). `dotnet format --verify-no-changes` clean.

### RC4 follow-up landed in the same slice (2026-05-22)

- `SessionStateMachine` now carries `PendingSendCount` and uses it as the per-turn
  boundary signal that pure state transitions could not provide on their own.
  - `BeginSend()` increments unconditionally (every call corresponds to a real stdin
    write claude will eventually drain); from Exited it is dropped so the count
    cannot desync from observable transitions.
  - `ResultEvent` decrements (clamped at 0 defensively). If the count is still
    positive the post-Result transition lands in `Sending` instead of `Idle`, so
    the next `AssistantEvent`'s `MaybeStartStreaming` flips to `Streaming`
    naturally - one clean Idle to Sending to Streaming to Idle cycle per claude
    turn, even when a second prompt was buffered on stdin.
- Direct test coverage in `tests/InfiniContext.Tests/State/StateMachineBufferedSendsTests.cs`
  (RC4 cycle, defensive clamp, Exited drop). The GUI tests in
  `SessionRowViewModelTests` continue to cover the operator-visible queue model.

### Out of scope (logged for follow-up)

- XAML badge for `IsQueued`: model is wired; view rendering is a polish slice.

### Migrated to docs/todo.md (2026-05-24)

- XAML `IsQueued` badge follow-up moved to `docs/todo.md` Section D (Stage 3 Avalonia GUI) so it lives on the single consolidated work queue. The bullet above is kept as historical context; the canonical pickup point is now todo.md.
- (RC4 was originally listed as out-of-scope here but landed in the same slice via the `PendingSendCount` mechanism in `SessionStateMachine` - see the "RC4 follow-up landed" section above. Not migrated to todo.md.)

## 2026-05-24

### Audit + RC4 direct test coverage

- Audited the uncommitted working tree against the 2026-05-22 progress notes. Drift
  found: progress.md listed RC4 as out-of-scope, but `SessionStateMachine.cs` had
  already landed the `PendingSendCount` mechanism. The doc was reconciled (the
  "RC4 follow-up landed" section above is the new canonical record).
- Added `tests/InfiniContext.Tests/State/StateMachineBufferedSendsTests.cs` covering
  the state-machine RC4 contract directly (the existing GUI tests only exercise it
  indirectly through `SessionRowViewModel`). Eight cases:
  - `Single_BeginSend_then_Result_lands_in_Idle_with_count_zero`
  - `Second_BeginSend_during_Streaming_increments_count_without_transition`
    (regression for the no-op-from-non-Idle contract pinned in
    `StateMachineNoOpTests.BeginSend_from_Streaming_is_a_noop`; ensures the count
    side-effect doesn't sneak in a transition).
  - `Result_with_buffered_send_cycles_back_to_Sending` (core RC4)
  - `Result_reason_includes_remaining_queue_depth_for_observability` (locks the
    `result/<subtype> (queued sends remain: N)` reason string so future log readers
    can correlate the cycle-back with the buffered send).
  - `Two_buffered_sends_produce_two_clean_cycles` (end-to-end transition trace)
  - `Trailing_Result_from_Idle_is_still_ignored_after_count_drop` (regression
    guard for `StateMachineNoOpTests.Result_event_while_Idle_is_ignored` under the
    new decrement code)
  - `BeginSend_from_Exited_does_not_increment_count` (early-return guard)
  - `Cancel_during_buffered_sends_still_routes_Result_through_queue_depth`
    (Cancelling state is also in the Result arm)
- Verification deferred: the test project chains through `InfiniContext.Gui.csproj`
  and three `InfiniContext.Gui.exe` processes are holding the binary, so `dotnet
  test` cannot run until the operator closes those instances. Logic was traced by
  hand against `SessionStateMachine.cs` lines 69-87 (BeginSend) and 126-148
  (ResultEvent arm).
