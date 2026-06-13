using InfiniContext.Core.Events;

namespace InfiniContext.Core.State;

/// <summary>
/// Top-level session state machine driven by parsed claude events plus host-driven
/// inputs (BeginSend, Cancel, ProcessExit). Tool-use lifecycle is tracked in a parallel
/// correlation map keyed by tool_use.id, NOT in the top-level state — sub-agent tool calls
/// are top-level events with parent_tool_use_id set, so the "stack" is logical, not nested.
///
/// Transition rules (design doc §4.4, fixtures-driven):
///   Idle      → Sending      via BeginSend()
///   Sending   → Streaming    on first assistant / user / stream_event
///   Streaming → Compacting   on system/compact (then back to Streaming on next non-compact event)
///   *         → Cancelling   via Cancel() (only from Sending/Streaming/Compacting)
///   Sending|Streaming|Cancelling|Compacting → Idle    on result event WHEN PendingSendCount drops to 0
///   Sending|Streaming|Cancelling|Compacting -> Sending on result event WHEN PendingSendCount remains > 0
///   *         → Exited       via HandleProcessExit()
///
/// <para><b>Per-turn boundaries with buffered sends (task 0011 RC4).</b>
/// <see cref="BeginSend"/> increments <see cref="PendingSendCount"/> on every call (not just on
/// the Idle-to-Sending transition), and the <c>ResultEvent</c> handler decrements it. While more
/// sends remain pending, the post-Result transition lands in <see cref="SessionState.Sending"/>
/// rather than <see cref="SessionState.Idle"/>, signalling that claude has started draining the
/// next buffered prompt. Without this, a buffered second send produced no state-machine signal
/// at all: the second <c>BeginSend</c> from non-Idle was a no-op, and the first <c>ResultEvent</c>
/// took the machine to Idle where nothing would move it back. Consumers (the GUI's
/// <c>IsAgentTurnInFlight</c>, future TUI / Web UIs) can now subscribe to <see cref="Transitioned"/>
/// alone and see one clean Idle-to-Sending-to-Streaming-to-Idle cycle per claude turn.</para>
/// </summary>
public sealed class SessionStateMachine
{
    private readonly Dictionary<string, ToolUseSlot> _openToolUses = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    // Serializes Apply / BeginSend / Cancel / HandleProcessExit across the stdout-loop thread,
    // the REPL/sender thread, and the Ctrl+C handler thread. Held while invoking
    // Transitioned / ToolStackChanged handlers — handlers must NOT re-enter the state machine.
    private readonly object _sync = new();

