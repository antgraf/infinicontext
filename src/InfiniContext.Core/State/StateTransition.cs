using InfiniContext.Core.Events;

namespace InfiniContext.Core.State;

public enum SessionState
{
    Idle,
    Sending,
    Streaming,
    Cancelling,
    Compacting,
    Retrying,
    Errored,
    Exited,
}

public sealed record StateTransition(
    SessionState From,
    SessionState To,
    string Reason,
    DateTimeOffset Timestamp,
    ClaudeEvent? EventThatCaused);

public sealed record ToolUseSlot(
    string Id,
    string Name,
    DateTimeOffset OpenedAt,
    string? ParentToolUseId);

// Closed = a matching tool_result arrived. Abandoned = the slot was force-drained at a turn
// boundary or process exit without a result (interrupt mid-tool, dropped event, early exit) — M10.
public enum ToolStackChangeKind { Opened, Closed, Abandoned }

public sealed record ToolStackChange(
    ToolStackChangeKind Kind,
    ToolUseSlot Slot,
    DateTimeOffset Timestamp);
