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

public enum ToolStackChangeKind { Opened, Closed }

public sealed record ToolStackChange(
    ToolStackChangeKind Kind,
    ToolUseSlot Slot,
    DateTimeOffset Timestamp);