    public SessionStateMachine(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public SessionState State { get; private set; } = SessionState.Idle;

    /// <summary>Open tool calls keyed by <c>tool_use.id</c>. Includes both parent and sub-agent calls.</summary>
    public IReadOnlyDictionary<string, ToolUseSlot> OpenToolUses => _openToolUses;

    /// <summary>Set while a tool call is awaiting an approval response from the host MCP server. Stage 1H wires the toggle.</summary>
    public bool AwaitingApproval { get; internal set; }

    /// <summary>Mirror of the parser's drift counter — incremented as the state machine sees <see cref="UnknownEvent"/> or <see cref="UnknownBlock"/>.</summary>
    public int UnknownEventCount { get; private set; }

    /// <summary>Number of sends initiated via <see cref="BeginSend"/> whose <c>ResultEvent</c>
    /// has not yet arrived. Incremented on every <see cref="BeginSend"/> call (not just on the
    /// Idle to Sending transition), decremented on every <c>ResultEvent</c>. A value greater
    /// than one means at least one send is buffered on claude's stdin behind a streaming turn.
    /// The <c>ResultEvent</c> handler uses this to decide whether the post-result transition is
    /// to <see cref="SessionState.Idle"/> (count drops to 0 - claude is done) or back to
    /// <see cref="SessionState.Sending"/> (count remains positive - claude is starting the next
    /// buffered turn). Task 0011 RC4.</summary>
    public int PendingSendCount { get; private set; }

    public event Action<StateTransition>? Transitioned;
    public event Action<ToolStackChange>? ToolStackChanged;

    public void BeginSend()
    {
        lock (_sync)
        {
            // Once the process is gone, sends are dropped silently (the writer in
            // ClaudeSession.SendUserMessageAsync throws first, but defend-in-depth here so the
            // count can never desync from observable transitions).
            if (State == SessionState.Exited) return;
            // Always count the send: even from non-Idle states, this call corresponds to a real
            // stdin write that claude will eventually drain. The state transition itself only
            // fires from Idle (the prior contract); the count carries the per-turn boundary
            // signal that the state alone cannot. See class doc-comment RC4.
            PendingSendCount++;
            if (State == SessionState.Idle)
            {
                Transition(SessionState.Sending, "BeginSend", null);
            }
        }
    }

    /// <summary>
    /// Roll back the most recent <see cref="BeginSend"/> when the corresponding stdin write threw
    /// or was cancelled (M6). Decrements <see cref="PendingSendCount"/> and, if this was the only
    /// in-flight send and the machine never advanced past <see cref="SessionState.Sending"/>,
    /// returns it to <see cref="SessionState.Idle"/> — otherwise the session is stuck "busy"
    /// forever and rotation / status logic sees a phantom turn.
    /// </summary>
    public void AbortSend()
    {
        lock (_sync)
        {
            if (PendingSendCount > 0) PendingSendCount--;
            if (PendingSendCount == 0 && State == SessionState.Sending)
                Transition(SessionState.Idle, "AbortSend", null);
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            if (State is SessionState.Sending or SessionState.Streaming or SessionState.Compacting)
            {
                Transition(SessionState.Cancelling, "Cancel", null);
            }
        }
    }

    public void HandleProcessExit(int? exitCode)
    {
        lock (_sync)
        {
            if (State != SessionState.Exited)
            {
                // Process is gone: no tool_result will ever arrive for an open slot. Drain so the
                // correlation map doesn't leak and consumers see the tools abandoned (M10).
                DrainOpenToolUses("process exit");
                Transition(SessionState.Exited, $"ProcessExit(exitCode={exitCode?.ToString() ?? "<null>"})", null);
            }
        }
    }

    public void Apply(ClaudeEvent ev)
    {
        lock (_sync)
        {
            ApplyLocked(ev);
        }
    }

    private void ApplyLocked(ClaudeEvent ev)
    {
        // After process exit no further events should mutate state — drop them defensively.
        if (State == SessionState.Exited) return;

        switch (ev)
        {
            case ResultEvent result:
                if (State is SessionState.Sending or SessionState.Streaming
                          or SessionState.Cancelling or SessionState.Compacting)
                {
                    // Decrement before the transition so the new state reflects the *remaining*
                    // queue depth. Clamp at 0 in case of a Result with no matching BeginSend
                    // (defensive against parser anomalies / future claude behaviour we haven't
                    // seen yet).
                    if (PendingSendCount > 0) PendingSendCount--;
                    // A result ends a turn: any tool_use still open never got its tool_result
                    // (interrupt mid-tool, abandoned call, dropped event). Drain it so slots don't
                    // leak across long sessions and ghost "running tools" don't linger (M10).
                    DrainOpenToolUses($"turn ended (result/{result.Subtype ?? "<null>"})");
                    if (PendingSendCount > 0)
                    {
                        // Buffered sends remain: claude has finished the current turn and is
                        // about to drain the next stdin message. Cycle back to Sending so the
                        // next AssistantEvent's MaybeStartStreaming arm flips us into Streaming
                        // naturally - that produces a clean per-turn cycle for every buffered
                        // send instead of stranding the state machine in Idle. RC4.
                        Transition(SessionState.Sending, $"result/{result.Subtype ?? "<null>"} (queued sends remain: {PendingSendCount})", ev);
                    }
                    else
                    {
                        Transition(SessionState.Idle, $"result/{result.Subtype ?? "<null>"}", ev);
                    }
                }
                break;

            case SystemCompactEvent:
                if (State == SessionState.Streaming)
                {
                    Transition(SessionState.Compacting, "system/compact", ev);
                }
                break;

            case AssistantEvent a:
                MaybeStartStreaming(ev);
                LeaveCompactingIfNeeded(ev);
                foreach (var block in a.Content)
                {
                    if (block is ToolUseBlock tu) OpenToolUse(tu, a.ParentToolUseId, ev);
                    else if (block is UnknownBlock) UnknownEventCount++;
                }
                break;

            case UserEvent u:
                MaybeStartStreaming(ev);
                LeaveCompactingIfNeeded(ev);
                foreach (var block in u.Content)
                {
                    if (block is ToolResultBlock tr) CloseToolUse(tr.ToolUseId, ev);
                    else if (block is UnknownBlock) UnknownEventCount++;
                }
                break;

            case PartialMessageEvent:
                MaybeStartStreaming(ev);
                LeaveCompactingIfNeeded(ev);
                break;

            case RateLimitEvent:
            case SystemTaskStartedEvent:
            case SystemTaskProgressEvent:
            case SystemTaskNotificationEvent:
            case SystemStatusEvent:
            case SystemHookResponseEvent:
            case SystemPluginInstallEvent:
            case SystemThinkingTokensEvent:
            case SystemInitEvent:
            case ControlResponseEvent:
                // Informational — no state change.
                break;

            case UnknownEvent:
                UnknownEventCount++;
                break;
        }
    }

    private void MaybeStartStreaming(ClaudeEvent ev)
    {
        if (State == SessionState.Sending)
        {
            Transition(SessionState.Streaming, ev.Type, ev);
        }
    }

    private void LeaveCompactingIfNeeded(ClaudeEvent ev)
    {
        if (State == SessionState.Compacting)
        {
            Transition(SessionState.Streaming, ev.Type, ev);
        }
    }

    private void OpenToolUse(ToolUseBlock block, string? parentToolUseId, ClaudeEvent ev)
    {
        if (string.IsNullOrEmpty(block.Id)) return;
        if (_openToolUses.ContainsKey(block.Id)) return;

        var slot = new ToolUseSlot(block.Id, block.Name, _clock(), parentToolUseId);
        _openToolUses[block.Id] = slot;
        ToolStackChanged?.Invoke(new ToolStackChange(ToolStackChangeKind.Opened, slot, slot.OpenedAt));
    }

    private void CloseToolUse(string toolUseId, ClaudeEvent ev)
    {
        if (string.IsNullOrEmpty(toolUseId)) return;
        if (!_openToolUses.TryGetValue(toolUseId, out var slot)) return;
        _openToolUses.Remove(toolUseId);
        ToolStackChanged?.Invoke(new ToolStackChange(ToolStackChangeKind.Closed, slot, _clock()));
    }

    /// <summary>
    /// Force-close every open tool-use slot, emitting an <see cref="ToolStackChangeKind.Abandoned"/>
    /// notification per slot. Called at turn boundaries (<c>ResultEvent</c>) and process exit so
    /// slots whose <c>tool_result</c> never arrived don't leak forever (M10). Must be called under
    /// <see cref="_sync"/>.
    /// </summary>
    private void DrainOpenToolUses(string reason)
    {
        if (_openToolUses.Count == 0) return;
        var slots = _openToolUses.Values.ToList();
        _openToolUses.Clear();
        var now = _clock();
        foreach (var slot in slots)
            ToolStackChanged?.Invoke(new ToolStackChange(ToolStackChangeKind.Abandoned, slot, now));
    }

    private void Transition(SessionState to, string reason, ClaudeEvent? cause)
    {
        var from = State;
        State = to;
        Transitioned?.Invoke(new StateTransition(from, to, reason, _clock(), cause));
    }
}
