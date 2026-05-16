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
///   Sending|Streaming|Cancelling|Compacting → Idle on result event
///   *         → Exited       via HandleProcessExit()
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

    public event Action<StateTransition>? Transitioned;
    public event Action<ToolStackChange>? ToolStackChanged;

    public void BeginSend()
    {
        lock (_sync)
        {
            if (State == SessionState.Idle)
            {
                Transition(SessionState.Sending, "BeginSend", null);
            }
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
                    Transition(SessionState.Idle, $"result/{result.Subtype ?? "<null>"}", ev);
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

    private void Transition(SessionState to, string reason, ClaudeEvent? cause)
    {
        var from = State;
        State = to;
        Transitioned?.Invoke(new StateTransition(from, to, reason, _clock(), cause));
    }
}
